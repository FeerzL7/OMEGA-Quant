using Omega.Core.MarketData;

namespace Omega.Core.Tests.MarketData;

public class MarketStateEvaluatorTests
{
    private static readonly DateTimeOffset Open = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Candle Latest = Candle.Create(
        "BTCUSDT", CandleInterval.FiveMinutes, Open, Open.AddMinutes(5).AddMilliseconds(-1), 100m, 101m, 99m, 100.5m, 1m, 100m, 10).Value;

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    [Fact]
    public void Without_candles_the_data_is_unreliable()
    {
        var state = Evaluate(null, [], Open);

        Assert.Equal(DataFreshness.NoData, state.Freshness);
        Assert.False(state.IsReliable);
        Assert.Equal([MarketStateIssues.NoData], state.Issues);
    }

    [Fact]
    public void Data_is_fresh_until_the_next_close_plus_grace_and_stale_after()
    {
        var nextClose = Latest.CloseTimeUtc.AddMinutes(5);

        var fresh = Evaluate(Latest, [], nextClose + Grace);
        var stale = Evaluate(Latest, [], nextClose + Grace + TimeSpan.FromMilliseconds(1));

        Assert.Equal(DataFreshness.Fresh, fresh.Freshness);
        Assert.True(fresh.IsReliable);
        Assert.Equal(nextClose, fresh.NextCloseExpectedUtc);
        Assert.Equal(DataFreshness.Stale, stale.Freshness);
        Assert.Equal([MarketStateIssues.StaleData], stale.Issues);
    }

    [Fact]
    public void Data_age_is_measured_from_the_last_close()
    {
        var state = Evaluate(Latest, [], Latest.CloseTimeUtc.AddSeconds(90));

        Assert.Equal(TimeSpan.FromSeconds(90), state.DataAge);
    }

    [Fact]
    public void Only_gaps_overlapping_the_integrity_window_make_the_data_unreliable()
    {
        var now = Latest.CloseTimeUtc.AddSeconds(1);
        var old = new CandleGap(now.AddHours(-30), 12);                 // ends one hour before the window starts
        var recent = new CandleGap(now.AddHours(-24).AddMinutes(-5), 3); // straddles the window start

        var withOld = Evaluate(Latest, [old], now);
        var withRecent = Evaluate(Latest, [old, recent], now);

        Assert.True(withOld.IsReliable);
        Assert.Empty(withOld.RecentGaps);
        Assert.Equal([recent], withRecent.RecentGaps);
        Assert.Equal([MarketStateIssues.GapsInWindow], withRecent.Issues);
    }

    [Fact]
    public void Stale_data_with_gaps_reports_both_issues()
    {
        var now = Latest.CloseTimeUtc.AddHours(1);

        var state = Evaluate(Latest, [new CandleGap(Open.AddHours(-1), 2)], now);

        Assert.Equal([MarketStateIssues.StaleData, MarketStateIssues.GapsInWindow], state.Issues);
    }

    private static MarketState Evaluate(Candle? latest, IReadOnlyList<CandleGap> gaps, DateTimeOffset now) =>
        MarketStateEvaluator.Evaluate("BTCUSDT", CandleInterval.FiveMinutes, latest, gaps, now, Grace, Window);
}
