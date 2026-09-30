using Omega.Core.MarketData;
using Omega.Features;

namespace Omega.Api.Contracts;

/// <summary>Response of <c>GET /api/features/catalog</c>.</summary>
public sealed record FeatureCatalogResponse(string Version, string Hash, int MaxLookback, IReadOnlyList<FeatureDefinition> Features)
{
    public static FeatureCatalogResponse From(FeatureSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new FeatureCatalogResponse(set.Version, set.Hash, set.MaxLookback, set.Definitions);
    }
}

/// <summary>Response of <c>GET /api/market/{symbol}/{interval}/features/latest</c>.</summary>
/// <param name="Symbol">Symbol.</param>
/// <param name="Interval">Interval code.</param>
/// <param name="OpenTimeUtc">Open time of the candle the vector describes.</param>
/// <param name="AvailableAtUtc">Close of that candle: the earliest moment the vector may be used.</param>
/// <param name="FeatureSetVersion">Feature set version.</param>
/// <param name="FeatureSetHash">Feature set hash.</param>
/// <param name="IsComplete">False while some features are still warming up.</param>
/// <param name="Values">Value per feature; null while warming up or when undefined.</param>
public sealed record FeatureVectorResponse(
    string Symbol,
    string Interval,
    DateTimeOffset OpenTimeUtc,
    DateTimeOffset AvailableAtUtc,
    string FeatureSetVersion,
    string FeatureSetHash,
    bool IsComplete,
    IReadOnlyDictionary<string, double?> Values)
{
    public static FeatureVectorResponse From(FeatureVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        return new FeatureVectorResponse(
            vector.Symbol, vector.Interval.ToCode(), vector.OpenTimeUtc, vector.AvailableAtUtc,
            vector.FeatureSetVersion, vector.FeatureSetHash, vector.IsComplete, vector.Values);
    }
}
