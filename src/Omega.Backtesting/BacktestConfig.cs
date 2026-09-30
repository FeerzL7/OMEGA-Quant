namespace Omega.Backtesting;

/// <summary>
/// Simulation assumptions. Every value is a research parameter that must be recorded with the result;
/// none is a universal truth (CLAUDE.md §20, §22).
/// </summary>
public sealed record BacktestConfig
{
    /// <summary>Starting capital in quote currency (USDT).</summary>
    public decimal InitialCapital { get; init; } = 10_000m;

    /// <summary>
    /// Commission per side as a fraction of notional. Default 0.001 (0.10 %): Binance Spot regular (VIP 0)
    /// tier as of 2026. The BNB discount is not modelled (conservative). Check your own tier.
    /// </summary>
    public decimal FeeRate { get; init; } = 0.001m;

    /// <summary>Full bid-ask spread in basis points; market orders pay half of it.</summary>
    public decimal SpreadBps { get; init; } = 1m;

    /// <summary>Adverse price movement on market orders (entries, stop losses, signal/time exits), in basis points.</summary>
    public decimal SlippageBps { get; init; } = 2m;

    /// <summary>Fraction of equity risked per trade: loss if the stop loss is hit, before costs (§18).</summary>
    public decimal RiskPerTrade { get; init; } = 0.01m;

    /// <summary>Maximum position value as a fraction of equity. 1 = no leverage (Spot).</summary>
    public decimal MaxPositionFraction { get; init; } = 1m;

    /// <summary>Quantity step (lot size) to round down to, if set. Verify with the exchange's exchangeInfo.</summary>
    public decimal? QuantityStep { get; init; }

    /// <summary>Minimum order value in quote currency, if set. Verify with the exchange's exchangeInfo.</summary>
    public decimal? MinNotional { get; init; }

    /// <summary>Close a position at the close of this many candles after entry (triple-barrier timeout), if set.</summary>
    public int? MaxHoldingCandles { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (InitialCapital <= 0) errors.Add("InitialCapital must be positive.");
        if (FeeRate is < 0 or >= 0.1m) errors.Add("FeeRate must be in [0, 0.1).");
        if (SpreadBps is < 0 or > 1000) errors.Add("SpreadBps must be in [0, 1000].");
        if (SlippageBps is < 0 or > 1000) errors.Add("SlippageBps must be in [0, 1000].");
        if (RiskPerTrade is <= 0 or > 1) errors.Add("RiskPerTrade must be in (0, 1].");
        if (MaxPositionFraction is <= 0 or > 1) errors.Add("MaxPositionFraction must be in (0, 1] (Spot: no leverage).");
        if (QuantityStep is <= 0) errors.Add("QuantityStep must be positive when set.");
        if (MinNotional is < 0) errors.Add("MinNotional cannot be negative.");
        if (MaxHoldingCandles is < 1) errors.Add("MaxHoldingCandles must be at least 1 when set.");

        return errors;
    }

    internal decimal HalfSpread => SpreadBps / 2m / 10_000m;

    internal decimal Slippage => SlippageBps / 10_000m;

    /// <summary>Price paid by a market buy whose reference price is <paramref name="price"/>.</summary>
    internal decimal MarketBuyPrice(decimal price) => price * (1m + HalfSpread + Slippage);

    /// <summary>Price received by a market sell whose reference price is <paramref name="price"/>.</summary>
    internal decimal MarketSellPrice(decimal price) => price * (1m - HalfSpread - Slippage);
}
