using Omega.Core.Results;

namespace Omega.Core.MarketData;

/// <summary>
/// A closed OHLCV candle, normalized from an exchange event.
/// Instances can only be created through <see cref="Create"/>, which enforces
/// the exchange-independent invariants. Exchange-specific rules (time alignment,
/// close-time conventions) are checked by the market-data adapter.
/// </summary>
public sealed record Candle
{
    private Candle(
        string symbol,
        CandleInterval interval,
        DateTimeOffset openTimeUtc,
        DateTimeOffset closeTimeUtc,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal baseVolume,
        decimal quoteVolume,
        long tradeCount)
    {
        Symbol = symbol;
        Interval = interval;
        OpenTimeUtc = openTimeUtc;
        CloseTimeUtc = closeTimeUtc;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        BaseVolume = baseVolume;
        QuoteVolume = quoteVolume;
        TradeCount = tradeCount;
    }

    /// <summary>Exchange symbol in upper case, for example BTCUSDT.</summary>
    public string Symbol { get; }

    public CandleInterval Interval { get; }

    /// <summary>Exchange-provided open time, UTC.</summary>
    public DateTimeOffset OpenTimeUtc { get; }

    /// <summary>Exchange-provided close time, UTC.</summary>
    public DateTimeOffset CloseTimeUtc { get; }

    public decimal Open { get; }

    public decimal High { get; }

    public decimal Low { get; }

    public decimal Close { get; }

    /// <summary>Traded volume in the base asset (BTC for BTCUSDT).</summary>
    public decimal BaseVolume { get; }

    /// <summary>Traded volume in the quote asset (USDT for BTCUSDT).</summary>
    public decimal QuoteVolume { get; }

    public long TradeCount { get; }

    public static Result<Candle> Create(
        string symbol,
        CandleInterval interval,
        DateTimeOffset openTimeUtc,
        DateTimeOffset closeTimeUtc,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal baseVolume,
        decimal quoteVolume,
        long tradeCount)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Fail(CandleErrors.SymbolRequired, "Symbol is required.");
        }

        if (!Enum.IsDefined(interval))
        {
            return Fail(CandleErrors.IntervalInvalid, $"Interval '{interval}' is not supported.");
        }

        if (openTimeUtc.Offset != TimeSpan.Zero || closeTimeUtc.Offset != TimeSpan.Zero)
        {
            return Fail(CandleErrors.TimeNotUtc, "Open and close times must be expressed in UTC.");
        }

        if (closeTimeUtc <= openTimeUtc || closeTimeUtc - openTimeUtc > interval.ToTimeSpan())
        {
            return Fail(CandleErrors.TimeRangeInvalid, "Close time must be after open time and within one interval.");
        }

        if (open <= 0 || high <= 0 || low <= 0 || close <= 0)
        {
            return Fail(CandleErrors.PriceNotPositive, "Prices must be greater than zero.");
        }

        if (high < Math.Max(open, close) || low > Math.Min(open, close) || low > high)
        {
            return Fail(CandleErrors.PriceRangeInconsistent, "High and low must contain open and close.");
        }

        if (baseVolume < 0 || quoteVolume < 0)
        {
            return Fail(CandleErrors.VolumeNegative, "Volumes cannot be negative.");
        }

        if (tradeCount < 0)
        {
            return Fail(CandleErrors.TradeCountNegative, "Trade count cannot be negative.");
        }

        return Result.Success(new Candle(
            symbol, interval, openTimeUtc, closeTimeUtc,
            open, high, low, close, baseVolume, quoteVolume, tradeCount));
    }

    private static Result<Candle> Fail(string code, string message) =>
        Result.Failure<Candle>(new Error(code, message));
}

/// <summary>Error codes produced by <see cref="Candle.Create"/>.</summary>
public static class CandleErrors
{
    public const string SymbolRequired = "CANDLE_SYMBOL_REQUIRED";
    public const string IntervalInvalid = "CANDLE_INTERVAL_INVALID";
    public const string TimeNotUtc = "CANDLE_TIME_NOT_UTC";
    public const string TimeRangeInvalid = "CANDLE_TIME_RANGE_INVALID";
    public const string PriceNotPositive = "CANDLE_PRICE_NOT_POSITIVE";
    public const string PriceRangeInconsistent = "CANDLE_PRICE_RANGE_INCONSISTENT";
    public const string VolumeNegative = "CANDLE_VOLUME_NEGATIVE";
    public const string TradeCountNegative = "CANDLE_TRADE_COUNT_NEGATIVE";
}
