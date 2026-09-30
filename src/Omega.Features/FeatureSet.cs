using System.Security.Cryptography;
using System.Text;
using Omega.Features.Indicators;

namespace Omega.Features;

/// <summary>
/// An immutable, versioned list of features. The hash identifies the exact definitions, so an experiment
/// can record which features it used (CLAUDE.md §22). Changing any definition changes the hash; a changed
/// definition must also get a new version.
/// </summary>
public sealed class FeatureSet
{
    public FeatureSet(string version, IReadOnlyList<IFeature> features)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(features);

        if (features.Count == 0)
        {
            throw new ArgumentException("A feature set needs at least one feature.", nameof(features));
        }

        var duplicate = features.GroupBy(f => f.Definition.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate feature name '{duplicate.Key}'.", nameof(features));
        }

        Version = version;
        Features = features;
        MaxLookback = features.Max(f => f.Definition.Lookback);
        Hash = ComputeHash(version, features);
    }

    public string Version { get; }

    public IReadOnlyList<IFeature> Features { get; }

    /// <summary>Candles needed for every feature to have a value.</summary>
    public int MaxLookback { get; }

    /// <summary>SHA-256 (hex) of the version and every definition field.</summary>
    public string Hash { get; }

    public IReadOnlyList<FeatureDefinition> Definitions => [.. Features.Select(f => f.Definition)];

    private static string ComputeHash(string version, IReadOnlyList<IFeature> features)
    {
        var text = new StringBuilder(version).Append('\n');
        foreach (var d in features.Select(f => f.Definition).OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            text.Append(d.Name).Append('|').Append(d.Formula).Append('|')
                .Append(d.Lookback.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(d.RequiredData).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

/// <summary>The feature sets OMEGA knows. Never modify a published set: add a new version.</summary>
public static class FeatureSets
{
    public const string V1Version = "features-v1";

    /// <summary>Initial feature set (Phase 4). Parameters are research defaults, not tuned values.</summary>
    public static FeatureSet V1() => new(V1Version,
    [
        new LogReturn(1),
        new LogReturn(3),
        new LogReturn(12),
        new SimpleMovingAverage(20),
        new ExponentialMovingAverage(20),
        new ExponentialMovingAverage(50),
        new DistanceFromEma(20),
        new RelativeStrengthIndex(14),
        new AverageTrueRange(14),
        new MovingAverageConvergenceDivergence(12, 26, 9, MovingAverageConvergenceDivergence.Output.Line),
        new MovingAverageConvergenceDivergence(12, 26, 9, MovingAverageConvergenceDivergence.Output.Signal),
        new MovingAverageConvergenceDivergence(12, 26, 9, MovingAverageConvergenceDivergence.Output.Histogram),
        new RealizedVolatility(20),
        new Volume(),
        new VolumeZScore(20),
        new AverageDirectionalIndex(14),
    ]);
}
