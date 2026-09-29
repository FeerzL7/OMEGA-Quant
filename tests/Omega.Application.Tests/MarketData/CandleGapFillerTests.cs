using Microsoft.Extensions.Logging.Abstractions;
using Omega.Application.MarketData;
using Omega.Application.Tests.TestSupport;
using Omega.Core.MarketData;
using Omega.Core.Resilience;
using Omega.Core.SystemEvents;
using Omega.MarketData;
using static Omega.Application.Tests.TestSupport.TestCandles;

namespace Omega.Application.Tests.MarketData;

public class CandleGapFillerTests
{
    private readonly InMemoryCandleStore _candles = new();
    private readonly InMemorySystemEventStore _systemEvents = new();

    [Fact]
    public async Task Gap_is_filled_and_recorded()
    {
        var source = new FakeHistoricalSource(2, 3, 4);

        var result = await Filler(source).FillAsync("BTCUSDT", CandleInterval.FiveMinutes, new CandleGap(OpenTime(2), 3), CancellationToken.None);

        Assert.Equal(new GapFillResult(3, 3, 0), result);
        Assert.Equal([OpenTime(2), OpenTime(3), OpenTime(4)], _candles.Candles.Select(c => c.OpenTimeUtc));
        Assert.Equal(("test-history", ManualNow), _candles.Lineage(OpenTime(3)));
        var filled = Assert.Single(_systemEvents.Events);
        Assert.Equal(SystemEventTypes.MarketDataGapFilled, filled.EventType);
        Assert.Equal(SystemEventSeverity.Information, filled.Severity);
    }

    [Fact]
    public async Task Candles_the_exchange_does_not_have_are_reported_as_still_missing()
    {
        var source = new FakeHistoricalSource(2, 4);

        var result = await Filler(source).FillAsync("BTCUSDT", CandleInterval.FiveMinutes, new CandleGap(OpenTime(2), 3), CancellationToken.None);

        Assert.Equal(new GapFillResult(3, 2, 1), result);
        var filled = Assert.Single(_systemEvents.Events);
        Assert.Equal(SystemEventSeverity.Warning, filled.Severity);
        Assert.Equal("1", filled.Details["stillMissing"]);
    }

    [Fact]
    public async Task Transient_failures_are_retried()
    {
        var source = new FakeHistoricalSource(2);
        source.Failures.Enqueue(new HistoricalDataException("Timeout.", isTransient: true));

        var result = await Filler(source).FillAsync("BTCUSDT", CandleInterval.FiveMinutes, new CandleGap(OpenTime(2), 1), CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Retry_waits_at_least_the_retry_after_requested_by_the_exchange()
    {
        var source = new FakeHistoricalSource(2);
        source.Failures.Enqueue(new HistoricalDataException("Rate limited.", isTransient: true, retryAfter: TimeSpan.FromMilliseconds(300)));
        var started = System.Diagnostics.Stopwatch.StartNew();

        await Filler(source).FillAsync("BTCUSDT", CandleInterval.FiveMinutes, new CandleGap(OpenTime(2), 1), CancellationToken.None);

        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(290), $"Waited only {started.Elapsed.TotalMilliseconds} ms.");
    }

    [Fact]
    public async Task Persistent_transient_failure_gives_up_after_max_attempts_and_is_recorded()
    {
        var source = new FakeHistoricalSource { AlwaysFailWith = new HistoricalDataException("Unreachable.", isTransient: true) };

        var result = await Filler(source).FillAsync("BTCUSDT", CandleInterval.FiveMinutes, new CandleGap(OpenTime(2), 4), CancellationToken.None);

        Assert.Equal(new GapFillResult(4, 0, 4), result);
        Assert.Equal(3, source.Calls);
        Assert.Equal(SystemEventTypes.MarketDataGapFillFailed, Assert.Single(_systemEvents.Events).EventType);
    }

    [Fact]
    public async Task Non_transient_failure_is_not_retried()
    {
        var source = new FakeHistoricalSource { AlwaysFailWith = new HistoricalDataException("Invalid symbol.", isTransient: false) };

        await Filler(source).FillAsync("BTCUSDT", CandleInterval.FiveMinutes, new CandleGap(OpenTime(2), 1), CancellationToken.None);

        Assert.Equal(1, source.Calls);
        Assert.Equal(SystemEventTypes.MarketDataGapFillFailed, Assert.Single(_systemEvents.Events).EventType);
    }

    [Fact]
    public async Task Start_up_fills_the_recent_gaps_found_in_the_store()
    {
        foreach (var index in new[] { 0, 1, 4, 5, 8 })
        {
            _candles.Seed(Candle(index));
        }

        var results = await Filler(new FakeHistoricalSource(2, 3, 6, 7)).FillRecentGapsAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal([.. Enumerable.Range(0, 9).Select(OpenTime)], _candles.Candles.Select(c => c.OpenTimeUtc));
    }

    private static readonly DateTimeOffset ManualNow = OpenTime(20);

    private CandleGapFiller Filler(IHistoricalCandleSource source) =>
        new(source, _candles, _systemEvents, new BackfillOptions(), new ManualTimeProvider(ManualNow),
            NullLogger<CandleGapFiller>.Instance, new ExponentialBackoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), new Random(1)));
}
