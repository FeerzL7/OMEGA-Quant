using Omega.Core.MarketData;
using Omega.Core.Results;
using Omega.Features;

namespace Omega.Application.Features;

/// <summary>Error codes of <see cref="FeatureService"/> (besides those of <see cref="FeatureEngine"/>).</summary>
public static class FeatureServiceErrors
{
    public const string NoData = "FEATURES_NO_DATA";
}

/// <summary>Computes feature vectors from the stored closed candles.</summary>
public sealed class FeatureService(ICandleStore candles, FeatureEngine engine)
{
    public FeatureSet FeatureSet => engine.FeatureSet;

    /// <summary>
    /// Vector at the latest stored candle. Fails with <see cref="FeatureErrors.NotContiguous"/> when the
    /// window the features need contains a gap: partial data never becomes a feature value.
    /// </summary>
    /// <exception cref="Omega.Core.Persistence.PersistenceException">The store is unavailable.</exception>
    public async Task<Result<FeatureVector>> ComputeLatestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var latest = await candles.GetLatestAsync(symbol, interval, cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return Result.Failure<FeatureVector>(new Error(FeatureServiceErrors.NoData, $"No stored {symbol} {interval.ToCode()} candles."));
        }

        var length = interval.ToTimeSpan();
        var from = latest.OpenTimeUtc - (length * (engine.FeatureSet.MaxLookback - 1));
        var history = await candles.GetRangeAsync(symbol, interval, from, latest.OpenTimeUtc + length, cancellationToken).ConfigureAwait(false);

        return engine.Compute(history);
    }
}
