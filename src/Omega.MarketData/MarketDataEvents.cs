using Omega.Core.MarketData;

namespace Omega.MarketData;

/// <summary>Base type of everything a market-data stream reports.</summary>
/// <param name="ObservedAtUtc">Local time (UTC) at which OMEGA observed the event.</param>
public abstract record MarketDataEvent(DateTimeOffset ObservedAtUtc);

/// <summary>A candle closed on the exchange and passed all validation and sequencing checks.</summary>
public sealed record CandleClosedEvent(Candle Candle, DateTimeOffset ObservedAtUtc)
    : MarketDataEvent(ObservedAtUtc);

/// <summary>
/// One or more closed candles between two received candles never arrived.
/// Market-data integrity for the gap period cannot be trusted.
/// </summary>
/// <param name="Symbol">Affected symbol.</param>
/// <param name="Interval">Affected interval.</param>
/// <param name="FirstMissingOpenTimeUtc">Open time of the first missing candle.</param>
/// <param name="MissingCandles">Number of consecutive missing candles.</param>
/// <param name="ObservedAtUtc">When the gap was detected.</param>
public sealed record DataGapDetectedEvent(
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset FirstMissingOpenTimeUtc,
    int MissingCandles,
    DateTimeOffset ObservedAtUtc)
    : MarketDataEvent(ObservedAtUtc);

/// <summary>The connection to the market-data source changed state.</summary>
public sealed record ConnectionStatusChangedEvent(ConnectionStatus Status, string Reason, DateTimeOffset ObservedAtUtc)
    : MarketDataEvent(ObservedAtUtc);

public enum ConnectionStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
}
