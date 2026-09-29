using Omega.Core.MarketData;
using Omega.MarketData.Binance;

namespace Omega.MarketData.Tests.Binance;

public class BinanceKlineNormalizerTests
{
    private static readonly long OpenMs = new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void Closed_kline_becomes_a_utc_candle_with_exchange_timestamps()
    {
        var result = BinanceKlineNormalizer.ToClosedCandle(Kline(), "BTCUSDT", CandleInterval.FiveMinutes);

        Assert.True(result.IsSuccess);
        var candle = result.Value;
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero), candle.OpenTimeUtc);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 9, 59, 999, TimeSpan.Zero), candle.CloseTimeUtc);
        Assert.Equal(TimeSpan.Zero, candle.OpenTimeUtc.Offset);
        Assert.Equal(100.5m, candle.Close);
        Assert.Equal(1250m, candle.QuoteVolume);
        Assert.Equal(42, candle.TradeCount);
    }

    [Fact]
    public void Open_kline_is_rejected()
    {
        Assert.Equal(MarketDataErrors.KlineNotClosed, Normalize(Kline() with { IsClosed = false }));
    }

    [Fact]
    public void Symbol_and_interval_must_match_the_subscription()
    {
        Assert.Equal(MarketDataErrors.SymbolMismatch, Normalize(Kline() with { Symbol = "ETHUSDT" }));
        Assert.Equal(MarketDataErrors.IntervalMismatch, Normalize(Kline() with { Interval = "1m" }));
    }

    [Fact]
    public void Open_time_must_be_aligned_to_the_interval()
    {
        Assert.Equal(MarketDataErrors.OpenTimeMisaligned, Normalize(Kline() with { StartTimeMs = OpenMs + 1_000, CloseTimeMs = OpenMs + 300_999 }));
    }

    [Fact]
    public void Close_time_must_be_one_millisecond_before_the_next_interval()
    {
        Assert.Equal(MarketDataErrors.CloseTimeInconsistent, Normalize(Kline() with { CloseTimeMs = OpenMs + 300_000 }));
    }

    [Fact]
    public void Domain_invariants_are_enforced()
    {
        Assert.Equal(CandleErrors.PriceRangeInconsistent, Normalize(Kline() with { High = 100.2m }));
    }

    private static string Normalize(BinanceKline kline) =>
        BinanceKlineNormalizer.ToClosedCandle(kline, "BTCUSDT", CandleInterval.FiveMinutes).Error!.Code;

    private static BinanceKline Kline() => new(
        StartTimeMs: OpenMs,
        CloseTimeMs: OpenMs + 299_999,
        Symbol: "BTCUSDT",
        Interval: "5m",
        Open: 100m,
        High: 101m,
        Low: 99.5m,
        Close: 100.5m,
        BaseVolume: 12.5m,
        QuoteVolume: 1250m,
        TradeCount: 42,
        IsClosed: true);
}
