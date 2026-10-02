using System.Globalization;
using Omega.Backtesting;
using Omega.Core.MarketData;
using Omega.Core.Results;
using Omega.Core.Trading;
using Omega.Features;
using Omega.Strategy;
using Omega.Strategy.Models;

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
/// <param name="ModelId">Registered model for the <c>model-ev</c> strategy.</param>
/// <param name="MinExpectedReturn">Minimum expected return to enter, for <c>model-ev</c> (default 0).</param>
public sealed record BacktestRequest(
    string StrategyName,
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string PeriodLabel,
    decimal? FeeRate = null,
    decimal? SpreadBps = null,
    decimal? SlippageBps = null,
    string? ModelId = null,
    double? MinExpectedReturn = null);

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
    public const string ModelUnavailable = "BACKTEST_MODEL_UNAVAILABLE";
}

/// <summary>Runs backtests on stored candles and records every run.</summary>
public sealed class BacktestService(
    ICandleStore candles, IBacktestRunStore runs, FeatureEngine featureEngine, TimeProvider timeProvider, string? modelsDirectory = null)
{
    /// <summary>Strategy name for model-driven backtests (needs a model id).</summary>
    public const string ModelStrategy = ModelExpectedValueStrategy.NamePrefix;

    /// <summary>Longest period accepted in one run (keeps a synchronous request bounded).</summary>
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(3 * 366);

    /// <exception cref="Omega.Core.Persistence.PersistenceException">A store is unavailable.</exception>
    public async Task<Result<BacktestOutcome>> RunAsync(BacktestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.StrategyName == ModelStrategy)
        {
            return await RunModelAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var definition = StrategyCatalog.Find(request.StrategyName);
        if (definition is null)
        {
            return Fail(BacktestServiceErrors.UnknownStrategy,
                $"Unknown strategy '{request.StrategyName}'. Known: {string.Join(", ", StrategyCatalog.All.Select(d => d.Name).Append(ModelStrategy))}.");
        }

        return await RunAsync(request, definition.Create(), definition.DefaultConfig, definition.IsBenchmark, extraNotices: [], cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<BacktestOutcome>> RunModelAsync(BacktestRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ModelId) || request.ModelId.Contains("..", StringComparison.Ordinal)
            || request.ModelId.IndexOfAny(['/', '\\']) >= 0 || modelsDirectory is null)
        {
            return Fail(BacktestServiceErrors.ModelUnavailable, "The model-ev strategy needs a valid registered model id.");
        }

        var loaded = ModelPackage.Load(Path.Combine(modelsDirectory, request.ModelId));
        if (loaded.IsFailure)
        {
            return Fail(BacktestServiceErrors.ModelUnavailable, loaded.Error!.Message);
        }

        using var package = loaded.Value;
        var config = new BacktestConfig { MaxHoldingCandles = package.HorizonCandles };
        var costs = new TradingCosts(
            request.FeeRate ?? config.FeeRate, request.SpreadBps ?? config.SpreadBps, request.SlippageBps ?? config.SlippageBps);
        var strategy = new ModelExpectedValueStrategy(
            package, package.Calibrator, package.Profile, costs, package.StopAtr, package.TargetAtr, request.MinExpectedReturn ?? 0);

        var notices = new List<string>();
        if (Overlaps(package.TrainingPeriod, request.FromUtc, request.ToUtc))
        {
            notices.Add($"The model was trained on part of this period ({package.TrainingPeriod}): the result is in-sample and is not evidence of out-of-sample performance.");
        }

        return await RunAsync(request, strategy, config, isBenchmark: false, notices, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<BacktestOutcome>> RunAsync(
        BacktestRequest request, IStrategy strategy, BacktestConfig defaultConfig, bool isBenchmark, IReadOnlyList<string> extraNotices,
        CancellationToken cancellationToken)
    {
        var length = request.Interval.ToTimeSpan();
        if (request.ToUtc <= request.FromUtc || request.ToUtc - request.FromUtc > MaxPeriod
            || request.FromUtc.UtcTicks % length.Ticks != 0 || request.ToUtc.UtcTicks % length.Ticks != 0)
        {
            return Fail(BacktestServiceErrors.InvalidPeriod,
                "The period must be aligned to the interval, end after it starts, and last at most 3 years.");
        }

        var config = defaultConfig with
        {
            FeeRate = request.FeeRate ?? defaultConfig.FeeRate,
            SpreadBps = request.SpreadBps ?? defaultConfig.SpreadBps,
            SlippageBps = request.SlippageBps ?? defaultConfig.SlippageBps,
        };

        // Load the warm-up before the period so the first candle of the period can already trade.
        var warmUpStart = request.FromUtc - (length * (featureEngine.FeatureSet.MaxLookback - 1));
        var history = await candles.GetRangeAsync(request.Symbol, request.Interval, warmUpStart, request.ToUtc, cancellationToken).ConfigureAwait(false);

        if (!history.Any(c => c.OpenTimeUtc >= request.FromUtc))
        {
            return Fail(BacktestServiceErrors.NotEnoughData,
                $"No stored {request.Symbol} {request.Interval.ToCode()} candles in the period. Import history first (MarketData:Backfill:HistoryStart).");
        }

        var result = new BacktestEngine(featureEngine, config).Run(history, strategy, request.FromUtc);
        if (result.IsFailure)
        {
            return Result.Failure<BacktestOutcome>(result.Error!);
        }

        var previous = await runs.CountEvaluationsAsync(
            strategy.Identity.Name, request.Symbol, request.Interval, request.FromUtc, request.ToUtc, cancellationToken).ConfigureAwait(false);

        var run = new BacktestRun(
            Guid.NewGuid(), timeProvider.GetUtcNow(), request.PeriodLabel, request.FromUtc, request.ToUtc,
            result.Value with { EquityCurve = DailyCurve(result.Value.EquityCurve) });

        await runs.SaveAsync(run, cancellationToken).ConfigureAwait(false);
        return Result.Success(new BacktestOutcome(run, previous, [.. extraNotices, .. Notices(request, isBenchmark, run, previous)]));
    }

    /// <summary>True when [from, to) overlaps a "start..end" training period.</summary>
    internal static bool Overlaps(string trainingPeriod, DateTimeOffset from, DateTimeOffset to)
    {
        var parts = trainingPeriod.Split("..");
        if (parts.Length != 2
            || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)
            || !DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end))
        {
            return true; // unknown training period: assume the worst
        }

        return from < end && start < to;
    }

    /// <summary>
    /// Holdout periods lose value as evidence every time they are looked at, whatever the label or costs of
    /// the earlier look; development periods are meant to be looked at repeatedly. Candidate strategies with
    /// few trades cannot be told apart from chance; benchmarks are exempt (buy and hold makes one trade by design).
    /// </summary>
    internal static IReadOnlyList<string> Notices(BacktestRequest request, bool isBenchmark, BacktestRun run, int previousEvaluations)
    {
        var notices = new List<string>();

        if (previousEvaluations > 0 && request.PeriodLabel.Contains("holdout", StringComparison.Ordinal))
        {
            notices.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"This holdout period had already been evaluated {previousEvaluations} time(s) with this strategy; each look reduces its value as out-of-sample evidence."));
        }

        if (!isBenchmark && run.Result.Metrics.TradeCount < MinimumTradesForStatistics)
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
