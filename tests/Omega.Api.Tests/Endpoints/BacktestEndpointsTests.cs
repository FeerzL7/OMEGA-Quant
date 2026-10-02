using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Contracts;
using Omega.Api.Endpoints;
using Omega.Application.Backtesting;
using Omega.Backtesting;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Features;

namespace Omega.Api.Tests.Endpoints;

public class BacktestEndpointsTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Strategies_lists_the_baseline_and_the_benchmark()
    {
        var strategies = BacktestEndpoints.GetStrategies().Value!;

        Assert.Equal(["baseline-ema-trend", "buy-and-hold", "model-ev"], strategies.Select(s => s.Name));
        Assert.Equal(48, strategies[0].DefaultConfig.MaxHoldingCandles);
    }

    [Fact]
    public async Task Invalid_requests_are_rejected_before_running()
    {
        var request = new BacktestRunRequest(null, "btc", "7m", null, null, "Hold Out", null, null, null);

        var result = await BacktestEndpoints.RunAsync(request, Service(new CandleStore([]), new RunStore()), CancellationToken.None);

        var problem = Assert.IsType<ValidationProblem>(result.Result);
        Assert.Equal(["strategy", "symbol", "interval", "fromUtc", "toUtc", "periodLabel"], problem.ProblemDetails.Errors.Keys);
    }

    [Fact]
    public async Task Run_is_created_stored_and_warns_when_a_period_is_evaluated_again()
    {
        var runs = new RunStore();
        var service = Service(new CandleStore(Candles(-1, 2)), runs);
        var request = new BacktestRunRequest("baseline-ema-trend", "BTCUSDT", "5m", Start, Start.AddDays(1), "holdout", null, null, null);

        var first = Assert.IsType<Created<BacktestRunResponse>>((await BacktestEndpoints.RunAsync(request, service, CancellationToken.None)).Result);
        var second = Assert.IsType<Created<BacktestRunResponse>>((await BacktestEndpoints.RunAsync(request, service, CancellationToken.None)).Result);

        Assert.Equal($"/api/backtests/{first.Value!.Id}", first.Location);
        Assert.DoesNotContain(first.Value.Notices, n => n.Contains("already been evaluated", StringComparison.Ordinal));
        Assert.Contains(second.Value!.Notices, n => n.Contains("already been evaluated 1 time(s)", StringComparison.Ordinal));
        Assert.Equal(2, runs.Runs.Count);
    }

    [Fact]
    public async Task Repeated_development_runs_and_benchmarks_carry_no_misleading_notices()
    {
        var service = Service(new CandleStore(Candles(-1, 2)), new RunStore());
        var development = new BacktestRunRequest("baseline-ema-trend", "BTCUSDT", "5m", Start, Start.AddDays(1), "development", null, null, null);
        var benchmark = development with { Strategy = "buy-and-hold" };

        await BacktestEndpoints.RunAsync(development, service, CancellationToken.None);
        var again = Assert.IsType<Created<BacktestRunResponse>>((await BacktestEndpoints.RunAsync(development, service, CancellationToken.None)).Result);
        var buyAndHold = Assert.IsType<Created<BacktestRunResponse>>((await BacktestEndpoints.RunAsync(benchmark, service, CancellationToken.None)).Result);

        Assert.DoesNotContain(again.Value!.Notices, n => n.Contains("holdout", StringComparison.Ordinal));
        Assert.Empty(buyAndHold.Value!.Notices);
    }

    [Fact]
    public async Task A_cost_variant_on_the_holdout_still_counts_as_a_look_at_it()
    {
        var service = Service(new CandleStore(Candles(-1, 2)), new RunStore());
        var holdout = new BacktestRunRequest("baseline-ema-trend", "BTCUSDT", "5m", Start, Start.AddDays(1), "holdout", null, null, null);

        await BacktestEndpoints.RunAsync(holdout with { PeriodLabel = "holdout-cost-stress", FeeRate = 0.002m }, service, CancellationToken.None);
        var result = Assert.IsType<Created<BacktestRunResponse>>((await BacktestEndpoints.RunAsync(holdout, service, CancellationToken.None)).Result);

        Assert.Contains(result.Value!.Notices, n => n.Contains("already been evaluated 1 time(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_data_is_422_and_unknown_strategy_is_a_validation_problem()
    {
        var service = Service(new CandleStore([]), new RunStore());
        var noData = new BacktestRunRequest("baseline-ema-trend", "BTCUSDT", "5m", Start, Start.AddDays(1), "development", null, null, null);
        var unknown = noData with { Strategy = "magic" };

        var noDataResult = await BacktestEndpoints.RunAsync(noData, service, CancellationToken.None);
        var unknownResult = await BacktestEndpoints.RunAsync(unknown, service, CancellationToken.None);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsType<ProblemHttpResult>(noDataResult.Result).StatusCode);
        Assert.IsType<ValidationProblem>(unknownResult.Result);
    }

    [Fact]
    public async Task Stored_runs_can_be_listed_and_read_and_unknown_ids_are_404()
    {
        var runs = new RunStore();
        var service = Service(new CandleStore(Candles(-1, 2)), runs);
        var created = Assert.IsType<Created<BacktestRunResponse>>((await BacktestEndpoints.RunAsync(
            new BacktestRunRequest("buy-and-hold", "BTCUSDT", "5m", Start, Start.AddDays(1), "development", null, null, null), service, CancellationToken.None)).Result);

        var read = await BacktestEndpoints.GetAsync(created.Value!.Id, runs, CancellationToken.None);
        var missing = await BacktestEndpoints.GetAsync(Guid.NewGuid(), runs, CancellationToken.None);
        var badLimit = await BacktestEndpoints.ListAsync(runs, CancellationToken.None, limit: 0);

        Assert.Equal("buy-and-hold", Assert.IsType<Ok<BacktestRunResponse>>(read.Result).Value!.Result.Strategy.Name);
        Assert.IsType<NotFound>(missing.Result);
        Assert.IsType<ValidationProblem>(badLimit.Result);
    }

    [Fact]
    public async Task Unavailable_store_is_503()
    {
        var failing = new RunStore { Failure = new PersistenceException("down", isTransient: true) };

        var result = await BacktestEndpoints.GetAsync(Guid.NewGuid(), failing, CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ProblemHttpResult>(result.Result).StatusCode);
    }

    private static BacktestService Service(ICandleStore candles, IBacktestRunStore runs) =>
        new(candles, runs, new FeatureEngine(FeatureSets.V1()), TimeProvider.System);

    private static List<Candle> Candles(int fromDay, int days)
    {
        var first = Start.AddDays(fromDay);
        return [.. Enumerable.Range(0, days * 288).Select(i =>
        {
            var open = first.AddMinutes(5 * i);
            var close = 100m + (decimal)Math.Round(4 * Math.Sin(i / 30.0), 2);
            return Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
                close, close + 0.3m, close - 0.3m, close, 5m + (i % 4), 500m, 5).Value;
        })];
    }

    private sealed class CandleStore(IReadOnlyList<Candle> candles) : ICandleStore
    {
        public Task<IReadOnlyList<Candle>> GetRangeAsync(
            string symbol, CandleInterval interval, DateTimeOffset fromOpenTimeUtc, DateTimeOffset toOpenTimeUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Candle>>([.. candles.Where(c => c.OpenTimeUtc >= fromOpenTimeUtc && c.OpenTimeUtc < toOpenTimeUtc)]);

        public Task<Candle?> GetEarliestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Candle?> GetLatestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CandleGap>> FindGapsAsync(
            string symbol, CandleInterval interval, DateTimeOffset fromOpenTimeUtc, DateTimeOffset toOpenTimeUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CandleSaveOutcome> SaveAsync(Candle candle, string source, DateTimeOffset observedAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RunStore : IBacktestRunStore
    {
        public List<BacktestRun> Runs { get; } = [];

        public Exception? Failure { get; init; }

        public Task SaveAsync(BacktestRun run, CancellationToken cancellationToken)
        {
            Runs.Add(run);
            return Task.CompletedTask;
        }

        public Task<BacktestRun?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(Runs.FirstOrDefault(r => r.Id == id)) : Task.FromException<BacktestRun?>(Failure);

        public Task<IReadOnlyList<BacktestRunSummary>> ListAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BacktestRunSummary>>([]);

        public Task<int> CountEvaluationsAsync(
            string strategyName, string symbol, CandleInterval interval, DateTimeOffset tradingStartUtc, DateTimeOffset tradingEndUtc, CancellationToken cancellationToken) =>
            Task.FromResult(Runs.Count(r => r.Result.Strategy.Name == strategyName && r.TradingStartUtc == tradingStartUtc && r.TradingEndUtc == tradingEndUtc));
    }
}
