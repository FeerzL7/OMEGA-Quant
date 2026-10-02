using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Omega.Application.Paper;
using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Execution;
using Omega.Execution.Paper;
using Omega.Features;
using Omega.Infrastructure.Persistence;
using Omega.Infrastructure.Persistence.Migrations;
using Omega.Risk;
using Omega.Strategy.Baseline;

namespace Omega.Integration.Tests.Persistence;

public class PostgresPaperTradingStoreTests : DatabaseTest
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [DatabaseFact]
    public async Task A_paper_session_on_postgres_survives_a_restart_with_complete_order_histories()
    {
        await MigrateAsync();
        var candles = Series(3 * 288);
        var store = new PostgresPaperTradingStore(DataSource);
        var candleStore = new PostgresCandleStore(DataSource);
        var clock = new Clock();

        var first = Engine(store, candleStore, clock);
        var firstStart = await first.StartAsync(CancellationToken.None);
        Assert.True(firstStart.IsSuccess, firstStart.Error?.Message);
        await FeedAsync(first, candleStore, clock, candles.Take(500));

        var second = Engine(store, candleStore, clock);                     // restart: everything comes from PostgreSQL
        var secondStart = await second.StartAsync(CancellationToken.None);
        Assert.True(secondStart.IsSuccess, secondStart.Error?.Message);
        await FeedAsync(second, candleStore, clock, candles.Skip(500));

        // Same trades as an uninterrupted in-memory reference run (same engine, same candles).
        var reference = await ReferenceTradesAsync(candles);
        var stored = (await store.GetTradesAsync(second.SessionId, 1_000, CancellationToken.None)).Reverse().ToList();
        Assert.Equal(reference, stored);
        Assert.True(stored.Count >= 5, $"only {stored.Count} trades");

        // Every order has a complete, gap-free history ending in its current status.
        var orders = await store.GetOrdersAsync(second.SessionId, 1_000, CancellationToken.None);
        foreach (var order in orders)
        {
            var events = await store.GetOrderEventsAsync(order.Id, CancellationToken.None);
            Assert.Equal(Enumerable.Range(0, events.Count), events.Select(e => e.Sequence));
            Assert.Equal(OrderStatus.Created, events[0].Status);
            Assert.Equal(order.Status, events[^1].Status);
        }

        Assert.Contains(orders, o => o.Type == OrderType.StopMarket && o.Status == OrderStatus.Canceled);
        Assert.Equal(candles.Count, (await store.GetDecisionsAsync(second.SessionId, 10_000, CancellationToken.None)).Count);
    }

    [DatabaseFact]
    public async Task A_step_is_all_or_nothing()
    {
        await MigrateAsync();
        var store = new PostgresPaperTradingStore(DataSource);
        var session = new PaperSessionRecord(Guid.NewGuid(), "atomic", "BTCUSDT", "5m", "{}", "{}", Start, Start, null);
        await store.CreateSessionAsync(session, CancellationToken.None);
        var trade = new PaperTrade(Start, 100m, 1m, 0.1m, 95m, 110m, Start.AddMinutes(5), 101m, 0.1m, ExitReason.TakeProfit, 1, 0.8m, null);
        var decision = new PaperDecision(Start, SignalDirection.Hold, null, null, new Dictionary<string, double>(), "HOLD", null, null, 10_000m, []);
        var step = new PaperStep(session.Id, Start, """{"cash":1}""", [], [trade], decision);

        await store.SaveStepAsync(step, CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveStepAsync(step with { StateJson = """{"cash":2}""" }, CancellationToken.None));

        Assert.Single(await store.GetTradesAsync(session.Id, 10, CancellationToken.None));   // the second trade was rolled back with the step
        Assert.Contains("\"cash\": 1", (await store.GetSessionAsync("atomic", CancellationToken.None))!.StateJson, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Commands_are_applied_once_with_their_state()
    {
        await MigrateAsync();
        var store = new PostgresPaperTradingStore(DataSource);
        var session = new PaperSessionRecord(Guid.NewGuid(), "commands", "BTCUSDT", "5m", "{}", "{}", Start, Start, null);
        await store.CreateSessionAsync(session, CancellationToken.None);

        var id = await store.EnqueueCommandAsync(session.Id, PaperCommandType.TripKillSwitch, "incident", "fer", Start, CancellationToken.None);
        var pending = Assert.Single(await store.GetPendingCommandsAsync(session.Id, CancellationToken.None));
        await store.CompleteCommandAsync(id, session.Id, "Kill switch tripped.", """{"killed":true}""", Start.AddSeconds(5), CancellationToken.None);

        Assert.Equal((PaperCommandType.TripKillSwitch, "incident", "fer"), (pending.Type, pending.Reason, pending.RequestedBy));
        Assert.Empty(await store.GetPendingCommandsAsync(session.Id, CancellationToken.None));
        Assert.Equal("Kill switch tripped.", (await store.GetCommandsAsync(session.Id, 5, CancellationToken.None))[0].Result);
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteCommandAsync(id, session.Id, "again", "{}", Start, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => store.EnqueueCommandAsync(session.Id, PaperCommandType.ResetKillSwitch, " ", "fer", Start, CancellationToken.None));
    }

    private async Task MigrateAsync() =>
        await new DatabaseMigrator(DataSource, NullLogger<DatabaseMigrator>.Instance).EnsureUpToDateAsync(applyPending: true, CancellationToken.None);

    private static PaperTradingEngine Engine(IPaperTradingStore store, ICandleStore candles, TimeProvider clock) =>
        new(store, candles, new FeatureEngine(FeatureSets.V1()), new EmaTrendBaseline(), new PaperSessionConfig
        {
            Name = "integration",
            Symbol = "BTCUSDT",
            Interval = CandleInterval.FiveMinutes,
            Strategy = new EmaTrendBaseline().Identity,
            InitialCapital = 10_000m,
            Costs = new TradingCosts(0.001m, 1m, 2m),
            Risk = RiskLimits.Default,
            MaxHoldingCandles = 48,
            StartFromUtc = Start,
        }, clock, NullLogger.Instance);

    private static async Task FeedAsync(PaperTradingEngine engine, ICandleStore candles, Clock clock, IEnumerable<Candle> series)
    {
        foreach (var candle in series)
        {
            await candles.SaveAsync(candle, "test", candle.CloseTimeUtc, CancellationToken.None);
            clock.Now = candle.CloseTimeUtc.AddSeconds(1);
            await engine.ProcessNewCandlesAsync(CancellationToken.None);
        }
    }

    private static async Task<List<PaperTrade>> ReferenceTradesAsync(IReadOnlyList<Candle> candles)
    {
        var store = new MemoryStore();
        var candleStore = new MemoryCandles();
        var clock = new Clock();
        var engine = Engine(store, candleStore, clock);
        await engine.StartAsync(CancellationToken.None);
        await FeedAsync(engine, candleStore, clock, candles);
        return store.Trades;
    }

    private static List<Candle> Series(int count)
    {
        var candles = new List<Candle>(count);
        for (var i = 0; i < count; i++)
        {
            var close = Math.Round(100m + (decimal)((5 * Math.Sin(i / 40.0)) + (0.4 * Math.Sin(i * 1.3))), 2);
            var open = i == 0 ? 100m : candles[^1].Close;
            var time = Start.AddMinutes(5 * i);
            candles.Add(Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, time, time.AddMinutes(5).AddMilliseconds(-1),
                open, Math.Max(open, close) + 0.25m, Math.Min(open, close) - 0.25m, close, 10m + (i % 3), 1000m, 10).Value);
        }

        return candles;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryCandles : ICandleStore
    {
        private readonly SortedDictionary<DateTimeOffset, Candle> _candles = [];

        public Task<IReadOnlyList<Candle>> GetRangeAsync(string s, CandleInterval i, DateTimeOffset from, DateTimeOffset to, CancellationToken c) =>
            Task.FromResult<IReadOnlyList<Candle>>([.. _candles.Values.Where(x => x.OpenTimeUtc >= from && x.OpenTimeUtc < to)]);

        public Task<Candle?> GetEarliestAsync(string s, CandleInterval i, CancellationToken c) => Task.FromResult(_candles.Values.FirstOrDefault());

        public Task<Candle?> GetLatestAsync(string s, CandleInterval i, CancellationToken c) => Task.FromResult(_candles.Values.LastOrDefault());

        public Task<IReadOnlyList<CandleGap>> FindGapsAsync(string s, CandleInterval i, DateTimeOffset f, DateTimeOffset t, CancellationToken c) => throw new NotSupportedException();

        public Task<CandleSaveOutcome> SaveAsync(Candle candle, string source, DateTimeOffset observedAtUtc, CancellationToken c)
        {
            _candles[candle.OpenTimeUtc] = candle;
            return Task.FromResult(CandleSaveOutcome.Inserted);
        }
    }

    private sealed class MemoryStore : IPaperTradingStore
    {
        private PaperSessionRecord? _session;
        private readonly Dictionary<Guid, Order> _orders = [];

        public List<PaperTrade> Trades { get; } = [];

        public Task<PaperSessionRecord?> GetSessionAsync(string name, CancellationToken c) => Task.FromResult(_session);

        public Task<IReadOnlyList<PaperSessionRecord>> ListSessionsAsync(CancellationToken c) => throw new NotSupportedException();

        public Task CreateSessionAsync(PaperSessionRecord session, CancellationToken c)
        {
            _session = session;
            return Task.CompletedTask;
        }

        public Task SaveStepAsync(PaperStep step, CancellationToken c)
        {
            _session = _session! with { StateJson = step.StateJson };
            foreach (var o in step.Orders) _orders[o.Id] = o;
            Trades.AddRange(step.Trades);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Order>> GetWorkingOrdersAsync(Guid id, CancellationToken c) => Task.FromResult<IReadOnlyList<Order>>([.. _orders.Values.Where(o => o.IsWorking)]);

        public Task<IReadOnlyList<PaperCommand>> GetPendingCommandsAsync(Guid id, CancellationToken c) => Task.FromResult<IReadOnlyList<PaperCommand>>([]);

        public Task<long> EnqueueCommandAsync(Guid id, PaperCommandType t, string r, string b, DateTimeOffset at, CancellationToken c) => throw new NotSupportedException();

        public Task CompleteCommandAsync(long id, Guid s, string r, string st, DateTimeOffset at, CancellationToken c) => throw new NotSupportedException();

        public Task<IReadOnlyList<PaperCommand>> GetCommandsAsync(Guid id, int l, CancellationToken c) => throw new NotSupportedException();

        public Task<IReadOnlyList<Order>> GetOrdersAsync(Guid id, int l, CancellationToken c) => throw new NotSupportedException();

        public Task<IReadOnlyList<OrderEvent>> GetOrderEventsAsync(Guid id, CancellationToken c) => throw new NotSupportedException();

        public Task<IReadOnlyList<PaperTrade>> GetTradesAsync(Guid id, int l, CancellationToken c) => throw new NotSupportedException();

        public Task<IReadOnlyList<PaperDecision>> GetDecisionsAsync(Guid id, int l, CancellationToken c) => throw new NotSupportedException();
    }
}
