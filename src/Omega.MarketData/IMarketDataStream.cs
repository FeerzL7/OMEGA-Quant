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
    IAsyncEnumerable<MarketDataEvent> ReadEventsAsync(CancellationToken cancellationToken);
}
