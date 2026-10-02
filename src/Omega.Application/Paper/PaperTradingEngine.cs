using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Omega.Core.MarketData;
using Omega.Core.Results;
using Omega.Core.Trading;
using Omega.Execution;
using Omega.Execution.Paper;
using Omega.Features;
using Omega.Risk;
using Omega.Strategy;

namespace Omega.Application.Paper;

/// <summary>Fixed configuration of a paper session. A session never changes configuration: a new one is needed.</summary>
public sealed record PaperSessionConfig
{
    public required string Name { get; init; }

    public required string Symbol { get; init; }

    public required CandleInterval Interval { get; init; }

    public required StrategyIdentity Strategy { get; init; }

    public required decimal InitialCapital { get; init; }

    public required TradingCosts Costs { get; init; }

    public required RiskLimits Risk { get; init; }

    /// <summary>Triple-barrier timeout: close at the close of this many candles after entry, if set.</summary>
    public int? MaxHoldingCandles { get; init; }

    public InstrumentFilters Filters { get; init; } = InstrumentFilters.None;

    /// <summary>A candle older than this when it is processed is stale: it can close positions but never open one.</summary>
    public TimeSpan FreshnessGrace { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>First candle to process for a new session; null = the next candle after the latest stored one.</summary>
    public DateTimeOffset? StartFromUtc { get; init; }
}

public static class PaperErrors
{
    public const string ConfigurationChanged = "PAPER_CONFIGURATION_CHANGED";
}

/// <summary>
/// Paper trading (Phase 12): the full pipeline on each new closed candle — features → strategy → risk → decision →
/// simulated execution → journal — with exactly the event order and fill rules of the backtester (a parity test
/// checks it). Each candle is persisted in one transaction, so a restart resumes where it stopped.
/// </summary>
public sealed class PaperTradingEngine
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly IPaperTradingStore _store;
    private readonly ICandleStore _candles;
    private readonly FeatureEngine _featureEngine;
    private readonly IStrategy _strategy;
    private readonly PaperSessionConfig _config;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly PaperExecutionProvider _execution;
    private readonly TimeSpan _length;

    private PaperSessionRecord _session = null!;
    private PaperState _state = null!;
    private RiskManager _risk = null!;

    public PaperTradingEngine(
        IPaperTradingStore store, ICandleStore candles, FeatureEngine featureEngine, IStrategy strategy, PaperSessionConfig config, TimeProvider time, ILogger logger)
    {
        _store = store;
        _candles = candles;
        _featureEngine = featureEngine;
        _strategy = strategy;
        _config = config;
        _time = time;
        _logger = logger;
        _execution = new PaperExecutionProvider(config.Costs);
        _length = config.Interval.ToTimeSpan();
    }

    public Guid SessionId => _session.Id;

    public RiskManager Risk => _risk;

    /// <summary>Opens the session (creating it the first time) and restores its state and working orders.</summary>
    /// <exception cref="Omega.Core.Persistence.PersistenceException">The store is unavailable.</exception>
    public async Task<Result> StartAsync(CancellationToken cancellationToken)
    {
        var configJson = JsonSerializer.Serialize(new { _config.Symbol, Interval = _config.Interval.ToCode(), _config.Strategy, _config.InitialCapital, _config.Costs, _config.Risk, _config.MaxHoldingCandles, _config.Filters }, Json);
        var existing = await _store.GetSessionAsync(_config.Name, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            // Compared by meaning, not text: a database may store JSON normalized (PostgreSQL jsonb reorders keys and
            // drops whitespace), and a textual comparison would refuse every restart.
            if (!JsonNode.DeepEquals(JsonNode.Parse(existing.ConfigJson), JsonNode.Parse(configJson)))
            {
                return Result.Failure(new Error(PaperErrors.ConfigurationChanged,
                    $"Paper session '{_config.Name}' was started with another configuration (strategy, costs, risk or capital). " +
                    "Use a new session name: a journal must never mix configurations."));
            }

            _session = existing;
            _state = JsonSerializer.Deserialize<PaperState>(existing.StateJson, Json)!;
            _risk = RiskManager.Restore(_config.Risk, _state.Risk);
            _execution.Restore(await _store.GetWorkingOrdersAsync(existing.Id, cancellationToken).ConfigureAwait(false), _state.OrderSequence);
            LogResumed(_logger, _config.Name, _state.LastCandleOpenTimeUtc);
            return Result.Success();
        }

        var now = _time.GetUtcNow();
        var cursor = _config.StartFromUtc is { } start
            ? start - _length
            : (await _candles.GetLatestAsync(_config.Symbol, _config.Interval, cancellationToken).ConfigureAwait(false))?.OpenTimeUtc;
        _risk = new RiskManager(_config.Risk, _config.InitialCapital, cursor ?? now);
        _state = new PaperState(_config.InitialCapital, null, null, false, _risk.Snapshot(), 0, cursor, 0);
        _session = new PaperSessionRecord(Guid.NewGuid(), _config.Name, _config.Symbol, _config.Interval.ToCode(), configJson, Serialize(), now, now, cursor);
        await _store.CreateSessionAsync(_session, cancellationToken).ConfigureAwait(false);
        LogCreated(_logger, _config.Name, _config.Strategy.Name, cursor);
        return Result.Success();
    }

