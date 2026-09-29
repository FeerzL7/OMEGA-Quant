using System.Globalization;
using Microsoft.Extensions.Logging;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Core.Resilience;
using Omega.Core.SystemEvents;
using Omega.MarketData;
using Omega.MarketData.Configuration;

namespace Omega.Application.MarketData;

/// <summary>
/// Market data → persistence. Resumes from the latest stored candle, stores
/// every closed candle, and records connection changes, gaps and conflicts as
/// system events.
/// </summary>
/// <remarks>
/// <para>
/// Candles are never dropped silently: a transient storage failure is retried
/// with backoff until it succeeds or the service stops (the stream keeps
/// buffering meanwhile). A non-transient failure stops the service. A candle
/// that could not be stored before shutdown is reported as a gap on the next start.
/// </para>
/// <para>System events are best effort: failing to store one is logged and does not stop ingestion.</para>
/// </remarks>
public sealed partial class MarketDataIngestionService
{
    public const string EventSource = "MarketDataIngestion";

    private static readonly TimeSpan StopEventTimeout = TimeSpan.FromSeconds(5);

    private readonly IMarketDataStream _stream;
    private readonly ICandleStore _candles;
    private readonly ISystemEventStore _systemEvents;
    private readonly MarketDataOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MarketDataIngestionService> _logger;
    private readonly ExponentialBackoff _saveBackoff;

