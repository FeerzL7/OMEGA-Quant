namespace Omega.Backtesting;

/// <summary>
/// Summary statistics. Definitions are documented in docs/BACKTESTING.md; ratios that are undefined
/// (for example no losing trades) are null rather than infinite.
/// </summary>
public sealed record BacktestMetrics(
    decimal InitialCapital,
    decimal FinalEquity,
    decimal NetProfit,
    double TotalReturn,
    int TradeCount,
    int WinningTrades,
    double? WinRate,
    decimal GrossProfit,
    decimal GrossLoss,
    double? ProfitFactor,
    decimal? Expectancy,
    double MaxDrawdown,
    double? Sharpe,
    double? Sortino,
    decimal TotalFees,
    double Exposure)
{
    /// <param name="initialCapital">Starting capital.</param>
    /// <param name="curve">Equity at every candle close.</param>
    /// <param name="trades">Completed trades.</param>
    /// <param name="candlesInPosition">Candles that ended with an open position (for exposure).</param>
    /// <param name="periodsPerYear">Candles per year, for annualizing Sharpe and Sortino.</param>
    public static BacktestMetrics From(
        decimal initialCapital, IReadOnlyList<EquityPoint> curve, IReadOnlyList<BacktestTrade> trades, int candlesInPosition, double periodsPerYear)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(trades);

        var finalEquity = curve.Count == 0 ? initialCapital : curve[^1].Equity;
        var wins = trades.Where(t => t.NetPnl > 0).ToList();
        var grossProfit = wins.Sum(t => t.NetPnl);
        var grossLoss = trades.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);

        // Per-candle simple returns of the equity curve (starting from the initial capital).
        var returns = new double[curve.Count];
        var previous = (double)initialCapital;
        for (var i = 0; i < curve.Count; i++)
        {
            var equity = (double)curve[i].Equity;
            returns[i] = (equity / previous) - 1;
            previous = equity;
        }

        return new BacktestMetrics(
            InitialCapital: initialCapital,
            FinalEquity: finalEquity,
            NetProfit: finalEquity - initialCapital,
            TotalReturn: (double)(finalEquity / initialCapital) - 1,
            TradeCount: trades.Count,
            WinningTrades: wins.Count,
            WinRate: trades.Count == 0 ? null : (double)wins.Count / trades.Count,
            GrossProfit: grossProfit,
            GrossLoss: grossLoss,
            ProfitFactor: grossLoss == 0 ? null : (double)(grossProfit / -grossLoss),
            Expectancy: trades.Count == 0 ? null : trades.Average(t => t.NetPnl),
            MaxDrawdown: curve.Count == 0 ? 0 : (double)curve.Min(p => p.Drawdown),
            Sharpe: AnnualizedSharpe(returns, periodsPerYear),
            Sortino: AnnualizedSortino(returns, periodsPerYear),
            TotalFees: trades.Sum(t => t.EntryFee + t.ExitFee),
            Exposure: curve.Count == 0 ? 0 : (double)candlesInPosition / curve.Count);
    }

    private static double? AnnualizedSharpe(double[] returns, double periodsPerYear)
    {
        if (returns.Length < 2)
        {
            return null;
        }

        var mean = returns.Average();
        var std = Math.Sqrt(returns.Sum(r => (r - mean) * (r - mean)) / (returns.Length - 1));
        return std == 0 ? null : mean / std * Math.Sqrt(periodsPerYear);
    }

    private static double? AnnualizedSortino(double[] returns, double periodsPerYear)
    {
        if (returns.Length < 2)
        {
            return null;
        }

        var downside = Math.Sqrt(returns.Sum(r => Math.Min(r, 0) * Math.Min(r, 0)) / returns.Length);
        return downside == 0 ? null : returns.Average() / downside * Math.Sqrt(periodsPerYear);
    }
}
