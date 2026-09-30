using Omega.Core.MarketData;

namespace Omega.Backtesting;

/// <summary>A stored backtest (experiment record, CLAUDE.md §22).</summary>
/// <param name="Id">Identifier.</param>
/// <param name="CreatedAtUtc">When it was run.</param>
/// <param name="PeriodLabel">Evaluation period it belongs to, for example <c>development</c> or <c>holdout</c>.</param>
/// <param name="TradingStartUtc">First candle that could trade.</param>
/// <param name="TradingEndUtc">End of the evaluated period (exclusive).</param>
/// <param name="Result">The result. The stored equity curve may be resampled (see the service that saves it).</param>
public sealed record BacktestRun(
    Guid Id, DateTimeOffset CreatedAtUtc, string PeriodLabel, DateTimeOffset TradingStartUtc, DateTimeOffset TradingEndUtc, BacktestResult Result);

public sealed record BacktestRunSummary(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    string PeriodLabel,
    string StrategyName,
    string StrategyVersion,
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset TradingStartUtc,
    DateTimeOffset TradingEndUtc,
    string DatasetSha256,
    int TradeCount,
    double TotalReturn);

/// <summary>Durable, append-only record of backtests. Implementations throw <c>PersistenceException</c> on failures.</summary>
public interface IBacktestRunStore
{
    Task SaveAsync(BacktestRun run, CancellationToken cancellationToken);

    Task<BacktestRun?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Most recent first.</summary>
    Task<IReadOnlyList<BacktestRunSummary>> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// How many times this strategy was already evaluated on exactly this symbol, interval and period.
    /// Repeated evaluations of a holdout period erode its value as an out-of-sample test.
    /// </summary>
    Task<int> CountEvaluationsAsync(
        string strategyName, string symbol, CandleInterval interval, DateTimeOffset tradingStartUtc, DateTimeOffset tradingEndUtc,
        CancellationToken cancellationToken);
}