    /// <summary>Applies pending operator commands (kill switch), each persisted with the state it produced.</summary>
    public async Task<int> ApplyCommandsAsync(CancellationToken cancellationToken)
    {
        var commands = await _store.GetPendingCommandsAsync(_session.Id, cancellationToken).ConfigureAwait(false);
        foreach (var command in commands)
        {
            var now = _time.GetUtcNow();
            string result;
            switch (command.Type)
            {
                case PaperCommandType.TripKillSwitch:
                    var wasActive = _risk.KillSwitch.IsActive;
                    _risk.KillSwitch.Trip($"manual: {command.Reason} (by {command.RequestedBy})", now);
                    result = wasActive ? "Kill switch was already active; its original reason is kept." : "Kill switch tripped.";
                    break;
                case PaperCommandType.ResetKillSwitch:
                    result = _risk.KillSwitch.IsActive ? "Kill switch reset." : "Kill switch was not active.";
                    _risk.KillSwitch.Reset(command.RequestedBy);
                    break;
                default:
                    result = "Unknown command; ignored.";
                    break;
            }

            _state = _state with { Risk = _risk.Snapshot() };
            await _store.CompleteCommandAsync(command.Id, _session.Id, result, Serialize(), now, cancellationToken).ConfigureAwait(false);
            LogCommand(_logger, command.Type, command.RequestedBy, result);
        }

        return commands.Count;
    }