    public MarketDataIngestionService(
        IMarketDataStream stream,
        ICandleStore candles,
        ISystemEventStore systemEvents,
        MarketDataOptions options,
        TimeProvider timeProvider,
        ILogger<MarketDataIngestionService> logger,
        ExponentialBackoff? saveBackoff = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(systemEvents);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _stream = stream;
        _candles = candles;
        _systemEvents = systemEvents;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _saveBackoff = saveBackoff ?? new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), Random.Shared);
    }

    /// <summary>
    /// Runs until <paramref name="cancellationToken"/> is cancelled (returns normally)
    /// or a non-recoverable error occurs (throws).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var latest = await _candles.GetLatestAsync(_options.Symbol, _options.Interval, cancellationToken).ConfigureAwait(false);
        var resumeAfter = latest?.OpenTimeUtc;
        var stored = 0;

        LogStarting(_logger, _options.Symbol, _options.Interval, resumeAfter);
        await RecordAsync(
            SystemEventTypes.IngestionStarted,
            SystemEventSeverity.Information,
            $"Market-data ingestion started for {_options.Symbol} {_options.Interval}.",
            Details(("symbol", _options.Symbol), ("interval", _options.Interval.ToCode()), ("resumeAfterOpenTimeUtc", Format(resumeAfter))),
            cancellationToken).ConfigureAwait(false);

        try
        {
            await foreach (var marketDataEvent in _stream.ReadEventsAsync(resumeAfter, cancellationToken).ConfigureAwait(false))
            {
                switch (marketDataEvent)
                {
                    case CandleClosedEvent closed:
                        if (await StoreAsync(closed, cancellationToken).ConfigureAwait(false) == CandleSaveOutcome.Inserted)
                        {
                            stored++;
                        }

                        break;

                    case DataGapDetectedEvent gap:
                        await RecordGapAsync(gap, cancellationToken).ConfigureAwait(false);
                        break;

                    case ConnectionStatusChangedEvent connection:
                        await RecordConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown, possibly while retrying a save.
        }
        finally
        {
            LogStopped(_logger, stored);

            await RecordAsync(
                SystemEventTypes.IngestionStopped,
                SystemEventSeverity.Information,
                "Market-data ingestion stopped.",
                Details(("candlesStored", stored.ToString(CultureInfo.InvariantCulture))),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CandleSaveOutcome> StoreAsync(CandleClosedEvent closed, CancellationToken cancellationToken)
    {
        var candle = closed.Candle;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var outcome = await _candles.SaveAsync(candle, closed.Source, closed.ObservedAtUtc, cancellationToken).ConfigureAwait(false);
                await ReportSavedAsync(closed, outcome, cancellationToken).ConfigureAwait(false);
                return outcome;
            }
            catch (PersistenceException ex) when (ex.IsTransient)
            {
                var delay = _saveBackoff.GetDelay(attempt);
                LogSaveRetry(_logger, ex, candle.Symbol, candle.OpenTimeUtc, attempt + 1, delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    LogNotStored(_logger, candle.Symbol, candle.OpenTimeUtc);
                    throw;
                }
            }
        }
    }

    private async Task ReportSavedAsync(CandleClosedEvent closed, CandleSaveOutcome outcome, CancellationToken cancellationToken)
    {
        var candle = closed.Candle;

        switch (outcome)
        {
            case CandleSaveOutcome.Inserted:
                LogCandleStored(
                    _logger, candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.Open, candle.High, candle.Low, candle.Close,
                    candle.BaseVolume, candle.TradeCount, (closed.ObservedAtUtc - candle.CloseTimeUtc).TotalMilliseconds);
                break;

            case CandleSaveOutcome.AlreadyStored:
                LogCandleAlreadyStored(_logger, candle.Symbol, candle.OpenTimeUtc);
                break;

            case CandleSaveOutcome.Conflict:
                LogCandleConflict(_logger, candle.Symbol, candle.OpenTimeUtc);
                await RecordAsync(
                    SystemEventTypes.CandleConflict,
                    SystemEventSeverity.Warning,
                    $"Received {candle.Symbol} {candle.Interval} candle {Format(candle.OpenTimeUtc)} differs from the stored one; the stored candle was kept.",
                    Details(
                        ("symbol", candle.Symbol),
                        ("interval", candle.Interval.ToCode()),
                        ("openTimeUtc", Format(candle.OpenTimeUtc)),
                        ("receivedOpen", Format(candle.Open)),
                        ("receivedHigh", Format(candle.High)),
                        ("receivedLow", Format(candle.Low)),
                        ("receivedClose", Format(candle.Close)),
                        ("receivedBaseVolume", Format(candle.BaseVolume)),
                        ("source", closed.Source)),
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private Task RecordGapAsync(DataGapDetectedEvent gap, CancellationToken cancellationToken)
    {
        LogGap(_logger, gap.MissingCandles, gap.Symbol, gap.Interval, gap.FirstMissingOpenTimeUtc);

        return RecordAsync(
            SystemEventTypes.MarketDataGapDetected,
            SystemEventSeverity.Warning,
            $"{gap.MissingCandles} {gap.Symbol} {gap.Interval} candle(s) missing from {Format(gap.FirstMissingOpenTimeUtc)}.",
            Details(
                ("symbol", gap.Symbol),
                ("interval", gap.Interval.ToCode()),
                ("firstMissingOpenTimeUtc", Format(gap.FirstMissingOpenTimeUtc)),
                ("missingCandles", gap.MissingCandles.ToString(CultureInfo.InvariantCulture))),
            cancellationToken);
    }

    private Task RecordConnectionAsync(ConnectionStatusChangedEvent connection, CancellationToken cancellationToken)
    {
        LogConnection(_logger, connection.Status, connection.Reason);

        // "Connecting" is transient noise; connected/disconnected are the facts worth keeping.
        // A disconnection is a warning unless it is part of an orderly shutdown.
        var disconnectSeverity = cancellationToken.IsCancellationRequested
            ? SystemEventSeverity.Information
            : SystemEventSeverity.Warning;

        return connection.Status switch
        {
            ConnectionStatus.Connected => RecordAsync(
                SystemEventTypes.MarketDataConnected, SystemEventSeverity.Information, connection.Reason, null, cancellationToken),
            ConnectionStatus.Disconnected => RecordAsync(
                SystemEventTypes.MarketDataDisconnected, disconnectSeverity, connection.Reason, null, cancellationToken),
            _ => Task.CompletedTask,
        };
    }

    private async Task RecordAsync(
        string eventType,
        SystemEventSeverity severity,
        string message,
        IReadOnlyDictionary<string, string>? details,
        CancellationToken cancellationToken)
    {
        var systemEvent = new SystemEvent(_timeProvider.GetUtcNow(), EventSource, eventType, severity, message, details);

        // Events produced while stopping (the final disconnection, the stop itself) must
        // still be stored, so once shutdown has started they get a short budget of their own.
        using var shutdownBudget = cancellationToken.IsCancellationRequested
            ? new CancellationTokenSource(StopEventTimeout, _timeProvider)
            : null;
        var token = shutdownBudget?.Token ?? cancellationToken;

        try
        {
            await _systemEvents.AppendAsync(systemEvent, token).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            LogEventNotStored(_logger, ex, eventType);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogEventNotStored(_logger, null, eventType);
        }
    }

    private static Dictionary<string, string> Details(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static string Format(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture) ?? "none";

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Starting market-data ingestion for {Symbol} {Interval}; resuming after {ResumeAfterOpenTimeUtc:O} (null means no stored candles).")]
    private static partial void LogStarting(ILogger logger, string symbol, CandleInterval interval, DateTimeOffset? resumeAfterOpenTimeUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Market-data ingestion stopped; {Count} candle(s) stored in this run.")]
    private static partial void LogStopped(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Candle stored {Symbol} {Interval} {OpenTimeUtc:O} O={Open} H={High} L={Low} C={Close} V={BaseVolume} Trades={TradeCount} LatencyMs={LatencyMs:0}")]
    private static partial void LogCandleStored(
        ILogger logger, string symbol, CandleInterval interval, DateTimeOffset openTimeUtc, decimal open, decimal high, decimal low,
        decimal close, decimal baseVolume, long tradeCount, double latencyMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Candle {Symbol} {OpenTimeUtc:O} was already stored.")]
    private static partial void LogCandleAlreadyStored(ILogger logger, string symbol, DateTimeOffset openTimeUtc);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Candle {Symbol} {OpenTimeUtc:O} differs from the stored one; the stored candle was kept.")]
    private static partial void LogCandleConflict(ILogger logger, string symbol, DateTimeOffset openTimeUtc);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Could not store candle {Symbol} {OpenTimeUtc:O} (attempt {Attempt}); retrying in {DelaySeconds:0.0} s.")]
    private static partial void LogSaveRetry(ILogger logger, Exception exception, string symbol, DateTimeOffset openTimeUtc, int attempt, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Shutdown while candle {Symbol} {OpenTimeUtc:O} was not yet stored; it will appear as a gap on the next start.")]
    private static partial void LogNotStored(ILogger logger, string symbol, DateTimeOffset openTimeUtc);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Market-data gap: {MissingCandles} {Symbol} {Interval} candle(s) missing from {FirstMissingOpenTimeUtc:O}. Data integrity is not guaranteed for this period.")]
    private static partial void LogGap(ILogger logger, int missingCandles, string symbol, CandleInterval interval, DateTimeOffset firstMissingOpenTimeUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Market-data connection {Status}: {Reason}")]
    private static partial void LogConnection(ILogger logger, ConnectionStatus status, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "System event {EventType} could not be stored; it is only in the logs.")]
    private static partial void LogEventNotStored(ILogger logger, Exception? exception, string eventType);
}
