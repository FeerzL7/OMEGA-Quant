using Omega.Core.Results;

namespace Omega.Backtesting.MonteCarlo;

public enum MonteCarloMethod
{
    /// <summary>Trades drawn independently, with replacement.</summary>
    Bootstrap = 1,

    /// <summary>Consecutive blocks of trades drawn with replacement (circular), preserving streaks and short-range dependence.</summary>
    BlockBootstrap = 2,

    /// <summary>The same trades in a random order: terminal capital is fixed, path statistics (drawdown, streaks) vary.</summary>
    Shuffle = 3,
}

/// <summary>Scenario-analysis settings (CLAUDE.md §21). All of them are recorded with the result.</summary>
public sealed record MonteCarloOptions
{
    public MonteCarloMethod Method { get; init; } = MonteCarloMethod.Bootstrap;

    public int Paths { get; init; } = 10_000;

    /// <summary>Trades per path; null = as many as the source backtest had.</summary>
    public int? HorizonTrades { get; init; }

    /// <summary>Block length for <see cref="MonteCarloMethod.BlockBootstrap"/>.</summary>
    public int BlockLength { get; init; } = 5;

    /// <summary>A path is ruined if its equity ever falls to this fraction of the initial capital.</summary>
    public double RuinLevel { get; init; } = 0.5;

    /// <summary>Robustness: extra cost per side, in basis points of the position value, charged on every trade.</summary>
    public double ExtraCostBps { get; init; }

    /// <summary>Robustness: probability that a trade is missed (no fill, equity unchanged).</summary>
    public double SkipProbability { get; init; }

    public int Seed { get; init; } = 20_261_001;

    public const int MaxPaths = 100_000;
    public const int MaxHorizon = 10_000;
    public const long MaxSteps = 20_000_000;

    public IReadOnlyList<string> Validate(int sourceTrades)
    {
        var errors = new List<string>();
        var horizon = HorizonTrades ?? sourceTrades;
        if (!Enum.IsDefined(Method)) errors.Add("Unknown method.");
        if (Paths is < 100 or > MaxPaths) errors.Add($"Paths must be between 100 and {MaxPaths}.");
        if (horizon is < 1 or > MaxHorizon) errors.Add($"HorizonTrades must be between 1 and {MaxHorizon}.");
        if ((long)Paths * horizon > MaxSteps) errors.Add($"Paths × HorizonTrades must not exceed {MaxSteps}.");
        if (BlockLength < 1) errors.Add("BlockLength must be at least 1.");
        if (RuinLevel is <= 0 or >= 1) errors.Add("RuinLevel must be in (0, 1).");
        if (ExtraCostBps is < 0 or > 500) errors.Add("ExtraCostBps must be in [0, 500].");
        if (SkipProbability is < 0 or >= 1) errors.Add("SkipProbability must be in [0, 1).");
        if (Method == MonteCarloMethod.Shuffle && horizon != sourceTrades) errors.Add("Shuffle reorders the source trades: HorizonTrades must equal their number.");
        return errors;
    }
}

/// <summary>One trade as a scenario ingredient: its net return on the equity before it, and the position value as a fraction of that equity.</summary>
public sealed record TradeOutcome(double ReturnOnEquity, double NotionalFraction);

/// <summary>Summary of a simulated quantity across paths (percentiles: linear interpolation, numpy's default).</summary>
public sealed record Distribution(double Mean, double Min, double P5, double P25, double P50, double P75, double P95, double Max);

/// <summary>Equity percentiles across paths after a number of trades.</summary>
public sealed record EquityBand(int Trade, double P5, double P25, double P50, double P75, double P95);

/// <summary>Statistics of the original trade sequence, measured the same way as the simulated paths.</summary>
/// <param name="TerminalEquity">Equity after the last trade.</param>
/// <param name="MaxDrawdownDepth">Largest fall below the running peak, measured between trades (0.12 = 12 %).</param>
/// <param name="LongestLosingStreak">Longest run of consecutive losing trades.</param>
public sealed record PathStatistics(double TerminalEquity, double MaxDrawdownDepth, int LongestLosingStreak);

