using Omega.Core.MarketData;

namespace Omega.Core.Tests.MarketData;

public class CandleTests
{
    private static readonly DateTimeOffset OpenTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Valid_values_create_a_candle()
    {
        var result = Create();

        Assert.True(result.IsSuccess);
        Assert.Equal("BTCUSDT", result.Value.Symbol);
        Assert.Equal(OpenTime, result.Value.OpenTimeUtc);
        Assert.Equal(101m, result.Value.High);
    }

    [Fact]
    public void Missing_symbol_is_rejected()
    {
        Assert.Equal(CandleErrors.SymbolRequired, Create(symbol: " ").Error!.Code);
    }

    [Fact]
    public void Undefined_interval_is_rejected()
    {
        Assert.Equal(CandleErrors.IntervalInvalid, Create(interval: default).Error!.Code);
    }

    [Fact]
    public void Non_utc_times_are_rejected()
    {
        var local = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.FromHours(1));

        Assert.Equal(CandleErrors.TimeNotUtc, Create(openTime: local, closeTime: local.AddMinutes(5)).Error!.Code);
    }

    [Fact]
    public void Close_time_must_be_after_open_time_and_within_one_interval()
    {
        Assert.Equal(CandleErrors.TimeRangeInvalid, Create(closeTime: OpenTime).Error!.Code);
        Assert.Equal(CandleErrors.TimeRangeInvalid, Create(closeTime: OpenTime.AddMinutes(6)).Error!.Code);
    }

    [Fact]
    public void Prices_must_be_positive()
    {
        Assert.Equal(CandleErrors.PriceNotPositive, Create(low: 0m).Error!.Code);
    }

    [Fact]
    public void High_and_low_must_contain_open_and_close()
    {
        Assert.Equal(CandleErrors.PriceRangeInconsistent, Create(high: 100.2m).Error!.Code);
        Assert.Equal(CandleErrors.PriceRangeInconsistent, Create(low: 100.1m).Error!.Code);
    }

    [Fact]
    public void Negative_volume_or_trade_count_is_rejected()
    {
        Assert.Equal(CandleErrors.VolumeNegative, Create(baseVolume: -1m).Error!.Code);
        Assert.Equal(CandleErrors.TradeCountNegative, Create(tradeCount: -1).Error!.Code);
    }

    private static Omega.Core.Results.Result<Candle> Create(
        string symbol = "BTCUSDT",
        CandleInterval interval = CandleInterval.FiveMinutes,
        DateTimeOffset? openTime = null,
        DateTimeOffset? closeTime = null,
        decimal open = 100m,
        decimal high = 101m,
        decimal low = 99.5m,
        decimal close = 100.5m,
        decimal baseVolume = 12.5m,
        long tradeCount = 42) =>
        Candle.Create(
            symbol,
            interval,
            openTime ?? OpenTime,
            closeTime ?? OpenTime.AddMinutes(5).AddMilliseconds(-1),
            open, high, low, close,
            baseVolume,
            quoteVolume: 1250m,
            tradeCount);
}
