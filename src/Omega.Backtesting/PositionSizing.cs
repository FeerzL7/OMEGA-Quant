namespace Omega.Backtesting;

/// <summary>
/// Decides the quantity of a new position. Phase 5 placeholder for the Risk Engine (Phase 10), which will
/// own sizing and exposure limits.
/// </summary>
public interface IPositionSizer
{
    /// <returns>Quantity to buy (base asset); 0 or less means "do not open".</returns>
    decimal Size(decimal equity, decimal entryPrice, decimal stopLossPrice, BacktestConfig config);
}

/// <summary>
/// Fixed-fractional sizing (CLAUDE.md §18): quantity = equity × RiskPerTrade / (entry − stop), capped so the
/// position plus its entry fee never exceeds equity × MaxPositionFraction (no leverage), rounded down to the
/// quantity step.
/// </summary>
public sealed class FixedFractionalSizer : IPositionSizer
{
    public decimal Size(decimal equity, decimal entryPrice, decimal stopLossPrice, BacktestConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var stopDistance = entryPrice - stopLossPrice;
        if (stopDistance <= 0 || entryPrice <= 0 || equity <= 0)
        {
            return 0;
        }

        var byRisk = equity * config.RiskPerTrade / stopDistance;
        var byCapital = equity * config.MaxPositionFraction / (entryPrice * (1m + config.FeeRate));
        var quantity = Math.Min(byRisk, byCapital);

        return config.QuantityStep is { } step ? Math.Floor(quantity / step) * step : quantity;
    }
}
