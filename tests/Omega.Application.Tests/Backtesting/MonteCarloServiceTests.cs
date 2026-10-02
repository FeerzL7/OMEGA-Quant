using Omega.Application.Backtesting;
using Omega.Application.Tests.TestSupport;
using Omega.Backtesting.MonteCarlo;
using Omega.Core.MarketData;
using Omega.Features;
using Omega.Risk;

namespace Omega.Application.Tests.Backtesting;

public class MonteCarloServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly InMemoryCandleStore _candles = new();
    private readonly InMemoryBacktestRunStore _runs = new();

    public MonteCarloServiceTests()
    {
        var first = Start.AddDays(-2);
        for (var i = 0; i < 5 * 288; i++)
        {
            var open = first.AddMinutes(5 * i);
            var close = 100m + (decimal)Math.Round(5 * Math.Sin(i / 40.0) + (0.3 * Math.Sin(i * 1.3)), 2);
            _candles.Seed(Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
                close, close + 0.2m, close - 0.2m, close, 10m + (i % 3), 1000m, 10).Value);
        }
    }

    [Fact]
    public async Task Scenarios_are_built_from_a_stored_backtest_and_say_what_they_are()
    {
        var run = await StoredRun(new RiskLimits { MaxDrawdown = 0.12m });

        var outcome = (await new MonteCarloService(_runs).RunAsync(run.Id, new MonteCarloOptions { Paths = 500 }, CancellationToken.None)).Value;

        Assert.Equal(run.Id, outcome.RunId);
        Assert.Equal(run.Result.Trades.Count, outcome.Result.SourceTrades);
        Assert.Equal(0.12, outcome.Result.KillSwitchDrawdownLimit);
        Assert.Equal((double)run.Result.Metrics.FinalEquity, outcome.Result.Original.TerminalEquity, 6);
        Assert.Contains(outcome.Notices, n => n.Contains("not a forecast", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_runs_and_invalid_options_are_reported()
    {
        var run = await StoredRun(RiskLimits.Default);
        var service = new MonteCarloService(_runs);

        Assert.Equal(MonteCarloServiceErrors.RunNotFound, (await service.RunAsync(Guid.NewGuid(), new MonteCarloOptions(), CancellationToken.None)).Error!.Code);
        Assert.Equal(MonteCarloErrors.InvalidOptions, (await service.RunAsync(run.Id, new MonteCarloOptions { RuinLevel = 2 }, CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task Few_source_trades_are_flagged()
    {
        var run = await StoredRun(RiskLimits.Default, days: 1);

        var outcome = (await new MonteCarloService(_runs).RunAsync(run.Id, new MonteCarloOptions { Paths = 200 }, CancellationToken.None)).Value;

        Assert.True(outcome.Result.SourceTrades < 30);
        Assert.Contains(outcome.Notices, n => n.Contains("source trades", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runs_stored_before_risk_policies_existed_get_no_kill_switch_probability_and_say_so()
    {
        var run = await StoredRun(RiskLimits.Default);
        var legacy = run with { Id = Guid.NewGuid(), Result = run.Result with { Risk = null! } };   // as deserialized from a Phase 6-9 run
        await _runs.SaveAsync(legacy, CancellationToken.None);

        var outcome = (await new MonteCarloService(_runs).RunAsync(legacy.Id, new MonteCarloOptions { Paths = 200 }, CancellationToken.None)).Value;

        Assert.Null(outcome.Result.ProbabilityKillSwitch);
        Assert.Contains(outcome.Notices, n => n.Contains("predates the recording of risk policies", StringComparison.Ordinal));
    }

    private async Task<Omega.Backtesting.BacktestRun> StoredRun(RiskLimits risk, int days = 3)
    {
        var service = new BacktestService(_candles, _runs, new FeatureEngine(FeatureSets.V1()), TimeProvider.System, riskPolicy: risk);
        var outcome = await service.RunAsync(new BacktestRequest("baseline-ema-trend", "BTCUSDT", CandleInterval.FiveMinutes, Start, Start.AddDays(days), "development"), CancellationToken.None);
        Assert.True(outcome.Value.Run.Result.Trades.Count > 0);
        return outcome.Value.Run;
    }
}
