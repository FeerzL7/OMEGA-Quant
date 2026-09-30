using Microsoft.Extensions.Logging.Abstractions;
using Omega.Core.MarketData;
using Omega.Infrastructure.Persistence;
using Omega.Infrastructure.Persistence.Migrations;

namespace Omega.Integration.Tests.Persistence;

public class CandleGapQueryTests : DatabaseTest
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [DatabaseFact]
    public async Task Interior_gaps_are_found_with_their_size()
    {
        var store = await StoreWithAsync("BTCUSDT", 0, 1, 4, 5, 9);

        var gaps = await store.FindGapsAsync("BTCUSDT", CandleInterval.FiveMinutes, At(0), At(10), CancellationToken.None);

        Assert.Equal([new CandleGap(At(2), 2), new CandleGap(At(6), 3)], gaps);
    }

    [DatabaseFact]
    public async Task Gap_crossing_the_start_of_the_range_is_reported_whole()
    {
        var store = await StoreWithAsync("BTCUSDT", 0, 6, 7);

        var gaps = await store.FindGapsAsync("BTCUSDT", CandleInterval.FiveMinutes, At(3), At(8), CancellationToken.None);

        Assert.Equal([new CandleGap(At(1), 5)], gaps);
    }

    [DatabaseFact]
    public async Task Gaps_outside_the_range_are_ignored()
    {
        var store = await StoreWithAsync("BTCUSDT", 0, 3, 4, 5, 9);

        var gaps = await store.FindGapsAsync("BTCUSDT", CandleInterval.FiveMinutes, At(3), At(6), CancellationToken.None);

        Assert.Empty(gaps);
    }

    [DatabaseFact]
    public async Task Missing_candles_after_the_latest_one_are_not_a_gap()
    {
        var store = await StoreWithAsync("BTCUSDT", 0, 1);

        Assert.Empty(await store.FindGapsAsync("BTCUSDT", CandleInterval.FiveMinutes, At(0), At(50), CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Other_symbols_do_not_affect_the_result()
    {
        var store = await StoreWithAsync("BTCUSDT", 0, 1, 2);
        foreach (var index in new[] { 0, 5 })
        {
            await store.SaveAsync(Candle("ETHUSDT", index), "binance-spot-ws", Start, CancellationToken.None);
        }

        Assert.Empty(await store.FindGapsAsync("BTCUSDT", CandleInterval.FiveMinutes, At(0), At(10), CancellationToken.None));
        Assert.Equal([new CandleGap(At(1), 4)], await store.FindGapsAsync("ETHUSDT", CandleInterval.FiveMinutes, At(0), At(10), CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Earliest_and_latest_candles_are_per_symbol()
    {
        var store = await StoreWithAsync("BTCUSDT", 4, 2, 9);
        await store.SaveAsync(Candle("ETHUSDT", 0), "binance-spot-ws", Start, CancellationToken.None);

        Assert.Equal(At(2), (await store.GetEarliestAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None))!.OpenTimeUtc);
        Assert.Equal(At(9), (await store.GetLatestAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None))!.OpenTimeUtc);
        Assert.Null(await store.GetEarliestAsync("SOLUSDT", CandleInterval.FiveMinutes, CancellationToken.None));
    }

    private async Task<PostgresCandleStore> StoreWithAsync(string symbol, params int[] indices)
    {
        await new DatabaseMigrator(DataSource, NullLogger<DatabaseMigrator>.Instance)
            .EnsureUpToDateAsync(applyPending: true, CancellationToken.None);
        var store = new PostgresCandleStore(DataSource);

        foreach (var index in indices)
        {
            await store.SaveAsync(Candle(symbol, index), "binance-spot-ws", Start, CancellationToken.None);
        }

        return store;
    }

    private static DateTimeOffset At(int index) => Start.AddMinutes(5 * index);

    private static Candle Candle(string symbol, int index) => Omega.Core.MarketData.Candle.Create(
        symbol, CandleInterval.FiveMinutes, At(index), At(index + 1).AddMilliseconds(-1), 100m, 101m, 99m, 100.5m, 1m, 100m, 10).Value;
}
