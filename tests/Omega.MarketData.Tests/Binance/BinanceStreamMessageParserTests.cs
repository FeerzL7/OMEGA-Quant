using Omega.MarketData.Binance;
using Omega.MarketData.Tests.TestSupport;

namespace Omega.MarketData.Tests.Binance;

public class BinanceStreamMessageParserTests
{
    // Payload from the official Binance Spot "WebSocket Streams" documentation
    // (Kline/Candlestick Streams for UTC), with the comments removed.
    private const string OfficialKlineExample = """
        {"e":"kline","E":1672515782136,"s":"BNBBTC","k":{"t":1672515780000,"T":1672515839999,"s":"BNBBTC","i":"1m","f":100,"L":200,"o":"0.0010","c":"0.0020","h":"0.0025","l":"0.0015","v":"1000","n":100,"x":false,"q":"1.0000","V":"500","Q":"0.500","B":"123456"}}
        """;

    [Fact]
    public void Parses_the_official_kline_example()
    {
        var message = Assert.IsType<BinanceKlineMessage>(BinanceStreamMessageParser.Parse(OfficialKlineExample));
        var kline = message.Kline;

        Assert.Equal(1672515782136, message.EventTimeMs);
        Assert.Equal(1672515780000, kline.StartTimeMs);
        Assert.Equal(1672515839999, kline.CloseTimeMs);
        Assert.Equal("BNBBTC", kline.Symbol);
        Assert.Equal("1m", kline.Interval);
        Assert.Equal(0.0010m, kline.Open);
        Assert.Equal(0.0025m, kline.High);
        Assert.Equal(0.0015m, kline.Low);
        Assert.Equal(0.0020m, kline.Close);
        Assert.Equal(1000m, kline.BaseVolume);
        Assert.Equal(1.0000m, kline.QuoteVolume);
        Assert.Equal(100, kline.TradeCount);
        Assert.False(kline.IsClosed);
    }

    [Fact]
    public void Parses_the_closed_flag()
    {
        var message = Assert.IsType<BinanceKlineMessage>(BinanceStreamMessageParser.Parse(KlineMessages.Kline(0)));

        Assert.True(message.Kline.IsClosed);
    }

    [Fact]
    public void Parses_the_server_shutdown_event()
    {
        var message = Assert.IsType<BinanceServerShutdownMessage>(BinanceStreamMessageParser.Parse(KlineMessages.ServerShutdown));

        Assert.Equal(1770123456789, message.EventTimeMs);
    }

    [Theory]
    [InlineData("""{"result":null,"id":1}""")]
    [InlineData("""{"e":"aggTrade","E":1672515782136,"s":"BNBBTC"}""")]
    public void Control_responses_and_other_events_are_ignored(string json)
    {
        Assert.IsType<BinanceIgnoredMessage>(BinanceStreamMessageParser.Parse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"E":1}""")]
    [InlineData("""{"e":"kline","E":1}""")]
    [InlineData("""{"e":"kline","E":1,"k":{"t":0,"T":299999,"s":"BTCUSDT","i":"5m","o":"abc","c":"1","h":"1","l":"1","v":"1","n":1,"x":true,"q":"1"}}""")]
    [InlineData("""{"e":"kline","E":1,"k":{"t":0,"T":299999,"s":"BTCUSDT","i":"5m","o":"-1","c":"1","h":"1","l":"1","v":"1","n":1,"x":true,"q":"1"}}""")]
    [InlineData("""{"e":"kline","E":1,"k":{"t":0,"T":299999,"s":"BTCUSDT","i":"5m","o":"1","c":"1","h":"1","l":"1","v":"1","n":1,"x":"yes","q":"1"}}""")]
    [InlineData("""{"e":"kline","E":1,"k":{"t":"0","T":299999,"s":"BTCUSDT","i":"5m","o":"1","c":"1","h":"1","l":"1","v":"1","n":1,"x":true,"q":"1"}}""")]
    [InlineData("""{"e":"kline","E":1,"k":{"t":0.5,"T":299999,"s":"BTCUSDT","i":"5m","o":"1","c":"1","h":"1","l":"1","v":"1","n":1,"x":true,"q":"1"}}""")]
    public void Malformed_messages_become_invalid_instead_of_throwing(string json)
    {
        Assert.IsType<BinanceInvalidMessage>(BinanceStreamMessageParser.Parse(json));
    }
}
