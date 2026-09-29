using Omega.Core.MarketData;

namespace Omega.Api.Contracts;

/// <summary>Response of <c>GET /api/market/{symbol}/{interval}/state</c>.</summary>
/// <param name="Symbol">Symbol, for example BTCUSDT.</param>
/// <param name="Interval">Interval code, for example 5m.</param>
/// <param name="EvaluatedAtUtc">Server time (UTC) of the evaluation.</param>
/// <param name="IsReliable">False when there is no data, it is stale, or there are gaps in the integrity window.</param>
/// <param name="Issues">Why the data is not reliable (NO_DATA, STALE_DATA, GAPS_IN_INTEGRITY_WINDOW).</param>
/// <param name="Freshness">NO_DATA, FRESH or STALE.</param>
/// <param name="DataAgeSeconds">Seconds since the last stored candle closed.</param>
/// <param name="NextCloseExpectedUtc">When the next candle should close.</param>
/// <param name="LastClosedCandle">Last stored closed candle.</param>
/// <param name="IntegrityWindowStartUtc">Start of the window checked for gaps.</param>
/// <param name="RecentGaps">Gaps overlapping the integrity window.</param>
public sealed record MarketStateResponse(
    string Symbol,
    string Interval,
    DateTimeOffset EvaluatedAtUtc,
    bool IsReliable,
    IReadOnlyList<string> Issues,
    string Freshness,
    double? DataAgeSeconds,
    DateTimeOffset? NextCloseExpectedUtc,
    CandleResponse? LastClosedCandle,
    DateTimeOffset IntegrityWindowStartUtc,
    IReadOnlyList<CandleGapResponse> RecentGaps)
{
    public static MarketStateResponse From(MarketState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new MarketStateResponse(
            state.Symbol,
            state.Interval.ToCode(),
            state.EvaluatedAtUtc,
            state.IsReliable,
            state.Issues,
            state.Freshness switch
            {
                DataFreshness.Fresh => "FRESH",
                DataFreshness.Stale => "STALE",
                _ => "NO_DATA",
            },
            state.DataAge is { } age ? Math.Round(age.TotalSeconds, 3) : null,
            state.NextCloseExpectedUtc,
            state.LastClosedCandle is { } candle ? CandleResponse.From(candle) : null,
            state.IntegrityWindowStartUtc,
            [.. state.RecentGaps.Select(gap => new CandleGapResponse(gap.FirstMissingOpenTimeUtc, gap.MissingCandles))]);
    }
}

/// <summary>A closed candle. Prices and volumes are exact decimals.</summary>
public sealed record CandleResponse(
    DateTimeOffset OpenTimeUtc,
    DateTimeOffset CloseTimeUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal BaseVolume,
    decimal QuoteVolume,
    long TradeCount)
{
    public static CandleResponse From(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);

        return new CandleResponse(
            candle.OpenTimeUtc, candle.CloseTimeUtc, candle.Open, candle.High, candle.Low, candle.Close,
            candle.BaseVolume, candle.QuoteVolume, candle.TradeCount);
    }
}

public sealed record CandleGapResponse(DateTimeOffset FirstMissingOpenTimeUtc, int MissingCandles);
