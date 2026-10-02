using Microsoft.Extensions.Logging.Abstractions;
using Omega.Application.Paper;
using Omega.Application.Tests.TestSupport;
using Omega.Backtesting;
using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Execution;
using Omega.Execution.Paper;
using Omega.Features;
using Omega.Risk;
using Omega.Strategy;
using Omega.Strategy.Baseline;

namespace Omega.Application.Tests.Paper;

public class PaperTradingEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FeatureEngine Features = new(FeatureSets.V1());
    private static readonly TradingCosts Costs = new(0.001m, 1m, 2m);

    [Fact]
    public async Task Paper_trading_reproduces_the_backtester_trade_for_trade_with_the_baseline()
    {
        var candles = Series(4 * 288);
        var backtest = Backtest(candles, new EmaTrendBaseline(), maxHolding: 48);

        var (store, _) = await Paper(candles, new EmaTrendBaseline(), maxHolding: 48);

        AssertSameTrades(backtest, store.Trades);
        Assert.True(store.Trades.Count >= 10, $"only {store.Trades.Count} trades");
    }

    [Fact]
    public async Task Paper_trading_reproduces_signal_exits_timeouts_and_data_gaps()
    {
        // A scripted strategy that also exits by signal, and a 30-minute hole in the data right after an entry signal
        // (candle 540): the pending entry must expire, in both engines.
        var candles = Series(3 * 288).Where((c, i) => i is < 541 or > 546).ToList();
        var backtest = Backtest(candles, new Scripted(), maxHolding: 20);

        var (store, _) = await Paper(candles, new Scripted(), maxHolding: 20);

        AssertSameTrades(backtest, store.Trades);
        if (Environment.GetEnvironmentVariable("OMEGA_DEBUG_PARITY") == "1")
        {
            Console.WriteLine("#exits " + string.Join(", ", store.Trades.GroupBy(t => t.ExitReason).Select(g => $"{g.Key}={g.Count()}")));
        }

        Assert.Contains(store.Trades, t => t.ExitReason == ExitReason.Signal);
        Assert.Contains(store.Trades, t => t.ExitReason == ExitReason.TimeLimit);
        Assert.Contains(store.Decisions, d => d.Notes.Any(n => n.StartsWith("SIGNAL_EXPIRED_BY_DATA_GAP", StringComparison.Ordinal)));
        Assert.Contains(backtest.Rejections, r => r.Code == "SIGNAL_EXPIRED_BY_DATA_GAP");
        Assert.Contains(store.Trades, t => t.ExitReason is ExitReason.StopLoss or ExitReason.TakeProfit);
    }

    [Fact]
    public async Task A_restart_in_the_middle_changes_nothing()
    {
        var candles = Series(4 * 288);
        var (uninterrupted, _) = await Paper(candles, new EmaTrendBaseline(), maxHolding: 48);

        var store = new InMemoryPaperTradingStore();
        var candleStore = new InMemoryCandleStore();
        var clock = new ManualClock(Start);
        var first = Engine(store, candleStore, clock, new EmaTrendBaseline(), 48);
        await first.StartAsync(CancellationToken.None);
        await Feed(first, candleStore, clock, candles.Take(600));

        var second = Engine(store, candleStore, clock, new EmaTrendBaseline(), 48);   // a new process: state comes from the store
        Assert.True((await second.StartAsync(CancellationToken.None)).IsSuccess);
        await Feed(second, candleStore, clock, candles.Skip(600));

        Assert.Equal(uninterrupted.Trades, store.Trades);
    }

    [Fact]
    public async Task Stale_candles_can_close_positions_but_never_open_them()
    {
        var candles = Series(3 * 288);
        var store = new InMemoryPaperTradingStore();
        var candleStore = new InMemoryCandleStore();
        var clock = new ManualClock(Start);
        var engine = Engine(store, candleStore, clock, new EmaTrendBaseline(), 48);
        await engine.StartAsync(CancellationToken.None);

        foreach (var candle in candles)
        {
            candleStore.Seed(candle);
        }

        clock.Now = candles[^1].CloseTimeUtc.AddHours(2);   // the worker was down: everything is old now
        await engine.ProcessNewCandlesAsync(CancellationToken.None);

        Assert.Empty(store.Trades);
        Assert.Contains(store.Decisions, d => d.RiskCode == "RISK_MARKET_DATA_UNRELIABLE");
        Assert.DoesNotContain(store.Decisions, d => d.Action == "ENTRY_SCHEDULED");
        Assert.All(store.Decisions, d => Assert.Contains(d.Notes, n => n.StartsWith("STALE", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_session_refuses_to_continue_with_another_configuration()
    {
        var store = new InMemoryPaperTradingStore();
        var candleStore = new InMemoryCandleStore();
        var clock = new ManualClock(Start);
        await Engine(store, candleStore, clock, new EmaTrendBaseline(), 48).StartAsync(CancellationToken.None);

        var changed = Engine(store, candleStore, clock, new EmaTrendBaseline(), 48, risk: RiskLimits.Default with { MaxDailyLoss = 0.05m });

        Assert.Equal(PaperErrors.ConfigurationChanged, (await changed.StartAsync(CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task A_session_resumes_when_the_store_normalizes_its_stored_json()
    {
        // PostgreSQL jsonb reorders keys and removes whitespace; the configuration must still be recognised as the same.
        var store = new InMemoryPaperTradingStore();
        var candleStore = new InMemoryCandleStore();
        var clock = new ManualClock(Start);
        await Engine(store, candleStore, clock, new EmaTrendBaseline(), 48).StartAsync(CancellationToken.None);
        var stored = (await store.GetSessionAsync("test", CancellationToken.None))!;
        store.Replace(stored with { ConfigJson = Normalize(stored.ConfigJson) });

        var resumed = await Engine(store, candleStore, clock, new EmaTrendBaseline(), 48).StartAsync(CancellationToken.None);

        Assert.True(resumed.IsSuccess, resumed.Error?.Message);
    }

    /// <summary>Same JSON, keys in reverse order at every level and no whitespace: what jsonb does to a document.</summary>
    private static string Normalize(string json)
    {
        static System.Text.Json.Nodes.JsonNode? Reorder(System.Text.Json.Nodes.JsonNode? node) => node switch
        {
            System.Text.Json.Nodes.JsonObject o => new System.Text.Json.Nodes.JsonObject(o.Reverse().Select(p => KeyValuePair.Create(p.Key, Reorder(p.Value?.DeepClone())))),
            System.Text.Json.Nodes.JsonArray a => new System.Text.Json.Nodes.JsonArray([.. a.Select(i => Reorder(i?.DeepClone()))]),
            _ => node?.DeepClone(),
        };

        var normalized = Reorder(System.Text.Json.Nodes.JsonNode.Parse(json))!.ToJsonString();
        Assert.NotEqual(json, normalized);
        return normalized;
    }

    [Fact]
    public async Task Kill_switch_commands_block_and_release_entries_and_are_audited()
    {
        var candles = Series(3 * 288);
        var store = new InMemoryPaperTradingStore();
        var candleStore = new InMemoryCandleStore();
        var clock = new ManualClock(Start);
        var engine = Engine(store, candleStore, clock, new AlwaysLong(), maxHolding: 3);
        await engine.StartAsync(CancellationToken.None);

        await store.EnqueueCommandAsync(engine.SessionId, PaperCommandType.TripKillSwitch, "exchange incident", "fer", Start, CancellationToken.None);
        Assert.Equal(1, await engine.ApplyCommandsAsync(CancellationToken.None));
        await Feed(engine, candleStore, clock, candles.Take(300));
        var whileTripped = store.Decisions.Where(d => d.Direction == SignalDirection.Long).ToList();

        await store.EnqueueCommandAsync(engine.SessionId, PaperCommandType.ResetKillSwitch, "incident solved", "fer", clock.Now, CancellationToken.None);
        await engine.ApplyCommandsAsync(CancellationToken.None);
        await Feed(engine, candleStore, clock, candles.Skip(300).Take(20));

        Assert.NotEmpty(whileTripped);
        Assert.All(whileTripped, d => Assert.Equal("RISK_KILL_SWITCH", d.RiskCode));
        Assert.Contains("manual: exchange incident (by fer)", whileTripped[0].RiskDetail, StringComparison.Ordinal);
        Assert.Empty(store.Trades.Where(t => t.EntryCandleOpenTimeUtc <= candles[299].OpenTimeUtc));
        Assert.NotEmpty(store.Trades);   // entries resumed after the reset
        var commands = await store.GetCommandsAsync(engine.SessionId, 10, CancellationToken.None);
        Assert.All(commands, c => Assert.NotNull(c.AppliedAtUtc));
        Assert.Equal("Kill switch reset.", commands[0].Result);
    }

    [Fact]
    public async Task A_tripped_kill_switch_survives_a_restart_of_the_worker()
    {
        var candles = Series(3 * 288);
        var store = new InMemoryPaperTradingStore();
        var candleStore = new InMemoryCandleStore();
        var clock = new ManualClock(Start);
        var first = Engine(store, candleStore, clock, new AlwaysLong(), maxHolding: 3);
        await first.StartAsync(CancellationToken.None);
        await store.EnqueueCommandAsync(first.SessionId, PaperCommandType.TripKillSwitch, "incident", "fer", Start, CancellationToken.None);
        await first.ApplyCommandsAsync(CancellationToken.None);

        var second = Engine(store, candleStore, clock, new AlwaysLong(), maxHolding: 3);   // the process restarts
        await second.StartAsync(CancellationToken.None);
        await Feed(second, candleStore, clock, candles.Take(400));

        Assert.True(second.Risk.KillSwitch.IsActive);
        Assert.Empty(store.Trades);
        Assert.All(store.Decisions.Where(d => d.Direction == SignalDirection.Long), d => Assert.Equal("RISK_KILL_SWITCH", d.RiskCode));
    }

    [Fact]
    public async Task Every_order_goes_through_the_full_lifecycle_and_brackets_cancel_each_other()
    {
        var (store, _) = await Paper(Series(3 * 288), new EmaTrendBaseline(), maxHolding: 48);

        var orders = store.AllOrders;
        Assert.All(orders.Where(o => o.Status == OrderStatus.Filled), o => Assert.Equal(
            [OrderStatus.Created, OrderStatus.Submitted, OrderStatus.Acknowledged, OrderStatus.Filled], o.Events.Select(e => e.Status)));
        foreach (var group in orders.Where(o => o.OcoGroup is not null).GroupBy(o => o.OcoGroup))
        {
            Assert.True(group.Count(o => o.Status == OrderStatus.Filled) <= 1, "both protective orders of a bracket filled");
        }

        Assert.Contains(orders, o => o.Status == OrderStatus.Canceled);
    }

    [Fact]
    public async Task The_journal_records_every_candle_with_its_decision()
    {
        var candles = Series(2 * 288);

        var (store, _) = await Paper(candles, new EmaTrendBaseline(), maxHolding: 48);

        Assert.Equal(candles.Count, store.Decisions.Count);
        Assert.Equal(candles.Select(c => c.OpenTimeUtc), store.Decisions.Select(d => d.CandleOpenTimeUtc));
        Assert.Contains(store.Decisions, d => d.Direction == SignalDirection.NoTrade && d.NoTradeReason == NoTradeReason.FeaturesUnavailable);
        Assert.Contains(store.Decisions, d => d.Action == "ENTRY_SCHEDULED");
    }

    private static void AssertSameTrades(BacktestResult backtest, IReadOnlyList<PaperTrade> paper)
    {
        var expected = backtest.Trades.Where(t => t.ExitReason != ExitReason.EndOfData).ToList();
        if (Environment.GetEnvironmentVariable("OMEGA_DEBUG_PARITY") == "1")
        {
            for (var k = 0; k < Math.Max(expected.Count, paper.Count) && k < 6; k++)
            {
                var b = k < expected.Count ? expected[k] : null;
                var q = k < paper.Count ? paper[k] : null;
                Console.WriteLine($"#{k} BT: {b?.EntryCandleOpenTimeUtc:MM-dd HH:mm} q={b?.Quantity:0.####} in={b?.EntryPrice:0.####} out={b?.ExitCandleOpenTimeUtc:MM-dd HH:mm} {b?.ExitReason} h={b?.HoldingCandles}");
                Console.WriteLine($"#{k} PP: {q?.EntryCandleOpenTimeUtc:MM-dd HH:mm} q={q?.Quantity:0.####} in={q?.EntryPrice:0.####} out={q?.ExitCandleOpenTimeUtc:MM-dd HH:mm} {q?.ExitReason} h={q?.HoldingCandles}");
            }
        }

        Assert.Equal(expected.Count, paper.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var b = expected[i];
            var p = paper[i];
            Assert.Equal(
                (b.EntryCandleOpenTimeUtc, b.EntryPrice, b.Quantity, b.EntryFee, b.StopLossPrice, b.TakeProfitPrice, b.ExitCandleOpenTimeUtc, b.ExitPrice, b.ExitFee, b.ExitReason, b.HoldingCandles, b.NetPnl),
                (p.EntryCandleOpenTimeUtc, p.EntryPrice, p.Quantity, p.EntryFee, p.StopLossPrice, p.TakeProfitPrice, p.ExitCandleOpenTimeUtc, p.ExitPrice, p.ExitFee, p.ExitReason, p.HoldingCandles, p.NetPnl));
        }
    }

    private static BacktestResult Backtest(IReadOnlyList<Candle> candles, IStrategy strategy, int maxHolding) =>
        new BacktestEngine(Features, new BacktestConfig { FeeRate = Costs.FeeRate, SpreadBps = Costs.SpreadBps, SlippageBps = Costs.SlippageBps, MaxHoldingCandles = maxHolding })
            .Run(candles, strategy).Value;

    private static async Task<(InMemoryPaperTradingStore Store, PaperTradingEngine Engine)> Paper(IReadOnlyList<Candle> candles, IStrategy strategy, int maxHolding)
    {
        var store = new InMemoryPaperTradingStore();
        var candleStore = new InMemoryCandleStore();
        var clock = new ManualClock(Start);
        var engine = Engine(store, candleStore, clock, strategy, maxHolding);
        Assert.True((await engine.StartAsync(CancellationToken.None)).IsSuccess);
        await Feed(engine, candleStore, clock, candles);
        return (store, engine);
    }

    /// <summary>Candles arrive one at a time; each is processed a second after it closes (fresh).</summary>
    private static async Task Feed(PaperTradingEngine engine, InMemoryCandleStore candleStore, ManualClock clock, IEnumerable<Candle> candles)
    {
        foreach (var candle in candles)
        {
            candleStore.Seed(candle);
            clock.Now = candle.CloseTimeUtc.AddSeconds(1);
            await engine.ProcessNewCandlesAsync(CancellationToken.None);
        }
    }

    private static PaperTradingEngine Engine(
        InMemoryPaperTradingStore store, InMemoryCandleStore candles, ManualClock clock, IStrategy strategy, int maxHolding, RiskLimits? risk = null) =>
        new(store, candles, Features, strategy, new PaperSessionConfig
        {
            Name = "test",
            Symbol = "BTCUSDT",
            Interval = CandleInterval.FiveMinutes,
            Strategy = strategy.Identity,
            InitialCapital = 10_000m,
            Costs = Costs,
            Risk = risk ?? RiskLimits.Default,
            MaxHoldingCandles = maxHolding,
            StartFromUtc = Start,
        }, clock, NullLogger.Instance);

    private static List<Candle> Series(int count)
    {
        var candles = new List<Candle>(count);
        var close = 100m;
        for (var i = 0; i < count; i++)
        {
            var open = i % 41 == 0 ? close + (i % 2 == 0 ? 0.6m : -0.6m) : close;   // occasional gaps between candles
            close = Math.Round(100m + (decimal)((5 * Math.Sin(i / 40.0)) + (0.4 * Math.Sin(i * 1.3))), 2);
            var time = Start.AddMinutes(5 * i);
            candles.Add(Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, time, time.AddMinutes(5).AddMilliseconds(-1),
                open, Math.Max(open, close) + 0.25m, Math.Min(open, close) - 0.25m, close, 10m + (i % 3), 1000m, 10).Value);
        }

        return candles;
    }

    /// <summary>Long every 37th candle when flat; exits by signal every 29th candle while in a position.</summary>
    private sealed class Scripted : IStrategy
    {
        public StrategyIdentity Identity { get; } = new("scripted", "1", new Dictionary<string, string>());

        public Signal Evaluate(StrategyContext context)
        {
            var index = (int)((context.Candle.OpenTimeUtc - Start).TotalMinutes / 5);
            if (context.Position is not null)
            {
                return index % 29 == 0 ? Signal.Short("scripted exit") : Signal.Hold();
            }

            var close = context.Candle.Close;
            return index % 37 == 0 || index == 540 ? Signal.Long(close - 1.5m, close + 2m, "scripted entry") : Signal.Hold();
        }
    }

    private sealed class AlwaysLong : IStrategy
    {
        public StrategyIdentity Identity { get; } = new("always-long", "1", new Dictionary<string, string>());

        public Signal Evaluate(StrategyContext context) =>
            context.Position is null ? Signal.Long(context.Candle.Close - 3m, context.Candle.Close + 3m) : Signal.Hold();
    }
}
