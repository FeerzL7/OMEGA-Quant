using Omega.Core.MarketData;
using Omega.Core.Trading;

namespace Omega.Execution;

/// <summary>Why a position was closed.</summary>
public enum ExitReason
{
    StopLoss = 1,
    TakeProfit = 2,
    Signal = 3,
    TimeLimit = 4,
    EndOfData = 5,
    Manual = 6,
}

/// <summary>A protective order that triggered inside a candle.</summary>
public sealed record ProtectiveFill(ExitReason Reason, decimal Price);

/// <summary>
/// Fill rules shared by the backtester and paper trading, so both simulate execution identically (a parity test
/// checks it). Market orders pay half the spread plus slippage; a take profit is a limit order filled at its price.
/// </summary>
public static class CandleFillModel
{
    /// <summary>Price paid by a market buy whose reference price is <paramref name="price"/>.</summary>
    public static decimal MarketBuyPrice(decimal price, TradingCosts costs)
    {
        ArgumentNullException.ThrowIfNull(costs);
        return price * (1m + HalfSpread(costs) + Slippage(costs));
    }

    /// <summary>Price received by a market sell whose reference price is <paramref name="price"/>.</summary>
    public static decimal MarketSellPrice(decimal price, TradingCosts costs)
    {
        ArgumentNullException.ThrowIfNull(costs);
        return price * (1m - HalfSpread(costs) - Slippage(costs));
    }

    /// <summary>
    /// Whether a long's stop loss or take profit triggers in <paramref name="candle"/>, in the conservative order:
    /// a gap through the stop exits at the open (market); a gap through the target exits at the target (no credit
    /// for the gap); if both levels are inside the candle, the stop is assumed first.
    /// </summary>
    public static ProtectiveFill? CheckProtection(Candle candle, decimal stopLoss, decimal? takeProfit, TradingCosts costs)
    {
        ArgumentNullException.ThrowIfNull(candle);

        if (candle.Open <= stopLoss)
        {
            return new ProtectiveFill(ExitReason.StopLoss, MarketSellPrice(candle.Open, costs));
        }

        if (takeProfit is { } gapTarget && candle.Open >= gapTarget)
        {
            return new ProtectiveFill(ExitReason.TakeProfit, gapTarget);
        }

        if (candle.Low <= stopLoss)
        {
            return new ProtectiveFill(ExitReason.StopLoss, MarketSellPrice(stopLoss, costs));
        }

        if (takeProfit is { } target && candle.High >= target)
        {
            return new ProtectiveFill(ExitReason.TakeProfit, target);
        }

        return null;
    }

    private static decimal HalfSpread(TradingCosts costs) => costs.SpreadBps / 2m / 10_000m;

    private static decimal Slippage(TradingCosts costs) => costs.SlippageBps / 10_000m;
}
