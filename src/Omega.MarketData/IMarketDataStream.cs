namespace Omega.MarketData;

/// <summary>
/// A continuous source of market-data events for one symbol and interval.
/// Implementations handle connection lifecycle, reconnection and integrity
/// checks; consumers only see normalized events.
/// </summary>
public interface IMarketDataStream
{
    /// <summary>
    /// Reads events until <paramref name="cancellationToken"/> is cancelled.
    /// Cancellation ends the sequence normally; it does not throw.
    /// </summary>
    /// <param name="lastKnownOpenTimeUtc">
    /// Open time of the last candle the consumer already has (for example the
    /// latest persisted one), or null. Candles at or before it are not emitted
    /// again, and missing candles after it are reported as a gap.
    /// </param>
    /// <param name="cancellationToken">Stops the stream.</param>
    IAsyncEnumerable<MarketDataEvent> ReadEventsAsync(DateTimeOffset? lastKnownOpenTimeUtc, CancellationToken cancellationToken);
}
