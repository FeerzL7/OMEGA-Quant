using System.Reflection;
using System.Text.Json;
using Omega.UI.Api;

namespace Omega.UI.Tests;

/// <summary>The dashboard reads the JSON the API actually sends (samples generated from the API's own types).</summary>
public class ContractTests
{
    [Fact]
    public void Every_contract_sample_is_read_by_the_dashboard()
    {
        Assert.Equal(15, Assembly.GetExecutingAssembly().GetManifestResourceNames().Count(n => n.StartsWith("contracts/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Market_and_system()
    {
        var status = Read<SystemStatusDto>("system_status");
        var market = Read<MarketStateDto>("market_state");
        var candles = Read<List<CandleDto>>("candles");
        var health = Read<SystemHealthDto>("system_health");
        var events = Read<List<SystemEventDto>>("system_events");

        Assert.Equal("PAPER", status.TradingMode);
        Assert.Equal(("BTCUSDT", false, "Stale"), (market.Symbol, market.IsReliable, market.Freshness));
        Assert.NotNull(market.LastClosedCandle);
        Assert.Equal(3, candles.Count);
        Assert.True(candles[0].High >= candles[0].Low && candles[0].Close > 0);
        Assert.Equal(["Healthy", "Degraded", "Down", "NotAvailable"], health.Components.Select(c => c.State));
        Assert.All(health.Components, c => Assert.False(string.IsNullOrEmpty(c.Basis)));
        Assert.Equal("Warning", Assert.Single(events).Severity);
    }

    [Fact]
    public void Paper_trading()
    {
        var session = Read<PaperSessionDto>("paper_session");
        var decisions = Read<List<PaperDecisionDto>>("paper_decisions");
        var trades = Read<List<PaperTradeDto>>("paper_trades");
        var orders = Read<List<PaperOrderViewDto>>("paper_orders");
        var commands = Read<List<PaperCommandDto>>("paper_commands");
        var accepted = Read<KillSwitchAcceptedDto>("kill_switch_accepted");

        Assert.Equal(("paper-1", 10_250.5m, true), (session.Name, session.LastEquity, session.KillSwitch.Active));
        Assert.Equal(63_400m, session.Position!.StopLoss);
        Assert.Equal(("Long", "RISK_REJECTED", "RISK_KILL_SWITCH"), (decisions[0].Direction, decisions[0].Action, decisions[0].RiskCode));
        Assert.Equal(0.58, decisions[0].Metrics["calibrated_probability"]);
        Assert.Equal("ExpectedValueTooLow", decisions[1].NoTradeReason);
        Assert.Equal(("TakeProfit", 100.68m), (trades[0].ExitReason, trades[0].NetPnl));
        Assert.Equal(["Filled", "Canceled", "Filled"], orders.Select(o => o.Order.Status));
        Assert.Equal(["Created", "Submitted", "Acknowledged", "Filled"], orders[0].Events.Select(e => e.Status));
        Assert.Equal(("TripKillSwitch", "fer"), (commands[0].Type, commands[0].RequestedBy));
        Assert.Equal(8, accepted.CommandId);
    }

    [Fact]
    public void Risk_backtests_and_monte_carlo()
    {
        var limits = Read<RiskLimitsDto>("risk_limits");
        var summaries = Read<List<BacktestRunSummaryDto>>("backtest_summaries");
        var run = Read<BacktestRunDto>("backtest_run");
        var monteCarlo = Read<MonteCarloOutcomeDto>("monte_carlo");

        Assert.Equal((0.01m, 0.03m, 0.20m), (limits.RiskPerTrade, limits.MaxDailyLoss, limits.MaxDrawdown));
        Assert.Equal(("holdout", 22), (summaries[0].PeriodLabel, summaries[0].TradeCount));
        Assert.Equal("baseline-ema-trend", run.Result.Strategy.Name);
        Assert.NotEmpty(run.Result.EquityCurve);
        Assert.Equal(run.Result.Trades.Count, run.Result.Metrics.TradeCount);
        Assert.NotEmpty(run.Notices);
        Assert.Equal(3, monteCarlo.Result.SourceTrades);
        Assert.InRange(monteCarlo.Result.ProbabilityOfLoss, 0, 1);
        Assert.NotNull(monteCarlo.Result.ProbabilityKillSwitch);
    }

    private static T Read<T>(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"contracts/{name}.json")
            ?? throw new InvalidOperationException($"Missing contract sample {name}.");
        return JsonSerializer.Deserialize<T>(stream, OmegaApiClient.Json) ?? throw new InvalidOperationException($"Empty sample {name}.");
    }
}
