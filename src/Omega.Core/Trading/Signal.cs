namespace Omega.Core.Trading;

/// <summary>Why a strategy explicitly decided not to trade (CLAUDE.md §8).</summary>
/// <remarks>Zero is not defined, so an uninitialized reason is rejected.</remarks>
public enum NoTradeReason
{
    ProbabilityTooLow = 1,
    ExpectedValueTooLow = 2,
    RiskLimitReached = 3,
    MarketRegimeBlocked = 4,
    InvalidMarketData = 5,
    ExcessiveSpread = 6,
    ExcessiveSlippage = 7,
    ModelUnavailable = 8,
    ExecutionUnavailable = 9,

    /// <summary>Features are not available yet (warm-up) or could not be computed.</summary>
    FeaturesUnavailable = 10,
}

/// <summary>
/// A strategy decision at the close of a candle.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="SignalDirection.Long"/>: be long; opening a position requires a stop loss (position sizing needs it).</item>
/// <item><see cref="SignalDirection.Short"/>: expect the price to fall. On Spot this closes a long; it never opens a short.</item>
/// <item><see cref="SignalDirection.Hold"/>: no change.</item>
/// <item><see cref="SignalDirection.NoTrade"/>: do not open a trade, always with a <see cref="NoTradeReason"/>.</item>
/// </list>
/// </remarks>
public sealed record Signal
{
    private Signal(SignalDirection direction, NoTradeReason? reason, decimal? stopLossPrice, decimal? takeProfitPrice, string? explanation)
    {
        Direction = direction;
        Reason = reason;
        StopLossPrice = stopLossPrice;
        TakeProfitPrice = takeProfitPrice;
        Explanation = explanation;
    }

    public SignalDirection Direction { get; }

    /// <summary>Set only for <see cref="SignalDirection.NoTrade"/>.</summary>
    public NoTradeReason? Reason { get; }

    /// <summary>Absolute stop-loss price for a new long.</summary>
    public decimal? StopLossPrice { get; }

    /// <summary>Absolute take-profit price for a new long; optional.</summary>
    public decimal? TakeProfitPrice { get; }

    /// <summary>Human-readable reason for the decision (logged and shown in the dashboard).</summary>
    public string? Explanation { get; }

    public static Signal Long(decimal stopLossPrice, decimal? takeProfitPrice = null, string? explanation = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopLossPrice, 0m);
        if (takeProfitPrice is { } takeProfit && takeProfit <= stopLossPrice)
        {
            throw new ArgumentException("The take profit must be above the stop loss.", nameof(takeProfitPrice));
        }

        return new Signal(SignalDirection.Long, null, stopLossPrice, takeProfitPrice, explanation);
    }

    public static Signal Short(string? explanation = null) => new(SignalDirection.Short, null, null, null, explanation);

    public static Signal Hold(string? explanation = null) => new(SignalDirection.Hold, null, null, null, explanation);

    public static Signal NoTrade(NoTradeReason reason, string? explanation = null)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "A NO_TRADE signal needs a defined reason.");
        }

        return new Signal(SignalDirection.NoTrade, reason, null, null, explanation);
    }
}
