using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Omega.Core.MarketData;
using Omega.Infrastructure.Persistence;
using Omega.Infrastructure.Persistence.Migrations;

namespace Omega.Integration.Tests.Persistence;

public class PostgresCandleStoreTests : DatabaseTest
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [DatabaseFact]
    public async Task Round_trip_preserves_exact_decimals_and_utc_times()
    {
        var store = await CreateStoreAsync();
        var candle = Candle("BTCUSDT", 0, open: 64123.45678901m, high: 64200.00000001m, low: 64000.1m, close: 64150.5m,
            baseVolume: 0.00000001m, quoteVolume: 123456789.123456789m);

        Assert.Equal(CandleSaveOutcome.Inserted, await store.SaveAsync(candle, "binance-spot-ws", Start.AddMinutes(5), CancellationToken.None));

        var stored = await store.GetLatestAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);
        Assert.Equal(candle, stored);
        Assert.Equal(TimeSpan.Zero, stored!.OpenTimeUtc.Offset);
        Assert.Equal(123456789.123456789m, stored.QuoteVolume);
    }

    [DatabaseFact]
    public async Task Lineage_is_stored_with_the_candle()
    {
        var store = await CreateStoreAsync();
        var observedAt = new DateTimeOffset(2026, 1, 1, 0, 5, 0, 250, TimeSpan.Zero);

        await store.SaveAsync(Candle("BTCUSDT", 0), "binance-spot-ws", observedAt, CancellationToken.None);

        Assert.Equal("binance-spot-ws", await ScalarAsync("SELECT source FROM candles"));
        var storedObservedAt = Assert.IsType<DateTime>(await ScalarAsync("SELECT observed_at FROM candles"));
        Assert.Equal(observedAt.UtcDateTime, storedObservedAt);
    }

    [DatabaseFact]
    public async Task Saving_the_same_candle_twice_is_idempotent()
    {
        var store = await CreateStoreAsync();

        Assert.Equal(CandleSaveOutcome.Inserted, await store.SaveAsync(Candle("BTCUSDT", 0), "binance-spot-ws", Start, CancellationToken.None));
        Assert.Equal(CandleSaveOutcome.AlreadyStored, await store.SaveAsync(Candle("BTCUSDT", 0), "binance-spot-ws", Start.AddHours(1), CancellationToken.None));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM candles"));
    }

    [DatabaseFact]
    public async Task Different_candle_for_a_stored_key_is_a_conflict_and_the_original_is_kept()
    {
        var store = await CreateStoreAsync();
        await store.SaveAsync(Candle("BTCUSDT", 0, close: 100.5m), "binance-spot-ws", Start, CancellationToken.None);

        var outcome = await store.SaveAsync(Candle("BTCUSDT", 0, close: 100.7m), "binance-spot-ws", Start, CancellationToken.None);

        Assert.Equal(CandleSaveOutcome.Conflict, outcome);
        Assert.Equal(100.5m, (await store.GetLatestAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None))!.Close);
    }

    [DatabaseFact]
    public async Task Latest_candle_is_per_symbol()
    {
        var store = await CreateStoreAsync();
        await store.SaveAsync(Candle("BTCUSDT", 0), "binance-spot-ws", Start, CancellationToken.None);
        await store.SaveAsync(Candle("BTCUSDT", 1), "binance-spot-ws", Start, CancellationToken.None);
        await store.SaveAsync(Candle("ETHUSDT", 5), "binance-spot-ws", Start, CancellationToken.None);

        Assert.Equal(Start.AddMinutes(5), (await store.GetLatestAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None))!.OpenTimeUtc);
        Assert.Null(await store.GetLatestAsync("SOLUSDT", CandleInterval.FiveMinutes, CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Range_is_ordered_and_excludes_its_end()
    {
        var store = await CreateStoreAsync();
        foreach (var index in new[] { 3, 1, 2, 0 })
        {
            await store.SaveAsync(Candle("BTCUSDT", index), "binance-spot-ws", Start, CancellationToken.None);
        }

        var range = await store.GetRangeAsync("BTCUSDT", CandleInterval.FiveMinutes, Start.AddMinutes(5), Start.AddMinutes(15), CancellationToken.None);

        Assert.Equal([Start.AddMinutes(5), Start.AddMinutes(10)], range.Select(c => c.OpenTimeUtc));
    }

    [DatabaseFact]
    public async Task Database_constraints_reject_candles_that_break_invariants()
    {
        await CreateStoreAsync();

        // Written directly, bypassing Candle.Create: the schema is the last line of defence.
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("""
            INSERT INTO candles (symbol, interval_code, open_time, close_time, open_price, high_price, low_price, close_price,
                                 base_volume, quote_volume, trade_count, source, observed_at)
            VALUES ('BTCUSDT', '5m', '2026-01-01T00:00:00Z', '2026-01-01T00:04:59.999Z', 100, 99, 101, 100, 1, 100, 1,
                    'binance-spot-ws', now())
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    private async Task<PostgresCandleStore> CreateStoreAsync()
    {
        await new DatabaseMigrator(DataSource, NullLogger<DatabaseMigrator>.Instance)
            .EnsureUpToDateAsync(applyPending: true, CancellationToken.None);
        return new PostgresCandleStore(DataSource);
    }

    private static Candle Candle(
        string symbol, int index, decimal open = 100m, decimal high = 101m, decimal low = 99.5m, decimal close = 100.5m,
        decimal baseVolume = 12.5m, decimal quoteVolume = 1250m)
    {
        var openTime = Start.AddMinutes(5 * index);
        return Omega.Core.MarketData.Candle.Create(
            symbol, CandleInterval.FiveMinutes, openTime, openTime.AddMinutes(5).AddMilliseconds(-1),
            open, high, low, close, baseVolume, quoteVolume, 42).Value;
    }
}
