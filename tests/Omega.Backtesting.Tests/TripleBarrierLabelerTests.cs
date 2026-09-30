using Omega.Backtesting.Tests.TestSupport;
using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Strategy;
using static Omega.Backtesting.Tests.TestSupport.Bars;

namespace Omega.Backtesting.Tests;

public class TripleBarrierLabelerTests
{
    // Decision close 100, ATR 5: stop 90, target 115.
    private static readonly TripleBarrierSpec Spec = new(2m, 3m, 3);

    [Fact]
    public void Target_reached_first_exits_at_the_target()
    {
        var candles = new[] { Flat(0, 100m), At(1, 101m, 105m, 99m, 104m), At(2, 104m, 116m, 103m, 112m), Flat(3, 112m) };

        var label = TripleBarrierLabeler.Label(candles, 0, 5m, Spec)!;

        Assert.Equal((BarrierOutcome.TakeProfitFirst, 115m, 2, OpenTime(2)), (label.Outcome, label.ExitPrice, label.HoldingCandles, label.ExitCandleOpenTimeUtc));
        Assert.Equal((101m, 90m, 115m), (label.EntryPrice, label.StopLossPrice, label.TakeProfitPrice));
        Assert.Equal(115m / 101m - 1m, label.GrossReturn);
    }

    [Fact]
    public void Both_barriers_in_one_candle_count_as_stop_loss()
    {
        var candles = new[] { Flat(0, 100m), At(1, 100m, 120m, 85m, 100m), Flat(2, 100m), Flat(3, 100m) };

        Assert.Equal(BarrierOutcome.StopLossFirst, TripleBarrierLabeler.Label(candles, 0, 5m, Spec)!.Outcome);
    }

    [Fact]
    public void Gaps_exit_at_the_open_through_the_stop_and_at_the_target_through_the_target()
    {
        var gapDown = new[] { Flat(0, 100m), Flat(1, 100m), At(2, 80m, 82m, 79m, 81m), Flat(3, 81m) };
        var gapUp = new[] { Flat(0, 100m), Flat(1, 100m), At(2, 130m, 131m, 129m, 130m), Flat(3, 130m) };

        Assert.Equal((BarrierOutcome.StopLossFirst, 80m), Outcome(gapDown));
        Assert.Equal((BarrierOutcome.TakeProfitFirst, 115m), Outcome(gapUp));
    }

    [Fact]
    public void Without_a_barrier_the_label_times_out_at_the_last_close_of_the_horizon()
    {
        var candles = Flats(100m, 100m, 101m, 102m, 103m);

        var label = TripleBarrierLabeler.Label(candles, 0, 5m, Spec)!;

        Assert.Equal((BarrierOutcome.Timeout, 102m, 3), (label.Outcome, label.ExitPrice, label.HoldingCandles));
    }

    [Fact]
    public void Undeterminable_samples_have_no_label()
    {
        var shortHistory = Flats(100m, 100m, 100m);
        var withGap = new[] { Flat(0, 100m), Flat(1, 100m), Flat(3, 100m), Flat(4, 100m) };
        var entryBelowStop = new[] { Flat(0, 100m), At(1, 89m, 90m, 88m, 89m), Flat(2, 89m), Flat(3, 89m) };

        Assert.Null(TripleBarrierLabeler.Label(shortHistory, 0, 5m, Spec));
        Assert.Null(TripleBarrierLabeler.Label(withGap, 0, 5m, Spec));
        Assert.Null(TripleBarrierLabeler.Label(entryBelowStop, 0, 5m, Spec));
        Assert.Null(TripleBarrierLabeler.Label(Flats(100m, 100m, 100m, 100m), 0, 0m, Spec));
    }

    [Fact]
    public void Labels_match_what_the_backtester_does_with_the_same_barriers()
    {
        var candles = Wavy(1_200);
        const decimal atr = 1.5m;
        var spec = new TripleBarrierSpec(2m, 3m, 12);
        var decisions = Enumerable.Range(1, 55).Select(k => k * 20).ToHashSet();

        var engine = Engines.Create(Engines.NoCosts with { MaxHoldingCandles = spec.HorizonCandles });
        var result = engine.Run(candles, new BarrierStrategy(decisions, atr, spec)).Value;

        var compared = 0;
        foreach (var trade in result.Trades.Where(t => t.ExitReason != ExitReason.EndOfData))
        {
            var decisionIndex = Array.FindIndex(candles, c => c.OpenTimeUtc == trade.EntryCandleOpenTimeUtc) - 1;
            var label = TripleBarrierLabeler.Label(candles, decisionIndex, atr, spec)!;
            var expectedReason = label.Outcome switch
            {
                BarrierOutcome.TakeProfitFirst => ExitReason.TakeProfit,
                BarrierOutcome.StopLossFirst => ExitReason.StopLoss,
                _ => ExitReason.TimeLimit,
            };

            Assert.Equal((expectedReason, label.ExitPrice, label.HoldingCandles, label.ExitCandleOpenTimeUtc),
                (trade.ExitReason, trade.ExitPrice, trade.HoldingCandles, trade.ExitCandleOpenTimeUtc));
            compared++;
        }

        Assert.True(compared >= 40, $"Only {compared} trades compared.");
        Assert.Contains(result.Trades, t => t.ExitReason == ExitReason.TakeProfit);
        Assert.Contains(result.Trades, t => t.ExitReason == ExitReason.StopLoss);
        Assert.Contains(result.Trades, t => t.ExitReason == ExitReason.TimeLimit);
    }

    private static (BarrierOutcome, decimal) Outcome(Candle[] candles)
    {
        var label = TripleBarrierLabeler.Label(candles, 0, 5m, Spec)!;
        return (label.Outcome, label.ExitPrice);
    }

    /// <summary>Deterministic candles with trends, noise and occasional gaps (open differs from the previous close).</summary>
    private static Candle[] Wavy(int count)
    {
        var candles = new Candle[count];
        var close = 100m;
        for (var i = 0; i < count; i++)
        {
            var open = i % 37 == 0 ? close + (i % 2 == 0 ? 2.5m : -2.5m) : close;
            close = Math.Round(open + (decimal)(0.6 * Math.Sin(i / 9.0) + 0.4 * Math.Sin(i * 2.3)), 2);
            var high = Math.Max(open, close) + (decimal)Math.Round(0.2 + Math.Abs(Math.Sin(i * 1.7)), 2);
            var low = Math.Min(open, close) - (decimal)Math.Round(0.2 + Math.Abs(Math.Cos(i * 1.3)), 2);
            candles[i] = At(i, open, high, low, close);
        }

        return candles;
    }

    /// <summary>Goes long at the given decision indices with the label's barriers; never exits by signal.</summary>
    private sealed class BarrierStrategy(HashSet<int> decisions, decimal atr, TripleBarrierSpec spec) : IStrategy
    {
        public StrategyIdentity Identity { get; } = new("barrier-test", "1", new Dictionary<string, string>());

        public Signal Evaluate(StrategyContext context)
        {
            var index = (int)((context.Candle.OpenTimeUtc - Start).TotalMinutes / 5);
            if (context.Position is not null || !decisions.Contains(index))
            {
                return Signal.Hold();
            }

            var close = context.Candle.Close;
            return Signal.Long(close - (spec.StopAtr * atr), close + (spec.TargetAtr * atr));
        }
    }
}
