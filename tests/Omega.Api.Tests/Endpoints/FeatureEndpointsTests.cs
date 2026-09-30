using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Contracts;
using Omega.Api.Endpoints;
using Omega.Application.Features;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Features;

namespace Omega.Api.Tests.Endpoints;

public class FeatureEndpointsTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Catalog_lists_the_active_set_with_its_hash()
    {
        var catalog = FeatureEndpoints.GetCatalog(Service(new RangeStore([]))).Value!;

        Assert.Equal(FeatureSets.V1Version, catalog.Version);
        Assert.Equal(FeatureSets.V1().Hash, catalog.Hash);
        Assert.Equal(16, catalog.Features.Count);
    }

    [Fact]
    public async Task Latest_vector_is_returned_with_its_availability_time()
    {
        var store = new RangeStore([.. Enumerable.Range(0, 260).Select(Candle)]);

        var result = await FeatureEndpoints.GetLatestAsync("BTCUSDT", "5m", Service(store), CancellationToken.None);

        var vector = Assert.IsType<Ok<FeatureVectorResponse>>(result.Result).Value!;
        Assert.True(vector.IsComplete);
        Assert.Equal(Start.AddMinutes(5 * 259), vector.OpenTimeUtc);
        Assert.Equal(Start.AddMinutes(5 * 260).AddMilliseconds(-1), vector.AvailableAtUtc);
        Assert.Equal("5m", vector.Interval);
    }

    [Fact]
    public async Task No_data_is_404_and_a_gap_is_422()
    {
        var empty = await FeatureEndpoints.GetLatestAsync("BTCUSDT", "5m", Service(new RangeStore([])), CancellationToken.None);
        var withGap = await FeatureEndpoints.GetLatestAsync(
            "BTCUSDT", "5m", Service(new RangeStore([.. Enumerable.Range(0, 300).Where(i => i != 250).Select(Candle)])), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ProblemHttpResult>(empty.Result).StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsType<ProblemHttpResult>(withGap.Result).StatusCode);
    }

    [Fact]
    public async Task Invalid_parameters_and_unavailable_store_are_reported()
    {
        var invalid = await FeatureEndpoints.GetLatestAsync("btc", "5m", Service(new RangeStore([])), CancellationToken.None);
        var down = await FeatureEndpoints.GetLatestAsync(
            "BTCUSDT", "5m", Service(new RangeStore([]) { Failure = new PersistenceException("down", isTransient: true) }), CancellationToken.None);

        Assert.IsType<ValidationProblem>(invalid.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ProblemHttpResult>(down.Result).StatusCode);
    }

    private static FeatureService Service(ICandleStore store) => new(store, new FeatureEngine(FeatureSets.V1()));

    private static Candle Candle(int index)
    {
        var open = Start.AddMinutes(5 * index);
        var close = 100m + (index % 9 * 0.5m);
        return Omega.Core.MarketData.Candle.Create(
            "BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
            100m, 105m, 95m, close, 10m + (index % 4), 1000m, 10).Value;
    }

    private sealed class RangeStore(IReadOnlyList<Candle> candles) : ICandleStore
    {
        public Exception? Failure { get; init; }

        public Task<Candle?> GetEarliestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Candle?> GetLatestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(candles.Count == 0 ? null : candles[^1]) : Task.FromException<Candle?>(Failure);

        public Task<IReadOnlyList<Candle>> GetRangeAsync(
            string symbol, CandleInterval interval, DateTimeOffset fromOpenTimeUtc, DateTimeOffset toOpenTimeUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Candle>>([.. candles.Where(c => c.OpenTimeUtc >= fromOpenTimeUtc && c.OpenTimeUtc < toOpenTimeUtc)]);

        public Task<IReadOnlyList<CandleGap>> FindGapsAsync(
            string symbol, CandleInterval interval, DateTimeOffset fromOpenTimeUtc, DateTimeOffset toOpenTimeUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CandleGap>>([]);

        public Task<CandleSaveOutcome> SaveAsync(Candle candle, string source, DateTimeOffset observedAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
