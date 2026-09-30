using Microsoft.Extensions.Logging.Abstractions;
using Omega.Application.MarketData;
using Omega.Application.Tests.TestSupport;
using Omega.Core.MarketData;
using Omega.Core.Resilience;
using Omega.Core.SystemEvents;
using Omega.MarketData;
using static Omega.Application.Tests.TestSupport.TestCandles;

namespace Omega.Application.Tests.MarketData;

public class HistoryImportTests
{
    private readonly InMemoryCandleStore _candles = new();
    private readonly InMemorySystemEventStore _systemEvents = new();

    [Fact]
    public async Task Nothing_happens_without_a_history_start()
    {
        var source = new FakeHistoricalSource(0, 1, 2);

        await Filler(source, historyStart: null, now: OpenTime(10)).ImportHistoryAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(0, source.Calls);
        Assert.Empty(_candles.Candles);
    }

    [Fact]
    public async Task Empty_store_imports_up_to_the_candle_still_forming()
    {
        // Now is 2 minutes into candle 20: candles 0-19 are closed.
        var source = new FakeHistoricalSource([.. Enumerable.Range(0, 21)]);

        await Filler(source, historyStart: OpenTime(0), now: OpenTime(20).AddMinutes(2))
            .ImportHistoryAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal([.. Enumerable.Range(0, 20).Select(OpenTime)], _candles.Candles.Select(c => c.OpenTimeUtc));
        var imported = Assert.Single(_systemEvents.Events);
        Assert.Equal(SystemEventTypes.HistoryImported, imported.EventType);
        Assert.Equal(SystemEventSeverity.Information, imported.Severity);
    }

    [Fact]
    public async Task Imports_only_what_is_older_than_the_stored_history()
    {
        _candles.Seed(Candle(10));
        _candles.Seed(Candle(11));
        var source = new FakeHistoricalSource([.. Enumerable.Range(0, 12)]);

        await Filler(source, historyStart: OpenTime(5), now: OpenTime(30)).ImportHistoryAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal([.. Enumerable.Range(5, 7).Select(OpenTime)], _candles.Candles.Select(c => c.OpenTimeUtc));
    }

    [Fact]
    public async Task Already_covered_history_is_not_requested_again()
    {
        _candles.Seed(Candle(3));
        var source = new FakeHistoricalSource(0, 1, 2);

        await Filler(source, historyStart: OpenTime(3), now: OpenTime(30)).ImportHistoryAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Long_periods_are_imported_in_30_day_chunks_and_missing_data_is_reported()
    {
        var source = new FakeHistoricalSource();
        var start = OpenTime(0);

        await Filler(source, historyStart: start, now: start.AddDays(70)).ImportHistoryAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(3, source.Calls);
        var imported = Assert.Single(_systemEvents.Events);
        Assert.Equal(SystemEventSeverity.Warning, imported.Severity);
        Assert.Equal("20160", imported.Details["expectedCandles"]);
    }

    [Fact]
    public async Task Failure_is_recorded_and_does_not_throw()
    {
        var source = new FakeHistoricalSource { AlwaysFailWith = new HistoricalDataException("Invalid symbol.", isTransient: false) };

        await Filler(source, historyStart: OpenTime(0), now: OpenTime(20)).ImportHistoryAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(SystemEventTypes.HistoryImportFailed, Assert.Single(_systemEvents.Events).EventType);
    }

    [Fact]
    public void History_start_must_be_utc_and_not_before_2017()
    {
        Assert.Empty(new BackfillOptions { HistoryStart = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) }.Validate());
        Assert.NotEmpty(new BackfillOptions { HistoryStart = new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero) }.Validate());
        Assert.NotEmpty(new BackfillOptions { HistoryStart = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.FromHours(-6)) }.Validate());
    }

    private CandleGapFiller Filler(IHistoricalCandleSource source, DateTimeOffset? historyStart, DateTimeOffset now) =>
        new(source, _candles, _systemEvents, new BackfillOptions { HistoryStart = historyStart }, new ManualTimeProvider(now),
            NullLogger<CandleGapFiller>.Instance, new ExponentialBackoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), new Random(1)));
}
