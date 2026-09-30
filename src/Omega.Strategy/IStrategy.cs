using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Features;

namespace Omega.Strategy;

/// <summary>Identifies a strategy implementation; recorded with every backtest (CLAUDE.md §22).</summary>
public sealed record StrategyIdentity(string Name, string Version, IReadOnlyDictionary<string, string> Parameters);

/// <summary>The open position as a strategy may see it.</summary>
public sealed record PositionView(DateTimeOffset EntryTimeUtc, decimal EntryPrice, decimal Quantity, decimal StopLossPrice, decimal? TakeProfitPrice);

/// <summary>
/// Everything a strategy may use at the close of <see cref="Candle"/>. It deliberately contains nothing
/// about later candles: the strategy cannot look ahead, whatever it does.
/// </summary>
/// <param name="Candle">The candle that just closed.</param>
/// <param name="Features">Features at that candle (available from its close).</param>
/// <param name="Position">Open position, or null when flat.</param>
public sealed record StrategyContext(Candle Candle, FeatureVector Features, PositionView? Position);

/// <summary>
/// A trading strategy: turns the information available at a candle close into a <see cref="Signal"/>.
/// It does not size positions (risk does) and must not know whether it runs in a backtest, on paper or live.
/// </summary>
public interface IStrategy
{
    StrategyIdentity Identity { get; }

    /// <summary>Called at every candle close whose features are complete.</summary>
    Signal Evaluate(StrategyContext context);
}
