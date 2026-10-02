using System.Reflection;
using System.Text.Json;
using Omega.Application.Backtesting;
using Omega.Application.Research;
using Omega.Application.Tests.TestSupport;
using Omega.Core.MarketData;
using Omega.Features;

namespace Omega.Application.Tests.Backtesting;

public sealed class ModelBacktestTests : IDisposable
{
    private readonly string _models = Directory.CreateTempSubdirectory("omega-models-").FullName;
    private readonly string _modelId;
    private readonly InMemoryCandleStore _candles = new();
    private readonly InMemoryBacktestRunStore _runs = new();

    public ModelBacktestTests()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var staging = Directory.CreateTempSubdirectory("omega-package-").FullName;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("model_package/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(Path.Combine(staging, name["model_package/".Length..]));
            stream.CopyTo(file);
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "manifest.json")));
        _modelId = manifest.RootElement.GetProperty("modelId").GetString()!;
        Directory.Move(staging, Path.Combine(_models, _modelId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("random_forest-000000000000")]
    public async Task Missing_or_invalid_models_are_reported(string modelId)
    {
        var result = await Service().RunAsync(Request(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)) with { ModelId = modelId }, CancellationToken.None);

        Assert.Equal(BacktestServiceErrors.ModelUnavailable, result.Error!.Code);
    }

    [Fact]
    public async Task Model_strategy_runs_with_the_package_barriers_and_horizon()
    {
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        Seed(start.AddDays(-2), days: 3);

        var outcome = (await Service().RunAsync(Request(start), CancellationToken.None)).Value;

        var result = outcome.Run.Result;
        Assert.Equal($"model-ev:{_modelId}", result.Strategy.Name);
        Assert.Equal(48, result.Config.MaxHoldingCandles);
        Assert.Equal(_modelId, result.Strategy.Parameters["modelId"]);
        Assert.DoesNotContain(outcome.Notices, n => n.Contains("in-sample", StringComparison.Ordinal));
        Assert.True(result.NoTradeCounts.Values.Sum() > 0 || result.Trades.Count > 0);
    }

    [Fact]
    public async Task Backtesting_on_the_training_period_is_flagged_as_in_sample()
    {
        var start = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);   // the reference model trained on 2025-01..2025-06
        Seed(start.AddDays(-2), days: 3);

        var outcome = (await Service().RunAsync(Request(start), CancellationToken.None)).Value;

        Assert.Contains(outcome.Notices, n => n.Contains("in-sample", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Holdout_looks_are_counted_per_model()
    {
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        Seed(start.AddDays(-2), days: 3);
        var service = Service();

        await service.RunAsync(Request(start, "holdout"), CancellationToken.None);
        var second = (await service.RunAsync(Request(start, "holdout"), CancellationToken.None)).Value;
        var baseline = (await service.RunAsync(Request(start, "holdout") with { StrategyName = "baseline-ema-trend", ModelId = null }, CancellationToken.None)).Value;

        Assert.Equal(1, second.PreviousEvaluationsOfThisPeriod);
        Assert.Equal(0, baseline.PreviousEvaluationsOfThisPeriod);
    }

    [Theory]
    [InlineData("2026-01-01", "2026-02-01", true)]
    [InlineData("2026-03-01", "2026-04-01", false)]
    [InlineData("2025-12-01", "2026-01-01", false)]
    [InlineData("2025-12-31", "2026-01-02", true)]
    public void Overlap_with_the_training_period_is_detected(string from, string to, bool overlaps)
    {
        const string training = "2026-01-01T00:00:00+00:00..2026-03-01T00:00:00+00:00";

        Assert.Equal(overlaps, BacktestService.Overlaps(training, DateTimeOffset.Parse(from + "T00:00:00Z"), DateTimeOffset.Parse(to + "T00:00:00Z")));
    }

    [Fact]
    public void Registered_models_are_listed_with_their_readiness()
    {
        var models = ModelRegistry.List(_models);

        var model = Assert.Single(models);
        Assert.Equal(_modelId, model.ModelId);
        Assert.Equal("platt", model.CalibrationMethod);
        Assert.True(model.ReadyForExpectedValue);
        Assert.Empty(ModelRegistry.List(Path.Combine(_models, "missing")));
    }

    public void Dispose() => Directory.Delete(_models, recursive: true);

    private BacktestService Service() => new(_candles, _runs, new FeatureEngine(FeatureSets.V1()), TimeProvider.System, _models);

    private BacktestRequest Request(DateTimeOffset start, string label = "development") =>
        new("model-ev", "BTCUSDT", CandleInterval.FiveMinutes, start, start.AddDays(1), label, ModelId: _modelId);

    private void Seed(DateTimeOffset first, int days)
    {
        for (var i = 0; i < days * 288; i++)
        {
            var open = first.AddMinutes(5 * i);
            var close = 60_000m + (decimal)Math.Round(400 * Math.Sin(i / 40.0) + (30 * Math.Sin(i * 1.3)), 2);
            _candles.Seed(Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
                close, close + 40m, close - 40m, close, 10m + (i % 3), 600_000m, 100).Value);
        }
    }
}
