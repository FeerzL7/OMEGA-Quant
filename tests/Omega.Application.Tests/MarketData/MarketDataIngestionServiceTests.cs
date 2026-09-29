using Microsoft.Extensions.Logging.Abstractions;
using Omega.Application.MarketData;
using Omega.Application.Tests.TestSupport;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Core.Resilience;
using Omega.Core.SystemEvents;
using Omega.MarketData;
using Omega.MarketData.Configuration;
using static Omega.Application.Tests.TestSupport.TestCandles;

namespace Omega.Application.Tests.MarketData;

public class MarketDataIngestionServiceTests
{
    private readonly InMemoryCandleStore _candles = new();
    private readonly InMemorySystemEventStore _systemEvents = new();

    [Fact]
    public async Task Resumes_after_the_latest_stored_candle()
    {
        _candles.Seed(Candle(3));
        var stream = new FakeMarketDataStream();

        await CreateService(stream).RunAsync(CancellationToken.None);

        Assert.Equal(OpenTime(3), stream.ReceivedLastKnownOpenTimeUtc);
    }

    [Fact]
    public async Task Starts_from_scratch_when_nothing_is_stored()
    {
        var stream = new FakeMarketDataStream();

        await CreateService(stream).RunAsync(CancellationToken.None);

        Assert.True(stream.WasRead);
        Assert.Null(stream.ReceivedLastKnownOpenTimeUtc);
    }

    [Fact]
    public async Task Stores_closed_candles_with_their_lineage()
    {
        var stream = new FakeMarketDataStream(Closed(0), Closed(1));

        await CreateService(stream).RunAsync(CancellationToken.None);

        Assert.Equal([Candle(0), Candle(1)], _candles.Candles);
        Assert.Equal(("test-source", OpenTime(1).AddMinutes(5).AddMilliseconds(150)), _candles.Lineage(OpenTime(1)));
    }

    [Fact]
    public async Task Records_start_connections_gaps_and_stop_but_not_connection_attempts()
    {
        var now = DateTimeOffset.UtcNow;
        var stream = new FakeMarketDataStream(
            new ConnectionStatusChangedEvent(ConnectionStatus.Connecting, "Connecting", now),
            new ConnectionStatusChangedEvent(ConnectionStatus.Connected, "Connected", now),
            new DataGapDetectedEvent("BTCUSDT", CandleInterval.FiveMinutes, OpenTime(2), 3, now),
            new ConnectionStatusChangedEvent(ConnectionStatus.Disconnected, "Server closed the connection.", now));

        await CreateService(stream).RunAsync(CancellationToken.None);

        Assert.Equal(
            [
                SystemEventTypes.IngestionStarted,
                SystemEventTypes.MarketDataConnected,
                SystemEventTypes.MarketDataGapDetected,
                SystemEventTypes.MarketDataDisconnected,
                SystemEventTypes.IngestionStopped,
            ],
            _systemEvents.EventTypes);

        var gap = _systemEvents.Events[2];
        Assert.Equal(SystemEventSeverity.Warning, gap.Severity);
        Assert.Equal("3", gap.Details["missingCandles"]);
        Assert.Equal("2026-01-01T00:10:00.0000000+00:00", gap.Details["firstMissingOpenTimeUtc"]);
        Assert.Equal(MarketDataIngestionService.EventSource, gap.Source);
    }

    [Fact]
    public async Task Transient_storage_failures_are_retried_until_the_candle_is_stored()
    {
        _candles.SaveFailures.Enqueue(new PersistenceException("Connection lost.", isTransient: true));
        _candles.SaveFailures.Enqueue(new PersistenceException("Timeout.", isTransient: true));

        await CreateService(new FakeMarketDataStream(Closed(0))).RunAsync(CancellationToken.None);

        Assert.Equal([Candle(0)], _candles.Candles);
        Assert.Equal(3, _candles.SaveCalls);
    }

    [Fact]
    public async Task Non_transient_storage_failure_stops_ingestion()
    {
        _candles.SaveFailures.Enqueue(new PersistenceException("Constraint violated.", isTransient: false));
        var service = CreateService(new FakeMarketDataStream(Closed(0), Closed(1)));

        await Assert.ThrowsAsync<PersistenceException>(() => service.RunAsync(CancellationToken.None));

        Assert.Empty(_candles.Candles);
        Assert.Equal(SystemEventTypes.IngestionStopped, _systemEvents.EventTypes[^1]);
    }

    [Fact]
    public async Task Conflicting_candle_is_recorded_and_the_stored_one_is_kept()
    {
        _candles.Seed(Candle(0, close: 100.5m));
        var stream = new FakeMarketDataStream(Closed(0, close: 100.7m));

        await CreateService(stream).RunAsync(CancellationToken.None);

        Assert.Equal(100.5m, Assert.Single(_candles.Candles).Close);
        var conflict = Assert.Single(_systemEvents.Events, e => e.EventType == SystemEventTypes.CandleConflict);
        Assert.Equal(SystemEventSeverity.Warning, conflict.Severity);
        Assert.Equal("100.7", conflict.Details["receivedClose"]);
    }

