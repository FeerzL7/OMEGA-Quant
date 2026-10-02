using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Endpoints;
using Omega.Application.Research;
using Omega.Core.MarketData;
using Omega.Features;

namespace Omega.Api.Tests.Endpoints;

public class DatasetEndpointsTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Invalid_requests_are_rejected()
    {
        var result = await DatasetEndpoints.ExportAsync(new DatasetExportRequest("btc", "1h", null, null), Builder([]), Paths(), CancellationToken.None);

        Assert.Equal(["symbol", "interval", "fromUtc", "toUtc"], Assert.IsType<ValidationProblem>(result.Result).ProblemDetails.Errors.Keys);
    }

    [Fact]
    public async Task Period_without_labelled_samples_is_422()
    {
        var result = await DatasetEndpoints.ExportAsync(
            new DatasetExportRequest("BTCUSDT", "5m", Start, Start.AddDays(1)), Builder([]), Paths(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsType<ProblemHttpResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task Export_writes_the_dataset_and_returns_its_manifest()
    {
        var paths = Paths();
        var candles = Enumerable.Range(0, 600).Select(i =>
        {
            var open = Start.AddMinutes(5 * i);
            var close = 100m + (decimal)Math.Round(4 * Math.Sin(i / 25.0), 2);
            return Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
                close, close + 0.5m, close - 0.5m, close, 5m + (i % 3), 500m, 5).Value;
        }).ToList();

        var result = await DatasetEndpoints.ExportAsync(
            new DatasetExportRequest("BTCUSDT", "5m", Start.AddMinutes(5 * 300), Start.AddMinutes(5 * 500)), Builder(candles), paths, CancellationToken.None);

        var created = Assert.IsType<Created<DatasetExportResponse>>(result.Result).Value!;
        Assert.True(File.Exists(Path.Combine(created.Directory, "dataset.csv")));
        Assert.True(File.Exists(Path.Combine(created.Directory, "manifest.json")));
        Assert.Equal(created.DatasetId, created.Manifest.DatasetId);
        Directory.Delete(paths.DatasetsDirectory, recursive: true);
    }

    private static ResearchPaths Paths() => new(Directory.CreateTempSubdirectory("omega-api-datasets-").FullName, Path.GetTempPath());

    private static DatasetBuilder Builder(IReadOnlyList<Candle> candles) =>
        new(new Store(candles), new FeatureEngine(FeatureSets.V1()), TimeProvider.System);

    private sealed class Store(IReadOnlyList<Candle> candles) : ICandleStore
    {
        public Task<IReadOnlyList<Candle>> GetRangeAsync(
            string symbol, CandleInterval interval, DateTimeOffset fromOpenTimeUtc, DateTimeOffset toOpenTimeUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Candle>>([.. candles.Where(c => c.OpenTimeUtc >= fromOpenTimeUtc && c.OpenTimeUtc < toOpenTimeUtc)]);

        public Task<Candle?> GetEarliestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Candle?> GetLatestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CandleGap>> FindGapsAsync(
            string symbol, CandleInterval interval, DateTimeOffset fromOpenTimeUtc, DateTimeOffset toOpenTimeUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CandleSaveOutcome> SaveAsync(Candle candle, string source, DateTimeOffset observedAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
