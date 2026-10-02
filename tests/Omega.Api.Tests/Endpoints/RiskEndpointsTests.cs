using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Contracts;
using Omega.Api.Endpoints;
using Omega.Application.Backtesting;
using Omega.Core.MarketData;
using Omega.Features;
using Omega.Risk;

namespace Omega.Api.Tests.Endpoints;

public class RiskEndpointsTests
{
    [Fact]
    public void The_configured_risk_policy_is_exposed()
    {
        var policy = new RiskLimits { MaxDailyLoss = 0.02m };

        Assert.Equal(policy, RiskEndpoints.GetLimits(policy).Value);
    }

    [Fact]
    public async Task Invalid_risk_overrides_are_a_validation_problem()
    {
        var service = new BacktestService(new EmptyStore(), new NoRuns(), new FeatureEngine(FeatureSets.V1()), TimeProvider.System);
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var request = new BacktestRunRequest("baseline-ema-trend", "BTCUSDT", "5m", start, start.AddDays(1), "development", null, null, null,
            Risk: new RiskOverrides(RiskPerTrade: 0m));

        var result = await BacktestEndpoints.RunAsync(request, service, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblem>(result.Result);
        Assert.Contains(BacktestServiceErrors.InvalidRiskLimits, problem.ProblemDetails.Errors.Keys);
    }

    private sealed class EmptyStore : ICandleStore
    {
        public Task<IReadOnlyList<Candle>> GetRangeAsync(string s, CandleInterval i, DateTimeOffset f, DateTimeOffset t, CancellationToken c) =>
            Task.FromResult<IReadOnlyList<Candle>>([]);

        public Task<Candle?> GetEarliestAsync(string s, CandleInterval i, CancellationToken c) => throw new NotSupportedException();

        public Task<Candle?> GetLatestAsync(string s, CandleInterval i, CancellationToken c) => throw new NotSupportedException();

        public Task<IReadOnlyList<CandleGap>> FindGapsAsync(string s, CandleInterval i, DateTimeOffset f, DateTimeOffset t, CancellationToken c) =>
            throw new NotSupportedException();

        public Task<CandleSaveOutcome> SaveAsync(Candle candle, string source, DateTimeOffset observedAtUtc, CancellationToken c) =>
            throw new NotSupportedException();
    }

    private sealed class NoRuns : Omega.Backtesting.IBacktestRunStore
    {
        public Task SaveAsync(Omega.Backtesting.BacktestRun run, CancellationToken c) => Task.CompletedTask;

        public Task<Omega.Backtesting.BacktestRun?> GetAsync(Guid id, CancellationToken c) => Task.FromResult<Omega.Backtesting.BacktestRun?>(null);

        public Task<IReadOnlyList<Omega.Backtesting.BacktestRunSummary>> ListAsync(int limit, CancellationToken c) =>
            Task.FromResult<IReadOnlyList<Omega.Backtesting.BacktestRunSummary>>([]);

        public Task<int> CountEvaluationsAsync(string a, string b, CandleInterval i, DateTimeOffset f, DateTimeOffset t, CancellationToken c) => Task.FromResult(0);
    }
}
