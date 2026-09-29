using Omega.Core.MarketData;
using Omega.Core.Results;

namespace Omega.MarketData.Binance;

/// <summary>
/// Converts a closed Binance kline into a domain <see cref="Candle"/>, checking
/// Binance-specific conventions first: UTC klines open on interval boundaries
/// and close one millisecond before the next interval starts.
/// </summary>
internal static class BinanceKlineNormalizer
{
    public static Result<Candle> ToClosedCandle(BinanceKline kline, string expectedSymbol, CandleInterval expectedInterval)
    {
        ArgumentNullException.ThrowIfNull(kline);

        if (!kline.IsClosed)
        {
            return Fail(MarketDataErrors.KlineNotClosed, "Kline is not closed.");
        }

        if (!string.Equals(kline.Symbol, expectedSymbol, StringComparison.Ordinal))
        {
            return Fail(MarketDataErrors.SymbolMismatch, $"Expected symbol {expectedSymbol}, got {kline.Symbol}.");
        }

        if (!string.Equals(kline.Interval, BinanceIntervals.ToCode(expectedInterval), StringComparison.Ordinal))
        {
            return Fail(MarketDataErrors.IntervalMismatch, $"Expected interval {BinanceIntervals.ToCode(expectedInterval)}, got {kline.Interval}.");
        }

        var intervalMs = (long)expectedInterval.ToTimeSpan().TotalMilliseconds;

        if (kline.StartTimeMs < 0 || kline.StartTimeMs % intervalMs != 0)
        {
            return Fail(MarketDataErrors.OpenTimeMisaligned, $"Open time {kline.StartTimeMs} is not aligned to the interval.");
        }

        if (kline.CloseTimeMs != kline.StartTimeMs + intervalMs - 1)
        {
            return Fail(MarketDataErrors.CloseTimeInconsistent, $"Close time {kline.CloseTimeMs} does not match open time {kline.StartTimeMs}.");
        }

        return Candle.Create(
            kline.Symbol,
            expectedInterval,
            DateTimeOffset.FromUnixTimeMilliseconds(kline.StartTimeMs),
            DateTimeOffset.FromUnixTimeMilliseconds(kline.CloseTimeMs),
            kline.Open,
            kline.High,
            kline.Low,
            kline.Close,
            kline.BaseVolume,
            kline.QuoteVolume,
            kline.TradeCount);
    }

    private static Result<Candle> Fail(string code, string message) =>
        Result.Failure<Candle>(new Error(code, message));
}

internal static class BinanceIntervals
{
    public static string ToCode(CandleInterval interval) => interval switch
    {
        CandleInterval.FiveMinutes => "5m",
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Interval has no Binance code."),
    };
}

/// <summary>Error codes produced by the market-data adapter.</summary>
public static class MarketDataErrors
{
    public const string KlineNotClosed = "MARKETDATA_KLINE_NOT_CLOSED";
    public const string SymbolMismatch = "MARKETDATA_SYMBOL_MISMATCH";
    public const string IntervalMismatch = "MARKETDATA_INTERVAL_MISMATCH";
    public const string OpenTimeMisaligned = "MARKETDATA_OPEN_TIME_MISALIGNED";
    public const string CloseTimeInconsistent = "MARKETDATA_CLOSE_TIME_INCONSISTENT";
}
