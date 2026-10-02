using Omega.Backtesting.Tests.TestSupport;
using Omega.Core.Trading;
using Omega.Strategy.Baseline;
using static Omega.Backtesting.Tests.TestSupport.Bars;

namespace Omega.Backtesting.Tests;

public class TradingStartAndStatisticsTests
{
    [Fact]
    public void Candles_before_the_trading_start_only_warm_up()
    {
        var candles = Flats(100m, 100m, 100m, 101m, 102m, 103m);
        var strategy = new ScriptedStrategy(new() { [0] = Signal.Long(90m), [2] = Signal.Long(90m) });

        var result = Engines.Create().Run(candles, strategy, tradingStartUtc: OpenTime(3)).Value;

        // The signal at candle 0 is ignored; the one at the close of candle 2 (last warm-up candle) enters at the open of 3.
        var trade = Assert.Single(result.Trades);
        Assert.Equal(OpenTime(3), trade.EntryCandleOpenTimeUtc);
        Assert.Equal(3, result.EquityCurve.Count);
        Assert.Equal(OpenTime(4).AddMilliseconds(-1), result.EquityCurve[0].TimeUtc);
    }

    [Fact]
    public void Buy_and_hold_matches_the_closed_form_result_after_costs()
    {
        var config = new BacktestConfig();   // default costs: 0.10 %, 1 bp, 2 bp
        var candles = Flats(100m, 100m, 104m, 120m);

        var result = Engines.Create(config, risk: new Omega.Risk.RiskLimits { RiskPerTrade = 1m }).Run(candles, new BuyAndHold()).Value;

        var entry = 100m * (1m + 0.00005m + 0.0002m);
        var quantity = 10_000m / (entry * 1.001m);                // capped by capital including the fee
        var exit = 120m * (1m - 0.00005m - 0.0002m);
        var expected = 10_000m - (quantity * entry) - (quantity * entry * 0.001m) + (quantity * exit) - (quantity * exit * 0.001m);
        Assert.Equal(expected, result.Metrics.FinalEquity);
        Assert.Equal(ExitReason.EndOfData, Assert.Single(result.Trades).ExitReason);
    }

    [Fact]
    public void Trade_statistics_measure_whether_the_mean_trade_differs_from_zero()
    {
        var trades = new[] { Trade(0.01m), Trade(-0.005m), Trade(0.02m), Trade(0.004m) };

        var stats = TradeStatistics.From(trades);

        var returns = trades.Select(t => (double)t.ReturnOnCost).ToArray();
        var mean = returns.Average();
        var std = Math.Sqrt(returns.Sum(r => (r - mean) * (r - mean)) / 3);
        Assert.Equal(mean, stats.MeanReturn!.Value, 12);
        Assert.Equal(std, stats.StandardDeviation!.Value, 12);
        Assert.Equal(mean / (std / 2), stats.TStatistic!.Value, 12);
        Assert.InRange(mean, stats.BootstrapCi95Lower!.Value, stats.BootstrapCi95Upper!.Value);
        Assert.Equal(stats, TradeStatistics.From(trades)); // fixed seed: reproducible
    }

    [Fact]
    public void Statistics_need_at_least_two_trades()
    {
        Assert.Null(TradeStatistics.From([]).MeanReturn);
        Assert.Null(TradeStatistics.From([Trade(0.01m)]).TStatistic);
    }

    /// <summary>A trade whose return on cost is exactly <paramref name="returnOnCost"/> (cost 1 000).</summary>
    private static BacktestTrade Trade(decimal returnOnCost) => new(
        OpenTime(0), 100m, 10m, 0m, 90m, null, OpenTime(1), 100m, 0m, ExitReason.Signal, 1, returnOnCost * 1_000m, null);
}
