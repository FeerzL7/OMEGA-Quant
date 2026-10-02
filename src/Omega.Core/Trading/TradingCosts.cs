namespace Omega.Core.Trading;

/// <summary>
/// Execution costs assumed for decisions and simulations, as fractions of notional (fee) and basis points.
/// The same values must be used to decide (expected value) and to simulate (backtest), or the decision is judged
/// under different costs than it was made with.
/// </summary>
public sealed record TradingCosts(decimal FeeRate, decimal SpreadBps, decimal SlippageBps)
{
    /// <summary>Cost of one market order: commission + half the spread + slippage.</summary>
    public double MarketOrder => (double)(FeeRate + (SpreadBps / 2m / 10_000m) + (SlippageBps / 10_000m));

    /// <summary>Cost of one limit order filled at its price: commission only.</summary>
    public double LimitOrder => (double)FeeRate;
}
