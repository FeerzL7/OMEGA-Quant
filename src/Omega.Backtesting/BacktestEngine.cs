using System.Globalization;
using Omega.Core.MarketData;
using Omega.Core.Results;
using Omega.Core.Trading;
using Omega.Features;
using Omega.Strategy;

namespace Omega.Backtesting;

public static class BacktestErrors
{
    public const string InvalidConfig = "BACKTEST_INVALID_CONFIG";
    public const string NoCandles = "BACKTEST_NO_CANDLES";
}

/// <summary>
/// Replays closed candles through a strategy and simulates Spot execution (long only).
/// </summary>
/// <remarks>
/// Order of events for every candle i (this order is what prevents look-ahead):
/// <list type="number">
/// <item>At the open of i: execute what was decided at the close of i−1 (signal exit, then entry). Market orders
/// fill at the open with half the spread and the slippage against us.</item>
/// <item>During i: stop loss / take profit of the open position, from the candle's open, high and low. If the open
/// is already beyond the stop, the fill is the open (gap). If both levels are inside the range, the stop loss is
/// assumed to come first (conservative). The take profit fills exactly at its price (limit order).</item>
/// <item>At the close of i: time limit and end-of-data exits at the close; equity is marked at the close.</item>
/// <item>At the close of i: the strategy sees candle i and its features (available only now) and decides for i+1.</item>
/// </list>
/// A signal pending across a data gap is discarded (it would be stale). Short signals close a long and never open
/// a short (Spot).
/// </remarks>
public sealed class BacktestEngine(FeatureEngine featureEngine, BacktestConfig config, IPositionSizer? sizer = null)
{
    private readonly IPositionSizer _sizer = sizer ?? new FixedFractionalSizer();

    public Result<BacktestResult> Run(IReadOnlyList<Candle> candles, IStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(strategy);

        var configErrors = config.Validate();
        if (configErrors.Count > 0)
        {
            return Fail(BacktestErrors.InvalidConfig, string.Join(" ", configErrors));
        }

        if (candles.Count == 0)
        {
            return Fail(BacktestErrors.NoCandles, "No candles to replay.");
        }

        var series = featureEngine.ComputeSeries(candles);
        if (series.IsFailure)
        {
            return Result.Failure<BacktestResult>(series.Error!);
        }

        var run = new Simulation(config, _sizer, strategy, candles, series.Value);
        run.Execute();

        var intervalSeconds = candles[0].Interval.ToTimeSpan().TotalSeconds;
        var metrics = BacktestMetrics.From(
            config.InitialCapital, run.Equity, run.Trades, run.CandlesInPosition, 365 * 24 * 3600 / intervalSeconds);

        return Result.Success(new BacktestResult(
            strategy.Identity,
            featureEngine.FeatureSet.Version,
            featureEngine.FeatureSet.Hash,
            config,
            DatasetFingerprint.From(candles),
            run.Trades,
            run.Equity,
            run.Rejections,
            run.NoTradeCounts,
            run.Warnings,
            metrics));
    }

    private static Result<BacktestResult> Fail(string code, string message) =>
        Result.Failure<BacktestResult>(new Error(code, message));

