using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Omega.Core.MarketData;
using Omega.Core.Resilience;
using Omega.MarketData.Configuration;
using Omega.MarketData.Integrity;
using Omega.MarketData.Transport;

namespace Omega.MarketData.Binance;

/// <summary>
/// Closed-candle stream for one symbol and interval from the Binance Spot
/// kline raw stream (<c>&lt;symbol&gt;@kline_&lt;interval&gt;</c>).
/// </summary>
/// <remarks>
/// Guarantees to consumers:
/// <list type="bullet">
/// <item>Only closed klines become <see cref="CandleClosedEvent"/>; in-progress updates are never emitted.</item>
/// <item>Candles are emitted once, in increasing open-time order, across reconnections.</item>
/// <item>Invalid messages are logged and skipped; they never produce a candle.</item>
/// <item>Missing candles are reported as <see cref="DataGapDetectedEvent"/>; they are not back-filled here.</item>
/// <item>The connection is re-established with exponential backoff after failures, silence
/// (<see cref="MarketDataOptions.ReceiveIdleTimeout"/>), a <c>serverShutdown</c> event, or
/// when it approaches the exchange's 24-hour connection limit.</item>
/// </list>
/// </remarks>
public sealed partial class BinanceKlineStream : IMarketDataStream
{
    /// <summary>Lineage recorded with every candle produced by this adapter.</summary>
    public const string SourceName = "binance-spot-ws";

    private readonly MarketDataOptions _options;
    private readonly IWebSocketTransportFactory _transportFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BinanceKlineStream> _logger;
    private readonly ExponentialBackoff _backoff;

    public BinanceKlineStream(
        MarketDataOptions options,
        IWebSocketTransportFactory transportFactory,
        TimeProvider timeProvider,
        ILogger<BinanceKlineStream> logger,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid market-data options: {string.Join(" ", errors)}", nameof(options));
        }

        _options = options;
        _transportFactory = transportFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _backoff = new ExponentialBackoff(options.ReconnectInitialDelay, options.ReconnectMaxDelay, random ?? Random.Shared);

