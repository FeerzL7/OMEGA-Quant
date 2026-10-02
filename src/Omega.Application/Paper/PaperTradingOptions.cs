using Omega.Application.Backtesting;
using Omega.Core.Results;
using Omega.Core.Trading;
using Omega.Strategy;
using Omega.Strategy.Models;

namespace Omega.Application.Paper;

/// <summary>Section <c>Paper</c>: which strategy the paper session runs and under which assumptions.</summary>
public sealed class PaperTradingOptions
{
    public const string SectionName = "Paper";

    /// <summary>Session name (lower-case words and hyphens). A new configuration needs a new name.</summary>
    public string Session { get; set; } = "paper-1";

    /// <summary>A catalog strategy (for example baseline-ema-trend) or model-ev.</summary>
    public string Strategy { get; set; } = "baseline-ema-trend";

    /// <summary>Registered model for model-ev.</summary>
    public string? ModelId { get; set; }

    public double MinExpectedReturn { get; set; }

    public decimal InitialCapital { get; set; } = 10_000m;

    public decimal FeeRate { get; set; } = 0.001m;

    public decimal SpreadBps { get; set; } = 1m;

    public decimal SlippageBps { get; set; } = 2m;

    /// <summary>How often the worker looks for new closed candles and operator commands.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A candle processed later than this after its close is stale: it can close positions but not open them.</summary>
    public TimeSpan FreshnessGrace { get; set; } = TimeSpan.FromMinutes(1);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Session) || !System.Text.RegularExpressions.Regex.IsMatch(Session, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            errors.Add("Paper:Session must be lower-case words separated by hyphens.");
        if (string.IsNullOrWhiteSpace(Strategy)) errors.Add("Paper:Strategy is required.");
        if (Strategy == BacktestService.ModelStrategy && string.IsNullOrWhiteSpace(ModelId)) errors.Add("Paper:ModelId is required for model-ev.");
        if (InitialCapital <= 0) errors.Add("Paper:InitialCapital must be positive.");
        if (FeeRate is < 0 or >= 0.1m || SpreadBps < 0 || SlippageBps < 0) errors.Add("Paper costs must be non-negative (FeeRate below 0.1).");
        if (PollInterval < TimeSpan.FromSeconds(1)) errors.Add("Paper:PollInterval must be at least 1 second.");
        if (FreshnessGrace <= TimeSpan.Zero) errors.Add("Paper:FreshnessGrace must be positive.");
        return errors;
    }

    public TradingCosts Costs => new(FeeRate, SpreadBps, SlippageBps);
}

/// <summary>A strategy ready to run, with the timeout of its label and the resources to release when it stops.</summary>
public sealed record PaperStrategy(IStrategy Strategy, int? MaxHoldingCandles, IDisposable? Resource);

/// <summary>Builds the strategy of a paper session: a candidate from the catalog, or model-ev from a verified model package.</summary>
public static class PaperStrategyFactory
{
    public static Result<PaperStrategy> Create(PaperTradingOptions options, string? modelsDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Strategy == BacktestService.ModelStrategy)
        {
            if (modelsDirectory is null || options.ModelId is null || options.ModelId.Contains("..", StringComparison.Ordinal) || options.ModelId.IndexOfAny(['/', '\\']) >= 0)
            {
                return Result.Failure<PaperStrategy>(new Error(ModelPackageErrors.NotFound, "model-ev needs a registered Paper:ModelId and Research:ModelsDirectory."));
            }

            var package = ModelPackage.Load(Path.Combine(modelsDirectory, options.ModelId));
            if (package.IsFailure)
            {
                return Result.Failure<PaperStrategy>(package.Error!);
            }

            var p = package.Value;
            return Result.Success(new PaperStrategy(
                new ModelExpectedValueStrategy(p, p.Calibrator, p.Profile, options.Costs, p.StopAtr, p.TargetAtr, options.MinExpectedReturn),
                p.HorizonCandles,
                p));
        }

        var definition = StrategyCatalog.Find(options.Strategy);
        if (definition is null || definition.IsBenchmark)
        {
            return Result.Failure<PaperStrategy>(new Error(BacktestServiceErrors.UnknownStrategy,
                $"'{options.Strategy}' is not a candidate strategy for paper trading (benchmarks are for comparison only)."));
        }

        return Result.Success(new PaperStrategy(definition.Create(), definition.DefaultConfig.MaxHoldingCandles, null));
    }
}
