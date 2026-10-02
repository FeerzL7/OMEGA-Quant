using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Contracts;
using Omega.Api.Endpoints;
using Omega.Application.Backtesting;
using Omega.Backtesting;
using Omega.Core.MarketData;

namespace Omega.Api.Tests.Endpoints;

public class MonteCarloEndpointsTests
{
    [Fact]
    public async Task Unknown_method_is_a_validation_problem_and_unknown_run_is_404()
    {
        var service = new MonteCarloService(new NoRuns());

        var badMethod = await BacktestEndpoints.MonteCarloAsync(Guid.NewGuid(), new MonteCarloRequest(Method: "prophecy"), service, CancellationToken.None);
        var missing = await BacktestEndpoints.MonteCarloAsync(Guid.NewGuid(), null, service, CancellationToken.None);

        Assert.IsType<ValidationProblem>(badMethod.Result);
        Assert.IsType<NotFound>(missing.Result);
    }

    private sealed class NoRuns : IBacktestRunStore
    {
        public Task SaveAsync(BacktestRun run, CancellationToken c) => Task.CompletedTask;

        public Task<BacktestRun?> GetAsync(Guid id, CancellationToken c) => Task.FromResult<BacktestRun?>(null);

        public Task<IReadOnlyList<BacktestRunSummary>> ListAsync(int limit, CancellationToken c) => Task.FromResult<IReadOnlyList<BacktestRunSummary>>([]);

        public Task<int> CountEvaluationsAsync(string a, string b, CandleInterval i, DateTimeOffset f, DateTimeOffset t, CancellationToken c) => Task.FromResult(0);
    }
}