    /// <summary>Processes every stored closed candle after the cursor, in order, persisting each one.</summary>
    public async Task<int> ProcessNewCandlesAsync(CancellationToken cancellationToken, int maxCandles = 10_000)
    {
        var from = _state.LastCandleOpenTimeUtc is { } last ? last + _length : DateTimeOffset.UnixEpoch;
        var pending = await _candles.GetRangeAsync(_config.Symbol, _config.Interval, from, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);

        var processed = 0;
        foreach (var candle in pending.Take(maxCandles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessCandleAsync(candle, cancellationToken).ConfigureAwait(false);
            processed++;
        }

        return processed;
    }

    private async Task ProcessCandleAsync(Candle candle, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var trades = new List<PaperTrade>();
        var touched = new List<Order>();
        var contiguous = _state.LastCandleOpenTimeUtc is not { } previous || candle.OpenTimeUtc - previous == _length;

        // 0. A held position spends one more candle in the market (the candle it exits in counts, as in the backtester).
        if (_state.Position is { } held)
        {
            _state = _state with { Position = held with { HoldingCandles = held.HoldingCandles + 1 } };
        }

        // 1. Open of the candle: a gap invalidates the pending entry (its features may be stale).
        if (!contiguous && _state.PendingEntry is { } stale)
        {
            notes.Add(Text($"SIGNAL_EXPIRED_BY_DATA_GAP: entry signal from {stale.SignalTimeUtc:O} expired."));
            _state = _state with { PendingEntry = null };
        }

        // 2. Open: exit decided at the previous close.
        if (_state.PendingExit && _state.Position is { } exiting)
        {
            await ClosePositionAsync(exiting, candle, candle.Open, ExitReason.Signal, touched, trades, notes, cancellationToken).ConfigureAwait(false);
        }

        _state = _state with { PendingExit = false };

        // 3. Open: entry decided at the previous close, sized now by the Risk Engine at the fill price.
        if (_state.PendingEntry is { } entry && _state.Position is null)
        {
            await OpenPositionAsync(entry, candle, touched, notes, cancellationToken).ConfigureAwait(false);
        }

        _state = _state with { PendingEntry = null };

        // 4. During the candle: protective orders (stop first when both levels are inside the candle).
        foreach (var report in await _execution.SynchronizeAsync(candle, cancellationToken).ConfigureAwait(false))
        {
            touched.Add(report.Filled);
            if (report.Canceled is { } canceled)
            {
                touched.Add(canceled);
            }

            var position = _state.Position!;
            trades.Add(Trade(position, candle, report.Filled.AverageFillPrice!.Value, report.Filled.Fee, report.Reason));
            notes.Add(Text($"{report.Reason}: sold {report.Filled.Quantity} at {report.Filled.AverageFillPrice}."));
            _state = _state with { Cash = _state.Cash + ((report.Filled.Quantity * report.Filled.AverageFillPrice!.Value) - report.Filled.Fee), Position = null };
            _risk.OnTradeClosed(trades[^1].NetPnl);
        }

        // 5. Close: triple-barrier timeout.
        if (_state.Position is { } timed && _config.MaxHoldingCandles is { } maxHolding && timed.HoldingCandles >= maxHolding)
        {
            await ClosePositionAsync(timed, candle, candle.Close, ExitReason.TimeLimit, touched, trades, notes, cancellationToken).ConfigureAwait(false);
        }

        // 6. Close: equity, marked to market, feeds the Risk Engine (daily loss, drawdown, kill switch).
        var equity = _state.Cash + (_state.Position is { } open ? open.Quantity * candle.Close : 0m);
        _risk.OnEquity(candle.CloseTimeUtc, equity);

        // 7. Close: decide for the next candle.
        var decision = await DecideAsync(candle, contiguous, equity, notes, cancellationToken).ConfigureAwait(false);

        _state = _state with
        {
            Risk = _risk.Snapshot(),
            OrderSequence = _execution.Sequence,
            LastCandleOpenTimeUtc = candle.OpenTimeUtc,
            CandlesProcessed = _state.CandlesProcessed + 1,
        };

        await _store.SaveStepAsync(
            new PaperStep(_session.Id, candle.OpenTimeUtc, Serialize(), touched.DistinctBy(o => o.Id).ToList(), trades, decision),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenPositionAsync(PendingEntry entry, Candle candle, List<Order> touched, List<string> notes, CancellationToken cancellationToken)
    {
        if (entry.StopLoss >= candle.Open)
        {
            notes.Add(Text($"STOP_NOT_BELOW_ENTRY: stop {entry.StopLoss} is not below the open {candle.Open}."));
            return;
        }

        var fill = CandleFillModel.MarketBuyPrice(candle.Open, _config.Costs);
        if (entry.TakeProfit is { } target && target <= fill)
        {
            notes.Add(Text($"TAKE_PROFIT_NOT_ABOVE_ENTRY: take profit {target} is not above the entry {fill}."));
            return;
        }

        var sizing = _risk.Size(new PortfolioSnapshot(_state.Cash, _state.Cash, 0, 0m), fill, entry.StopLoss, _config.Costs.FeeRate, _config.Filters);
        if (!sizing.Approved)
        {
            notes.Add($"{sizing.Code}: {sizing.Detail}");
            return;
        }

        var result = await _execution.PlaceMarketOrderAsync(
            new MarketOrderRequest(OrderSide.Buy, sizing.Quantity, candle.Open, candle.OpenTimeUtc, new Bracket(entry.StopLoss, entry.TakeProfit), "Entry"),
            cancellationToken).ConfigureAwait(false);

        var order = result.Order;
        touched.Add(order);
        touched.AddRange(result.CreatedOrders);
        _state = _state with
        {
            // Same arithmetic order as the backtester (cash − (cost + fee)): decimal rounding must match to the last digit.
            Cash = _state.Cash - ((order.FilledQuantity * order.AverageFillPrice!.Value) + order.Fee),
            Position = new PaperPosition(candle.OpenTimeUtc, order.AverageFillPrice.Value, order.FilledQuantity, order.Fee, entry.StopLoss, entry.TakeProfit, 1, entry.Explanation),
        };
        notes.Add(Text($"ENTRY: bought {order.FilledQuantity} at {order.AverageFillPrice} ({sizing.Detail})"));
    }

    private async Task ClosePositionAsync(
        PaperPosition position, Candle candle, decimal referencePrice, ExitReason reason, List<Order> touched, List<PaperTrade> trades, List<string> notes,
        CancellationToken cancellationToken)
    {
        foreach (var protective in _execution.WorkingOrders.ToList())
        {
            touched.Add(await _execution.CancelOrderAsync(protective.Id, candle.OpenTimeUtc, $"Position closed: {reason}.", cancellationToken).ConfigureAwait(false));
        }

        var result = await _execution.PlaceMarketOrderAsync(
            new MarketOrderRequest(OrderSide.Sell, position.Quantity, referencePrice, candle.OpenTimeUtc, Note: reason.ToString()), cancellationToken).ConfigureAwait(false);
        var order = result.Order;
        touched.Add(order);

        trades.Add(Trade(position, candle, order.AverageFillPrice!.Value, order.Fee, reason));
        notes.Add(Text($"{reason}: sold {order.FilledQuantity} at {order.AverageFillPrice}."));
        _state = _state with { Cash = _state.Cash + ((order.FilledQuantity * order.AverageFillPrice.Value) - order.Fee), Position = null };
        _risk.OnTradeClosed(trades[^1].NetPnl);
    }

    private async Task<PaperDecision> DecideAsync(Candle candle, bool contiguous, decimal equity, List<string> notes, CancellationToken cancellationToken)
    {
        var window = await _candles.GetRangeAsync(
            _config.Symbol, _config.Interval, candle.OpenTimeUtc - (_length * (_featureEngine.FeatureSet.MaxLookback - 1)), candle.OpenTimeUtc + _length, cancellationToken).ConfigureAwait(false);
        var features = _featureEngine.Compute(window);
        var position = _state.Position is { } p ? new PositionView(p.EntryCandleOpenTimeUtc, p.EntryPrice, p.Quantity, p.StopLoss, p.TakeProfit) : null;

        var signal = features.IsSuccess && features.Value.IsComplete
            ? _strategy.Evaluate(new StrategyContext(candle, features.Value, position))
            : Signal.NoTrade(NoTradeReason.FeaturesUnavailable);

        var fresh = _time.GetUtcNow() - candle.CloseTimeUtc <= _config.FreshnessGrace;
        string action;
        RiskDecision? check = null;

        switch (signal.Direction)
        {
            case SignalDirection.Long when _state.Position is null:
                check = _risk.CheckEntry(
                    new PortfolioSnapshot(_state.Cash, _state.Cash, 0, 0m),
                    new MarketConditions(_config.Costs.SpreadBps, _config.Costs.SlippageBps, MarketDataReliable: contiguous && fresh, ExecutionAvailable: true));
                if (check.Approved)
                {
                    _state = _state with { PendingEntry = new PendingEntry(candle.CloseTimeUtc, signal.StopLossPrice!.Value, signal.TakeProfitPrice, signal.Explanation) };
                    action = "ENTRY_SCHEDULED";
                }
                else
                {
                    action = "RISK_REJECTED";
                }

                break;
            case SignalDirection.Short when _state.Position is not null:
                _state = _state with { PendingExit = true };
                action = "EXIT_SCHEDULED";
                break;
            case SignalDirection.Short:
                action = "IGNORED";
                notes.Add("SHORT_NOT_SUPPORTED_ON_SPOT: no position to close.");
                break;
            case SignalDirection.NoTrade:
                action = "NO_TRADE";
                break;
            default:
                action = "HOLD";
                break;
        }

        if (!fresh)
        {
            notes.Add(Text($"STALE: processed {_time.GetUtcNow() - candle.CloseTimeUtc:g} after its close; no new entries from it."));
        }

        return new PaperDecision(
            candle.OpenTimeUtc, signal.Direction, signal.Reason, signal.Explanation, signal.Metrics, action,
            check is { Approved: false } ? check.Code : null, check is { Approved: false } ? check.Detail : null, equity, notes);
    }

    private static PaperTrade Trade(PaperPosition position, Candle candle, decimal exitPrice, decimal exitFee, ExitReason reason)
    {
        var proceeds = position.Quantity * exitPrice;
        return new PaperTrade(
            position.EntryCandleOpenTimeUtc, position.EntryPrice, position.Quantity, position.EntryFee, position.StopLoss, position.TakeProfit,
            candle.OpenTimeUtc, exitPrice, exitFee, reason, position.HoldingCandles,
            proceeds - exitFee - ((position.Quantity * position.EntryPrice) + position.EntryFee), position.Explanation);
    }

    private string Serialize() => JsonSerializer.Serialize(_state, Json);

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static void LogCreated(ILogger logger, string session, string strategy, DateTimeOffset? cursor) =>
        logger.LogInformation("Paper session {Session} created with {Strategy}; first candle after {Cursor}.", session, strategy, cursor);

    private static void LogResumed(ILogger logger, string session, DateTimeOffset? cursor) =>
        logger.LogInformation("Paper session {Session} resumed after candle {Cursor}.", session, cursor);

    private static void LogCommand(ILogger logger, PaperCommandType type, string by, string result) =>
        logger.LogWarning("Paper command {Command} by {By}: {Result}", type, by, result);
}

/// <summary>What an operator needs to see about a paper session.</summary>
public sealed record PaperSessionSummary(
    string Name,
    string Symbol,
    string Interval,
    string Strategy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastCandleOpenTimeUtc,
    int CandlesProcessed,
    decimal Cash,
    decimal LastEquity,
    decimal PeakEquity,
    decimal Drawdown,
    decimal DayStartEquity,
    int ConsecutiveLosses,
    PaperPositionView? Position,
    bool EntryPending,
    bool ExitPending,
    PaperKillSwitchView KillSwitch);

public sealed record PaperPositionView(DateTimeOffset EntryCandleOpenTimeUtc, decimal EntryPrice, decimal Quantity, decimal StopLoss, decimal? TakeProfit, int HoldingCandles);

public sealed record PaperKillSwitchView(bool Active, string? Reason, DateTimeOffset? TrippedAtUtc, string? ResetBy);

public static class PaperSessions
{
    /// <summary>Reads a stored session for display (the state JSON is owned by the engine).</summary>
    public static PaperSessionSummary Describe(PaperSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var state = JsonSerializer.Deserialize<PaperState>(record.StateJson, PaperTradingEngine.Json)!;
        using var config = JsonDocument.Parse(record.ConfigJson);
        var strategy = config.RootElement.TryGetProperty("strategy", out var s) && s.TryGetProperty("name", out var n) ? n.GetString()! : "?";
        var risk = state.Risk;

        return new PaperSessionSummary(
            record.Name, record.Symbol, record.Interval, strategy, record.CreatedAtUtc, record.UpdatedAtUtc, record.LastCandleOpenTimeUtc, state.CandlesProcessed,
            state.Cash, risk.LastEquity, risk.PeakEquity, risk.PeakEquity > 0 ? (risk.LastEquity / risk.PeakEquity) - 1m : 0m, risk.DayStartEquity, risk.ConsecutiveLosses,
            state.Position is { } p ? new PaperPositionView(p.EntryCandleOpenTimeUtc, p.EntryPrice, p.Quantity, p.StopLoss, p.TakeProfit, p.HoldingCandles) : null,
            state.PendingEntry is not null, state.PendingExit,
            new PaperKillSwitchView(risk.KillSwitchActive, risk.KillSwitchReason, risk.KillSwitchTrippedAtUtc, risk.KillSwitchResetBy));
    }
}

/// <summary>Evolving state of a paper session (persisted as JSON after every candle and command).</summary>
internal sealed record PaperState(
    decimal Cash,
    PaperPosition? Position,
    PendingEntry? PendingEntry,
    bool PendingExit,
    RiskState Risk,
    long OrderSequence,
    DateTimeOffset? LastCandleOpenTimeUtc,
    int CandlesProcessed);

internal sealed record PaperPosition(
    DateTimeOffset EntryCandleOpenTimeUtc, decimal EntryPrice, decimal Quantity, decimal EntryFee, decimal StopLoss, decimal? TakeProfit, int HoldingCandles, string? Explanation);

internal sealed record PendingEntry(DateTimeOffset SignalTimeUtc, decimal StopLoss, decimal? TakeProfit, string? Explanation);
