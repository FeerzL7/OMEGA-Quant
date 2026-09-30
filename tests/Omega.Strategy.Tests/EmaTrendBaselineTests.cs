using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Features;
using Omega.Strategy;
using Omega.Strategy.Baseline;

namespace Omega.Strategy.Tests;

public class EmaTrendBaselineTests
{
    private static readonly DateTimeOffset Open = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly EmaTrendBaseline Baseline = new();

    [Fact]
    public void Uptrend_gives_a_long_with_atr_barriers()
    {
        var signal = Baseline.Evaluate(Context(close: 105m, ema20: 104, ema50: 100, atr: 2));

        Assert.Equal(SignalDirection.Long, signal.Direction);
        Assert.Equal(105m - (2m * 2m), signal.StopLossPrice);
        Assert.Equal(105m + (3m * 2m), signal.TakeProfitPrice);
    }

    [Theory]
    [InlineData(99, 100)]    // EMA20 below EMA50: no uptrend
    [InlineData(106, 100)]   // uptrend but close (105) below EMA20
    public void Without_a_confirmed_uptrend_it_holds(double ema20, double ema50)
    {
        Assert.Equal(SignalDirection.Hold, Baseline.Evaluate(Context(close: 105m, ema20: ema20, ema50: ema50, atr: 2)).Direction);
    }

    [Fact]
    public void Open_position_is_closed_when_the_trend_reverses_and_kept_otherwise()
    {
        var position = new PositionView(Open, 100m, 1m, 96m, 106m);

        Assert.Equal(SignalDirection.Short, Baseline.Evaluate(Context(105m, ema20: 99, ema50: 100, atr: 2, position)).Direction);
        Assert.Equal(SignalDirection.Hold, Baseline.Evaluate(Context(105m, ema20: 104, ema50: 100, atr: 2, position)).Direction);
    }

    [Fact]
    public void Zero_atr_is_invalid_market_data_not_a_trade()
    {
        var signal = Baseline.Evaluate(Context(close: 105m, ema20: 104, ema50: 100, atr: 0));

        Assert.Equal(SignalDirection.NoTrade, signal.Direction);
        Assert.Equal(NoTradeReason.InvalidMarketData, signal.Reason);
    }

    [Fact]
    public void Parameters_are_recorded_in_the_identity()
    {
        Assert.Equal("baseline-ema-trend", Baseline.Identity.Name);
        Assert.Equal("2", Baseline.Identity.Parameters["stopAtr"]);
        Assert.Equal("3", Baseline.Identity.Parameters["targetAtr"]);
        Assert.Equal("48", Baseline.Identity.Parameters["timeoutCandles"]);
    }

    [Fact]
    public void Buy_and_hold_buys_once_and_never_exits()
    {
        var buyAndHold = new BuyAndHold();

        var entry = buyAndHold.Evaluate(Context(100m, 1, 1, 1));
        var held = buyAndHold.Evaluate(Context(100m, 1, 1, 1, new PositionView(Open, 100m, 1m, 0.0001m, null)));

        Assert.Equal(SignalDirection.Long, entry.Direction);
        Assert.Null(entry.TakeProfitPrice);
        Assert.True(entry.StopLossPrice < 0.001m);
        Assert.Equal(SignalDirection.Hold, held.Direction);
    }

    private static StrategyContext Context(decimal close, double ema20, double ema50, double atr, PositionView? position = null)
    {
        var candle = Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, Open, Open.AddMinutes(5).AddMilliseconds(-1),
            close, close + 1, close - 1, close, 1m, 1m, 1).Value;
        var values = new Dictionary<string, double?> { ["ema_20"] = ema20, ["ema_50"] = ema50, ["atr_14"] = atr };
        return new StrategyContext(candle, new FeatureVector("BTCUSDT", CandleInterval.FiveMinutes, Open, candle.CloseTimeUtc, "t", "h", values), position);
    }
}
