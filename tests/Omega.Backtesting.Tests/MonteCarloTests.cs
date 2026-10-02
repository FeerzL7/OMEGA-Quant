using Omega.Backtesting.MonteCarlo;
using Omega.Backtesting.Tests.TestSupport;
using Omega.Core.Trading;
using static Omega.Backtesting.Tests.TestSupport.Bars;

namespace Omega.Backtesting.Tests;

public class MonteCarloTests
{
    private const double Capital = 10_000;

    [Theory]
    [InlineData(0.25, 1.75)]
    [InlineData(0.50, 2.50)]
    [InlineData(0.95, 3.85)]
    [InlineData(0.00, 1.00)]
    [InlineData(1.00, 4.00)]
    public void Percentiles_match_numpy_linear_interpolation(double p, double expected)
    {
        Assert.Equal(expected, MonteCarloSimulator.Percentile([1, 2, 3, 4], p), 12);
    }

    [Fact]
    public void Trade_outcomes_recompose_the_backtest_final_equity()
    {
        var candles = new[]
        {
            Flat(0, 100m), Flat(1, 100m), At(2, 100m, 106m, 99m, 105m),      // +5 % target
            Flat(3, 105m), At(4, 105m, 106m, 101m, 102m),                    // stopped
            Flat(5, 102m), Flat(6, 102m), At(7, 102m, 110m, 101m, 109m),
        };
        var strategy = new ScriptedStrategy(new()
        {
            [1] = Signal.Long(97m, 105m), [3] = Signal.Long(102m, 110m), [6] = Signal.Long(99m, 108m),
        });
        var result = Engines.Create(new BacktestConfig()).Run(candles, strategy).Value;

        var outcomes = MonteCarloSimulator.TradeOutcomes(result);
        var recomposed = outcomes.Aggregate(Capital, (equity, t) => equity * (1 + t.ReturnOnEquity));

        Assert.Equal(3, outcomes.Count);
        Assert.Equal((double)result.Metrics.FinalEquity, recomposed, 6);
        Assert.All(outcomes, t => Assert.InRange(t.NotionalFraction, 0.01, 1.0));
    }

    [Fact]
    public void A_constant_return_gives_the_same_compounded_path_everywhere()
    {
        var trades = Enumerable.Repeat(new TradeOutcome(0.01, 0.5), 20).ToArray();

        var result = Run(trades, new MonteCarloOptions { Paths = 500 });

        Assert.Equal(Capital * Math.Pow(1.01, 20), result.TerminalEquity.Min, 6);
        Assert.Equal(Capital * Math.Pow(1.01, 20), result.TerminalEquity.Max, 6);
        Assert.Equal(0, result.ProbabilityOfLoss);
        Assert.Equal(0, result.ProbabilityOfRuin);
        Assert.Equal(0, result.MaxDrawdownDepth.Max);
    }

    [Fact]
    public void Shuffling_changes_the_path_but_never_the_terminal_capital()
    {
        var trades = SourceTrades();

        var result = Run(trades, new MonteCarloOptions { Method = MonteCarloMethod.Shuffle, Paths = 2_000 });

        Assert.Equal(result.Original.TerminalEquity, result.TerminalEquity.Min, 6);
        Assert.Equal(result.Original.TerminalEquity, result.TerminalEquity.Max, 6);

        // No order can fall less than the worst single trade (1.2 %)...
        Assert.True(result.MaxDrawdownDepth.Min >= 0.012 - 1e-12);
        // ...and the source order, with every loss neatly separated by wins, reaches exactly that minimum: it was an
        // unusually lucky sequence. Reshuffling the same trades shows how much deeper the drawdown could have been.
        Assert.Equal(0.012, result.Original.MaxDrawdownDepth, 12);
        Assert.True(result.Original.MaxDrawdownDepth < result.MaxDrawdownDepth.P5);
        Assert.True(result.MaxDrawdownDepth.P95 > 3 * result.Original.MaxDrawdownDepth);
    }

