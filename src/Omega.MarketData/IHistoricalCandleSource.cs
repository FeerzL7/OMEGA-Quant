using Omega.Core.MarketData;

namespace Omega.MarketData;

/// <summary>Source of historical closed candles, used to fill gaps left by the live stream.</summary>
public interface IHistoricalCandleSource
{
    /// <summary>Lineage recorded with candles from this source.</summary>
    string SourceName { get; }

    /// <summary>
    /// Closed candles with open time in [<paramref name="fromOpenTimeUtc"/>, <paramref name="toOpenTimeUtc"/>),
    /// oldest first, validated like stream candles. A candle that has not closed yet is never returned.
    /// Periods for which the exchange has no candles (for example maintenance) are simply absent.
    /// </summary>
    /// <exception cref="HistoricalDataException">The source could not provide the data.</exception>
    Task<IReadOnlyList<Candle>> GetClosedCandlesAsync(
        string symbol,
        CandleInterval interval,
        DateTimeOffset fromOpenTimeUtc,
        DateTimeOffset toOpenTimeUtc,
        CancellationToken cancellationToken);
}

/// <summary>A historical-data request failed.</summary>
public sealed class HistoricalDataException : Exception
{
    public HistoricalDataException(string message, bool isTransient, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    public HistoricalDataException()
    {
    }

    public HistoricalDataException(string message)
        : base(message)
    {
    }

    public HistoricalDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Retrying later may succeed (network error, timeout, rate limit, server error).</summary>
    public bool IsTransient { get; }

    /// <summary>Minimum wait requested by the source before retrying (for example a rate-limit Retry-After).</summary>
    public TimeSpan? RetryAfter { get; }
}
