using Omega.Core.MarketData;
using Omega.MarketData.Integrity;

namespace Omega.MarketData.Tests.Integrity;

public class ClosedCandleSequencerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Consecutive_candles_are_accepted()
    {
        var sequencer = new ClosedCandleSequencer(CandleInterval.FiveMinutes);

        Assert.Equal(CandleSequenceOutcome.Accepted, sequencer.Accept(Candle(0)).Outcome);
        Assert.Equal(CandleSequenceOutcome.Accepted, sequencer.Accept(Candle(1)).Outcome);
        Assert.Equal(Start.AddMinutes(5), sequencer.LastOpenTimeUtc);
    }

    [Fact]
    public void Repeated_candle_is_a_duplicate_and_does_not_move_the_sequence()
    {
        var sequencer = new ClosedCandleSequencer(CandleInterval.FiveMinutes);
        sequencer.Accept(Candle(0));
        sequencer.Accept(Candle(1));

        var result = sequencer.Accept(Candle(1));

        Assert.Equal(CandleSequenceOutcome.Duplicate, result.Outcome);
        Assert.False(result.IsAccepted);
        Assert.Equal(Start.AddMinutes(5), sequencer.LastOpenTimeUtc);
    }

    [Fact]
    public void Older_candle_is_out_of_order()
    {
        var sequencer = new ClosedCandleSequencer(CandleInterval.FiveMinutes);
        sequencer.Accept(Candle(0));
        sequencer.Accept(Candle(1));

        Assert.Equal(CandleSequenceOutcome.OutOfOrder, sequencer.Accept(Candle(0)).Outcome);
    }

    [Fact]
    public void Gap_reports_first_missing_candle_and_count()
    {
        var sequencer = new ClosedCandleSequencer(CandleInterval.FiveMinutes);
        sequencer.Accept(Candle(0));

        var result = sequencer.Accept(Candle(4));

        Assert.Equal(CandleSequenceOutcome.AcceptedAfterGap, result.Outcome);
        Assert.True(result.IsAccepted);
        Assert.Equal(Start.AddMinutes(5), result.FirstMissingOpenTimeUtc);
        Assert.Equal(3, result.MissingCandles);
        Assert.Equal(CandleSequenceOutcome.Accepted, sequencer.Accept(Candle(5)).Outcome);
    }

    private static Candle Candle(int index)
    {
        var open = Start.AddMinutes(5 * index);

        return Omega.Core.MarketData.Candle.Create(
            "BTCUSDT", CandleInterval.FiveMinutes, open, open.AddMinutes(5).AddMilliseconds(-1),
            100m, 101m, 99m, 100.5m, 1m, 100m, 10).Value;
    }
}