public sealed record MonteCarloResult(
    MonteCarloOptions Options,
    int SourceTrades,
    int HorizonTrades,
    double InitialCapital,
    Distribution TerminalEquity,
    Distribution TerminalReturn,
    Distribution MaxDrawdownDepth,
    Distribution LongestLosingStreak,
    double ProbabilityOfLoss,
    double ProbabilityOfRuin,
    double? KillSwitchDrawdownLimit,
    double? ProbabilityKillSwitch,
    PathStatistics Original,
    IReadOnlyList<EquityBand> EquityBands,
    IReadOnlyList<string> Assumptions);

public static class MonteCarloErrors
{
    public const string NoTrades = "MONTE_CARLO_NO_TRADES";
    public const string InvalidOptions = "MONTE_CARLO_INVALID_OPTIONS";
}

/// <summary>
/// Scenario analysis over the trades of a backtest (CLAUDE.md §21): it recombines past trades to show how wide the
/// range of outcomes, drawdowns and losing streaks could be. It is not a prediction and contains no information that
/// the backtest did not already contain.
/// </summary>
public static class MonteCarloSimulator
{
    private const int MaxBands = 100;

    public static readonly IReadOnlyList<string> Assumptions =
    [
        "Scenarios recombine the trades of one backtest; they cannot contain market behaviour that the backtest did not see.",
        "Bootstrap and shuffle treat trades as exchangeable; block bootstrap keeps short-range dependence only up to the block length.",
        "Equity is measured between trades: drawdowns inside a trade (adverse excursions) are not seen, so depths are lower bounds of what the account would show candle by candle.",
        "Daily-loss and losing-streak limits of the Risk Engine are not simulated (resampled trades have no dates); the kill-switch probability is the share of paths whose drawdown reaches the policy's limit.",
        "Each trade keeps its return relative to the equity before it (fixed-fractional sizing), so results compound.",
    ];

    /// <summary>
    /// Converts a backtest's trades into scenario ingredients. With one position at a time the account is flat between
    /// trades, so the equity before trade k is the initial capital plus the net results of the trades before it.
    /// </summary>
    public static IReadOnlyList<TradeOutcome> TradeOutcomes(BacktestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var equity = result.Config.InitialCapital;
        var outcomes = new List<TradeOutcome>(result.Trades.Count);
        foreach (var trade in result.Trades)
        {
            outcomes.Add(new TradeOutcome((double)(trade.NetPnl / equity), (double)(trade.Quantity * trade.EntryPrice / equity)));
            equity += trade.NetPnl;
        }

        return outcomes;
    }

    public static Result<MonteCarloResult> Run(IReadOnlyList<TradeOutcome> trades, double initialCapital, MonteCarloOptions options, double? killSwitchDrawdown = null)
    {
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(options);

        if (trades.Count == 0)
        {
            return Result.Failure<MonteCarloResult>(new Error(MonteCarloErrors.NoTrades, "The backtest has no trades to resample."));
        }

        var errors = options.Validate(trades.Count);
        if (errors.Count > 0)
        {
            return Result.Failure<MonteCarloResult>(new Error(MonteCarloErrors.InvalidOptions, string.Join(" ", errors)));
        }

        var horizon = options.HorizonTrades ?? trades.Count;
        var checkpoints = Checkpoints(horizon);
        var bandValues = checkpoints.Select(_ => new double[options.Paths]).ToArray();
        var terminal = new double[options.Paths];
        var depth = new double[options.Paths];
        var streak = new double[options.Paths];
        var ruined = 0;
        var killSwitch = 0;

        var random = new Random(options.Seed);
        var extraCost = 2 * options.ExtraCostBps / 10_000;
        var order = new int[horizon];

        for (var path = 0; path < options.Paths; path++)
        {
            FillOrder(order, trades.Count, options, random);

            double equity = initialCapital, peak = initialCapital, maxDepth = 0;
            int currentStreak = 0, longestStreak = 0, checkpoint = 0;
            var isRuined = false;

            if (checkpoints[0] == 0)
            {
                bandValues[0][path] = equity;
                checkpoint = 1;
            }

            for (var step = 0; step < horizon; step++)
            {
                if (options.SkipProbability == 0 || random.NextDouble() >= options.SkipProbability)
                {
                    var trade = trades[order[step]];
                    var r = trade.ReturnOnEquity - (extraCost * trade.NotionalFraction);
                    equity = Math.Max(0, equity * (1 + r));
                    currentStreak = r < 0 ? currentStreak + 1 : 0;
                    longestStreak = Math.Max(longestStreak, currentStreak);
                    peak = Math.Max(peak, equity);
                    maxDepth = Math.Max(maxDepth, 1 - (equity / peak));
                    isRuined |= equity <= options.RuinLevel * initialCapital;
                }

                if (checkpoint < checkpoints.Length && checkpoints[checkpoint] == step + 1)
                {
                    bandValues[checkpoint][path] = equity;
                    checkpoint++;
                }
            }

            terminal[path] = equity;
            depth[path] = maxDepth;
            streak[path] = longestStreak;
            ruined += isRuined ? 1 : 0;
            killSwitch += killSwitchDrawdown is { } limit && maxDepth >= limit ? 1 : 0;
        }

        var bands = checkpoints.Select((trade, i) =>
        {
            Array.Sort(bandValues[i]);
            var v = bandValues[i];
            return new EquityBand(trade, Percentile(v, 0.05), Percentile(v, 0.25), Percentile(v, 0.50), Percentile(v, 0.75), Percentile(v, 0.95));
        }).ToList();

        var losses = terminal.Count(e => e < initialCapital);

        return Result.Success(new MonteCarloResult(
            options,
            trades.Count,
            horizon,
            initialCapital,
            Summarize(terminal),
            Summarize(terminal.Select(e => (e / initialCapital) - 1).ToArray()),
            Summarize(depth),
            Summarize(streak),
            (double)losses / options.Paths,
            (double)ruined / options.Paths,
            killSwitchDrawdown,
            killSwitchDrawdown is null ? null : (double)killSwitch / options.Paths,
            Original(trades, initialCapital),
            bands,
            Assumptions));
    }

