using System.Globalization;
using Omega.Backtesting;
using Omega.Backtesting.MonteCarlo;
using Omega.Core.Results;

namespace Omega.Application.Backtesting;

/// <summary>A scenario analysis and how to read it.</summary>
/// <param name="RunId">Backtest whose trades were recombined.</param>
/// <param name="Strategy">Strategy of that backtest.</param>
/// <param name="PeriodLabel">Evaluation period of that backtest.</param>
/// <param name="BacktestMaxDrawdownDepth">Drawdown of the backtest measured candle by candle (for comparison with the between-trades depths).</param>
/// <param name="Result">The simulation.</param>
/// <param name="Notices">Cautions for the reader.</param>
public sealed record MonteCarloOutcome(
    Guid RunId, string Strategy, string PeriodLabel, double BacktestMaxDrawdownDepth, MonteCarloResult Result, IReadOnlyList<string> Notices);

public static class MonteCarloServiceErrors
{
    public const string RunNotFound = "MONTE_CARLO_RUN_NOT_FOUND";
}

/// <summary>Scenario analysis (Phase 11) over a stored backtest. Read-only; reproducible from (run, options, seed).</summary>
public sealed class MonteCarloService(IBacktestRunStore runs)
{
    /// <exception cref="Omega.Core.Persistence.PersistenceException">The store is unavailable.</exception>
    public async Task<Result<MonteCarloOutcome>> RunAsync(Guid runId, MonteCarloOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var run = await runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Result.Failure<MonteCarloOutcome>(new Error(MonteCarloServiceErrors.RunNotFound, $"Backtest run {runId} does not exist."));
        }

        var backtest = run.Result;
        var simulation = MonteCarloSimulator.Run(
            MonteCarloSimulator.TradeOutcomes(backtest),
            (double)backtest.Config.InitialCapital,
            options,
            backtest.Risk is { } risk ? (double)risk.MaxDrawdown : null);

        if (simulation.IsFailure)
        {
            return Result.Failure<MonteCarloOutcome>(simulation.Error!);
        }

        var result = simulation.Value;
        var backtestDepth = -backtest.Metrics.MaxDrawdown;
        var notices = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"These scenarios recombine the {result.SourceTrades} trades of backtest {runId} ({backtest.Strategy.Name}, period '{run.PeriodLabel}'). They describe the range of outcomes those trades allow; they are not a forecast."),
        };

        if (result.SourceTrades < BacktestService.MinimumTradesForStatistics)
        {
            notices.Add(string.Create(CultureInfo.InvariantCulture,
                $"Only {result.SourceTrades} source trades: the resampled distributions are dominated by those few outcomes."));
        }

        // Runs stored before Phase 10 have no recorded risk policy; no limit is assumed for them.
        if (backtest.Risk is null)
        {
            notices.Add("This backtest predates the recording of risk policies, so the kill-switch probability is not computed. Re-run the backtest to obtain it.");
        }

        if (backtestDepth > result.Original.MaxDrawdownDepth + 1e-9)
        {
            notices.Add(string.Create(CultureInfo.InvariantCulture,
                $"Candle by candle the backtest fell {backtestDepth:P2} below its peak, deeper than the {result.Original.MaxDrawdownDepth:P2} seen between trades: simulated depths are lower bounds."));
        }

        return Result.Success(new MonteCarloOutcome(runId, backtest.Strategy.Name, run.PeriodLabel, backtestDepth, result, notices));
    }
}
