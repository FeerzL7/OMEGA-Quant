using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Omega.Backtesting;
using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Infrastructure.Persistence;
using Omega.Infrastructure.Persistence.Migrations;
using Omega.Strategy;

namespace Omega.Integration.Tests.Persistence;

public class PostgresBacktestRunStoreTests : DatabaseTest
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [DatabaseFact]
    public async Task Run_round_trips_with_its_complete_result()
    {
        var store = await CreateStoreAsync();
        var run = Run("development");

        await store.SaveAsync(run, CancellationToken.None);
        var loaded = await store.GetAsync(run.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal((run.Id, run.PeriodLabel, run.TradingStartUtc, run.TradingEndUtc), (loaded!.Id, loaded.PeriodLabel, loaded.TradingStartUtc, loaded.TradingEndUtc));
        Assert.Equal(Serialize(run.Result), Serialize(loaded.Result));
        Assert.Equal(7, loaded.Result.NoTradeCounts[NoTradeReason.FeaturesUnavailable]);
        Assert.Equal(0.02m, loaded.Result.Risk!.MaxDailyLoss);
        Assert.Equal(2, loaded.Result.RiskSummary!.RejectionsByCheck["RISK_DAILY_LOSS_LIMIT"]);
        Assert.Null(await store.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Runs_are_listed_newest_first_and_evaluations_are_counted_per_period()
    {
        var store = await CreateStoreAsync();
        await store.SaveAsync(Run("development", createdMinutes: 0), CancellationToken.None);
        await store.SaveAsync(Run("holdout", createdMinutes: 1), CancellationToken.None);
        await store.SaveAsync(Run("holdout", createdMinutes: 2), CancellationToken.None);

        var list = await store.ListAsync(10, CancellationToken.None);
        var count = await store.CountEvaluationsAsync("scripted", "BTCUSDT", CandleInterval.FiveMinutes, Start, Start.AddDays(1), CancellationToken.None);
        var otherPeriod = await store.CountEvaluationsAsync("scripted", "BTCUSDT", CandleInterval.FiveMinutes, Start, Start.AddDays(2), CancellationToken.None);

        Assert.Equal(["holdout", "holdout", "development"], list.Select(r => r.PeriodLabel));
        Assert.Equal(0.05, list[0].TotalReturn);
        Assert.Equal(3, count);
        Assert.Equal(0, otherPeriod);
    }

    private async Task<PostgresBacktestRunStore> CreateStoreAsync()
    {
        await new DatabaseMigrator(DataSource, NullLogger<DatabaseMigrator>.Instance)
            .EnsureUpToDateAsync(applyPending: true, CancellationToken.None);
        return new PostgresBacktestRunStore(DataSource);
    }

    private static string Serialize(BacktestResult result) => JsonSerializer.Serialize(result);

    private static BacktestRun Run(string label, int createdMinutes = 0)
    {
        var trade = new BacktestTrade(Start, 100.25m, 1.5m, 0.15m, 95m, 110m, Start.AddMinutes(30), 105.5m, 0.16m, ExitReason.TakeProfit, 6, 7.5m, "Uptrend.");
        var metrics = new BacktestMetrics(10_000m, 10_500m, 500m, 0.05, 1, 1, 1, 7.5m, 0m, null, 7.5m, -0.01, 1.2, null, 0.31m, 0.25);
        var result = new BacktestResult(
            new StrategyIdentity("scripted", "1", new Dictionary<string, string> { ["stopAtr"] = "2" }),
            "features-v1", "hash", new BacktestConfig { MaxHoldingCandles = 48 },
            new DatasetFingerprint("BTCUSDT", CandleInterval.FiveMinutes, Start, Start.AddDays(1), 288, "sha"),
            [trade], [new EquityPoint(Start.AddMinutes(5), 10_000m, 0m)], [new BacktestRejection(Start, "X", "Detail.")],
            new Dictionary<NoTradeReason, int> { [NoTradeReason.FeaturesUnavailable] = 7 }, ["A warning."], metrics,
            new TradeStatistics(1, 0.005, null, null, null, null),
            new Omega.Risk.RiskLimits { MaxDailyLoss = 0.02m },
            new RiskSummary(new Dictionary<string, int> { ["RISK_DAILY_LOSS_LIMIT"] = 2 }, Start.AddHours(3), "Maximum drawdown reached."));

        return new BacktestRun(Guid.NewGuid(), Start.AddMinutes(createdMinutes), label, Start, Start.AddDays(1), result);
    }
}
