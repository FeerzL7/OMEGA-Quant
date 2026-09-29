using Omega.Core.MarketData;

namespace Omega.MarketData;

/// <summary>Base type of everything a market-data stream reports.</summary>
/// <param name="ObservedAtUtc">Local time (UTC) at which OMEGA observed the event.</param>
public abstract record MarketDataEvent(DateTimeOffset ObservedAtUtc);

/// <summary>A candle closed on the exchange and passed all validation and sequencing checks.</summary>
/// <param name="Candle">The closed candle.</param>
/// <param name="Source">Data lineage, for example <c>binance-spot-ws</c>.</param>
/// <param name="ObservedAtUtc">When OMEGA received it.</param>
public sealed record CandleClosedEvent(Candle Candle, string Source, DateTimeOffset ObservedAtUtc)
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

/// <summary>
/// A candle was reported closed although, by the local clock, its close time is still
/// in the future: the local clock is behind the exchange by at least <paramref name="Skew"/>.
/// The candle is still accepted (exchange timestamps are authoritative), but anything that
/// relies on the local clock (freshness, latency) is off by that amount.
/// </summary>
public sealed record ClockSkewDetectedEvent(
    string Symbol,
    DateTimeOffset CandleCloseTimeUtc,
    TimeSpan Skew,
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