    [Fact]
    public void Bootstrap_agrees_with_the_closed_form_expectation_and_the_exact_loss_probability()
    {
        // Two equally likely outcomes: +2 % and -1 %, 50 trades per path.
        var trades = new[] { new TradeOutcome(0.02, 1), new TradeOutcome(-0.01, 1) };
        const int horizon = 50, paths = 40_000;

        var result = Run(trades, new MonteCarloOptions { Paths = paths, HorizonTrades = horizon, Seed = 7 });

        // E[terminal] = C × (1 + E[r])^T for independent trades.
        var expectedMean = Capital * Math.Pow(1.005, horizon);
        var variance = Capital * Capital * (Math.Pow(0.5 * ((1.02 * 1.02) + (0.99 * 0.99)), horizon) - Math.Pow(1.005, 2 * horizon));
        Assert.InRange(result.TerminalEquity.Mean, expectedMean - (4 * Math.Sqrt(variance / paths)), expectedMean + (4 * Math.Sqrt(variance / paths)));

        // Loss iff 1.02^k × 0.99^(50-k) < 1, k ~ Binomial(50, 0.5): exact probability by summation.
        var exact = Enumerable.Range(0, horizon + 1)
            .Where(k => Math.Pow(1.02, k) * Math.Pow(0.99, horizon - k) < 1)
            .Sum(k => Binomial(horizon, k) / Math.Pow(2, horizon));
        var tolerance = 4 * Math.Sqrt(exact * (1 - exact) / paths);
        Assert.InRange(result.ProbabilityOfLoss, exact - tolerance, exact + tolerance);
    }

    [Fact]
    public void Block_bootstrap_keeps_losing_streaks_that_independent_draws_break_up()
    {
        // Ten losses then ten wins, five times: streaks are a real feature of this sequence.
        var trades = Enumerable.Range(0, 100).Select(i => new TradeOutcome((i / 10) % 2 == 0 ? -0.005 : 0.006, 1)).ToArray();

        var independent = Run(trades, new MonteCarloOptions { Paths = 3_000 });
        var blocks = Run(trades, new MonteCarloOptions { Method = MonteCarloMethod.BlockBootstrap, BlockLength = 10, Paths = 3_000 });

        Assert.True(blocks.LongestLosingStreak.P50 >= independent.LongestLosingStreak.P50 + 3,
            $"block median {blocks.LongestLosingStreak.P50} vs independent {independent.LongestLosingStreak.P50}");
        Assert.True(blocks.MaxDrawdownDepth.P50 > independent.MaxDrawdownDepth.P50);
    }

    [Fact]
    public void Extra_costs_and_missed_trades_are_robustness_stresses()
    {
        var trades = SourceTrades();
        var baseOptions = new MonteCarloOptions { Paths = 3_000 };

        var plain = Run(trades, baseOptions);
        var costly = Run(trades, baseOptions with { ExtraCostBps = 10 });
        var skipping = Run(trades, baseOptions with { SkipProbability = 0.5 });

        Assert.True(costly.TerminalEquity.P50 < plain.TerminalEquity.P50);
        Assert.True(Math.Abs(skipping.TerminalEquity.P50 - Capital) < Math.Abs(plain.TerminalEquity.P50 - Capital));
    }

    [Fact]
    public void Ruin_and_kill_switch_probabilities_follow_the_thresholds()
    {
        var losing = Enumerable.Repeat(new TradeOutcome(-0.1, 1), 10).ToArray();   // 0.9^7 < 0.5

        var result = MonteCarloSimulator.Run(losing, Capital, new MonteCarloOptions { Paths = 200 }, killSwitchDrawdown: 0.2).Value;
        var withoutPolicy = Run(losing, new MonteCarloOptions { Paths = 200 });

        Assert.Equal(1, result.ProbabilityOfRuin);
        Assert.Equal(1, result.ProbabilityKillSwitch);
        Assert.Equal(0.2, result.KillSwitchDrawdownLimit);
        Assert.Null(withoutPolicy.ProbabilityKillSwitch);
    }

    [Fact]
    public void Ruin_counts_paths_that_ever_reached_the_threshold_even_if_they_recovered()
    {
        // ×0.25 then ×4: the account falls to 2 500 (ruin at 5 000) and recovers exactly to 10 000.
        // ×4 then ×0.25: it never goes below 10 000. Both orders end where they started.
        var trades = new[] { new TradeOutcome(-0.75, 1), new TradeOutcome(3.0, 1) };

        var result = Run(trades, new MonteCarloOptions { Method = MonteCarloMethod.Shuffle, Paths = 4_000 });

        Assert.Equal(Capital, result.TerminalEquity.Min);
        Assert.Equal(Capital, result.TerminalEquity.Max);
        Assert.Equal(0, result.ProbabilityOfLoss);
        Assert.InRange(result.ProbabilityOfRuin, 0.45, 0.55);   // half of the orders went through ruin
    }

