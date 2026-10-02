using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Omega.Application.Paper;
using Omega.Application.Tests.TestSupport;
using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Features;
using Omega.Risk;
using Omega.Strategy.Baseline;

namespace Omega.Application.Tests.Paper;

public class PaperConfigurationTests
{
    [Fact]
    public void Candidate_strategies_are_built_with_the_timeout_of_their_label()
    {
        var baseline = PaperStrategyFactory.Create(new PaperTradingOptions { Strategy = "baseline-ema-trend" }, null).Value;

        Assert.Equal("baseline-ema-trend", baseline.Strategy.Identity.Name);
        Assert.Equal(48, baseline.MaxHoldingCandles);
    }

    [Fact]
    public void Benchmarks_and_unknown_or_missing_models_are_refused()
    {
        Assert.True(PaperStrategyFactory.Create(new PaperTradingOptions { Strategy = "buy-and-hold" }, null).IsFailure);
        Assert.True(PaperStrategyFactory.Create(new PaperTradingOptions { Strategy = "nope" }, null).IsFailure);
        Assert.True(PaperStrategyFactory.Create(new PaperTradingOptions { Strategy = "model-ev", ModelId = "../x" }, Path.GetTempPath()).IsFailure);
        Assert.True(PaperStrategyFactory.Create(new PaperTradingOptions { Strategy = "model-ev", ModelId = "random_forest-000000000000" }, Path.GetTempPath()).IsFailure);
    }

    [Fact]
    public void A_model_strategy_loads_its_verified_package()
    {
        var models = Directory.CreateTempSubdirectory("omega-paper-models-").FullName;
        var modelId = ExtractReferencePackage(models);

        var created = PaperStrategyFactory.Create(new PaperTradingOptions { Strategy = "model-ev", ModelId = modelId }, models).Value;

        Assert.Equal($"model-ev:{modelId}", created.Strategy.Identity.Name);
        Assert.Equal(48, created.MaxHoldingCandles);
        created.Resource!.Dispose();
        Directory.Delete(models, recursive: true);
    }

    [Theory]
    [InlineData("Paper 1", "baseline-ema-trend", null)]
    [InlineData("paper-1", "model-ev", null)]
    public void Invalid_options_are_reported(string session, string strategy, string? modelId)
    {
        Assert.NotEmpty(new PaperTradingOptions { Session = session, Strategy = strategy, ModelId = modelId }.Validate());
        Assert.Empty(new PaperTradingOptions().Validate());
    }

    [Fact]
    public async Task A_stored_session_is_described_for_operators()
    {
        var store = new InMemoryPaperTradingStore();
        var clock = new ManualClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var engine = new PaperTradingEngine(store, new InMemoryCandleStore(), new FeatureEngine(FeatureSets.V1()), new EmaTrendBaseline(), new PaperSessionConfig
        {
            Name = "ops", Symbol = "BTCUSDT", Interval = CandleInterval.FiveMinutes, Strategy = new EmaTrendBaseline().Identity,
            InitialCapital = 5_000m, Costs = new TradingCosts(0.001m, 1m, 2m), Risk = RiskLimits.Default, StartFromUtc = clock.Now,
        }, clock, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);
        await store.EnqueueCommandAsync(engine.SessionId, Omega.Execution.Paper.PaperCommandType.TripKillSwitch, "test", "fer", clock.Now, CancellationToken.None);
        await engine.ApplyCommandsAsync(CancellationToken.None);

        var summary = PaperSessions.Describe((await store.GetSessionAsync("ops", CancellationToken.None))!);

        Assert.Equal(("ops", "baseline-ema-trend", 5_000m), (summary.Name, summary.Strategy, summary.Cash));
        Assert.Null(summary.Position);
        Assert.True(summary.KillSwitch.Active);
        Assert.Contains("by fer", summary.KillSwitch.Reason, StringComparison.Ordinal);
    }

    private static string ExtractReferencePackage(string root)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("model_package/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(Path.Combine(staging, name["model_package/".Length..]));
            stream.CopyTo(file);
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "manifest.json")));
        var modelId = manifest.RootElement.GetProperty("modelId").GetString()!;
        Directory.Move(staging, Path.Combine(root, modelId));
        return modelId;
    }
}
