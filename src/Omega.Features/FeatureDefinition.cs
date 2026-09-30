namespace Omega.Features;

/// <summary>
/// Documentation contract required for every feature (roadmap, Phase 4). All fields are mandatory.
/// </summary>
/// <param name="Name">Stable identifier (lower snake case), for example <c>rsi_14</c>. Never reused with another meaning.</param>
/// <param name="Purpose">What market property the feature measures.</param>
/// <param name="Formula">Exact computation, including initialization of recursive averages.</param>
/// <param name="Lookback">Exact number of consecutive closed candles, ending at candle t, the value depends on.</param>
/// <param name="RequiredData">Candle fields used.</param>
/// <param name="LeakageRisk">How the feature could leak future information and how that is prevented.</param>
public sealed record FeatureDefinition(
    string Name,
    string Purpose,
    string Formula,
    int Lookback,
    string RequiredData,
    string LeakageRisk);

/// <summary>A single feature computed at candle t from its trailing window.</summary>
public interface IFeature
{
    FeatureDefinition Definition { get; }

    /// <summary>
    /// Computes the value at the last candle of <paramref name="window"/>.
    /// </summary>
    /// <param name="window">
    /// Exactly <see cref="FeatureDefinition.Lookback"/> consecutive closed candles, oldest first. The engine
    /// guarantees length and contiguity; the feature must not look at anything else.
    /// </param>
    /// <returns>The value, or null when it is mathematically undefined (for example a zero standard deviation).</returns>
    double? Compute(ReadOnlySpan<Omega.Core.MarketData.Candle> window);
}
