namespace Omega.Risk;

/// <summary>
/// Risk policy (CLAUDE.md §18). Every value is a decision of the project owner; the defaults are conventional,
/// conservative starting points, not optimized values. Fractions are of equity unless stated otherwise.
/// </summary>
public sealed record RiskLimits
{
    public const string SectionName = "Risk";

    /// <summary>Equity lost if the stop loss is hit, before costs. Sizing: equity × RiskPerTrade / (entry − stop).</summary>
    public decimal RiskPerTrade { get; init; } = 0.01m;

    /// <summary>Largest single position value. 1 = no leverage (Spot).</summary>
    public decimal MaxPositionFraction { get; init; } = 1m;

    /// <summary>Maximum simultaneous open positions.</summary>
    public int MaxOpenPositions { get; init; } = 1;

    /// <summary>Maximum total value of open positions, including the new one.</summary>
    public decimal MaxExposureFraction { get; init; } = 1m;

    /// <summary>Loss from the start-of-day (UTC) equity that blocks new entries for the rest of the day.</summary>
    public decimal MaxDailyLoss { get; init; } = 0.03m;

    /// <summary>Drawdown from peak equity that trips the kill switch (only a manual reset re-enables trading).</summary>
    public decimal MaxDrawdown { get; init; } = 0.20m;

    /// <summary>Consecutive losing trades that block new entries until the next UTC day. Null disables the check.</summary>
    public int? MaxConsecutiveLosses { get; init; } = 6;

    /// <summary>Widest acceptable bid-ask spread for a new entry, in basis points.</summary>
    public decimal MaxSpreadBps { get; init; } = 10m;

    /// <summary>Largest acceptable expected slippage for a new entry, in basis points.</summary>
    public decimal MaxSlippageBps { get; init; } = 25m;

    public static RiskLimits Default { get; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (RiskPerTrade is <= 0 or > 1) errors.Add("RiskPerTrade must be in (0, 1].");
        if (MaxPositionFraction is <= 0 or > 1) errors.Add("MaxPositionFraction must be in (0, 1] (Spot: no leverage).");
        if (MaxOpenPositions < 1) errors.Add("MaxOpenPositions must be at least 1.");
        if (MaxExposureFraction is <= 0 or > 1) errors.Add("MaxExposureFraction must be in (0, 1] (Spot: no leverage).");
        if (MaxDailyLoss is <= 0 or >= 1) errors.Add("MaxDailyLoss must be in (0, 1).");
        if (MaxDrawdown is <= 0 or >= 1) errors.Add("MaxDrawdown must be in (0, 1).");
        if (MaxConsecutiveLosses is < 1) errors.Add("MaxConsecutiveLosses must be at least 1 when set.");
        if (MaxSpreadBps < 0) errors.Add("MaxSpreadBps cannot be negative.");
        if (MaxSlippageBps < 0) errors.Add("MaxSlippageBps cannot be negative.");
        return errors;
    }
}
