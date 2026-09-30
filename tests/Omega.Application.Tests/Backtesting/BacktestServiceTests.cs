using Omega.Application.Backtesting;
using Omega.Application.Tests.TestSupport;
using Omega.Backtesting;
using Omega.Core.MarketData;
using Omega.Features;

namespace Omega.Application.Tests.Backtesting;

public class BacktestServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly InMemoryCandleStore _candles = new();
    private readonly InMemoryBacktestRunStore _runs = new();

    [Fact]
    public async Task Unknown_strategy_and_invalid_periods_are_rejected()
    {
        var service = Service();

        Assert.Equal(BacktestServiceErrors.UnknownStrategy, (await service.RunAsync(Request("nope"), CancellationToken.None)).Error!.Code);
        Assert.Equal(BacktestServiceErrors.InvalidPeriod,
            (await service.RunAsync(Request(to: Start), CancellationToken.None)).Error!.Code);
        Assert.Equal(BacktestServiceErrors.InvalidPeriod,
            (await service.RunAsync(Request(from: Start.AddMinutes(1)), CancellationToken.None)).Error!.Code);
        Assert.Equal(BacktestServiceErrors.InvalidPeriod,
            (await service.RunAsync(Request(to: Start.AddDays(3 * 366 + 1)), CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task Missing_data_is_reported()
    {
        var result = await Service().RunAsync(Request(), CancellationToken.None);

        Assert.Equal(BacktestServiceErrors.NotEnoughData, result.Error!.Code);
    }

    [Fact]
    public async Task Warm_up_is_loaded_before_the_period_and_trading_starts_at_its_first_candle()
    {
        SeedDays(-2, 4);   // two days of warm-up history, then the two-day period

        var run = (await Service().RunAsync(Request(to: Start.AddDays(2)), CancellationToken.None)).Value.Run;

        Assert.Equal(Start.AddMinutes(5).AddMilliseconds(-1), run.Result.EquityCurve[0].TimeUtc);
        Assert.All(run.Result.Trades, trade => Assert.True(trade.EntryCandleOpenTimeUtc >= Start));
        // Only the warm-up the features need (MaxLookback − 1 = 249 candles) is loaded, not all the stored history.
        Assert.Equal(Start.AddMinutes(-5 * 249), run.Result.Dataset.FirstOpenTimeUtc);
    }

    [Fact]
    public async Task Every_run_is_stored_with_a_daily_equity_curve_and_its_evaluation_count()
    {
        SeedDays(-2, 5);
        var service = Service();

        var first = (await service.RunAsync(Request(to: Start.AddDays(3), label: "holdout"), CancellationToken.None)).Value;
        var second = (await service.RunAsync(Request(to: Start.AddDays(3), label: "holdout"), CancellationToken.None)).Value;

        Assert.Equal(0, first.PreviousEvaluationsOfThisPeriod);
        Assert.Equal(1, second.PreviousEvaluationsOfThisPeriod);
        Assert.Equal(2, _runs.Runs.Count);
        Assert.Equal("holdout", _runs.Runs[0].PeriodLabel);
        Assert.Equal(4, first.Run.Result.EquityCurve.Count);   // first point + the last point of each of the 3 days
    }

    [Fact]
    public async Task Cost_overrides_are_applied_and_recorded()
    {
        SeedDays(-2, 3);

        var run = (await Service().RunAsync(Request(to: Start.AddDays(1)) with { FeeRate = 0.002m, SlippageBps = 10m }, CancellationToken.None)).Value.Run;

        Assert.Equal(0.002m, run.Result.Config.FeeRate);
        Assert.Equal(10m, run.Result.Config.SlippageBps);
        Assert.Equal(48, run.Result.Config.MaxHoldingCandles);
    }

    [Fact]
    public void Daily_curve_keeps_the_first_point_and_each_days_last_point()
    {
        var curve = Enumerable.Range(0, 600).Select(i => new EquityPoint(Start.AddMinutes(5 * i + 5).AddMilliseconds(-1), 10_000m + i, 0m)).ToList();

        var daily = BacktestService.DailyCurve(curve);

        Assert.Equal(curve[0], daily[0]);
        Assert.Equal(curve[^1], daily[^1]);
        Assert.Equal(4, daily.Count);   // 600 candles span three UTC days
    }

    private BacktestService Service() => new(_candles, _runs, new FeatureEngine(FeatureSets.V1()), TimeProvider.System);

    private static BacktestRequest Request(string strategy = "baseline-ema-trend", DateTimeOffset? from = null, DateTimeOffset? to = null, string label = "development") =>
        new(strategy, "BTCUSDT", CandleInterval.FiveMinutes, from ?? Start, to ?? Start.AddDays(1), label);

    /// <summary>Deterministic wavy prices from <paramref name="fromDay"/> for <paramref name="days"/> days relative to Start.</summary>
    private void SeedDays(int fromDay, int days)
    {
        var first = Start.AddDays(fromDay);
        for (var i = 0; i < days * 288; i++)
        {
            var open = first.AddMinutes(5 * i);
            var close = 100m + (decimal)Math.Round(5 * Math.Sin(i / 40.0) + (0.3 * Math.Sin(i * 1.3)), 2);
            _candles.Seed(Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
                close, close + 0.2m, close - 0.2m, close, 10m + (i % 3), 1000m, 10).Value);
        }
    }
}

internal sealed class InMemoryBacktestRunStore : IBacktestRunStore
{
    public List<BacktestRun> Runs { get; } = [];

    public Task SaveAsync(BacktestRun run, CancellationToken cancellationToken)
    {
        Runs.Add(run);
        return Task.CompletedTask;
    }

    public Task<BacktestRun?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Runs.FirstOrDefault(r => r.Id == id));

    public Task<IReadOnlyList<BacktestRunSummary>> ListAsync(int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<int> CountEvaluationsAsync(
        string strategyName, string symbol, CandleInterval interval, DateTimeOffset tradingStartUtc, DateTimeOffset tradingEndUtc, CancellationToken cancellationToken) =>
        Task.FromResult(Runs.Count(r => r.Result.Strategy.Name == strategyName && r.Result.Dataset.Symbol == symbol
            && r.TradingStartUtc == tradingStartUtc && r.TradingEndUtc == tradingEndUtc));
}
