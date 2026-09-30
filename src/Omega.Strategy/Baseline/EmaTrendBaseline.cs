using System.Globalization;
using Omega.Core.Trading;

namespace Omega.Strategy.Baseline;

/// <summary>
/// Non-ML baseline (Phase 6): a plain trend-following rule, the benchmark any model must beat.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Entry (flat): EMA20 above EMA50 and close above EMA20 → long.</item>
/// <item>Barriers, identical to the Phase 7 triple-barrier target: stop = close − 2·ATR14, target = close + 3·ATR14;
/// the timeout (48 candles) is set in the backtest configuration.</item>
/// <item>Exit (long): EMA20 below EMA50 → short signal (closes the long on Spot).</item>
/// </list>
/// Parameters are conventional values fixed before looking at any result. They are deliberately not optimized:
/// tuning them would turn the benchmark into a fitted strategy (CLAUDE.md §22).
/// </remarks>
public sealed class EmaTrendBaseline : IStrategy
{
    public const string Name = "baseline-ema-trend";
    public const decimal StopAtr = 2m;
    public const decimal TargetAtr = 3m;
    public const int TimeoutCandles = 48;

    public StrategyIdentity Identity { get; } = new(Name, "1", new Dictionary<string, string>
    {
        ["trendFast"] = "ema_20",
        ["trendSlow"] = "ema_50",
        ["stopAtr"] = StopAtr.ToString(CultureInfo.InvariantCulture),
        ["targetAtr"] = TargetAtr.ToString(CultureInfo.InvariantCulture),
        ["atr"] = "atr_14",
        ["timeoutCandles"] = TimeoutCandles.ToString(CultureInfo.InvariantCulture),
    });

    public Signal Evaluate(StrategyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var values = context.Features.Values;
        var fast = values["ema_20"]!.Value;
        var slow = values["ema_50"]!.Value;

        if (context.Position is not null)
        {
            return fast < slow ? Signal.Short("Trend reversal: EMA20 below EMA50.") : Signal.Hold();
        }

        var close = context.Candle.Close;
        if (!(fast > slow && (double)close > fast))
        {
            return Signal.Hold("No uptrend.");
        }

        var atr = (decimal)values["atr_14"]!.Value;
        var stop = close - (StopAtr * atr);
        if (atr <= 0 || stop <= 0)
        {
            return Signal.NoTrade(NoTradeReason.InvalidMarketData, "ATR is zero or the stop would not be positive.");
        }

        return Signal.Long(stop, close + (TargetAtr * atr), "Uptrend: EMA20 > EMA50 and close > EMA20.");
    }
}

/// <summary>
/// Benchmark: buy at the first opportunity and hold to the end. Run it with RiskPerTrade = 1 so the whole capital
/// is invested. Its stop is symbolic (a 99.9999 % fall), so it never exits early.
/// </summary>
public sealed class BuyAndHold : IStrategy
{
    public const string Name = "buy-and-hold";

    public StrategyIdentity Identity { get; } = new(Name, "1", new Dictionary<string, string>());

    public Signal Evaluate(StrategyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Position is null
            ? Signal.Long(context.Candle.Close * 0.000001m, explanation: "Buy and hold.")
            : Signal.Hold();
    }
}
