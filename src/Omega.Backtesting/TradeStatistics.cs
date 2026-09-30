namespace Omega.Backtesting;

/// <summary>
/// Is the average trade distinguishable from zero? Computed on each trade's net return on committed capital
/// (<see cref="BacktestTrade.ReturnOnCost"/>), after all costs.
/// </summary>
/// <remarks>
/// Both tests assume independent trades, which trading results rarely are (regimes, overlapping conditions);
/// treat them as a first filter, not as proof. A strategy with few trades cannot be judged.
/// </remarks>
/// <param name="TradeCount">Trades used.</param>
/// <param name="MeanReturn">Mean net return per trade.</param>
/// <param name="StandardDeviation">Sample standard deviation of the trade returns.</param>
/// <param name="TStatistic">mean / (std / √n); roughly, |t| &gt; 2 is unlikely by chance if trades were independent.</param>
/// <param name="BootstrapCi95Lower">2.5th percentile of the bootstrapped mean (10 000 resamples, fixed seed).</param>
/// <param name="BootstrapCi95Upper">97.5th percentile of the bootstrapped mean.</param>
public sealed record TradeStatistics(
    int TradeCount,
    double? MeanReturn,
    double? StandardDeviation,
    double? TStatistic,
    double? BootstrapCi95Lower,
    double? BootstrapCi95Upper)
{
    public const int BootstrapResamples = 10_000;

    /// <summary>Fixed so that results are reproducible.</summary>
    public const int BootstrapSeed = 20_260_930;

    public static TradeStatistics From(IReadOnlyList<BacktestTrade> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        var returns = trades.Select(t => (double)t.ReturnOnCost).ToArray();
        if (returns.Length < 2)
        {
            return new TradeStatistics(returns.Length, returns.Length == 1 ? returns[0] : null, null, null, null, null);
        }

        var mean = returns.Average();
        var std = Math.Sqrt(returns.Sum(r => (r - mean) * (r - mean)) / (returns.Length - 1));
        double? t = std == 0 ? null : mean / (std / Math.Sqrt(returns.Length));

        var random = new Random(BootstrapSeed);
        var means = new double[BootstrapResamples];
        for (var b = 0; b < BootstrapResamples; b++)
        {
            var sum = 0.0;
            for (var k = 0; k < returns.Length; k++)
            {
                sum += returns[random.Next(returns.Length)];
            }

            means[b] = sum / returns.Length;
        }

        Array.Sort(means);
        return new TradeStatistics(
            returns.Length, mean, std, t,
            means[(int)(0.025 * (BootstrapResamples - 1))],
            means[(int)(0.975 * (BootstrapResamples - 1))]);
    }
}
