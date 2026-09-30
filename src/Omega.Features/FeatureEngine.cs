using Omega.Core.MarketData;
using Omega.Core.Results;

namespace Omega.Features;

/// <summary>Feature values at candle t.</summary>
/// <param name="Symbol">Symbol.</param>
/// <param name="Interval">Interval.</param>
/// <param name="OpenTimeUtc">Open time of candle t.</param>
/// <param name="AvailableAtUtc">
/// Earliest moment the vector may be used: the close of candle t. Using it for anything decided before
/// this instant is look-ahead.
/// </param>
/// <param name="FeatureSetVersion">Feature set version.</param>
/// <param name="FeatureSetHash">Feature set hash.</param>
/// <param name="Values">Value per feature name; null while warming up or when mathematically undefined.</param>
public sealed record FeatureVector(
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset OpenTimeUtc,
    DateTimeOffset AvailableAtUtc,
    string FeatureSetVersion,
    string FeatureSetHash,
    IReadOnlyDictionary<string, double?> Values)
{
    public bool IsComplete => Values.Values.All(value => value is not null);
}

/// <summary>Error codes of <see cref="FeatureEngine"/>.</summary>
public static class FeatureErrors
{
    public const string EmptyHistory = "FEATURES_EMPTY_HISTORY";
    public const string MixedSeries = "FEATURES_MIXED_SERIES";
    public const string NotContiguous = "FEATURES_WINDOW_NOT_CONTIGUOUS";
    public const string NotFinite = "FEATURES_VALUE_NOT_FINITE";
}

/// <summary>
/// Computes a <see cref="FeatureSet"/> from closed candles.
/// </summary>
/// <remarks>
/// No look-ahead by construction: the vector for candle t is computed from the candles ending at t and
/// nothing after it. Each feature receives exactly its own lookback window, which must be contiguous
/// (a gap would silently mix non-consecutive candles). Only closed candles may be passed.
/// </remarks>
public sealed class FeatureEngine(FeatureSet featureSet)
{
    public FeatureSet FeatureSet { get; } = featureSet ?? throw new ArgumentNullException(nameof(featureSet));

    /// <summary>Feature vector at the last candle of <paramref name="history"/> (oldest first).</summary>
    public Result<FeatureVector> Compute(IReadOnlyList<Candle> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count == 0)
        {
            return Fail(FeatureErrors.EmptyHistory, "No candles.");
        }

        var candles = history as Candle[] ?? [.. history];
        var used = Math.Min(candles.Length, FeatureSet.MaxLookback);
        var validation = ValidateWindow(candles.AsSpan(candles.Length - used));

        return validation.IsFailure
            ? Result.Failure<FeatureVector>(validation.Error!)
            : ComputeAt(candles, candles.Length - used, candles.Length - 1);
    }

    /// <summary>
    /// One vector per candle, each computed only from the candles up to it (identical to calling
    /// <see cref="Compute"/> with the history ending at that candle). A gap restarts the warm-up.
    /// Intended for backtests and dataset generation.
    /// </summary>
    public Result<IReadOnlyList<FeatureVector>> ComputeSeries(IReadOnlyList<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        var all = candles as Candle[] ?? [.. candles];
        if (all.Length == 0)
        {
            return Result.Success<IReadOnlyList<FeatureVector>>([]);
        }

        var vectors = new List<FeatureVector>(all.Length);
        var segmentStart = 0;

        for (var i = 0; i < all.Length; i++)
        {
            if (i > 0)
            {
                var previous = all[i - 1];
                var current = all[i];

                if (previous.Symbol != current.Symbol || previous.Interval != current.Interval)
                {
                    return Result.Failure<IReadOnlyList<FeatureVector>>(
                        new Error(FeatureErrors.MixedSeries, "All candles must have the same symbol and interval."));
                }

                if (current.OpenTimeUtc <= previous.OpenTimeUtc)
                {
                    return Result.Failure<IReadOnlyList<FeatureVector>>(
                        new Error(FeatureErrors.NotContiguous, "Candles must be sorted by open time without duplicates."));
                }

                if (current.OpenTimeUtc - previous.OpenTimeUtc != current.Interval.ToTimeSpan())
                {
                    segmentStart = i; // gap: the warm-up restarts after it
                }
            }

            var result = ComputeAt(all, segmentStart, i);
            if (result.IsFailure)
            {
                return Result.Failure<IReadOnlyList<FeatureVector>>(result.Error!);
            }

            vectors.Add(result.Value);
        }

        return Result.Success<IReadOnlyList<FeatureVector>>(vectors);
    }

    /// <summary>Vector at <paramref name="index"/>, using only candles in [<paramref name="segmentStart"/>, index].</summary>
    private Result<FeatureVector> ComputeAt(Candle[] candles, int segmentStart, int index)
    {
        var last = candles[index];
        var available = index - segmentStart + 1;
        var values = new Dictionary<string, double?>(StringComparer.Ordinal);

        foreach (var feature in FeatureSet.Features)
        {
            var lookback = feature.Definition.Lookback;
            if (available < lookback)
            {
                values[feature.Definition.Name] = null; // warming up
                continue;
            }

            var value = feature.Compute(candles.AsSpan(index + 1 - lookback, lookback));
            if (value is { } number && !double.IsFinite(number))
            {
                return Fail(FeatureErrors.NotFinite, $"Feature {feature.Definition.Name} produced {number} at {last.OpenTimeUtc:O}.");
            }

            values[feature.Definition.Name] = value;
        }

        return Result.Success(new FeatureVector(
            last.Symbol, last.Interval, last.OpenTimeUtc, last.CloseTimeUtc, FeatureSet.Version, FeatureSet.Hash, values));
    }

    private static Result ValidateWindow(ReadOnlySpan<Candle> window)
    {
        for (var i = 1; i < window.Length; i++)
        {
            if (CheckPair(window[i - 1], window[i]) is { } error)
            {
                return Result.Failure(error);
            }
        }

        return Result.Success();
    }

    private static Error? CheckPair(Candle previous, Candle current)
    {
        if (previous.Symbol != current.Symbol || previous.Interval != current.Interval)
        {
            return new Error(FeatureErrors.MixedSeries, "All candles must have the same symbol and interval.");
        }

        return current.OpenTimeUtc - previous.OpenTimeUtc == current.Interval.ToTimeSpan()
            ? null
            : new Error(FeatureErrors.NotContiguous,
                $"Candle {current.OpenTimeUtc:O} does not follow {previous.OpenTimeUtc:O} by exactly one interval.");
    }

    private static Result<FeatureVector> Fail(string code, string message) =>
        Result.Failure<FeatureVector>(new Error(code, message));
}
