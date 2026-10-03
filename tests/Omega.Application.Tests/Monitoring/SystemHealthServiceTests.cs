using Omega.Application.MarketData;
using Omega.Application.Monitoring;
using Omega.Application.Tests.Paper;
using Omega.Application.Tests.TestSupport;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Execution.Paper;

namespace Omega.Application.Tests.Monitoring;

public class SystemHealthServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 30, TimeSpan.Zero);

    [Fact]
    public async Task An_unavailable_database_makes_the_rest_unjudgeable_not_healthy()
    {
        var health = await Service(new DownCandles(), new InMemoryPaperTradingStore()).CheckAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(ComponentState.Down, Get(health, "database").State);
        Assert.All(health.Components.Where(c => c.Name != "database"), c => Assert.Equal(ComponentState.NotAvailable, c.State));
    }

    [Fact]
    public async Task Fresh_market_data_is_healthy_and_stale_data_is_degraded()
    {
        var fresh = new InMemoryCandleStore();
        SeedUntil(fresh, Now.AddMinutes(-5));     // the last candle closed 30 s ago
        var stale = new InMemoryCandleStore();
        SeedUntil(stale, Now.AddHours(-2));

        var healthy = await Service(fresh, new InMemoryPaperTradingStore()).CheckAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);
        var degraded = await Service(stale, new InMemoryPaperTradingStore()).CheckAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);
        var empty = await Service(new InMemoryCandleStore(), new InMemoryPaperTradingStore()).CheckAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(ComponentState.Healthy, Get(healthy, "marketData").State);
        Assert.Equal(ComponentState.Degraded, Get(degraded, "marketData").State);
        Assert.Equal(ComponentState.NotAvailable, Get(empty, "marketData").State);
        Assert.All(healthy.Components, c => Assert.False(string.IsNullOrWhiteSpace(c.Basis)));
    }

    [Theory]
    [InlineData(4, ComponentState.Healthy)]
    [InlineData(20, ComponentState.Degraded)]
    [InlineData(45, ComponentState.Down)]
    public async Task The_paper_worker_is_judged_by_how_recently_its_session_was_updated(int minutesAgo, ComponentState expected)
    {
        var store = new InMemoryPaperTradingStore();
        await store.CreateSessionAsync(Session("paper-1", Now.AddMinutes(-minutesAgo), "{}"), CancellationToken.None);

        var health = await Service(new InMemoryCandleStore(), store).CheckAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(expected, Get(health, "paperWorker").State);
    }

    [Fact]
    public async Task Without_a_paper_session_the_worker_and_model_are_not_available()
    {
        var health = await Service(new InMemoryCandleStore(), new InMemoryPaperTradingStore()).CheckAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(ComponentState.NotAvailable, Get(health, "paperWorker").State);
        Assert.Equal(ComponentState.NotAvailable, Get(health, "model").State);
    }

    [Fact]
    public async Task A_session_using_a_missing_model_reports_the_model_down()
    {
        var store = new InMemoryPaperTradingStore();
        await store.CreateSessionAsync(Session("paper-model", Now, """{"strategy":{"name":"model-ev:rf-1","parameters":{"modelId":"random_forest-000000000000"}}}"""), CancellationToken.None);

        var health = await Service(new InMemoryCandleStore(), store, modelsDirectory: Path.GetTempPath()).CheckAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        var model = Get(health, "model");
        Assert.Equal(ComponentState.Down, model.State);
        Assert.Contains("random_forest-000000000000", model.Detail, StringComparison.Ordinal);
    }

    private static SystemHealthService Service(ICandleStore candles, IPaperTradingStore paper, string? modelsDirectory = null)
    {
        var clock = new ManualClock(Now);
        return new SystemHealthService(candles, new MarketStateService(candles, new MarketStateOptions(), clock), paper, modelsDirectory, clock);
    }

    private static ComponentHealth Get(SystemHealth health, string name) => health.Components.Single(c => c.Name == name);

    private static PaperSessionRecord Session(string name, DateTimeOffset updated, string config) =>
        new(Guid.NewGuid(), name, "BTCUSDT", "5m", config, "{}", updated.AddDays(-1), updated, updated);

    private static void SeedUntil(InMemoryCandleStore store, DateTimeOffset lastOpen)
    {
        for (var i = 288; i >= 0; i--)
        {
            var open = lastOpen.AddMinutes(-5 * i);
            store.Seed(Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1), 100m, 101m, 99m, 100m, 1m, 100m, 1).Value);
        }
    }

    private sealed class DownCandles : ICandleStore
    {
        public Task<IReadOnlyList<Candle>> GetRangeAsync(string s, CandleInterval i, DateTimeOffset f, DateTimeOffset t, CancellationToken c) => throw Down();

        public Task<Candle?> GetEarliestAsync(string s, CandleInterval i, CancellationToken c) => throw Down();

        public Task<Candle?> GetLatestAsync(string s, CandleInterval i, CancellationToken c) => throw Down();

        public Task<IReadOnlyList<CandleGap>> FindGapsAsync(string s, CandleInterval i, DateTimeOffset f, DateTimeOffset t, CancellationToken c) => throw Down();

        public Task<CandleSaveOutcome> SaveAsync(Candle candle, string source, DateTimeOffset at, CancellationToken c) => throw Down();

        private static PersistenceException Down() => new("connection refused", isTransient: true);
    }
}
