using Omega.Backtesting.Tests.TestSupport;
using Omega.Core.Trading;
using Omega.Risk;
using static Omega.Backtesting.Tests.TestSupport.Bars;

namespace Omega.Backtesting.Tests;

public class RiskIntegrationTests
{
    [Fact]
    public void Entries_are_refused_when_the_spread_exceeds_the_risk_limit()
    {
        var result = Engines.Create(Engines.NoCosts with { SpreadBps = 15m }).Run(Flats(100m, 100m, 100m), new ScriptedStrategy(new() { [0] = Signal.Long(98m) })).Value;

        Assert.Empty(result.Trades);
        Assert.Equal("RISK_EXCESSIVE_SPREAD", Assert.Single(result.Rejections).Code);
        Assert.Equal(1, result.RiskSummary!.RejectionsByCheck["RISK_EXCESSIVE_SPREAD"]);
    }

    [Fact]
    public void After_the_daily_loss_limit_no_new_entries_are_taken_that_day()
    {
        // Each long risks 2 % and is stopped out at once; the daily limit is 3 %.
        var candles = new[]
        {
            Flat(0, 100m), Flat(1, 100m), At(2, 100m, 100m, 97m, 97m),     // trade 1: -2 %
            Flat(3, 100m), At(4, 100m, 100m, 97m, 97m),                     // trade 2: -2 % → -4 % on the day
            Flat(5, 100m), Flat(6, 100m),
        };
        var strategy = new ScriptedStrategy(new() { [1] = Signal.Long(98m), [3] = Signal.Long(98m), [5] = Signal.Long(98m) });

        var result = Engines.Create(risk: new RiskLimits { RiskPerTrade = 0.02m }).Run(candles, strategy).Value;

        Assert.Equal(2, result.Trades.Count);
        Assert.Equal("RISK_DAILY_LOSS_LIMIT", Assert.Single(result.Rejections).Code);
    }

    [Fact]
    public void Maximum_drawdown_trips_the_kill_switch_and_open_positions_still_exit()
    {
        var candles = new[]
        {
            Flat(0, 100m), Flat(1, 100m),
            At(2, 100m, 100m, 89m, 90m),                // stopped out: -10 % of equity with RiskPerTrade 0.10 → kill switch at 10 %
            Flat(3, 90m), Flat(4, 90m), Flat(5, 90m),
        };
        var strategy = new ScriptedStrategy(new() { [1] = Signal.Long(90m), [3] = Signal.Long(85m) });
        var risk = new RiskLimits { RiskPerTrade = 0.10m, MaxDrawdown = 0.10m, MaxDailyLoss = 0.5m };

        var result = Engines.Create(risk: risk).Run(candles, strategy).Value;

        var trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.StopLoss, trade.ExitReason);                     // the exit was not blocked
        Assert.NotNull(result.RiskSummary!.KillSwitchTrippedAtUtc);
        Assert.Contains("Maximum drawdown", result.RiskSummary!.KillSwitchReason, StringComparison.Ordinal);
        Assert.Equal("RISK_KILL_SWITCH", Assert.Single(result.Rejections).Code);  // the later entry is refused
    }

    [Fact]
    public void A_decision_on_the_first_candle_after_a_data_gap_is_refused()
    {
        var candles = new[] { Flat(0, 100m), Flat(1, 100m), Flat(3, 100m), Flat(4, 100m), Flat(5, 100m) };
        var strategy = new ScriptedStrategy(new() { [3] = Signal.Long(98m), [4] = Signal.Long(98m) });

        var result = Engines.Create().Run(candles, strategy).Value;

        Assert.Contains(result.Rejections, r => r.Code == "RISK_MARKET_DATA_UNRELIABLE" && r.TimeUtc == OpenTime(4).AddMilliseconds(-1));
        Assert.Single(result.Trades);   // the decision one candle later is accepted
    }

    [Fact]
    public void The_risk_policy_used_is_recorded_with_the_result()
    {
        var limits = new RiskLimits { RiskPerTrade = 0.005m, MaxDailyLoss = 0.02m };

        var result = Engines.Create(risk: limits).Run(Flats(100m, 100m), new ScriptedStrategy()).Value;

        Assert.Equal(limits, result.Risk);
        Assert.Empty(result.RiskSummary!.RejectionsByCheck);
        Assert.Null(result.RiskSummary!.KillSwitchTrippedAtUtc);
    }
}