    [Fact]
    public void Extra_cost_is_charged_on_both_sides_of_each_trade_in_proportion_to_its_size()
    {
        // 10 bps per side on a position worth half the equity: 2 × 0.001 × 0.5 = 0.1 % of equity per trade.
        var trades = Enumerable.Repeat(new TradeOutcome(0.01, 0.5), 20).ToArray();

        var result = Run(trades, new MonteCarloOptions { Paths = 200, ExtraCostBps = 10 });

        Assert.Equal(Capital * Math.Pow(1.009, 20), result.TerminalEquity.P50, 6);
    }

    [Fact]
    public void Same_seed_same_scenarios_other_seed_other_scenarios()
    {
        var trades = SourceTrades();

        var a = Run(trades, new MonteCarloOptions { Paths = 1_000, Seed = 1 });
        var b = Run(trades, new MonteCarloOptions { Paths = 1_000, Seed = 1 });
        var c = Run(trades, new MonteCarloOptions { Paths = 1_000, Seed = 2 });

        Assert.Equal(a.TerminalEquity, b.TerminalEquity);
        Assert.Equal(a.EquityBands, b.EquityBands);
        Assert.NotEqual(a.TerminalEquity, c.TerminalEquity);
    }

    [Fact]
    public void Equity_bands_start_at_the_capital_end_at_the_horizon_and_are_ordered()
    {
        var result = Run(SourceTrades(), new MonteCarloOptions { Paths = 1_000, HorizonTrades = 250 });

        Assert.Equal(0, result.EquityBands[0].Trade);
        Assert.Equal(Capital, result.EquityBands[0].P5);
        Assert.Equal(Capital, result.EquityBands[0].P95);
        Assert.Equal(250, result.EquityBands[^1].Trade);
        Assert.True(result.EquityBands.Count <= 101);
        Assert.All(result.EquityBands, b => Assert.True(b.P5 <= b.P25 && b.P25 <= b.P50 && b.P50 <= b.P75 && b.P75 <= b.P95));
        Assert.Equal(result.TerminalEquity.P50, result.EquityBands[^1].P50, 9);
    }

    [Fact]
    public void Invalid_options_and_empty_backtests_are_refused()
    {
        var trades = SourceTrades();

        Assert.Equal(MonteCarloErrors.NoTrades, MonteCarloSimulator.Run([], Capital, new MonteCarloOptions()).Error!.Code);
        Assert.Equal(MonteCarloErrors.InvalidOptions, MonteCarloSimulator.Run(trades, Capital, new MonteCarloOptions { Paths = 50 }).Error!.Code);
        Assert.Equal(MonteCarloErrors.InvalidOptions, MonteCarloSimulator.Run(trades, Capital, new MonteCarloOptions { Method = MonteCarloMethod.Shuffle, HorizonTrades = 10 }).Error!.Code);
        Assert.Equal(MonteCarloErrors.InvalidOptions, MonteCarloSimulator.Run(trades, Capital, new MonteCarloOptions { RuinLevel = 1 }).Error!.Code);
        Assert.Equal(MonteCarloErrors.InvalidOptions, MonteCarloSimulator.Run(trades, Capital, new MonteCarloOptions { Paths = 100_000, HorizonTrades = 1_000 }).Error!.Code);
    }

    private static MonteCarloResult Run(IReadOnlyList<TradeOutcome> trades, MonteCarloOptions options) =>
        MonteCarloSimulator.Run(trades, Capital, options).Value;

    /// <summary>40 deterministic trades with a small positive edge.</summary>
    private static TradeOutcome[] SourceTrades() =>
        [.. Enumerable.Range(0, 40).Select(i => new TradeOutcome(i % 3 == 0 ? -0.012 : 0.009 + (i % 5) * 0.001, 0.8))];

    private static double Binomial(int n, int k)
    {
        double result = 1;
        for (var i = 1; i <= k; i++)
        {
            result = result * (n - k + i) / i;
        }

        return result;
    }
}
