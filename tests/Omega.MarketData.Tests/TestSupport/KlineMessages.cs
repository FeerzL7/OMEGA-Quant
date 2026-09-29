namespace Omega.MarketData.Tests.TestSupport;

/// <summary>Builds Binance Spot raw-stream messages in the documented format.</summary>
internal static class KlineMessages
{
    public static readonly DateTimeOffset FirstOpenTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public const string ServerShutdown = """{"e":"serverShutdown","E":1770123456789}""";

    public static DateTimeOffset OpenTime(int index) => FirstOpenTime.AddMinutes(5 * index);

    /// <summary>A BTCUSDT 5m kline event for the candle at <paramref name="index"/> (5-minute steps).</summary>
    public static string Kline(int index, bool closed = true, string symbol = "BTCUSDT", string interval = "5m")
    {
        var openMs = OpenTime(index).ToUnixTimeMilliseconds();
        var closeMs = openMs + 299_999;
        var eventMs = closed ? closeMs + 1 : openMs + 2_000;
        var isClosed = closed ? "true" : "false";

        return $$$"""
            {"e":"kline","E":{{{eventMs}}},"s":"{{{symbol}}}","k":{"t":{{{openMs}}},"T":{{{closeMs}}},"s":"{{{symbol}}}","i":"{{{interval}}}","f":1,"L":2,"o":"100.0","c":"100.5","h":"101.0","l":"99.5","v":"12.5","n":42,"x":{{{isClosed}}},"q":"1250.0","V":"6.0","Q":"600.0","B":"0"}}
            """;
    }
}
