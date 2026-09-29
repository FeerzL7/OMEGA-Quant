namespace Omega.Core.MarketData;

/// <summary>Whether the latest closed candle is as recent as it should be.</summary>
public enum DataFreshness
{
    /// <summary>No candle has ever been stored. Zero value: never mistaken for fresh.</summary>
    NoData = 0,

    /// <summary>The next candle is not overdue yet.</summary>
    Fresh = 1,

    /// <summary>The next candle should already have closed (plus a grace period) and has not been stored.</summary>
    Stale = 2,
}

/// <summary>A run of consecutive missing closed candles.</summary>
public sealed record CandleGap(DateTimeOffset FirstMissingOpenTimeUtc, int MissingCandles)
{
    /// <summary>Open time just after the last missing candle.</summary>
    public DateTimeOffset EndOpenTimeUtc(CandleInterval interval) =>
        FirstMissingOpenTimeUtc + (interval.ToTimeSpan() * MissingCandles);
}

/// <summary>Reasons why market data cannot currently be trusted.</summary>
public static class MarketStateIssues
{
    public const string NoData = "NO_DATA";
    public const string StaleData = "STALE_DATA";
    public const string GapsInWindow = "GAPS_IN_INTEGRITY_WINDOW";
}

/// <summary>
/// Snapshot of the stored closed-candle data for one symbol and interval.
/// Built only from closed candles, the only ones allowed to generate signals.
/// </summary>
/// <param name="Symbol">Symbol.</param>
/// <param name="Interval">Interval.</param>
/// <param name="EvaluatedAtUtc">When the state was computed.</param>
/// <param name="LastClosedCandle">Most recent stored closed candle, if any.</param>
/// <param name="Freshness">Freshness of <paramref name="LastClosedCandle"/>.</param>
/// <param name="DataAge">Time since the last candle closed, if any.</param>
/// <param name="NextCloseExpectedUtc">When the next candle should close, if any.</param>
/// <param name="IntegrityWindowStartUtc">Start of the window checked for gaps.</param>
/// <param name="RecentGaps">Gaps that overlap the integrity window.</param>
/// <param name="Issues">Why the data is not reliable; empty when it is.</param>
public sealed record MarketState(
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset EvaluatedAtUtc,
    Candle? LastClosedCandle,
    DataFreshness Freshness,
    TimeSpan? DataAge,
    DateTimeOffset? NextCloseExpectedUtc,
    DateTimeOffset IntegrityWindowStartUtc,
    IReadOnlyList<CandleGap> RecentGaps,
    IReadOnlyList<string> Issues)
{
    /// <summary>
    /// True only when there is data, it is fresh and there are no gaps in the
    /// integrity window. Anything that uses market data for decisions must treat
    /// false as "do not trust" (CLAUDE.md §14).
    /// </summary>
    public bool IsReliable => Issues.Count == 0;
}

/// <summary>Pure rules that turn stored-candle facts into a <see cref="MarketState"/>.</summary>
public static class MarketStateEvaluator
{
    /// <param name="symbol">Symbol.</param>
    /// <param name="interval">Interval.</param>
    /// <param name="latest">Latest stored closed candle, or null.</param>
    /// <param name="gaps">Known gaps (those that end before the window are ignored).</param>
    /// <param name="nowUtc">Evaluation time.</param>
    /// <param name="freshnessGracePeriod">Tolerated delay after the next expected close before data is stale.</param>
    /// <param name="integrityWindow">How far back a gap makes the data unreliable.</param>
    public static MarketState Evaluate(
        string symbol,
        CandleInterval interval,
        Candle? latest,
        IReadOnlyList<CandleGap> gaps,
        DateTimeOffset nowUtc,
        TimeSpan freshnessGracePeriod,
        TimeSpan integrityWindow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(gaps);
        ArgumentOutOfRangeException.ThrowIfLessThan(freshnessGracePeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(integrityWindow, TimeSpan.Zero);

        var windowStart = nowUtc - integrityWindow;
        var recentGaps = gaps.Where(gap => gap.EndOpenTimeUtc(interval) > windowStart).ToList();
        var issues = new List<string>();

        if (latest is null)
        {
            issues.Add(MarketStateIssues.NoData);
            return new MarketState(symbol, interval, nowUtc, null, DataFreshness.NoData, null, null, windowStart, recentGaps, issues);
        }

        // The candle after `latest` closes exactly one interval after it.
        var nextClose = latest.CloseTimeUtc + interval.ToTimeSpan();
        var freshness = nowUtc <= nextClose + freshnessGracePeriod ? DataFreshness.Fresh : DataFreshness.Stale;

        if (freshness == DataFreshness.Stale)
        {
            issues.Add(MarketStateIssues.StaleData);
        }

        if (recentGaps.Count > 0)
        {
            issues.Add(MarketStateIssues.GapsInWindow);
        }

        return new MarketState(
            symbol, interval, nowUtc, latest, freshness, nowUtc - latest.CloseTimeUtc, nextClose, windowStart, recentGaps, issues);
    }
}
