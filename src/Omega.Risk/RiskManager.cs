using System.Globalization;
using Omega.Core.Trading;

namespace Omega.Risk;

/// <summary>Everything the Risk Engine remembers, so it survives a restart (paper and live trading).</summary>
public sealed record RiskState(
    decimal PeakEquity,
    decimal DayStartEquity,
    decimal LastEquity,
    DateOnly Day,
    bool DailyLossBlocked,
    string? DailyLossTrigger,
    bool StreakBlocked,
    int ConsecutiveLosses,
    bool KillSwitchActive,
    string? KillSwitchReason,
    DateTimeOffset? KillSwitchTrippedAtUtc,
    string? KillSwitchResetBy);

/// <summary>
/// Risk Engine (CLAUDE.md §18): independent of any model, it can reject any entry and decides the size of every
/// position. It keeps the state its limits need (peak equity, start-of-day equity, losing streak, kill switch),
/// fed by <see cref="OnEquity"/> and <see cref="OnTradeClosed"/>. Exits are never blocked: reducing risk is always
/// allowed.
/// </summary>
public sealed class RiskManager
{
    private decimal _lastEquity;
    private DateOnly _day;
    private bool _dailyLossBlocked;
    private string? _dailyLossTrigger;
    private bool _streakBlocked;

    public RiskManager(RiskLimits limits, decimal initialEquity, DateTimeOffset startUtc)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var errors = limits.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(limits));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialEquity, 0);

        Limits = limits;
        PeakEquity = DayStartEquity = _lastEquity = initialEquity;
        _day = DateOnly.FromDateTime(startUtc.UtcDateTime);
    }

    public RiskLimits Limits { get; }

    public KillSwitch KillSwitch { get; } = new();

    public decimal PeakEquity { get; private set; }

    public decimal DayStartEquity { get; private set; }

    public int ConsecutiveLosses { get; private set; }

    public decimal Drawdown => _lastEquity / PeakEquity - 1m;

    public RiskState Snapshot() => new(
        PeakEquity, DayStartEquity, _lastEquity, _day, _dailyLossBlocked, _dailyLossTrigger, _streakBlocked, ConsecutiveLosses,
        KillSwitch.IsActive, KillSwitch.Reason, KillSwitch.TrippedAtUtc, KillSwitch.ResetBy);

    /// <summary>Rebuilds the Risk Engine exactly as it was (limits are the current policy; the state is restored).</summary>
    public static RiskManager Restore(RiskLimits limits, RiskState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var manager = new RiskManager(limits, state.LastEquity > 0 ? state.LastEquity : 1m, DateTimeOffset.UnixEpoch)
        {
            PeakEquity = state.PeakEquity,
            DayStartEquity = state.DayStartEquity,
            ConsecutiveLosses = state.ConsecutiveLosses,
        };
        manager._lastEquity = state.LastEquity;
        manager._day = state.Day;
        manager._dailyLossBlocked = state.DailyLossBlocked;
        manager._dailyLossTrigger = state.DailyLossTrigger;
        manager._streakBlocked = state.StreakBlocked;
        manager.KillSwitch.Restore(state.KillSwitchActive, state.KillSwitchReason, state.KillSwitchTrippedAtUtc, state.KillSwitchResetBy);
        return manager;
    }

    /// <summary>Marks equity at <paramref name="timeUtc"/> (mark-to-market). Rolls the UTC day and trips the kill switch on maximum drawdown.</summary>
    public void OnEquity(DateTimeOffset timeUtc, decimal equity)
    {
        var day = DateOnly.FromDateTime(timeUtc.UtcDateTime);
        if (day != _day)
        {
            // New UTC day: the day starts from the last equity of the previous one; day-scoped blocks expire.
            _day = day;
            DayStartEquity = _lastEquity;
            _dailyLossBlocked = false;
            _dailyLossTrigger = null;
            _streakBlocked = false;
            ConsecutiveLosses = 0;
        }

        _lastEquity = equity;
        PeakEquity = Math.Max(PeakEquity, equity);

        if (!_dailyLossBlocked && equity <= DayStartEquity * (1m - Limits.MaxDailyLoss))
        {
            _dailyLossBlocked = true;
            _dailyLossTrigger = Text($"at {timeUtc:O} equity was {equity:0.##}");
        }

        if (equity <= PeakEquity * (1m - Limits.MaxDrawdown))
        {
            KillSwitch.Trip(string.Create(CultureInfo.InvariantCulture,
                $"Maximum drawdown reached: equity {equity:0.##} is {Drawdown:P2} from the peak {PeakEquity:0.##} (limit {Limits.MaxDrawdown:P2})."), timeUtc);
        }
    }

    /// <summary>Records a closed trade's net result (after costs) for the losing-streak control.</summary>
    public void OnTradeClosed(decimal netPnl)
    {
        ConsecutiveLosses = netPnl < 0 ? ConsecutiveLosses + 1 : 0;
        if (Limits.MaxConsecutiveLosses is { } max && ConsecutiveLosses >= max)
        {
            _streakBlocked = true;
        }
    }

    /// <summary>Whether a new entry is allowed now. Checks run in a fixed order and the first failure is reported.</summary>
    public RiskDecision CheckEntry(PortfolioSnapshot portfolio, MarketConditions market)
    {
        ArgumentNullException.ThrowIfNull(portfolio);
        ArgumentNullException.ThrowIfNull(market);

        if (KillSwitch.IsActive)
        {
            return RiskDecision.Reject(RiskCheck.KillSwitch, $"Kill switch active since {KillSwitch.TrippedAtUtc:O}: {KillSwitch.Reason}");
        }

        if (!_dailyLossBlocked && portfolio.Equity <= DayStartEquity * (1m - Limits.MaxDailyLoss))
        {
            _dailyLossBlocked = true;
            _dailyLossTrigger = Text($"equity was {portfolio.Equity:0.##} when this entry was checked");
        }

        if (_dailyLossBlocked)
        {
            // Mark-to-market: the limit may have been reached while a position was open and the equity may have
            // recovered since; the detail names the value that triggered it, not only the current one.
            return RiskDecision.Reject(RiskCheck.DailyLossLimit, Text(
                $"Daily loss limit ({Limits.MaxDailyLoss:P2} of {DayStartEquity:0.##} at the start of the UTC day) reached: {_dailyLossTrigger}. Equity now {portfolio.Equity:0.##}. Entries resume tomorrow."));
        }

        if (_streakBlocked)
        {
            return RiskDecision.Reject(RiskCheck.ConsecutiveLosses, Text(
                $"{ConsecutiveLosses} consecutive losing trades (limit {Limits.MaxConsecutiveLosses}). Entries resume tomorrow."));
        }

        if (portfolio.OpenPositions >= Limits.MaxOpenPositions)
        {
            return RiskDecision.Reject(RiskCheck.MaxOpenPositions, Text($"{portfolio.OpenPositions} open position(s) (limit {Limits.MaxOpenPositions})."));
        }

        if (portfolio.Equity <= 0 || portfolio.OpenExposure >= portfolio.Equity * Limits.MaxExposureFraction)
        {
            return RiskDecision.Reject(RiskCheck.MaxExposure, Text(
                $"Exposure {portfolio.OpenExposure:0.##} already at the limit ({Limits.MaxExposureFraction:P0} of equity {portfolio.Equity:0.##})."));
        }

        if (market.SpreadBps > Limits.MaxSpreadBps)
        {
            return RiskDecision.Reject(RiskCheck.ExcessiveSpread, Text($"Spread {market.SpreadBps} bps above the limit {Limits.MaxSpreadBps} bps."));
        }

        if (market.ExpectedSlippageBps > Limits.MaxSlippageBps)
        {
            return RiskDecision.Reject(RiskCheck.ExcessiveSlippage, Text($"Expected slippage {market.ExpectedSlippageBps} bps above the limit {Limits.MaxSlippageBps} bps."));
        }

        if (!market.MarketDataReliable)
        {
            return RiskDecision.Reject(RiskCheck.MarketDataUnreliable, "Market data cannot be trusted (gap or stale data).");
        }

        if (!market.ExecutionAvailable)
        {
            return RiskDecision.Reject(RiskCheck.ExecutionUnavailable, "Execution is not available.");
        }

        return RiskDecision.Approve("All entry checks passed.");
    }

    /// <summary>
    /// Quantity for a long entry: the smallest of the risk budget (equity × RiskPerTrade / (entry − stop)), the
    /// single-position cap, the remaining exposure budget and the cash available including the entry fee; then
    /// rounded down to the quantity step and checked against the instrument minimums.
    /// </summary>
    public RiskDecision Size(PortfolioSnapshot portfolio, decimal entryPrice, decimal stopLossPrice, decimal feeRate, InstrumentFilters filters)
    {
        ArgumentNullException.ThrowIfNull(portfolio);
        ArgumentNullException.ThrowIfNull(filters);

        var stopDistance = entryPrice - stopLossPrice;
        if (entryPrice <= 0 || stopLossPrice <= 0 || stopDistance <= 0)
        {
            return RiskDecision.Reject(RiskCheck.InvalidStop, Text($"Stop {stopLossPrice} must be positive and below the entry {entryPrice}."));
        }

        var unitCost = entryPrice * (1m + feeRate);
        var byRisk = portfolio.Equity * Limits.RiskPerTrade / stopDistance;
        var byPosition = portfolio.Equity * Limits.MaxPositionFraction / unitCost;
        var byExposure = Math.Max(0m, (portfolio.Equity * Limits.MaxExposureFraction) - portfolio.OpenExposure) / unitCost;
        var byCash = Math.Max(0m, portfolio.Cash) / unitCost;
        var quantity = Math.Min(Math.Min(byRisk, byPosition), Math.Min(byExposure, byCash));

        if (filters.QuantityStep is { } step)
        {
            quantity = Math.Floor(quantity / step) * step;
        }

        var notional = quantity * entryPrice;
        if (quantity <= 0 || (filters.MinQuantity is { } minQuantity && quantity < minQuantity) || (filters.MinNotional is { } minNotional && notional < minNotional))
        {
            return RiskDecision.Reject(RiskCheck.PositionTooSmall, Text($"Quantity {quantity} (value {notional:0.####}) is zero or below the instrument minimum."));
        }

        return RiskDecision.Approve(Text($"Risk {quantity * stopDistance:0.##} ({quantity * stopDistance / portfolio.Equity:P2} of equity), value {notional:0.##}."), quantity);
    }

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
