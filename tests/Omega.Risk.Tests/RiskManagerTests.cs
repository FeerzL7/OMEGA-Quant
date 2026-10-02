using Omega.Core.Trading;
using Omega.Risk;

namespace Omega.Risk.Tests;

public class RiskManagerTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly MarketConditions Normal = new(1m, 2m, MarketDataReliable: true, ExecutionAvailable: true);
    private static readonly PortfolioSnapshot Flat = new(10_000m, 10_000m, 0, 0m);

    [Fact]
    public void Position_size_risks_the_configured_fraction_of_equity()
    {
        var risk = Manager();

        var decision = risk.Size(Flat, entryPrice: 100m, stopLossPrice: 98m, feeRate: 0m, InstrumentFilters.None);

        Assert.True(decision.Approved);
        Assert.Equal(10_000m * 0.01m / 2m, decision.Quantity);   // 50 units: a stop hit loses 100 = 1 % of equity
    }

    [Fact]
    public void Size_is_capped_by_position_size_exposure_and_cash()
    {
        var tightStop = new { entry = 100m, stop = 99.9m };   // risk budget alone would buy 1 000 units (100 000)

        var byPosition = Manager(new RiskLimits { MaxPositionFraction = 0.5m }).Size(Flat, tightStop.entry, tightStop.stop, 0m, InstrumentFilters.None);
        var byExposure = Manager(new RiskLimits { MaxOpenPositions = 3 }).Size(new PortfolioSnapshot(10_000m, 4_000m, 1, 6_000m), tightStop.entry, tightStop.stop, 0m, InstrumentFilters.None);
        var byCashWithFee = Manager().Size(new PortfolioSnapshot(10_000m, 1_001m, 0, 0m), tightStop.entry, tightStop.stop, 0.001m, InstrumentFilters.None);

        Assert.Equal(50m, byPosition.Quantity);
        Assert.Equal(40m, byExposure.Quantity);
        Assert.Equal(1_001m / (100m * 1.001m), byCashWithFee.Quantity);
    }

    [Fact]
    public void Instrument_filters_round_down_and_reject_orders_below_the_minimum()
    {
        var risk = Manager();

        var stepped = risk.Size(Flat, 100m, 97m, 0m, new InstrumentFilters(QuantityStep: 0.001m));
        var belowNotional = risk.Size(Flat, 100m, 90m, 0m, new InstrumentFilters(MinNotional: 5_000m));
        var belowQuantity = risk.Size(Flat, 100m, 90m, 0m, new InstrumentFilters(MinQuantity: 20m));

        Assert.Equal(33.333m, stepped.Quantity);
        Assert.Equal(RiskCheck.PositionTooSmall, belowNotional.Check);
        Assert.Equal(RiskCheck.PositionTooSmall, belowQuantity.Check);
        Assert.Equal("RISK_POSITION_TOO_SMALL", belowQuantity.Code);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(100, 101)]
    [InlineData(100, 0)]
    public void A_stop_that_is_not_a_positive_price_below_the_entry_is_rejected(double entry, double stop)
    {
        Assert.Equal(RiskCheck.InvalidStop, Manager().Size(Flat, (decimal)entry, (decimal)stop, 0m, InstrumentFilters.None).Check);
    }

    [Fact]
    public void Daily_loss_blocks_entries_until_the_next_utc_day()
    {
        var risk = Manager();
        risk.OnEquity(Day1.AddHours(1), 10_000m);
        risk.OnEquity(Day1.AddHours(5), 9_690m);   // -3.1 % on the day

        var sameDay = risk.CheckEntry(new PortfolioSnapshot(9_800m, 9_800m, 0, 0m), Normal);   // recovered a bit: still blocked
        risk.OnEquity(Day1.AddDays(1).AddMinutes(5), 9_800m);
        var nextDay = risk.CheckEntry(new PortfolioSnapshot(9_800m, 9_800m, 0, 0m), Normal);

        Assert.Equal(RiskCheck.DailyLossLimit, sameDay.Check);
        Assert.Contains("equity was 9690", sameDay.Detail, StringComparison.Ordinal);    // the value that triggered it
        Assert.Contains("Equity now 9800", sameDay.Detail, StringComparison.Ordinal);   // and the current one
        Assert.Equal(NoTradeReason.RiskLimitReached, sameDay.NoTradeReason);
        Assert.Equal("RISK_DAILY_LOSS_LIMIT", sameDay.Code);
        Assert.True(nextDay.Approved);
        Assert.Equal(9_690m, risk.DayStartEquity);   // the new day starts from the last equity marked the day before
    }

    [Fact]
    public void A_losing_streak_blocks_entries_until_the_next_day_and_a_win_resets_it()
    {
        var risk = Manager(new RiskLimits { MaxConsecutiveLosses = 3, MaxDailyLoss = 0.5m });
        risk.OnEquity(Day1.AddHours(1), 10_000m);

        risk.OnTradeClosed(-10m);
        risk.OnTradeClosed(-10m);
        risk.OnTradeClosed(+5m);
        risk.OnTradeClosed(-10m);
        risk.OnTradeClosed(-10m);
        var afterTwo = risk.CheckEntry(Flat, Normal);
        risk.OnTradeClosed(-10m);
        var afterThree = risk.CheckEntry(Flat, Normal);
        risk.OnEquity(Day1.AddDays(1).AddMinutes(5), 10_000m);

        Assert.True(afterTwo.Approved);
        Assert.Equal(RiskCheck.ConsecutiveLosses, afterThree.Check);
        Assert.True(risk.CheckEntry(Flat, Normal).Approved);
        Assert.Equal(0, risk.ConsecutiveLosses);
    }

    [Fact]
    public void Maximum_drawdown_trips_a_kill_switch_that_only_a_person_can_reset()
    {
        var risk = Manager(new RiskLimits { MaxDrawdown = 0.10m, MaxDailyLoss = 0.5m });
        risk.OnEquity(Day1.AddHours(1), 12_000m);
        risk.OnEquity(Day1.AddHours(2), 10_700m);   // -10.8 % from the peak

        var blocked = risk.CheckEntry(new PortfolioSnapshot(10_700m, 10_700m, 0, 0m), Normal);
        risk.OnEquity(Day1.AddDays(5), 12_500m);   // days pass, equity recovers: still blocked
        var stillBlocked = risk.CheckEntry(new PortfolioSnapshot(12_500m, 12_500m, 0, 0m), Normal);
        risk.KillSwitch.Reset("owner");

        Assert.Equal(RiskCheck.KillSwitch, blocked.Check);
        Assert.Equal(RiskCheck.KillSwitch, stillBlocked.Check);
        Assert.Contains("Maximum drawdown", blocked.Detail, StringComparison.Ordinal);
        Assert.True(risk.CheckEntry(new PortfolioSnapshot(12_500m, 12_500m, 0, 0m), Normal).Approved);
        Assert.Equal("owner", risk.KillSwitch.ResetBy);
        Assert.Throws<ArgumentException>(() => risk.KillSwitch.Reset(" "));
    }

    [Fact]
    public void A_manual_trip_keeps_its_first_reason()
    {
        var killSwitch = new KillSwitch();

        killSwitch.Trip("manual: exchange incident", Day1);
        killSwitch.Trip("second reason", Day1.AddHours(1));

        Assert.True(killSwitch.IsActive);
        Assert.Equal(("manual: exchange incident", Day1), (killSwitch.Reason!, killSwitch.TrippedAtUtc!.Value));
    }

    [Fact]
    public void Market_and_system_conditions_are_checked()
    {
        var risk = Manager();

        Assert.Equal(RiskCheck.ExcessiveSpread, risk.CheckEntry(Flat, Normal with { SpreadBps = 11m }).Check);
        Assert.Equal(RiskCheck.ExcessiveSlippage, risk.CheckEntry(Flat, Normal with { ExpectedSlippageBps = 26m }).Check);
        Assert.Equal(RiskCheck.MarketDataUnreliable, risk.CheckEntry(Flat, Normal with { MarketDataReliable = false }).Check);
        Assert.Equal(RiskCheck.ExecutionUnavailable, risk.CheckEntry(Flat, Normal with { ExecutionAvailable = false }).Check);
        Assert.Equal(NoTradeReason.InvalidMarketData, risk.CheckEntry(Flat, Normal with { MarketDataReliable = false }).NoTradeReason);
        Assert.Equal(NoTradeReason.ExcessiveSpread, risk.CheckEntry(Flat, Normal with { SpreadBps = 11m }).NoTradeReason);
    }

    [Fact]
    public void Position_count_and_exposure_limits_are_checked()
    {
        var risk = Manager(new RiskLimits { MaxOpenPositions = 2, MaxExposureFraction = 0.5m });

        Assert.Equal(RiskCheck.MaxOpenPositions, risk.CheckEntry(new PortfolioSnapshot(10_000m, 2_000m, 2, 8_000m), Normal).Check);
        Assert.Equal(RiskCheck.MaxExposure, risk.CheckEntry(new PortfolioSnapshot(10_000m, 5_000m, 1, 5_000m), Normal).Check);
        Assert.True(risk.CheckEntry(new PortfolioSnapshot(10_000m, 6_000m, 1, 4_000m), Normal).Approved);
    }

    [Fact]
    public void The_kill_switch_is_checked_before_everything_else()
    {
        // Every other control fails too (daily loss included): the most severe and only persistent cause is reported.
        var risk = Manager(new RiskLimits { MaxConsecutiveLosses = 1 });
        risk.OnEquity(Day1.AddHours(1), 9_000m);
        risk.OnTradeClosed(-1_000m);
        risk.KillSwitch.Trip("manual", Day1);

        var decision = risk.CheckEntry(new PortfolioSnapshot(9_000m, 0m, 5, 9_000m), Normal with { SpreadBps = 100m, ExecutionAvailable = false });

        Assert.Equal(RiskCheck.KillSwitch, decision.Check);
    }

    [Fact]
    public void Invalid_limits_are_refused()
    {
        Assert.NotEmpty(new RiskLimits { MaxPositionFraction = 2m }.Validate());
        Assert.NotEmpty(new RiskLimits { MaxDrawdown = 0m }.Validate());
        Assert.NotEmpty(new RiskLimits { MaxConsecutiveLosses = 0 }.Validate());
        Assert.Empty(RiskLimits.Default.Validate());
        Assert.Throws<ArgumentException>(() => new RiskManager(new RiskLimits { RiskPerTrade = 0m }, 10_000m, Day1));
    }

    private static RiskManager Manager(RiskLimits? limits = null) => new(limits ?? RiskLimits.Default, 10_000m, Day1);
}