    private sealed class Simulation(
        BacktestConfig config, IPositionSizer sizer, IStrategy strategy, IReadOnlyList<Candle> candles, IReadOnlyList<FeatureVector> features)
    {
        private decimal _cash = config.InitialCapital;
        private decimal _peak = config.InitialCapital;
        private OpenPosition? _position;
        private PendingEntry? _pendingEntry;
        private bool _pendingExit;

        public List<BacktestTrade> Trades { get; } = [];

        public List<EquityPoint> Equity { get; } = [];

        public List<BacktestRejection> Rejections { get; } = [];

        public Dictionary<NoTradeReason, int> NoTradeCounts { get; } = [];

        public List<string> Warnings { get; } = [];

        public int CandlesInPosition { get; private set; }

        public void Execute()
        {
            var last = candles.Count - 1;

            for (var i = 0; i <= last; i++)
            {
                var candle = candles[i];

                // 1. Open of candle i: act on what was decided at the close of i-1.
                if (i > 0 && candle.OpenTimeUtc - candles[i - 1].OpenTimeUtc != candle.Interval.ToTimeSpan())
                {
                    Warnings.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Data gap before {candle.OpenTimeUtc:O}; features restart their warm-up."));

                    if (_pendingEntry is { } stale)
                    {
                        Reject(candle.OpenTimeUtc, RejectionCodes.SignalExpiredByDataGap, $"Entry signal from {stale.SignalTimeUtc:O} expired.");
                        _pendingEntry = null;
                    }
                }

                if (_pendingExit && _position is not null)
                {
                    Close(candle, i, config.MarketSellPrice(candle.Open), ExitReason.Signal);
                }

                _pendingExit = false;

                if (_pendingEntry is { } entry && _position is null)
                {
                    TryOpen(candle, i, entry);
                }

                _pendingEntry = null;

                // Exposed for this candle if a position is open once the open has been processed,
                // even if it exits later in the candle.
                if (_position is not null)
                {
                    CandlesInPosition++;
                }

                // 2. During candle i: stop loss / take profit.
                if (_position is { } position)
                {
                    CheckStops(candle, i, position);
                }

                // 3. Close of candle i: time limit, end of data, equity.
                if (_position is { } held && config.MaxHoldingCandles is { } maxHolding && i - held.EntryIndex + 1 >= maxHolding)
                {
                    Close(candle, i, config.MarketSellPrice(candle.Close), ExitReason.TimeLimit);
                }

                if (_position is not null && i == last)
                {
                    Close(candle, i, config.MarketSellPrice(candle.Close), ExitReason.EndOfData);
                }

                MarkEquity(candle);

                // 4. Close of candle i: decide for candle i+1 (nothing can be executed after the last candle).
                if (i < last)
                {
                    Decide(candle, features[i]);
                }
            }
        }

        private void Decide(Candle candle, FeatureVector vector)
        {
            var signal = vector.IsComplete
                ? strategy.Evaluate(new StrategyContext(candle, vector, _position?.View))
                : Signal.NoTrade(NoTradeReason.FeaturesUnavailable);

            switch (signal.Direction)
            {
                case SignalDirection.Long when _position is null:
                    _pendingEntry = new PendingEntry(candle.CloseTimeUtc, signal.StopLossPrice!.Value, signal.TakeProfitPrice, signal.Explanation);
                    break;

                case SignalDirection.Short when _position is not null:
                    _pendingExit = true;
                    break;

                case SignalDirection.Short:
                    Reject(candle.CloseTimeUtc, RejectionCodes.ShortNotSupportedOnSpot, "Short positions are not possible on Spot; signal ignored.");
                    break;

                case SignalDirection.NoTrade:
                    NoTradeCounts[signal.Reason!.Value] = NoTradeCounts.GetValueOrDefault(signal.Reason.Value) + 1;
                    break;
            }
        }

        private void TryOpen(Candle candle, int index, PendingEntry entry)
        {
            if (entry.StopLoss >= candle.Open)
            {
                Reject(candle.OpenTimeUtc, RejectionCodes.StopNotBelowEntry,
                    string.Create(CultureInfo.InvariantCulture, $"Stop {entry.StopLoss} is not below the open {candle.Open}."));
                return;
            }

            var fill = config.MarketBuyPrice(candle.Open);
            if (entry.TakeProfit is { } takeProfit && takeProfit <= fill)
            {
                Reject(candle.OpenTimeUtc, RejectionCodes.TakeProfitNotAboveEntry,
                    string.Create(CultureInfo.InvariantCulture, $"Take profit {takeProfit} is not above the entry {fill}."));
                return;
            }

            var quantity = sizer.Size(_cash, fill, entry.StopLoss, config);
            var cost = quantity * fill;
            if (quantity <= 0 || (config.MinNotional is { } minNotional && cost < minNotional))
            {
                Reject(candle.OpenTimeUtc, RejectionCodes.PositionTooSmall,
                    string.Create(CultureInfo.InvariantCulture, $"Quantity {quantity} (value {cost}) is below the exchange minimum or zero."));
                return;
            }

            var fee = cost * config.FeeRate;
            _cash -= cost + fee;
            _position = new OpenPosition(index, candle.OpenTimeUtc, fill, quantity, fee, entry.StopLoss, entry.TakeProfit, entry.Explanation);
        }

        private void CheckStops(Candle candle, int index, OpenPosition position)
        {
            if (candle.Open <= position.StopLoss)
            {
                Close(candle, index, config.MarketSellPrice(candle.Open), ExitReason.StopLoss); // gap through the stop
            }
            else if (position.TakeProfit is { } gapTarget && candle.Open >= gapTarget)
            {
                Close(candle, index, gapTarget, ExitReason.TakeProfit); // conservative: no credit for the gap
            }
            else if (candle.Low <= position.StopLoss)
            {
                Close(candle, index, config.MarketSellPrice(position.StopLoss), ExitReason.StopLoss); // first if both touched
            }
            else if (position.TakeProfit is { } target && candle.High >= target)
            {
                Close(candle, index, target, ExitReason.TakeProfit);
            }
        }

        private void Close(Candle candle, int index, decimal fill, ExitReason reason)
        {
            var position = _position!;
            var proceeds = position.Quantity * fill;
            var fee = proceeds * config.FeeRate;
            _cash += proceeds - fee;

            Trades.Add(new BacktestTrade(
                position.EntryCandleOpenTimeUtc, position.EntryPrice, position.Quantity, position.EntryFee,
                position.StopLoss, position.TakeProfit, candle.OpenTimeUtc, fill, fee, reason,
                index - position.EntryIndex + 1,
                proceeds - fee - ((position.Quantity * position.EntryPrice) + position.EntryFee),
                position.Explanation));

            _position = null;
        }

        private void MarkEquity(Candle candle)
        {
            var equity = _cash + (_position is { } p ? p.Quantity * candle.Close : 0m);
            _peak = Math.Max(_peak, equity);
            Equity.Add(new EquityPoint(candle.CloseTimeUtc, equity, (equity / _peak) - 1m));
        }

        private void Reject(DateTimeOffset time, string code, string detail) => Rejections.Add(new BacktestRejection(time, code, detail));
    }

    private sealed record PendingEntry(DateTimeOffset SignalTimeUtc, decimal StopLoss, decimal? TakeProfit, string? Explanation);

    private sealed record OpenPosition(
        int EntryIndex, DateTimeOffset EntryCandleOpenTimeUtc, decimal EntryPrice, decimal Quantity, decimal EntryFee,
        decimal StopLoss, decimal? TakeProfit, string? Explanation)
    {
        public PositionView View => new(EntryCandleOpenTimeUtc, EntryPrice, Quantity, StopLoss, TakeProfit);
    }
}
