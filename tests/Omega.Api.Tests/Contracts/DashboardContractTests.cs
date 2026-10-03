using System.Text.Json;
using Omega.Api.Contracts;
using Omega.Api.Endpoints;
using Omega.Application.Monitoring;
using Omega.Application.Paper;
using Omega.Backtesting;
using Omega.Backtesting.MonteCarlo;
using Omega.Core.Configuration;
using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Execution;
using Omega.Execution.Paper;
using Omega.Features;
using Omega.Risk;
using Omega.Strategy.Baseline;
using Omega.Application.Backtesting;

namespace Omega.Api.Tests.Contracts;

/// <summary>
/// The JSON the dashboard (Omega.UI) is tested against, produced from the API's real response types with the API's
/// real serializer options. If a response shape changes, this test fails until the samples are regenerated
/// (OMEGA_UPDATE_CONTRACTS=1), and the UI's contract tests then show whether the dashboard still reads them.
/// </summary>
public class DashboardContractTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Contract_samples_match_what_the_api_serializes()
    {
        var directory = Path.Combine(RepositoryRoot(), "tests", "Contracts");
        var update = Environment.GetEnvironmentVariable("OMEGA_UPDATE_CONTRACTS") == "1";
        var stale = new List<string>();
        var options = new JsonSerializerOptions(ApiJson.Options) { WriteIndented = true };

        foreach (var (name, sample) in Samples())
        {
            var path = Path.Combine(directory, name + ".json");
            var json = JsonSerializer.Serialize(sample, options).ReplaceLineEndings("\n") + "\n";
            if (update)
            {
                File.WriteAllText(path, json);
            }
            else if (!File.Exists(path) || File.ReadAllText(path).ReplaceLineEndings("\n") != json)
            {
                stale.Add(name);
            }
        }

        Assert.True(stale.Count == 0,
            $"API responses changed shape: {string.Join(", ", stale)}. Regenerate with OMEGA_UPDATE_CONTRACTS=1 and check the UI contract tests.");
    }

    internal static IEnumerable<(string Name, object Sample)> Samples()
    {
        yield return ("system_status", SystemStatusResponse.Create(new TradingOptions { Mode = TradingMode.Paper }, new FixedTime(T)));

        var candles = Candles(300);
        var last = candles[^1];
        yield return ("candles", candles.TakeLast(3).Select(CandleResponse.From).ToList());
        yield return ("market_state", new MarketStateResponse(
            "BTCUSDT", "5m", T, false, ["Data is stale."], "Stale", 420.5, T.AddMinutes(-2), CandleResponse.From(last), T.AddDays(-1), []));

        yield return ("system_health", new SystemHealth(T,
        [
            new ComponentHealth("database", ComponentState.Healthy, "Query succeeded.", "A query to the candle store."),
            new ComponentHealth("marketData", ComponentState.Degraded, "Stale.", "Freshness of stored candles."),
            new ComponentHealth("paperWorker", ComponentState.Down, "Last update 40 min ago.", "Session update age."),
            new ComponentHealth("model", ComponentState.NotAvailable, "No session uses a model.", "Model package files."),
        ]));

        yield return ("system_events", new List<SystemEventResponse>
        {
            new(T, "MarketData", "CANDLE_GAP_DETECTED", "Warning", "Gap of 3 candles detected.", new Dictionary<string, string> { ["missing"] = "3" }),
        });

        yield return ("risk_limits", RiskLimits.Default);

        yield return ("paper_session", new PaperSessionSummary(
            "paper-1", "BTCUSDT", "5m", "model-ev:logistic_regression-3d80f01a363a", T.AddDays(-2), T, T.AddMinutes(-5), 576, 0m, 10_250.5m, 10_400m,
            -0.0144m, 10_100m, 2, new PaperPositionView(T.AddMinutes(-30), 64_010.5m, 0.1562m, 63_400m, 64_900m, 6), false, false,
            new PaperKillSwitchView(true, "manual: exchange incident (by fer)", T.AddMinutes(-10), null)));

        yield return ("paper_decisions", new List<PaperDecision>
        {
            new(T.AddMinutes(-5), SignalDirection.Long, null, "P raw 0.61, calibrated 0.58; EV +0.0012.",
                new Dictionary<string, double> { ["raw_probability"] = 0.61, ["calibrated_probability"] = 0.58, ["expected_return"] = 0.0012, ["gain"] = 0.008, ["loss"] = -0.004, ["costs"] = 0.0026 },
                "RISK_REJECTED", "RISK_KILL_SWITCH", "Kill switch active.", 10_250.5m, ["STALE: processed late."]),
            new(T.AddMinutes(-10), SignalDirection.NoTrade, NoTradeReason.ExpectedValueTooLow, null, new Dictionary<string, double>(), "NO_TRADE", null, null, 10_240m, []),
        });

        yield return ("paper_trades", new List<PaperTrade>
        {
            new(T.AddHours(-3), 64_000m, 0.15m, 9.6m, 63_500m, 64_800m, T.AddHours(-2), 64_800m, 9.72m, ExitReason.TakeProfit, 12, 100.68m, "Uptrend."),
        });

        yield return ("paper_orders", Orders().Select(o => new PaperOrderView(o, o.Events)).ToList());

        yield return ("paper_commands", new List<PaperCommand>
        {
            new(7, Guid.Parse("11111111-2222-3333-4444-555555555555"), PaperCommandType.TripKillSwitch, "exchange incident", "fer", T.AddMinutes(-11), T.AddMinutes(-10), "Kill switch tripped."),
        });

        yield return ("kill_switch_accepted", new KillSwitchAccepted(8, "paper-1", "reset", "Queued: the paper worker applies it within its poll interval."));

        var runId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        yield return ("backtest_summaries", new List<BacktestRunSummary>
        {
            new(runId, T, "holdout", "baseline-ema-trend", "1", "BTCUSDT", CandleInterval.FiveMinutes, T.AddDays(-7), T, "abc123", 22, 0.0135),
        });

        var result = new BacktestEngine(new FeatureEngine(FeatureSets.V1()), new BacktestConfig { MaxHoldingCandles = 48 })
            .Run(candles, new EmaTrendBaseline()).Value;
        var run = new BacktestRun(runId, T, "development", candles[0].OpenTimeUtc, last.OpenTimeUtc.AddMinutes(5), result with { EquityCurve = [.. result.EquityCurve.Where((_, i) => i % 50 == 0)] });
        yield return ("backtest_run", BacktestRunResponse.From(run, ["Only 3 trade(s) (fewer than 30): the statistics cannot distinguish skill from chance."]));

        var monteCarlo = MonteCarloSimulator.Run(
            [new TradeOutcome(0.01, 1), new TradeOutcome(-0.006, 1), new TradeOutcome(0.004, 1)], 10_000, new MonteCarloOptions { Paths = 200 }, 0.2).Value;
        yield return ("monte_carlo", new MonteCarloOutcome(runId, "baseline-ema-trend", "development", 0.031, monteCarlo, ["These scenarios recombine the 3 trades of backtest ...; they are not a forecast."]));
    }

    /// <summary>An entry and its bracket, with fixed ids (samples must be reproducible), through their real lifecycle.</summary>
    private static List<Order> Orders()
    {
        var group = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var entry = new Order(Guid.Parse("00000000-0000-0000-0000-000000000001"), "omega-paper-00000001", OrderSide.Buy, OrderType.Market, 0.15m, null, null, null, null, T);
        var stop = new Order(Guid.Parse("00000000-0000-0000-0000-000000000002"), "omega-paper-00000002", OrderSide.Sell, OrderType.StopMarket, 0.15m, 63_500m, null, entry.Id, group, T);
        var target = new Order(Guid.Parse("00000000-0000-0000-0000-000000000003"), "omega-paper-00000003", OrderSide.Sell, OrderType.Limit, 0.15m, null, 64_800m, entry.Id, group, T);
        foreach (var order in new[] { entry, stop, target })
        {
            order.Transition(OrderStatus.Submitted, T);
            order.Transition(OrderStatus.Acknowledged, T);
        }

        entry.Fill(0.15m, 64_016m, 9.6024m, T);
        target.Fill(0.15m, 64_800m, 9.72m, T.AddHours(1));
        stop.Transition(OrderStatus.Canceled, T.AddHours(1), "One-cancels-the-other: sibling filled.");
        return [entry, stop, target];
    }

    private static List<Candle> Candles(int count) =>
        [.. Enumerable.Range(0, count).Select(i =>
        {
            var open = T.AddMinutes(5 * (i - count));
            var close = 64_000m + (decimal)Math.Round(800 * Math.Sin(i / 30.0) + (60 * Math.Sin(i * 1.3)), 2);
            return Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
                close - 15m, close + 40m, close - 40m, close, 10m + (i % 4), 640_000m, 900 + i).Value;
        })];

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OMEGA.sln"))) return directory.FullName;
        }

        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OMEGA.sln"))) return directory.FullName;
        }

        throw new InvalidOperationException("OMEGA.sln not found above the test directory.");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
