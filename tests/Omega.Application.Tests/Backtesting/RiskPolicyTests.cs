using Omega.Application.Backtesting;
using Omega.Application.Tests.TestSupport;
using Omega.Core.MarketData;
using Omega.Features;
using Omega.Risk;

namespace Omega.Application.Tests.Backtesting;

public class RiskPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly InMemoryCandleStore _candles = new();
    private readonly InMemoryBacktestRunStore _runs = new();

    public RiskPolicyTests()
    {
        var first = Start.AddDays(-2);
        for (var i = 0; i < 4 * 288; i++)
        {
            var open = first.AddMinutes(5 * i);
            var close = 100m + (decimal)Math.Round(5 * Math.Sin(i / 40.0) + (0.3 * Math.Sin(i * 1.3)), 2);
            _candles.Seed(Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
                close, close + 0.2m, close - 0.2m, close, 10m + (i % 3), 1000m, 10).Value);   // varying volume: volume_zscore_20 is defined
        }
    }

    [Fact]
    public async Task Runs_use_the_configured_policy_and_record_it()
    {
        var policy = new RiskLimits { RiskPerTrade = 0.005m, MaxDailyLoss = 0.02m };

        var run = (await Service(policy).RunAsync(Request("baseline-ema-trend"), CancellationToken.None)).Value.Run;

        Assert.Equal(policy, run.Result.Risk);
    }

    [Fact]
    public async Task Per_run_overrides_change_only_the_given_limits()
    {
        var request = Request("baseline-ema-trend") with { Risk = new RiskOverrides(MaxDrawdown: 0.05m, MaxSpreadBps: 2m) };

        var risk = (await Service(RiskLimits.Default).RunAsync(request, CancellationToken.None)).Value.Run.Result.Risk!;

        Assert.Equal(RiskLimits.Default with { MaxDrawdown = 0.05m, MaxSpreadBps = 2m }, risk);
    }

    [Fact]
    public async Task Invalid_overrides_are_refused_before_running()
    {
        var request = Request("baseline-ema-trend") with { Risk = new RiskOverrides(MaxPositionFraction: 3m) };

        var result = await Service(RiskLimits.Default).RunAsync(request, CancellationToken.None);

        Assert.Equal(BacktestServiceErrors.InvalidRiskLimits, result.Error!.Code);
        Assert.Empty(_runs.Runs);
    }

    [Fact]
    public async Task Buy_and_hold_invests_all_capital_whatever_the_risk_per_trade_of_the_policy()
    {
        var run = (await Service(new RiskLimits { RiskPerTrade = 0.005m }).RunAsync(Request("buy-and-hold"), CancellationToken.None)).Value.Run;

        Assert.Equal(1m, run.Result.Risk!.RiskPerTrade);
        Assert.True(Assert.Single(run.Result.Trades).Quantity * run.Result.Trades[0].EntryPrice > 9_900m);
    }

    [Fact]
    public async Task A_tripped_kill_switch_is_reported_in_the_notices()
    {
        // Spread of 0 bps accepted but a drawdown limit of 0.1 % trips at the first small loss.
        var request = Request("baseline-ema-trend") with { Risk = new RiskOverrides(MaxDrawdown: 0.001m, MaxDailyLoss: 0.5m) };

        var outcome = (await Service(RiskLimits.Default).RunAsync(request, CancellationToken.None)).Value;

        Assert.NotNull(outcome.Run.Result.RiskSummary!.KillSwitchTrippedAtUtc);
        Assert.Contains(outcome.Notices, n => n.Contains("kill switch tripped", StringComparison.Ordinal));
    }

    private BacktestService Service(RiskLimits policy) =>
        new(_candles, _runs, new FeatureEngine(FeatureSets.V1()), TimeProvider.System, modelsDirectory: null, riskPolicy: policy);

    private static BacktestRequest Request(string strategy) =>
        new(strategy, "BTCUSDT", CandleInterval.FiveMinutes, Start, Start.AddDays(2), "development");
}
