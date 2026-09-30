using Omega.Core.MarketData;
using Omega.Features.Tests.TestSupport;

namespace Omega.Features.Tests;

public class FeatureEngineTests
{
    private static readonly FeatureEngine Engine = new(FeatureSets.V1());
    private static readonly Candle[] History = ReferenceData.Candles;

    [Fact]
    public void Every_feature_matches_the_independent_reference_at_every_candle()
    {
        var series = Engine.ComputeSeries(History).Value;
        var mismatches = new List<string>();

        for (var t = 0; t < History.Length; t++)
        {
            foreach (var (name, expected) in ReferenceData.Expected[t])
            {
                var actual = series[t].Values[name];
                if (!Close(expected, actual))
                {
                    mismatches.Add($"{name}@{t}: expected {expected}, got {actual}");
                }
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void Series_and_single_computation_agree()
    {
        var series = Engine.ComputeSeries(History).Value;

        foreach (var t in new[] { 0, 20, 174, 249, 250, 399 })
        {
            var single = Engine.Compute(History[..(t + 1)]).Value;
            Assert.Equal(series[t].Values, single.Values);
        }
    }

    [Fact]
    public void Changing_future_candles_never_changes_past_features()
    {
        const int t = 300;
        var altered = (Candle[])History.Clone();
        for (var i = t + 1; i < altered.Length; i++)
        {
            altered[i] = ReferenceData.Candle(altered[i].OpenTimeUtc, close: 1m, volume: 999_999m);
        }

        var original = Engine.ComputeSeries(History).Value;
        var withAlteredFuture = Engine.ComputeSeries(altered).Value;

        for (var i = 0; i <= t; i++)
        {
            Assert.Equal(original[i].Values, withAlteredFuture[i].Values);
        }

        Assert.NotEqual(original[t + 1].Values["log_return_1"], withAlteredFuture[t + 1].Values["log_return_1"]);
    }

    [Fact]
    public void Each_feature_depends_only_on_its_declared_lookback()
    {
        const int t = 399;

        foreach (var feature in Engine.FeatureSet.Features)
        {
            var lookback = feature.Definition.Lookback;
            var full = feature.Compute(History.AsSpan(0, t + 1)[^lookback..]);

            // Replacing every candle before the window must not matter.
            var tampered = (Candle[])History.Clone();
            for (var i = 0; i <= t - lookback; i++)
            {
                tampered[i] = ReferenceData.Candle(tampered[i].OpenTimeUtc, close: 12_345m);
            }

            var vector = Engine.Compute(tampered).Value;
            Assert.Equal(full, vector.Values[feature.Definition.Name]);
        }
    }

    [Fact]
    public void Vector_is_available_only_after_its_candle_closes()
    {
        var vector = Engine.Compute(History).Value;

        Assert.Equal(History[^1].OpenTimeUtc, vector.OpenTimeUtc);
        Assert.Equal(History[^1].CloseTimeUtc, vector.AvailableAtUtc);
        Assert.Equal(FeatureSets.V1Version, vector.FeatureSetVersion);
    }

    [Fact]
    public void Features_without_enough_history_are_null_never_zero()
    {
        var vector = Engine.Compute(History[..10]).Value;

        foreach (var feature in Engine.FeatureSet.Features)
        {
            var value = vector.Values[feature.Definition.Name];
            Assert.True(feature.Definition.Lookback <= 10 ? value is not null : value is null, feature.Definition.Name);
        }

        Assert.False(vector.IsComplete);
        Assert.True(Engine.Compute(History[..Engine.FeatureSet.MaxLookback]).Value.IsComplete);
    }

    [Fact]
    public void Gap_inside_the_window_is_rejected()
    {
        var withGap = History.Where((_, i) => i != 380).ToArray();

        var result = Engine.Compute(withGap);

        Assert.Equal(FeatureErrors.NotContiguous, result.Error!.Code);
    }

    [Fact]
    public void Gap_before_the_window_does_not_matter()
    {
        var withOldGap = History.Where((_, i) => i != 5).ToArray();

        Assert.Equal(Engine.Compute(History).Value.Values, Engine.Compute(withOldGap).Value.Values);
    }

    [Fact]
    public void In_a_series_a_gap_restarts_the_warm_up()
    {
        var withGap = History.Where((_, i) => i != 200).ToArray();

        var series = Engine.ComputeSeries(withGap).Value;

        // Index 200 of withGap is original candle 201: first candle after the gap.
        Assert.Null(series[200].Values["log_return_1"]);
        Assert.Equal(Engine.Compute(withGap[200..215]).Value.Values, series[214].Values);
    }

    [Fact]
    public void Mixed_symbols_and_unsorted_candles_are_rejected()
    {
        var mixed = History[..30].Append(ReferenceData.Candle(History[29].OpenTimeUtc.AddMinutes(5), 100m, symbol: "ETHUSDT")).ToArray();
        var unsorted = History[..30].Reverse().ToArray();

        Assert.Equal(FeatureErrors.MixedSeries, Engine.Compute(mixed).Error!.Code);
        Assert.Equal(FeatureErrors.MixedSeries, Engine.ComputeSeries(mixed).Error!.Code);
        Assert.Equal(FeatureErrors.NotContiguous, Engine.ComputeSeries(unsorted).Error!.Code);
    }

    [Fact]
    public void Undefined_values_are_null_and_degenerate_markets_do_not_produce_nan()
    {
        var start = History[0].OpenTimeUtc;
        var flat = Enumerable.Range(0, 260).Select(i => ReferenceData.Candle(start.AddMinutes(5 * i), 100m)).ToArray();
        var rising = Enumerable.Range(0, 260).Select(i => ReferenceData.Candle(start.AddMinutes(5 * i), 100m + i)).ToArray();

        var flatVector = Engine.Compute(flat).Value;
        var risingVector = Engine.Compute(rising).Value;

        Assert.Null(flatVector.Values["rsi_14"]);
        Assert.Null(flatVector.Values["volume_zscore_20"]);
        Assert.Equal(0, flatVector.Values["volatility_20"]);
        Assert.Equal(0, flatVector.Values["adx_14"]);
        Assert.Equal(100, risingVector.Values["rsi_14"]);
    }

    private static bool Close(double? expected, double? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        var tolerance = 1e-9 * Math.Max(1, Math.Abs(expected.Value));
        return Math.Abs(expected.Value - actual.Value) <= tolerance;
    }
}
