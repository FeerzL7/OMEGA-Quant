using System.Text.RegularExpressions;

namespace Omega.Features.Tests;

public partial class FeatureSetTests
{
    private static readonly FeatureSet V1 = FeatureSets.V1();

    [Fact]
    public void Every_feature_documents_name_purpose_formula_lookback_data_and_leakage_risk()
    {
        foreach (var d in V1.Definitions)
        {
            Assert.Matches(SnakeCase(), d.Name);
            Assert.False(string.IsNullOrWhiteSpace(d.Purpose), d.Name);
            Assert.False(string.IsNullOrWhiteSpace(d.Formula), d.Name);
            Assert.False(string.IsNullOrWhiteSpace(d.RequiredData), d.Name);
            Assert.False(string.IsNullOrWhiteSpace(d.LeakageRisk), d.Name);
            Assert.InRange(d.Lookback, 1, 1000);
        }
    }

    [Fact]
    public void V1_contains_the_roadmap_features()
    {
        Assert.Equal(
            [
                "log_return_1", "log_return_3", "log_return_12", "sma_20", "ema_20", "ema_50", "dist_ema_20", "rsi_14", "atr_14",
                "macd_line", "macd_signal", "macd_histogram", "volatility_20", "volume", "volume_zscore_20", "adx_14",
            ],
            V1.Definitions.Select(d => d.Name));
        Assert.Equal(250, V1.MaxLookback);
    }

    [Fact]
    public void Hash_is_stable_and_identifies_the_definitions()
    {
        Assert.Equal(FeatureSets.V1().Hash, V1.Hash);
        Assert.Equal(64, V1.Hash.Length);

        var other = new FeatureSet("features-v1", [.. V1.Features.Take(3)]);
        Assert.NotEqual(V1.Hash, other.Hash);
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new FeatureSet("dup", [V1.Features[0], V1.Features[0]]));
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$")]
    private static partial Regex SnakeCase();
}
