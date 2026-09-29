using Microsoft.Extensions.Logging.Abstractions;
using Omega.Application.MarketData;
using Omega.Application.Tests.TestSupport;
using Omega.Core.MarketData;
using Omega.Core.SystemEvents;
using static Omega.Application.Tests.TestSupport.TestCandles;

namespace Omega.Application.Tests.MarketData;

public class MarketStateTests
{
    private readonly InMemoryCandleStore _candles = new();
    private readonly InMemorySystemEventStore _systemEvents = new();
    private readonly ManualTimeProvider _clock = new(OpenTime(3));

    [Fact]
    public async Task State_combines_the_latest_candle_and_recent_gaps()
    {
        _candles.Seed(Candle(0));
        _candles.Seed(Candle(2));

        var state = await Service().GetStateAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(Candle(2), state.LastClosedCandle);
        Assert.Equal(DataFreshness.Fresh, state.Freshness);
        Assert.Equal([new CandleGap(OpenTime(1), 1)], state.RecentGaps);
        Assert.False(state.IsReliable);
    }

    [Fact]
    public async Task Without_data_no_gap_search_is_needed()
    {
        var state = await Service().GetStateAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None);

        Assert.Equal(DataFreshness.NoData, state.Freshness);
        Assert.Equal(0, _candles.FindGapsCalls);
    }

    [Fact]
    public async Task Monitor_records_each_freshness_transition_once()
    {
        _candles.Seed(Candle(0));
        _clock.Now = OpenTime(1);
        var monitor = Monitor();

        // Fresh at start: nothing to report.
        var freshness = await monitor.EvaluateAsync("BTCUSDT", CandleInterval.FiveMinutes, null, CancellationToken.None);
        Assert.Empty(_systemEvents.Events);

        // Next candle overdue by more than the grace period: stale, reported once.
        _clock.Now = OpenTime(2).AddMinutes(2);
        freshness = await monitor.EvaluateAsync("BTCUSDT", CandleInterval.FiveMinutes, freshness, CancellationToken.None);
        freshness = await monitor.EvaluateAsync("BTCUSDT", CandleInterval.FiveMinutes, freshness, CancellationToken.None);

        // New candle stored: fresh again, reported once.
        _candles.Seed(Candle(1));
        freshness = await monitor.EvaluateAsync("BTCUSDT", CandleInterval.FiveMinutes, freshness, CancellationToken.None);

        Assert.Equal(DataFreshness.Fresh, freshness);
        Assert.Equal([SystemEventTypes.MarketDataStale, SystemEventTypes.MarketDataFresh], _systemEvents.EventTypes);
        Assert.Equal(SystemEventSeverity.Warning, _systemEvents.Events[0].Severity);
    }

    [Fact]
    public async Task Monitor_reports_stale_data_found_at_start()
    {
        _candles.Seed(Candle(0));
        _clock.Now = OpenTime(10);

        await Monitor().EvaluateAsync("BTCUSDT", CandleInterval.FiveMinutes, null, CancellationToken.None);

        Assert.Equal([SystemEventTypes.MarketDataStale], _systemEvents.EventTypes);
    }

    private MarketStateService Service() => new(_candles, new MarketStateOptions(), _clock);

    private MarketDataFreshnessMonitor Monitor() =>
        new(Service(), _systemEvents, new MarketStateOptions(), _clock, NullLogger<MarketDataFreshnessMonitor>.Instance);
}
