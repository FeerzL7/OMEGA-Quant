using Omega.Backtesting;
using Omega.Core.MarketData;
using Omega.Core.Results;
using Omega.Features;

namespace Omega.Application.Backtesting;

/// <summary>What to backtest.</summary>
/// <param name="StrategyName">A name from <see cref="StrategyCatalog"/>.</param>
/// <param name="Symbol">Symbol.</param>
/// <param name="Interval">Interval.</param>
/// <param name="FromUtc">First candle that may trade (inclusive).</param>
/// <param name="ToUtc">End of the evaluated period (exclusive).</param>
/// <param name="PeriodLabel">Evaluation period, for example <c>development</c> or <c>holdout</c>.</param>
/// <param name="FeeRate">Overrides the default commission (for cost stress tests).</param>
/// <param name="SpreadBps">Overrides the default spread.</param>
/// <param name="SlippageBps">Overrides the default slippage.</param>
public sealed record BacktestRequest(
    string StrategyName,
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string PeriodLabel,
    decimal? FeeRate = null,
    decimal? SpreadBps = null,
    decimal? SlippageBps = null);

/// <summary>A stored run and the cautions that apply to reading it.</summary>
/// <param name="Run">The stored run.</param>
/// <param name="PreviousEvaluationsOfThisPeriod">Earlier runs of the same strategy on exactly this period (any label or costs).</param>
/// <param name="Notices">Cautions for the reader, in plain language.</param>
public sealed record BacktestOutcome(BacktestRun Run, int PreviousEvaluationsOfThisPeriod, IReadOnlyList<string> Notices);

public static class BacktestServiceErrors
{
    public const string UnknownStrategy = "BACKTEST_UNKNOWN_STRATEGY";
    public const string InvalidPeriod = "BACKTEST_INVALID_PERIOD";
    public const string NotEnoughData = "BACKTEST_NOT_ENOUGH_DATA";
}

/// <summary>Runs backtests on stored candles and records every run.</summary>
public sealed class BacktestService(ICandleStore candles, IBacktestRunStore runs, FeatureEngine featureEngine, TimeProvider timeProvider)
{
    /// <summary>Longest period accepted in one run (keeps a synchronous request bounded).</summary>
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(3 * 366);

    /// <exception cref="Omega.Core.Persistence.PersistenceException">A store is unavailable.</exception>
    public async Task<Result<BacktestOutcome>> RunAsync(BacktestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var definition = StrategyCatalog.Find(request.StrategyName);
        if (definition is null)
        {
            return Fail(BacktestServiceErrors.UnknownStrategy,
                $"Unknown strategy '{request.StrategyName}'. Known: {string.Join(", ", StrategyCatalog.All.Select(d => d.Name))}.");
        }

        var length = request.Interval.ToTimeSpan();
        if (request.ToUtc <= request.FromUtc || request.ToUtc - request.FromUtc > MaxPeriod
            || request.FromUtc.UtcTicks % length.Ticks != 0 || request.ToUtc.UtcTicks % length.Ticks != 0)
        {
            return Fail(BacktestServiceErrors.InvalidPeriod,
                "The period must be aligned to the interval, end after it starts, and last at most 3 years.");
        }

        var config = definition.DefaultConfig with
        {
            FeeRate = request.FeeRate ?? definition.DefaultConfig.FeeRate,
            SpreadBps = request.SpreadBps ?? definition.DefaultConfig.SpreadBps,
            SlippageBps = request.SlippageBps ?? definition.DefaultConfig.SlippageBps,
        };

        // Load the warm-up before the period so the first candle of the period can already trade.
        var warmUpStart = request.FromUtc - (length * (featureEngine.FeatureSet.MaxLookback - 1));
        var history = await candles.GetRangeAsync(request.Symbol, request.Interval, warmUpStart, request.ToUtc, cancellationToken).ConfigureAwait(false);

        if (!history.Any(c => c.OpenTimeUtc >= request.FromUtc))
        {
            return Fail(BacktestServiceErrors.NotEnoughData,
                $"No stored {request.Symbol} {request.Interval.ToCode()} candles in the period. Import history first (MarketData:Backfill:HistoryStart).");
        }

        var result = new BacktestEngine(featureEngine, config).Run(history, definition.Create(), request.FromUtc);
        if (result.IsFailure)
        {
            return Result.Failure<BacktestOutcome>(result.Error!);
        }

        var previous = await runs.CountEvaluationsAsync(
            definition.Name, request.Symbol, request.Interval, request.FromUtc, request.ToUtc, cancellationToken).ConfigureAwait(false);

        var run = new BacktestRun(
            Guid.NewGuid(), timeProvider.GetUtcNow(), request.PeriodLabel, request.FromUtc, request.ToUtc,
            result.Value with { EquityCurve = DailyCurve(result.Value.EquityCurve) });

        await runs.SaveAsync(run, cancellationToken).ConfigureAwait(false);
        return Result.Success(new BacktestOutcome(run, previous, Notices(request, definition, run, previous)));
    }

    /// <summary>
    /// Holdout periods lose value as evidence every time they are looked at, whatever the label or costs of
    /// the earlier look; development periods are meant to be looked at repeatedly. Candidate strategies with
    /// few trades cannot be told apart from chance; benchmarks are exempt (buy and hold makes one trade by design).
    /// </summary>
    internal static IReadOnlyList<string> Notices(BacktestRequest request, StrategyDefinition definition, BacktestRun run, int previousEvaluations)
    {
        var notices = new List<string>();

        if (previousEvaluations > 0 && request.PeriodLabel.Contains("holdout", StringComparison.Ordinal))
        {
            notices.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"This holdout period had already been evaluated {previousEvaluations} time(s) with this strategy; each look reduces its value as out-of-sample evidence."));
        }

        if (!definition.IsBenchmark && run.Result.Metrics.TradeCount < MinimumTradesForStatistics)
        {
            notices.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Only {run.Result.Metrics.TradeCount} trade(s) (fewer than {MinimumTradesForStatistics}): the statistics cannot distinguish skill from chance."));
        }

        return notices;
    }

    /// <summary>Rule of thumb below which trade statistics are not interpreted.</summary>
    public const int MinimumTradesForStatistics = 30;

    /// <summary>
    /// Last point of every UTC day, plus the first point. Metrics are computed on the full curve before this;
    /// the full curve is reproducible by re-running (the engine is deterministic).
    /// </summary>
    internal static IReadOnlyList<EquityPoint> DailyCurve(IReadOnlyList<EquityPoint> curve)
    {
        if (curve.Count <= 2)
        {
            return curve;
        }

        var daily = new List<EquityPoint> { curve[0] };
        for (var i = 1; i < curve.Count; i++)
        {
            if (i == curve.Count - 1 || curve[i + 1].TimeUtc.UtcDateTime.Date != curve[i].TimeUtc.UtcDateTime.Date)
            {
                daily.Add(curve[i]);
            }
        }

        return daily;
    }

    private static Result<BacktestOutcome> Fail(string code, string message) =>
        Result.Failure<BacktestOutcome>(new Error(code, message));
}
