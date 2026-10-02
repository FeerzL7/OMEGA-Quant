using Omega.Backtesting;

namespace Omega.Api.Contracts;

/// <summary>Body of <c>POST /api/backtests</c>.</summary>
/// <param name="Strategy">Strategy name (see <c>GET /api/strategies</c>).</param>
/// <param name="Symbol">Symbol, for example BTCUSDT.</param>
/// <param name="Interval">Interval code, for example 5m.</param>
/// <param name="FromUtc">First candle that may trade (inclusive, aligned to the interval).</param>
/// <param name="ToUtc">End of the period (exclusive, aligned to the interval).</param>
/// <param name="PeriodLabel">Evaluation period: lower-case words separated by hyphens, for example <c>development</c> or <c>holdout</c>.</param>
/// <param name="FeeRate">Optional commission override (cost stress test).</param>
/// <param name="SpreadBps">Optional spread override.</param>
/// <param name="SlippageBps">Optional slippage override.</param>
/// <param name="ModelId">Registered model, required by the <c>model-ev</c> strategy (see <c>GET /api/models</c>).</param>
/// <param name="MinExpectedReturn">For <c>model-ev</c>: minimum expected return after costs to enter (default 0).</param>
public sealed record BacktestRunRequest(
    string? Strategy,
    string? Symbol,
    string? Interval,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    string? PeriodLabel,
    decimal? FeeRate,
    decimal? SpreadBps,
    decimal? SlippageBps,
    string? ModelId = null,
    double? MinExpectedReturn = null);

/// <summary>A stored backtest with its full result (equity curve resampled to one point per day).</summary>
public sealed record BacktestRunResponse(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    string PeriodLabel,
    DateTimeOffset TradingStartUtc,
    DateTimeOffset TradingEndUtc,
    IReadOnlyList<string> Notices,
    BacktestResult Result)
{
    public static BacktestRunResponse From(BacktestRun run, IReadOnlyList<string>? notices = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new BacktestRunResponse(run.Id, run.CreatedAtUtc, run.PeriodLabel, run.TradingStartUtc, run.TradingEndUtc, notices ?? [], run.Result);
    }
}

/// <summary>Entry of <c>GET /api/strategies</c>.</summary>
public sealed record StrategyResponse(string Name, string Description, BacktestConfig DefaultConfig, bool IsBenchmark, bool RequiresModelId = false);
