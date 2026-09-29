using Omega.Core.MarketData;

namespace Omega.Application.MarketData;

/// <summary>Computes the <see cref="MarketState"/> of a symbol from the stored closed candles.</summary>
public sealed class MarketStateService(ICandleStore candles, MarketStateOptions options, TimeProvider timeProvider)
{
    /// <exception cref="Omega.Core.Persistence.PersistenceException">The store is unavailable.</exception>
    public async Task<MarketState> GetStateAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var now = timeProvider.GetUtcNow();
        var latest = await candles.GetLatestAsync(symbol, interval, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CandleGap> gaps = latest is null
            ? []
            : await candles.FindGapsAsync(symbol, interval, now - options.IntegrityWindow, now, cancellationToken).ConfigureAwait(false);

        return MarketStateEvaluator.Evaluate(
            symbol, interval, latest, gaps, now, options.FreshnessGracePeriod, options.IntegrityWindow);
    }
}
