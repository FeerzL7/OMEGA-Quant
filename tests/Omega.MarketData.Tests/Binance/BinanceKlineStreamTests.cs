using Microsoft.Extensions.Logging.Abstractions;
using Omega.MarketData.Binance;
using Omega.MarketData.Configuration;
using Omega.MarketData.Tests.TestSupport;
using static Omega.MarketData.Tests.TestSupport.KlineMessages;

namespace Omega.MarketData.Tests.Binance;

public class BinanceKlineStreamTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Stream_url_uses_the_lower_case_raw_kline_stream()
    {
        var stream = CreateStream(new ScriptedTransportFactory());

        Assert.Equal("wss://data-stream.binance.vision/ws/btcusdt@kline_5m", stream.StreamUri.ToString());
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        var options = new MarketDataOptions { Symbol = "btcusdt" };

        Assert.Throws<ArgumentException>(() => new BinanceKlineStream(
            options, new ScriptedTransportFactory(), TimeProvider.System, NullLogger<BinanceKlineStream>.Instance));
    }

    [Fact]
    public async Task Emits_only_closed_candles_in_order()
    {
        var factory = new ScriptedTransportFactory(
            ScriptedTransport.Sending(Kline(0, closed: false), Kline(0), Kline(1, closed: false), Kline(1)));

        var events = await CollectAsync(CreateStream(factory), CandleCount(2));

        Assert.Equal([OpenTime(0), OpenTime(1)], ClosedOpenTimes(events));
        Assert.Equal(ConnectionStatus.Connecting, Assert.IsType<ConnectionStatusChangedEvent>(events[0]).Status);
        Assert.Equal(ConnectionStatus.Connected, Assert.IsType<ConnectionStatusChangedEvent>(events[1]).Status);
        Assert.Equal(1, factory.CreatedCount);
    }

    [Fact]
    public async Task Resuming_skips_known_candles_and_reports_the_gap_since_the_last_known_one()
    {
        // The consumer already has candle 1 (for example persisted before a restart).
        var factory = new ScriptedTransportFactory(ScriptedTransport.Sending(Kline(1), Kline(4)));

        var events = await CollectAsync(CreateStream(factory), CandleCount(1), lastKnownOpenTimeUtc: OpenTime(1));

        Assert.Equal([OpenTime(4)], ClosedOpenTimes(events));
        var gap = Assert.Single(events.OfType<DataGapDetectedEvent>());
        Assert.Equal(OpenTime(2), gap.FirstMissingOpenTimeUtc);
        Assert.Equal(2, gap.MissingCandles);
    }

    [Fact]
    public async Task Closed_candles_carry_their_source()
    {
        var factory = new ScriptedTransportFactory(ScriptedTransport.Sending(Kline(0)));

        var events = await CollectAsync(CreateStream(factory), CandleCount(1));

        Assert.Equal("binance-spot-ws", Assert.Single(events.OfType<CandleClosedEvent>()).Source);
    }

    [Fact]
    public async Task Reconnects_after_the_server_closes_and_discards_the_resent_candle()
    {
        var first = ScriptedTransport.SendingThenClosing(Kline(0), Kline(1));
        var factory = new ScriptedTransportFactory(first, ScriptedTransport.Sending(Kline(1), Kline(2)));

        var events = await CollectAsync(CreateStream(factory), CandleCount(3));

        Assert.Equal([OpenTime(0), OpenTime(1), OpenTime(2)], ClosedOpenTimes(events));
        Assert.DoesNotContain(events, e => e is DataGapDetectedEvent);
        Assert.Contains(events, e => IsDisconnect(e, "Server closed the connection."));
        Assert.Equal(2, factory.CreatedCount);
        Assert.True(first.IsDisposed);
    }

    [Fact]
    public async Task Reports_a_gap_when_candles_are_missing_across_a_reconnection()
    {
        var factory = new ScriptedTransportFactory(
            ScriptedTransport.SendingThenFailing(Kline(0)),
            ScriptedTransport.Sending(Kline(3)));

        var events = await CollectAsync(CreateStream(factory), CandleCount(2));

        var gap = Assert.Single(events.OfType<DataGapDetectedEvent>());
        Assert.Equal(OpenTime(1), gap.FirstMissingOpenTimeUtc);
        Assert.Equal(2, gap.MissingCandles);
        Assert.Equal("BTCUSDT", gap.Symbol);
        Assert.True(events.IndexOf(gap) < events.FindLastIndex(e => e is CandleClosedEvent));
        Assert.Contains(events, e => IsDisconnect(e, "Receive failed: Connection reset."));
    }

    [Fact]
    public async Task Server_shutdown_event_triggers_an_immediate_reconnection()
    {
        var factory = new ScriptedTransportFactory(
            ScriptedTransport.Sending(Kline(0), ServerShutdown),
            ScriptedTransport.Sending(Kline(1)));

        // A long backoff proves the reconnection does not wait for it.
        var stream = CreateStream(factory, reconnectDelay: TimeSpan.FromMinutes(5));
        var events = await CollectAsync(stream, CandleCount(2));

        Assert.Equal([OpenTime(0), OpenTime(1)], ClosedOpenTimes(events));
        Assert.Contains(events, e => IsDisconnect(e, "Server announced shutdown (serverShutdown event)."));
    }

    [Fact]
    public async Task Silent_connection_is_replaced_after_the_idle_timeout()
    {
        var factory = new ScriptedTransportFactory(ScriptedTransport.Silent(), ScriptedTransport.Sending(Kline(0)));

        var stream = CreateStream(factory, idleTimeout: TimeSpan.FromMilliseconds(200));
        var events = await CollectAsync(stream, CandleCount(1));

        Assert.Equal([OpenTime(0)], ClosedOpenTimes(events));
        Assert.Contains(events, e => IsDisconnect(e, "No message received for 0.2 s."));
    }

    [Fact]
    public async Task Invalid_messages_are_skipped_without_dropping_the_connection()
    {
        var factory = new ScriptedTransportFactory(ScriptedTransport.Sending(
            "not json",
            """{"e":"kline","E":1}""",
            Kline(0, symbol: "ETHUSDT"),
            Kline(0, interval: "1m"),
            Kline(0)));

        var events = await CollectAsync(CreateStream(factory), CandleCount(1));

        var candle = Assert.Single(events.OfType<CandleClosedEvent>()).Candle;
        Assert.Equal("BTCUSDT", candle.Symbol);
        Assert.Equal(OpenTime(0), candle.OpenTimeUtc);
        Assert.Equal(1, factory.CreatedCount);
    }

    [Fact]
    public async Task Connection_failures_are_retried()
    {
        var factory = new ScriptedTransportFactory(ScriptedTransport.FailingToConnect(), ScriptedTransport.Sending(Kline(0)));

        var events = await CollectAsync(CreateStream(factory), CandleCount(1));

        Assert.Equal([OpenTime(0)], ClosedOpenTimes(events));
        Assert.Contains(events, e => IsDisconnect(e, "Connection failed: Host unreachable."));
        Assert.Equal(2, factory.CreatedCount);
    }

    [Fact]
    public async Task Connection_is_renewed_after_a_candle_closes_once_the_lifetime_is_reached()
    {
        // Kline(1) on the first connection is never read: the renewal happens right after Kline(0) closes.
        var first = ScriptedTransport.Sending(Kline(0), Kline(1));
        var factory = new ScriptedTransportFactory(first, ScriptedTransport.Sending(Kline(1)));

        // The clock jumps one hour per read, so the one-hour lifetime is exceeded at the first close.
        var options = TestOptions(reconnectDelay: TimeSpan.FromMinutes(5));
        options.MaxConnectionLifetime = TimeSpan.FromHours(1);
        var stream = new BinanceKlineStream(
            options, factory, new HourlySteppingTimeProvider(), NullLogger<BinanceKlineStream>.Instance, new Random(7));

        var events = await CollectAsync(stream, CandleCount(2));

        Assert.Equal([OpenTime(0), OpenTime(1)], ClosedOpenTimes(events));
        Assert.Contains(events, e => IsDisconnect(e, "Planned renewal before the exchange's 24-hour connection limit."));
        Assert.True(first.IsDisposed);
    }

    [Fact]
    public async Task Cancellation_ends_the_stream_without_throwing()
    {
        var stream = CreateStream(new ScriptedTransportFactory(ScriptedTransport.Silent()));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var events = new List<MarketDataEvent>();

        await foreach (var marketDataEvent in stream.ReadEventsAsync(null, cancellation.Token))
        {
            events.Add(marketDataEvent);
        }

        Assert.True(IsDisconnect(events[^1], "Stream stopped."));
    }

    private static BinanceKlineStream CreateStream(
        ScriptedTransportFactory factory, TimeSpan? idleTimeout = null, TimeSpan? reconnectDelay = null) =>
        new(TestOptions(idleTimeout, reconnectDelay), factory, TimeProvider.System, NullLogger<BinanceKlineStream>.Instance, new Random(7));

    private static MarketDataOptions TestOptions(TimeSpan? idleTimeout = null, TimeSpan? reconnectDelay = null)
    {
        var delay = reconnectDelay ?? TimeSpan.FromMilliseconds(1);

        return new MarketDataOptions
        {
            ReceiveIdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(5),
            ReconnectInitialDelay = delay,
            ReconnectMaxDelay = delay,
        };
    }

    private static async Task<List<MarketDataEvent>> CollectAsync(
        BinanceKlineStream stream, Func<List<MarketDataEvent>, bool> isComplete, DateTimeOffset? lastKnownOpenTimeUtc = null)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var events = new List<MarketDataEvent>();

        await foreach (var marketDataEvent in stream.ReadEventsAsync(lastKnownOpenTimeUtc, timeout.Token))
        {
            events.Add(marketDataEvent);

            if (isComplete(events))
            {
                break;
            }
        }

        return events;
    }

    private static Func<List<MarketDataEvent>, bool> CandleCount(int count) =>
        events => events.OfType<CandleClosedEvent>().Count() >= count;

    private static List<DateTimeOffset> ClosedOpenTimes(List<MarketDataEvent> events) =>
        [.. events.OfType<CandleClosedEvent>().Select(e => e.Candle.OpenTimeUtc)];

    private static bool IsDisconnect(MarketDataEvent marketDataEvent, string reason) =>
        marketDataEvent is ConnectionStatusChangedEvent { Status: ConnectionStatus.Disconnected } connection
        && connection.Reason == reason;
}
