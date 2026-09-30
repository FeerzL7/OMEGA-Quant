using Omega.Backtesting;
using Omega.Strategy;
using Omega.Strategy.Baseline;

namespace Omega.Application.Backtesting;

/// <summary>A strategy that can be backtested, with the configuration it is evaluated under by default.</summary>
/// <param name="Name">Name.</param>
/// <param name="Description">What it does.</param>
/// <param name="Create">Creates a fresh instance for each run.</param>
/// <param name="DefaultConfig">Simulation assumptions it is evaluated under.</param>
/// <param name="IsBenchmark">A reference to compare against (for example buy and hold), not a candidate strategy.</param>
public sealed record StrategyDefinition(string Name, string Description, Func<IStrategy> Create, BacktestConfig DefaultConfig, bool IsBenchmark = false);

/// <summary>Strategies available for backtesting. Benchmarks are strategies too, so they share the same rules.</summary>
public static class StrategyCatalog
{
    public static IReadOnlyList<StrategyDefinition> All { get; } =
    [
        new(
            EmaTrendBaseline.Name,
            "Non-ML baseline: long in an uptrend (EMA20 > EMA50, close > EMA20); stop 2·ATR14, target 3·ATR14, timeout 48 candles; exit when EMA20 < EMA50.",
            () => new EmaTrendBaseline(),
            new BacktestConfig { MaxHoldingCandles = EmaTrendBaseline.TimeoutCandles }),
        new(
            BuyAndHold.Name,
            "Benchmark: invest all capital at the first opportunity and hold to the end.",
            () => new BuyAndHold(),
            new BacktestConfig { RiskPerTrade = 1m },
            IsBenchmark: true),
    ];

    public static StrategyDefinition? Find(string name) =>
        All.FirstOrDefault(definition => string.Equals(definition.Name, name, StringComparison.Ordinal));
}
