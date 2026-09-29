using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Contracts;
using Omega.Api.Endpoints;
using Omega.Application.MarketData;
using Omega.Core.MarketData;
using Omega.Core.Persistence;

namespace Omega.Api.Tests.Endpoints;

public class MarketEndpointsTests
{
    private static readonly DateTimeOffset Open = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Returns_the_market_state()
    {
        var store = new SingleCandleStore(Candle());
        var now = Open.AddMinutes(6);

        var result = await MarketEndpoints.GetStateAsync("BTCUSDT", "5m", Service(store, now), CancellationToken.None);

        var response = Assert.IsType<Ok<MarketStateResponse>>(result.Result).Value!;
        Assert.Equal("BTCUSDT", response.Symbol);
        Assert.Equal("5m", response.Interval);
        Assert.Equal("FRESH", response.Freshness);
        Assert.True(response.IsReliable);
        Assert.Empty(response.Issues);
        Assert.Equal(60.001, response.DataAgeSeconds);
        Assert.Equal(100.5m, response.LastClosedCandle!.Close);
    }

    [Fact]
    public async Task Missing_data_is_reported_as_unreliable_not_as_an_error()
    {
        var result = await MarketEndpoints.GetStateAsync("BTCUSDT", "5m", Service(new SingleCandleStore(null), Open), CancellationToken.None);

        var response = Assert.IsType<Ok<MarketStateResponse>>(result.Result).Value!;
        Assert.Equal("NO_DATA", response.Freshness);
        Assert.False(response.IsReliable);
        Assert.Null(response.LastClosedCandle);
    }

    [Theory]
    [InlineData("btcusdt", "5m", "symbol")]
    [InlineData("BTC-USDT", "5m", "symbol")]
    [InlineData("BTCUSDT", "7m", "interval")]
    public async Task Invalid_parameters_are_rejected(string symbol, string interval, string field)
    {
        var result = await MarketEndpoints.GetStateAsync(symbol, interval, Service(new SingleCandleStore(null), Open), CancellationToken.None);

        var problem = Assert.IsType<ValidationProblem>(result.Result);
        Assert.Contains(field, problem.ProblemDetails.Errors.Keys);
    }

    [Fact]
    public async Task Unavailable_store_returns_503_without_internal_details()
    {
        var store = new SingleCandleStore(null) { Failure = new PersistenceException("Host 10.0.0.5 refused the connection.", isTransient: true) };

        var result = await MarketEndpoints.GetStateAsync("BTCUSDT", "5m", Service(store, Open), CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.DoesNotContain("10.0.0.5", problem.ProblemDetails.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    private static MarketStateService Service(ICandleStore store, DateTimeOffset now) =>
        new(store, new MarketStateOptions(), new FixedTimeProvider(now));

    private static Candle Candle() => Omega.Core.MarketData.Candle.Create(
        "BTCUSDT", CandleInterval.FiveMinutes, Open, Open.AddMinutes(5).AddMilliseconds(-1), 100m, 101m, 99m, 100.5m, 1m, 100m, 10).Value;

    private sealed class SingleCandleStore(Candle? latest) : ICandleStore
    {
        public Exception? Failure { get; init; }

        public Task<Candle?> GetLatestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(latest) : Task.FromException<Candle?>(Failure);

        public Task<IReadOnlyList<CandleGap>> FindGapsAsync(
            string symbol, CandleInterval interval, DateTimeOffset fromOpenTimeUtc, DateTimeOffset toOpenTimeUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CandleGap>>([]);

        public Task<CandleSaveOutcome> SaveAsync(Candle candle, string source, DateTimeOffset observedAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Candle>> GetRangeAsync(
            string symbol, CandleInterval interval, DateTimeOffset fromOpenTimeUtc, DateTimeOffset toOpenTimeUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