    /// <summary>Linear-interpolation percentile of a sorted array (numpy "linear", R type 7).</summary>
    public static double Percentile(double[] sorted, double p)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Length == 0)
        {
            throw new ArgumentException("Empty sample.", nameof(sorted));
        }

        var h = (sorted.Length - 1) * p;
        var lower = (int)Math.Floor(h);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + ((h - lower) * (sorted[upper] - sorted[lower]));
    }

    private static Distribution Summarize(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return new Distribution(
            sorted.Average(), sorted[0], Percentile(sorted, 0.05), Percentile(sorted, 0.25), Percentile(sorted, 0.50),
            Percentile(sorted, 0.75), Percentile(sorted, 0.95), sorted[^1]);
    }

    private static void FillOrder(int[] order, int sourceCount, MonteCarloOptions options, Random random)
    {
        switch (options.Method)
        {
            case MonteCarloMethod.Shuffle:
                for (var i = 0; i < order.Length; i++)
                {
                    order[i] = i;
                }

                for (var i = order.Length - 1; i > 0; i--)
                {
                    var j = random.Next(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }

                break;

            case MonteCarloMethod.BlockBootstrap:
                for (var i = 0; i < order.Length;)
                {
                    var start = random.Next(sourceCount);
                    for (var k = 0; k < options.BlockLength && i < order.Length; k++, i++)
                    {
                        order[i] = (start + k) % sourceCount;   // circular: no edge bias
                    }
                }

                break;

            default:
                for (var i = 0; i < order.Length; i++)
                {
                    order[i] = random.Next(sourceCount);
                }

                break;
        }
    }

    private static PathStatistics Original(IReadOnlyList<TradeOutcome> trades, double initialCapital)
    {
        double equity = initialCapital, peak = initialCapital, maxDepth = 0;
        int current = 0, longest = 0;
        foreach (var trade in trades)
        {
            equity *= 1 + trade.ReturnOnEquity;
            current = trade.ReturnOnEquity < 0 ? current + 1 : 0;
            longest = Math.Max(longest, current);
            peak = Math.Max(peak, equity);
            maxDepth = Math.Max(maxDepth, 1 - (equity / peak));
        }

        return new PathStatistics(equity, maxDepth, longest);
    }

    /// <summary>Up to 100 evenly spaced trade counts from 0 to the horizon (both included).</summary>
    private static int[] Checkpoints(int horizon)
    {
        var count = Math.Min(horizon, MaxBands);
        return Enumerable.Range(0, count + 1).Select(k => (int)Math.Round((double)k * horizon / count)).Distinct().ToArray();
    }
}
