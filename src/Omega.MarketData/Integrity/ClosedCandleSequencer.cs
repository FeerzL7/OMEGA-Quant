using Omega.Core.MarketData;

namespace Omega.MarketData.Integrity;

/// <summary>
/// Tracks the sequence of closed candles for one symbol and interval, across
/// reconnections. Detects duplicates (typically re-sent after reconnecting),
/// out-of-order candles and gaps.
/// </summary>
/// <remarks>
/// Without persisted history (Phase 2) the first candle after start-up cannot
/// be checked against anything, so a gap before it is not detectable yet.
/// </remarks>
public sealed class ClosedCandleSequencer(CandleInterval interval)
{
    private readonly TimeSpan _intervalLength = interval.ToTimeSpan();

    public DateTimeOffset? LastOpenTimeUtc { get; private set; }

    public CandleSequenceResult Accept(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);

        if (candle.Interval != interval)
        {
            throw new ArgumentException($"Expected a {interval} candle, got {candle.Interval}.", nameof(candle));
        }

        if (LastOpenTimeUtc is not { } last)
        {
            LastOpenTimeUtc = candle.OpenTimeUtc;
            return CandleSequenceResult.Accepted;
        }

        if (candle.OpenTimeUtc == last)
        {
            return CandleSequenceResult.Duplicate;
        }

        if (candle.OpenTimeUtc < last)
        {
            return CandleSequenceResult.OutOfOrder;
        }

        var expected = last + _intervalLength;
        LastOpenTimeUtc = candle.OpenTimeUtc;

        if (candle.OpenTimeUtc == expected)
        {
            return CandleSequenceResult.Accepted;
        }

        var missing = (int)((candle.OpenTimeUtc - expected).Ticks / _intervalLength.Ticks);
        return CandleSequenceResult.AcceptedAfterGap(expected, missing);
    }
}

public enum CandleSequenceOutcome
{
    Accepted = 1,
    AcceptedAfterGap = 2,
    Duplicate = 3,
    OutOfOrder = 4,
}

/// <param name="Outcome">What happened to the candle.</param>
/// <param name="FirstMissingOpenTimeUtc">Only for <see cref="CandleSequenceOutcome.AcceptedAfterGap"/>.</param>
/// <param name="MissingCandles">Only for <see cref="CandleSequenceOutcome.AcceptedAfterGap"/>.</param>
public readonly record struct CandleSequenceResult(
    CandleSequenceOutcome Outcome,
    DateTimeOffset? FirstMissingOpenTimeUtc,
    int MissingCandles)
{
    public static CandleSequenceResult Accepted => new(CandleSequenceOutcome.Accepted, null, 0);

    public static CandleSequenceResult Duplicate => new(CandleSequenceOutcome.Duplicate, null, 0);

    public static CandleSequenceResult OutOfOrder => new(CandleSequenceOutcome.OutOfOrder, null, 0);

    public static CandleSequenceResult AcceptedAfterGap(DateTimeOffset firstMissingOpenTimeUtc, int missingCandles) =>
        new(CandleSequenceOutcome.AcceptedAfterGap, firstMissingOpenTimeUtc, missingCandles);

    public bool IsAccepted => Outcome is CandleSequenceOutcome.Accepted or CandleSequenceOutcome.AcceptedAfterGap;
}