        StreamUri = BuildStreamUri(options);
    }

    /// <summary>Raw-stream URL, for example wss://data-stream.binance.vision/ws/btcusdt@kline_5m.</summary>
    public Uri StreamUri { get; }

    public async IAsyncEnumerable<MarketDataEvent> ReadEventsAsync(
        DateTimeOffset? lastKnownOpenTimeUtc,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<MarketDataEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = RunAsync(channel.Writer, lastKnownOpenTimeUtc, stop.Token);

        try
        {
            // Reading uses CancellationToken.None: the producer completes the channel
            // when cancelled, so every event already produced is still delivered.
            await foreach (var marketDataEvent in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return marketDataEvent;
            }
        }
        finally
        {
            // Also runs when the consumer stops early. Awaiting the producer
            // surfaces unexpected failures (bugs) instead of ending silently.
            await stop.CancelAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }
    }

    private async Task RunAsync(
        ChannelWriter<MarketDataEvent> writer, DateTimeOffset? lastKnownOpenTimeUtc, CancellationToken cancellationToken)
    {
        var sequencer = new ClosedCandleSequencer(_options.Interval, lastKnownOpenTimeUtc);
        var failedAttempts = 0;
        Exception? failure = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Publish(writer, new ConnectionStatusChangedEvent(ConnectionStatus.Connecting, $"Connecting to {StreamUri}", Now));

                var session = await RunSessionAsync(writer, sequencer, cancellationToken).ConfigureAwait(false);

                Publish(writer, new ConnectionStatusChangedEvent(ConnectionStatus.Disconnected, session.Reason, Now));

                if (session.ReceivedData)
                {
                    failedAttempts = 0;
                }

                if (session.ReconnectImmediately)
                {
                    LogReconnectingNow(_logger, session.Reason);
                    continue;
                }

                var delay = _backoff.GetDelay(failedAttempts);
                failedAttempts++;
                LogReconnectScheduled(_logger, session.Reason, delay.TotalSeconds, failedAttempts);

                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            failure = ex;
            LogStreamFailed(_logger, ex);
            throw;
        }
        finally
        {
            if (failure is null)
            {
                Publish(writer, new ConnectionStatusChangedEvent(ConnectionStatus.Disconnected, "Stream stopped.", Now));
            }

            writer.TryComplete(failure);
        }
    }

    private async Task<SessionOutcome> RunSessionAsync(
        ChannelWriter<MarketDataEvent> writer,
        ClosedCandleSequencer sequencer,
        CancellationToken cancellationToken)
    {
        var transport = _transportFactory.Create();
        await using var _ = transport.ConfigureAwait(false);

        try
        {
            using var connectTimeout = new CancellationTokenSource(_options.ConnectTimeout, _timeProvider);
            using var connectToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectTimeout.Token);
            await transport.ConnectAsync(StreamUri, connectToken.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SessionOutcome.Failed(string.Create(CultureInfo.InvariantCulture, $"Connection timed out after {_options.ConnectTimeout.TotalSeconds:0.#} s."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SessionOutcome.Failed($"Connection failed: {ex.Message}");
        }

        var connectedAt = Now;
        Publish(writer, new ConnectionStatusChangedEvent(ConnectionStatus.Connected, $"Connected to {StreamUri}", connectedAt));

        var receivedData = false;

        while (true)
        {
            string? message;

            try
            {
                using var idleTimeout = new CancellationTokenSource(_options.ReceiveIdleTimeout, _timeProvider);
                using var receiveToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idleTimeout.Token);
                message = await transport.ReceiveTextMessageAsync(receiveToken.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new SessionOutcome(
                    string.Create(CultureInfo.InvariantCulture, $"No message received for {_options.ReceiveIdleTimeout.TotalSeconds:0.#} s."),
                    receivedData,
                    false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new SessionOutcome($"Receive failed: {ex.Message}", receivedData, false);
            }

            if (message is null)
            {
                return new SessionOutcome("Server closed the connection.", receivedData, false);
            }

            switch (BinanceStreamMessageParser.Parse(message))
            {
                case BinanceKlineMessage { Kline: var kline }:
                    receivedData = true;

                    if (!kline.IsClosed)
                    {
                        break;
                    }

                    HandleClosedKline(writer, sequencer, kline);

                    if (Now - connectedAt >= _options.MaxConnectionLifetime)
                    {
                        // Renew right after a close: the next close is a full interval away.
                        return new SessionOutcome("Planned renewal before the exchange's 24-hour connection limit.", true, true);
                    }

                    break;

                case BinanceServerShutdownMessage:
                    return new SessionOutcome("Server announced shutdown (serverShutdown event).", receivedData, true);

                case BinanceInvalidMessage invalid:
                    LogInvalidMessage(_logger, invalid.Reason);
                    break;

                case BinanceIgnoredMessage ignored:
                    LogIgnoredMessage(_logger, ignored.Description);
                    break;
            }
        }
    }

    private void HandleClosedKline(ChannelWriter<MarketDataEvent> writer, ClosedCandleSequencer sequencer, BinanceKline kline)
    {
        var normalized = BinanceKlineNormalizer.ToClosedCandle(kline, _options.Symbol, _options.Interval);

        if (normalized.IsFailure)
        {
            LogRejectedKline(_logger, normalized.Error!.Code, normalized.Error.Message);
            return;
        }

        var candle = normalized.Value;
        var sequence = sequencer.Accept(candle);

        switch (sequence.Outcome)
        {
            case CandleSequenceOutcome.Duplicate:
                LogDuplicateCandle(_logger, candle.OpenTimeUtc);
                return;

            case CandleSequenceOutcome.OutOfOrder:
                LogOutOfOrderCandle(_logger, candle.OpenTimeUtc, sequencer.LastOpenTimeUtc);
                return;

            case CandleSequenceOutcome.AcceptedAfterGap:
                Publish(writer, new DataGapDetectedEvent(
                    candle.Symbol, candle.Interval, sequence.FirstMissingOpenTimeUtc!.Value, sequence.MissingCandles, Now));
                break;
        }

        Publish(writer, new CandleClosedEvent(candle, SourceName, Now));
    }

    private static void Publish(ChannelWriter<MarketDataEvent> writer, MarketDataEvent marketDataEvent) =>
        writer.TryWrite(marketDataEvent);

    private DateTimeOffset Now => _timeProvider.GetUtcNow();

    private static Uri BuildStreamUri(MarketDataOptions options)
    {
        var baseUrl = options.StreamBaseUrl.TrimEnd('/');
        var streamName = $"{options.Symbol.ToLowerInvariant()}@kline_{BinanceIntervals.ToCode(options.Interval)}";
        return new Uri($"{baseUrl}/ws/{streamName}");
    }

    private readonly record struct SessionOutcome(string Reason, bool ReceivedData, bool ReconnectImmediately)
    {
        public static SessionOutcome Failed(string reason) => new(reason, false, false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnecting immediately: {Reason}")]
    private static partial void LogReconnectingNow(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Market-data connection lost: {Reason} Reconnecting in {DelaySeconds:0.0} s (attempt {Attempt}).")]
    private static partial void LogReconnectScheduled(ILogger logger, string reason, double delaySeconds, int attempt);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Market-data stream stopped because of an unexpected error.")]
    private static partial void LogStreamFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid market-data message skipped: {Reason}")]
    private static partial void LogInvalidMessage(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Market-data message ignored: {Description}")]
    private static partial void LogIgnoredMessage(ILogger logger, string description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed kline rejected ({ErrorCode}): {ErrorMessage}")]
    private static partial void LogRejectedKline(ILogger logger, string errorCode, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Duplicate closed candle {OpenTimeUtc:O} discarded.")]
    private static partial void LogDuplicateCandle(ILogger logger, DateTimeOffset openTimeUtc);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Out-of-order closed candle {OpenTimeUtc:O} discarded; last accepted was {LastOpenTimeUtc:O}.")]
    private static partial void LogOutOfOrderCandle(ILogger logger, DateTimeOffset openTimeUtc, DateTimeOffset? lastOpenTimeUtc);
}