    [Fact]
    public async Task Failing_to_store_system_events_does_not_stop_ingestion()
    {
        _systemEvents.Fail = true;

        await CreateService(new FakeMarketDataStream(Closed(0), Closed(1))).RunAsync(CancellationToken.None);

        Assert.Equal([Candle(0), Candle(1)], _candles.Candles);
    }

    [Fact]
    public async Task Stopping_while_a_save_is_being_retried_ends_without_throwing()
    {
        _candles.AlwaysFailWith = new PersistenceException("Database down.", isTransient: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await CreateService(new FakeMarketDataStream(Closed(0))).RunAsync(cancellation.Token);

        Assert.Empty(_candles.Candles);
        Assert.True(_candles.SaveCalls > 1);
        Assert.Equal(SystemEventTypes.IngestionStopped, _systemEvents.EventTypes[^1]);
    }

    [Fact]
    public async Task Events_reported_while_stopping_are_still_recorded()
    {
        var systemEvents = new CancellationAwareSystemEventStore();
        var stream = new StreamReportingStopOnCancellation(Closed(0));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await CreateService(stream, systemEvents).RunAsync(cancellation.Token);

        Assert.Equal(
            [SystemEventTypes.IngestionStarted, SystemEventTypes.MarketDataDisconnected, SystemEventTypes.IngestionStopped],
            systemEvents.Events.Select(e => e.EventType));
        Assert.Equal("Stream stopped.", systemEvents.Events[1].Message);
        Assert.Equal(SystemEventSeverity.Information, systemEvents.Events[1].Severity);
    }

    [Fact]
    public async Task Gap_reported_by_the_stream_is_filled_before_the_next_candle_is_stored()
    {
        _candles.Seed(Candle(0));
        var now = DateTimeOffset.UtcNow;
        var stream = new FakeMarketDataStream(
            new DataGapDetectedEvent("BTCUSDT", CandleInterval.FiveMinutes, OpenTime(1), 2, now),
            Closed(3));

        await CreateService(stream, gapFiller: GapFiller(new FakeHistoricalSource(1, 2))).RunAsync(CancellationToken.None);

        Assert.Equal([.. Enumerable.Range(0, 4).Select(OpenTime)], _candles.Candles.Select(c => c.OpenTimeUtc));
        Assert.Contains(_systemEvents.Events, e => e.EventType == SystemEventTypes.MarketDataGapFilled);
    }

    [Fact]
    public async Task Recent_gaps_are_filled_at_start_up()
    {
        _candles.Seed(Candle(0));
        _candles.Seed(Candle(3));

        await CreateService(new FakeMarketDataStream(), gapFiller: GapFiller(new FakeHistoricalSource(1, 2))).RunAsync(CancellationToken.None);

        Assert.Equal([.. Enumerable.Range(0, 4).Select(OpenTime)], _candles.Candles.Select(c => c.OpenTimeUtc));
    }

    [Fact]
    public async Task Failed_fill_does_not_stop_ingestion()
    {
        _candles.Seed(Candle(0));
        var source = new FakeHistoricalSource { AlwaysFailWith = new HistoricalDataException("Unreachable.", isTransient: false) };
        var stream = new FakeMarketDataStream(
            new DataGapDetectedEvent("BTCUSDT", CandleInterval.FiveMinutes, OpenTime(1), 2, DateTimeOffset.UtcNow),
            Closed(3));

        await CreateService(stream, gapFiller: GapFiller(source)).RunAsync(CancellationToken.None);

        Assert.Equal([OpenTime(0), OpenTime(3)], _candles.Candles.Select(c => c.OpenTimeUtc));
        Assert.Contains(_systemEvents.Events, e => e.EventType == SystemEventTypes.MarketDataGapFillFailed);
    }

    [Fact]
    public async Task Clock_skew_is_recorded_as_a_warning()
    {
        var stream = new FakeMarketDataStream(
            new ClockSkewDetectedEvent("BTCUSDT", OpenTime(1), TimeSpan.FromMilliseconds(4200), DateTimeOffset.UtcNow));

        await CreateService(stream).RunAsync(CancellationToken.None);

        var skew = Assert.Single(_systemEvents.Events, e => e.EventType == SystemEventTypes.ClockSkewDetected);
        Assert.Equal(SystemEventSeverity.Warning, skew.Severity);
        Assert.Equal("4200", skew.Details["skewMs"]);
    }

    private CandleGapFiller GapFiller(IHistoricalCandleSource source) =>
        // Clock near the test candles: start-up back-fill only looks at the last 7 days.
        new(source, _candles, _systemEvents, new BackfillOptions(), new ManualTimeProvider(OpenTime(20)), NullLogger<CandleGapFiller>.Instance,
            new ExponentialBackoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), new Random(1)));

    private MarketDataIngestionService CreateService(
        IMarketDataStream stream, ISystemEventStore? systemEvents = null, CandleGapFiller? gapFiller = null) =>
        new(
            stream,
            _candles,
            systemEvents ?? _systemEvents,
            new MarketDataOptions(),
            TimeProvider.System,
            NullLogger<MarketDataIngestionService>.Instance,
            new ExponentialBackoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5), new Random(3)),
            gapFiller);
}
