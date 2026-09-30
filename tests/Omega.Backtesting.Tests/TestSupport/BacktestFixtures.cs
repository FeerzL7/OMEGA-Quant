using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Features;
using Omega.Strategy;

namespace Omega.Backtesting.Tests.TestSupport;

internal static class Bars
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset OpenTime(int index) => Start.AddMinutes(5 * index);

    /// <summary>Candle at <paramref name="index"/> with the given OHLC (volume 1).</summary>
    public static Candle At(int index, decimal open, decimal high, decimal low, decimal close) => Candle.Create(
        "BTCUSDT", CandleInterval.FiveMinutes, OpenTime(index), OpenTime(index + 1).AddMilliseconds(-1),
        open, high, low, close, 1m, close, 1).Value;

    /// <summary>A flat candle at <paramref name="price"/>.</summary>
    public static Candle Flat(int index, decimal price) => At(index, price, price, price, price);

    /// <summary>Flat candles at the given prices, one per index.</summary>
    public static Candle[] Flats(params decimal[] prices) => [.. prices.Select((price, i) => Flat(i, price))];
}

/// <summary>A feature available from the first candle, so tests control warm-up explicitly.</summary>
internal sealed class CloseFeature(int lookback = 1) : IFeature
{
    public FeatureDefinition Definition { get; } = new("close", "Test feature.", "close[t]", lookback, "close", "None (test).");

    public double? Compute(ReadOnlySpan<Candle> window) => (double)window[^1].Close;
}

internal static class Engines
{
    public static readonly BacktestConfig NoCosts = new() { FeeRate = 0m, SpreadBps = 0m, SlippageBps = 0m };

    public static BacktestEngine Create(BacktestConfig? config = null, int featureLookback = 1) =>
        new(new FeatureEngine(new FeatureSet("test-v1", [new CloseFeature(featureLookback)])), config ?? NoCosts);
}

/// <summary>Emits the given signal at the close of the given candle index; Hold otherwise. Records what it saw.</summary>
internal sealed class ScriptedStrategy(Dictionary<int, Signal>? script = null) : IStrategy
{
    private readonly Dictionary<DateTimeOffset, Signal> _byTime =
        (script ?? []).ToDictionary(pair => Bars.OpenTime(pair.Key), pair => pair.Value);

    public List<StrategyContext> Seen { get; } = [];

    public StrategyIdentity Identity { get; } = new("scripted", "1", new Dictionary<string, string>());

    public Signal Evaluate(StrategyContext context)
    {
        Seen.Add(context);
        return _byTime.TryGetValue(context.Candle.OpenTimeUtc, out var signal) ? signal : Signal.Hold();
    }
}

/// <summary>A simple rule on closes, to exercise many trades: long above the previous close, exit below it.</summary>
internal sealed class MomentumStrategy : IStrategy
{
    private decimal? _previousClose;

    public StrategyIdentity Identity { get; } = new("momentum-test", "1", new Dictionary<string, string>());

    public Signal Evaluate(StrategyContext context)
    {
        var close = context.Candle.Close;
        var signal = _previousClose is not { } previous ? Signal.Hold()
            : close > previous ? Signal.Long(close * 0.98m, close * 1.03m)
            : close < previous ? Signal.Short()
            : Signal.Hold();
        _previousClose = close;
        return signal;
    }
}
