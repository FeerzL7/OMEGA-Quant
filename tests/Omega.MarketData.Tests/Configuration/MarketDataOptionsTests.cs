using Omega.MarketData.Configuration;

namespace Omega.MarketData.Tests.Configuration;

public class MarketDataOptionsTests
{
    [Fact]
    public void Defaults_are_valid_and_target_btcusdt_5m_on_the_market_data_only_endpoint()
    {
        var options = new MarketDataOptions();

        Assert.Empty(options.Validate());
        Assert.Equal("wss://data-stream.binance.vision", options.StreamBaseUrl);
        Assert.Equal("BTCUSDT", options.Symbol);
    }

    [Fact]
    public void Only_secure_websocket_urls_are_accepted()
    {
        var options = new MarketDataOptions { StreamBaseUrl = "ws://data-stream.binance.vision" };

        Assert.Contains(options.Validate(), error => error.StartsWith("MarketData:StreamBaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Rest_endpoint_must_be_https_and_defaults_to_the_market_data_only_host()
    {
        Assert.Equal("https://data-api.binance.vision", new MarketDataOptions().RestBaseUrl);

        var options = new MarketDataOptions { RestBaseUrl = "http://data-api.binance.vision" };

        Assert.Contains(options.Validate(), error => error.StartsWith("MarketData:RestBaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Symbol_must_be_upper_case_alphanumeric()
    {
        var options = new MarketDataOptions { Symbol = "btcusdt" };

        Assert.Contains(options.Validate(), error => error.StartsWith("MarketData:Symbol", StringComparison.Ordinal));
    }

    [Fact]
    public void Connection_lifetime_must_stay_below_the_exchange_24_hour_limit()
    {
        var options = new MarketDataOptions { MaxConnectionLifetime = TimeSpan.FromHours(24) };

        Assert.Contains(options.Validate(), error => error.StartsWith("MarketData:MaxConnectionLifetime", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_problem_is_reported()
    {
        var options = new MarketDataOptions
        {
            ReceiveIdleTimeout = TimeSpan.Zero,
            ReconnectInitialDelay = TimeSpan.FromSeconds(10),
            ReconnectMaxDelay = TimeSpan.FromSeconds(1),
        };

        Assert.Equal(2, options.Validate().Count);
    }
}
