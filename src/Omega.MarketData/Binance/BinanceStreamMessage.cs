namespace Omega.MarketData.Binance;

/// <summary>A parsed message from a Binance Spot raw stream.</summary>
internal abstract record BinanceStreamMessage;

/// <summary><c>kline</c> event (Kline/Candlestick Streams for UTC).</summary>
internal sealed record BinanceKlineMessage(long EventTimeMs, BinanceKline Kline) : BinanceStreamMessage;

/// <summary><c>serverShutdown</c> event: the server is about to close the connection.</summary>
internal sealed record BinanceServerShutdownMessage(long EventTimeMs) : BinanceStreamMessage;

/// <summary>Well-formed JSON that is not an event OMEGA consumes (for example a control response).</summary>
internal sealed record BinanceIgnoredMessage(string Description) : BinanceStreamMessage;

/// <summary>Message that could not be parsed or is missing required fields.</summary>
internal sealed record BinanceInvalidMessage(string Reason) : BinanceStreamMessage;

/// <summary>The <c>k</c> object of a kline event. Prices and volumes are exact decimals.</summary>
internal sealed record BinanceKline(
    long StartTimeMs,
    long CloseTimeMs,
    string Symbol,
    string Interval,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal BaseVolume,
    decimal QuoteVolume,
    long TradeCount,
    bool IsClosed);
