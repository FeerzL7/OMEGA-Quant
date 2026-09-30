using Omega.Core.MarketData;

namespace Omega.Backtesting;

/// <summary>Which barrier a hypothetical long reached first (CLAUDE.md §11).</summary>
public enum BarrierOutcome
{
    TakeProfitFirst = 1,
    StopLossFirst = 2,
    Timeout = 3,
}

/// <summary>Barrier distances, in ATR multiples from the decision close, and the horizon in candles.</summary>
public sealed record TripleBarrierSpec(decimal StopAtr, decimal TargetAtr, int HorizonCandles)
{
    /// <summary>Same barriers as the baseline strategy, so models and baseline are compared on equal terms.</summary>
    public static TripleBarrierSpec Default { get; } = new(
        Omega.Strategy.Baseline.EmaTrendBaseline.StopAtr,
        Omega.Strategy.Baseline.EmaTrendBaseline.TargetAtr,
        Omega.Strategy.Baseline.EmaTrendBaseline.TimeoutCandles);
}

/// <summary>
/// Outcome of a long decided at the close of candle t, entered at the open of t+1.
/// Prices are gross (no fees, spread or slippage): costs belong to the expected-value phase (Phase 9).
/// </summary>
public sealed record BarrierLabel(
    DateTimeOffset DecisionOpenTimeUtc,
    decimal EntryPrice,
    decimal StopLossPrice,
    decimal TakeProfitPrice,
    BarrierOutcome Outcome,
    DateTimeOffset ExitCandleOpenTimeUtc,
    decimal ExitPrice,
    int HoldingCandles)
{
    public decimal GrossReturn => (ExitPrice / EntryPrice) - 1m;
}

/// <summary>
/// Triple-barrier labels computed with exactly the execution rules of <see cref="BacktestEngine"/>:
/// barriers from the decision close ± ATR multiples, entry at the next open, stop loss first when both barriers
/// are inside a candle, a gap through the stop exits at the open, a gap through the target exits at the target,
/// timeout at the close of the last candle of the horizon.
/// </summary>
/// <remarks>
/// The label looks into the future by definition; it must only ever be used as a training target, never as an
/// input. A sample has no label (null) when it cannot be determined honestly: not enough candles, a gap inside the
/// horizon, or an entry the backtester would reject (the entry open already beyond a barrier).
/// </remarks>
public static class TripleBarrierLabeler
{
    public static BarrierLabel? Label(IReadOnlyList<Candle> candles, int decisionIndex, decimal atr, TripleBarrierSpec spec)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentOutOfRangeException.ThrowIfNegative(decisionIndex);

        var entryIndex = decisionIndex + 1;
        var lastIndex = entryIndex + spec.HorizonCandles - 1;
        if (atr <= 0 || lastIndex >= candles.Count)
        {
            return null;
        }

        var length = candles[decisionIndex].Interval.ToTimeSpan();
        for (var i = decisionIndex + 1; i <= lastIndex; i++)
        {
            if (candles[i].OpenTimeUtc - candles[i - 1].OpenTimeUtc != length)
            {
                return null; // gap: the path is unknown
            }
        }

        var decisionClose = candles[decisionIndex].Close;
        var stop = decisionClose - (spec.StopAtr * atr);
        var target = decisionClose + (spec.TargetAtr * atr);
        var entry = candles[entryIndex].Open;

        if (stop <= 0 || entry <= stop || entry >= target)
        {
            return null; // the backtester would not enter
        }

        for (var i = entryIndex; i <= lastIndex; i++)
        {
            var candle = candles[i];
            var holding = i - entryIndex + 1;

            if (i > entryIndex && candle.Open <= stop)
            {
                return Make(BarrierOutcome.StopLossFirst, candle, candle.Open, holding);
            }

            if (i > entryIndex && candle.Open >= target)
            {
                return Make(BarrierOutcome.TakeProfitFirst, candle, target, holding);
            }

            if (candle.Low <= stop)
            {
                return Make(BarrierOutcome.StopLossFirst, candle, stop, holding);
            }

            if (candle.High >= target)
            {
                return Make(BarrierOutcome.TakeProfitFirst, candle, target, holding);
            }

            if (i == lastIndex)
            {
                return Make(BarrierOutcome.Timeout, candle, candle.Close, holding);
            }
        }

        return null;

        BarrierLabel Make(BarrierOutcome outcome, Candle exitCandle, decimal exitPrice, int holdingCandles) => new(
            candles[decisionIndex].OpenTimeUtc, entry, stop, target, outcome, exitCandle.OpenTimeUtc, exitPrice, holdingCandles);
    }
}
