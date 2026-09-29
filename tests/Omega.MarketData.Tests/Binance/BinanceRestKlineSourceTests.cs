using System.Net;
using System.Net.Http.Headers;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Omega.Core.MarketData;
using Omega.MarketData.Binance;
using Omega.MarketData.Configuration;
using Omega.MarketData.Tests.TestSupport;
using static Omega.MarketData.Tests.TestSupport.KlineMessages;

namespace Omega.MarketData.Tests.Binance;

public class BinanceRestKlineSourceTests
{
    // Long after every test candle: all of them are closed unless a test says otherwise.
    private static readonly DateTimeOffset Now = OpenTime(10_000);

    [Fact]
    public async Task Requests_the_documented_endpoint_with_utc_millisecond_bounds()
    {
        var handler = FakeKlinesEndpoint.Serving([2, 3]);

        var candles = await Fetch(handler, from: 2, to: 4);

        Assert.Equal([OpenTime(2), OpenTime(3)], candles.Select(c => c.OpenTimeUtc));
        var query = HttpUtility.ParseQueryString(Assert.Single(handler.Requests).Query);
        Assert.Equal("/api/v3/klines", handler.Requests[0].AbsolutePath);
        Assert.Equal("BTCUSDT", query["symbol"]);
        Assert.Equal("5m", query["interval"]);
        Assert.Equal(OpenTime(2).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), query["startTime"]);
        Assert.Equal((OpenTime(4).ToUnixTimeMilliseconds() - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), query["endTime"]);
        Assert.Equal("1000", query["limit"]);
    }

    [Fact]
    public async Task Values_are_parsed_exactly()
    {
        var candle = Assert.Single(await Fetch(FakeKlinesEndpoint.Serving([0]), from: 0, to: 1));

        Assert.Equal(100.5m, candle.Close);
        Assert.Equal(1250.0m, candle.QuoteVolume);
        Assert.Equal(42, candle.TradeCount);
        Assert.Equal(OpenTime(1).AddMilliseconds(-1), candle.CloseTimeUtc);
    }

    [Fact]
    public async Task Candle_still_forming_is_never_returned()
    {
        // Now is 3 minutes into candle 5: candles 0-4 are closed, 5 is forming.
        var now = OpenTime(5).AddMinutes(3);

        var candles = await Fetch(FakeKlinesEndpoint.Serving([0, 1, 2, 3, 4, 5]), from: 0, to: 6, now: now);

        Assert.Equal([.. Enumerable.Range(0, 5).Select(OpenTime)], candles.Select(c => c.OpenTimeUtc));
    }

    [Fact]
    public async Task Candle_that_just_closed_waits_for_the_clock_skew_tolerance()
    {
        var closeOfCandle4 = OpenTime(5).AddMilliseconds(-1);

        var tooSoon = await Fetch(FakeKlinesEndpoint.Serving([4]), from: 4, to: 5, now: closeOfCandle4.AddSeconds(1));
        var afterTolerance = await Fetch(FakeKlinesEndpoint.Serving([4]), from: 4, to: 5, now: closeOfCandle4.AddSeconds(3));

        Assert.Empty(tooSoon);
        Assert.Single(afterTolerance);
    }

    [Fact]
    public async Task Large_ranges_are_paged_1000_candles_at_a_time()
    {
        var handler = FakeKlinesEndpoint.Serving([.. Enumerable.Range(0, 2500)]);

        var candles = await Fetch(handler, from: 0, to: 2500);

        Assert.Equal(2500, candles.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(OpenTime(2499), candles[^1].OpenTimeUtc);
    }

    [Fact]
    public async Task Periods_without_candles_on_the_exchange_are_simply_absent()
    {
        var candles = await Fetch(FakeKlinesEndpoint.Serving([0, 1, 5, 6]), from: 0, to: 7);

        Assert.Equal([OpenTime(0), OpenTime(1), OpenTime(5), OpenTime(6)], candles.Select(c => c.OpenTimeUtc));
    }

    [Fact]
    public async Task Invalid_rows_are_skipped()
    {
        var body = $"[{FakeKlinesEndpoint.RestRow(OpenTime(0).ToUnixTimeMilliseconds(), high: "99.0")}," +
                   $"{FakeKlinesEndpoint.RestRow(OpenTime(1).ToUnixTimeMilliseconds())}]";

        var candles = await Fetch(FakeHttpHandler.Returning(HttpStatusCode.OK, body), from: 0, to: 2);

        Assert.Equal([OpenTime(1)], candles.Select(c => c.OpenTimeUtc));
    }

    [Fact]
    public async Task Rate_limit_is_transient_and_carries_retry_after()
    {
        var handler = FakeHttpHandler.Returning((HttpStatusCode)429, """{"code":-1003,"msg":"Too many requests."}""",
            response => response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)));

        var error = await Assert.ThrowsAsync<HistoricalDataException>(() => Fetch(handler, from: 0, to: 1));

        Assert.True(error.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
    }

    [Theory]
    [InlineData(418, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    public async Task Http_errors_are_classified(int status, bool transient)
    {
        var handler = FakeHttpHandler.Returning((HttpStatusCode)status, """{"code":-1121,"msg":"Invalid symbol."}""");

        var error = await Assert.ThrowsAsync<HistoricalDataException>(() => Fetch(handler, from: 0, to: 1));

        Assert.Equal(transient, error.IsTransient);
    }

    [Fact]
    public async Task Malformed_response_is_not_transient()
    {
        var error = await Assert.ThrowsAsync<HistoricalDataException>(
            () => Fetch(FakeHttpHandler.Returning(HttpStatusCode.OK, """{"not":"an array"}"""), from: 0, to: 1));

        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task Network_failure_and_timeout_are_transient()
    {
        var unreachable = new FakeHttpHandler((_, _) => throw new HttpRequestException("Connection refused."));
        var slow = new FakeHttpHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var network = await Assert.ThrowsAsync<HistoricalDataException>(() => Fetch(unreachable, from: 0, to: 1));
        var timeout = await Assert.ThrowsAsync<HistoricalDataException>(() => Fetch(slow, from: 0, to: 1, timeout: TimeSpan.FromMilliseconds(100)));

        Assert.True(network.IsTransient);
        Assert.True(timeout.IsTransient);
        Assert.Contains("timed out", timeout.Message, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<Candle>> Fetch(
        FakeHttpHandler handler, int from, int to, DateTimeOffset? now = null, TimeSpan? timeout = null)
    {
        var options = new MarketDataOptions { RestRequestTimeout = timeout ?? TimeSpan.FromSeconds(5) };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://data-api.binance.vision/") };
        var source = new BinanceRestKlineSource(httpClient, options, new FixedTimeProvider(now ?? Now), NullLogger<BinanceRestKlineSource>.Instance);

        return await source.GetClosedCandlesAsync("BTCUSDT", CandleInterval.FiveMinutes, OpenTime(from), OpenTime(to), CancellationToken.None);
    }
}
