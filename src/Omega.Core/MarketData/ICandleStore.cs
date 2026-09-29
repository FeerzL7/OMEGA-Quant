namespace Omega.Core.MarketData;

/// <summary>
/// Durable store of closed candles. Stored candles are immutable facts: a
/// candle is written once and never updated.
/// </summary>
/// <remarks>Implementations throw <see cref="Persistence.PersistenceException"/> on storage failures.</remarks>
public interface ICandleStore
{
    /// <summary>
    /// Stores a closed candle. Idempotent for identical candles; a different
    /// candle for an already stored (symbol, interval, open time) is reported as
    /// <see cref="CandleSaveOutcome.Conflict"/> and the stored one is kept.
    /// </summary>
    /// <param name="candle">The candle to store.</param>
    /// <param name="source">Data lineage, for example <c>binance-spot-ws</c>.</param>
    /// <param name="observedAtUtc">When OMEGA received the candle.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<CandleSaveOutcome> SaveAsync(Candle candle, string source, DateTimeOffset observedAtUtc, CancellationToken cancellationToken);

    /// <summary>Most recent stored candle for the symbol and interval, or null when there is none.</summary>
    Task<Candle?> GetLatestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken);

    /// <summary>Stored candles with open time in [<paramref name="fromOpenTimeUtc"/>, <paramref name="toOpenTimeUtc"/>), oldest first.</summary>
    Task<IReadOnlyList<Candle>> GetRangeAsync(
        string symbol,
        CandleInterval interval,
        DateTimeOffset fromOpenTimeUtc,
        DateTimeOffset toOpenTimeUtc,
        CancellationToken cancellationToken);
}

public enum CandleSaveOutcome
{
    /// <summary>The candle was new and has been stored.</summary>
    Inserted = 1,

    /// <summary>An identical candle was already stored; nothing changed.</summary>
    AlreadyStored = 2,

    /// <summary>A different candle with the same key was already stored; it was kept unchanged.</summary>
    Conflict = 3,
}
