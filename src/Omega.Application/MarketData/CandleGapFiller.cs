using System.Globalization;
using Microsoft.Extensions.Logging;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Core.Resilience;
using Omega.Core.SystemEvents;
using Omega.MarketData;

namespace Omega.Application.MarketData;

/// <summary>Result of filling one gap.</summary>
/// <param name="Requested">Candles that were missing.</param>
/// <param name="Stored">Candles obtained and newly stored.</param>
/// <param name="StillMissing">Candles the source did not provide (for example exchange maintenance) or that failed.</param>
public sealed record GapFillResult(int Requested, int Stored, int StillMissing)
{
    public bool IsComplete => StillMissing == 0;
}

/// <summary>
/// Fills gaps in the stored candles from an <see cref="IHistoricalCandleSource"/>.
/// Best effort by design: a failure is recorded and never stops live ingestion;
/// the gap stays detectable in the store and is retried on the next start-up.
/// </summary>
public sealed partial class CandleGapFiller
{
    public const string EventSource = "CandleGapFiller";

    private readonly IHistoricalCandleSource _source;
    private readonly ICandleStore _candles;
    private readonly ISystemEventStore _systemEvents;
    private readonly BackfillOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CandleGapFiller> _logger;
    private readonly ExponentialBackoff _backoff;

    public CandleGapFiller(
        IHistoricalCandleSource source,
        ICandleStore candles,
        ISystemEventStore systemEvents,
        BackfillOptions options,
        TimeProvider timeProvider,
        ILogger<CandleGapFiller> logger,
        ExponentialBackoff? backoff = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(systemEvents);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _candles = candles;
        _systemEvents = systemEvents;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _backoff = backoff ?? new ExponentialBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), Random.Shared);
    }

    /// <summary>Fills every stored gap that overlaps the last <see cref="BackfillOptions.StartupLookback"/>.</summary>
    public async Task<IReadOnlyList<GapFillResult>> FillRecentGapsAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        IReadOnlyList<CandleGap> gaps;

        try
        {
            gaps = await _candles.FindGapsAsync(symbol, interval, now - _options.StartupLookback, now, cancellationToken).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            LogGapSearchFailed(_logger, ex, symbol);
            return [];
        }

        LogGapsFound(_logger, gaps.Count, symbol, _options.StartupLookback.TotalDays);

        var results = new List<GapFillResult>();
        foreach (var gap in gaps)
        {
            results.Add(await FillAsync(symbol, interval, gap, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Fills one gap. Never throws except on cancellation.</summary>
    public async Task<GapFillResult> FillAsync(string symbol, CandleInterval interval, CandleGap gap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gap);

        try
        {
            var (stored, obtained) = await FetchAndStoreAsync(
                symbol, interval, gap.FirstMissingOpenTimeUtc, gap.EndOpenTimeUtc(interval), cancellationToken).ConfigureAwait(false);

            // Already-stored or conflicting candles exist, so they are not missing either.
            var result = new GapFillResult(gap.MissingCandles, stored, Math.Max(0, gap.MissingCandles - obtained));
            await RecordFilledAsync(symbol, interval, gap, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is HistoricalDataException or PersistenceException)
        {
            await RecordFailedAsync(symbol, interval, gap, ex, cancellationToken).ConfigureAwait(false);
            return new GapFillResult(gap.MissingCandles, 0, gap.MissingCandles);
        }
    }

    /// <summary>
    /// Imports history from <see cref="BackfillOptions.HistoryStart"/> up to the oldest stored candle (or up to
    /// the last closed candle when nothing is stored), in 30-day chunks. No-op when HistoryStart is not set or
    /// already covered. Never throws except on cancellation; a failure is recorded and the next start resumes.
    /// </summary>
    public async Task ImportHistoryAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken)
    {
        if (_options.HistoryStart is not { } historyStart)
        {
            return;
        }

        var length = interval.ToTimeSpan();
        var from = Align(historyStart, length);
        Candle? earliest;

        try
        {
            earliest = await _candles.GetEarliestAsync(symbol, interval, cancellationToken).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            LogGapSearchFailed(_logger, ex, symbol);
            return;
        }

        // Up to the oldest stored candle, or to the start of the candle still forming.
        var to = earliest?.OpenTimeUtc ?? Align(_timeProvider.GetUtcNow(), length);
        if (to <= from)
        {
            return;
        }

        var expected = (int)((to - from).Ticks / length.Ticks);
        LogHistoryImportStarted(_logger, symbol, from, to, expected);

        var storedTotal = 0;
        var obtainedTotal = 0;
        var chunk = TimeSpan.FromDays(30);

        try
        {
            for (var start = from; start < to; start += chunk)
            {
                var end = start + chunk < to ? start + chunk : to;
                var (stored, obtained) = await FetchAndStoreAsync(symbol, interval, start, end, cancellationToken).ConfigureAwait(false);
                storedTotal += stored;
                obtainedTotal += obtained;
                LogHistoryImportProgress(_logger, symbol, end, obtainedTotal, expected);
            }
        }
        catch (Exception ex) when (ex is HistoricalDataException or PersistenceException)
        {
            LogHistoryImportFailed(_logger, ex, symbol, storedTotal);
            await RecordAsync(
                SystemEventTypes.HistoryImportFailed,
                SystemEventSeverity.Warning,
                $"History import of {symbol} {interval} stopped after storing {storedTotal} candle(s): {ex.Message}. It resumes on the next start.",
                HistoryDetails(symbol, interval, from, to, expected, storedTotal),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var missing = Math.Max(0, expected - obtainedTotal);
        await RecordAsync(
            SystemEventTypes.HistoryImported,
            missing == 0 ? SystemEventSeverity.Information : SystemEventSeverity.Warning,
            $"History of {symbol} {interval} imported from {Format(from)} to {Format(to)}: {storedTotal} stored, {missing} not available from {_source.SourceName}.",
            HistoryDetails(symbol, interval, from, to, expected, storedTotal),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches [from, to) with retries for transient errors and stores it. Returns (newly stored, obtained).</summary>
    private async Task<(int Stored, int Obtained)> FetchAndStoreAsync(
        string symbol, CandleInterval interval, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var candles = await _source.GetClosedCandlesAsync(symbol, interval, from, to, cancellationToken).ConfigureAwait(false);

                var stored = 0;
                foreach (var candle in candles)
                {
                    var outcome = await _candles.SaveAsync(candle, _source.SourceName, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    if (outcome == CandleSaveOutcome.Inserted)
                    {
                        stored++;
                    }
                    else if (outcome == CandleSaveOutcome.Conflict)
                    {
                        LogConflict(_logger, symbol, candle.OpenTimeUtc);
                    }
                }

                return (stored, candles.Count);
            }
            catch (HistoricalDataException ex) when (ex.IsTransient && attempt < _options.MaxAttempts)
            {
                var delay = _backoff.GetDelay(attempt - 1);
                if (ex.RetryAfter is { } retryAfter && retryAfter > delay)
                {
                    delay = retryAfter;
                }

                LogRetry(_logger, ex, symbol, from, attempt, delay.TotalSeconds);
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static DateTimeOffset Align(DateTimeOffset time, TimeSpan length) =>
        new(time.UtcTicks - (time.UtcTicks % length.Ticks), TimeSpan.Zero);

    private Dictionary<string, string> HistoryDetails(
        string symbol, CandleInterval interval, DateTimeOffset from, DateTimeOffset to, int expected, int stored) => new(StringComparer.Ordinal)
    {
        ["symbol"] = symbol,
        ["interval"] = interval.ToCode(),
        ["fromOpenTimeUtc"] = Format(from),
        ["toOpenTimeUtc"] = Format(to),
        ["expectedCandles"] = expected.ToString(CultureInfo.InvariantCulture),
        ["stored"] = stored.ToString(CultureInfo.InvariantCulture),
        ["source"] = _source.SourceName,
    };

    private Task RecordFilledAsync(string symbol, CandleInterval interval, CandleGap gap, GapFillResult result, CancellationToken cancellationToken)
    {
        LogFilled(_logger, symbol, gap.FirstMissingOpenTimeUtc, result.Requested, result.Stored, result.StillMissing);

        var message = result.IsComplete
            ? $"Gap of {result.Requested} {symbol} {interval} candle(s) from {Format(gap.FirstMissingOpenTimeUtc)} filled."
            : $"Gap of {result.Requested} {symbol} {interval} candle(s) from {Format(gap.FirstMissingOpenTimeUtc)} partially filled: " +
              $"{result.StillMissing} not available from {_source.SourceName}.";

        return RecordAsync(
            SystemEventTypes.MarketDataGapFilled,
            result.IsComplete ? SystemEventSeverity.Information : SystemEventSeverity.Warning,
            message,
            Details(symbol, interval, gap, ("stored", result.Stored), ("stillMissing", result.StillMissing)),
            cancellationToken);
    }

    private Task RecordFailedAsync(string symbol, CandleInterval interval, CandleGap gap, Exception error, CancellationToken cancellationToken)
    {
        LogFailed(_logger, error, symbol, gap.FirstMissingOpenTimeUtc, gap.MissingCandles);

        return RecordAsync(
            SystemEventTypes.MarketDataGapFillFailed,
            SystemEventSeverity.Warning,
            $"Could not fill gap of {gap.MissingCandles} {symbol} {interval} candle(s) from {Format(gap.FirstMissingOpenTimeUtc)}: {error.Message}",
            Details(symbol, interval, gap),
            cancellationToken);
    }

    private async Task RecordAsync(
        string eventType, SystemEventSeverity severity, string message, Dictionary<string, string> details, CancellationToken cancellationToken)
    {
        try
        {
            await _systemEvents.AppendAsync(
                new SystemEvent(_timeProvider.GetUtcNow(), EventSource, eventType, severity, message, details),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PersistenceException ex)
        {
            LogEventNotStored(_logger, ex, eventType);
        }
    }

    private Dictionary<string, string> Details(string symbol, CandleInterval interval, CandleGap gap, params (string Key, int Value)[] counts)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["symbol"] = symbol,
            ["interval"] = interval.ToCode(),
            ["firstMissingOpenTimeUtc"] = Format(gap.FirstMissingOpenTimeUtc),
            ["missingCandles"] = gap.MissingCandles.ToString(CultureInfo.InvariantCulture),
            ["source"] = _source.SourceName,
        };

        foreach (var (key, value) in counts)
        {
            details[key] = value.ToString(CultureInfo.InvariantCulture);
        }

        return details;
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Importing {Symbol} history from {From:O} to {To:O} ({Expected} candle(s) expected).")]
    private static partial void LogHistoryImportStarted(ILogger logger, string symbol, DateTimeOffset from, DateTimeOffset to, int expected);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Symbol} history import: up to {Until:O}, {Obtained}/{Expected} candle(s).")]
    private static partial void LogHistoryImportProgress(ILogger logger, string symbol, DateTimeOffset until, int obtained, int expected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Symbol} history import stopped after storing {Stored} candle(s).")]
    private static partial void LogHistoryImportFailed(ILogger logger, Exception exception, string symbol, int stored);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} {Symbol} gap(s) found in the last {Days:0.#} day(s).")]
    private static partial void LogGapsFound(ILogger logger, int count, string symbol, double days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not search {Symbol} gaps; back-fill skipped.")]
    private static partial void LogGapSearchFailed(ILogger logger, Exception exception, string symbol);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Gap {Symbol} from {FirstMissingOpenTimeUtc:O}: {Requested} missing, {Stored} stored, {StillMissing} still missing.")]
    private static partial void LogFilled(ILogger logger, string symbol, DateTimeOffset firstMissingOpenTimeUtc, int requested, int stored, int stillMissing);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Back-fill of {Symbol} from {FirstMissingOpenTimeUtc:O} failed (attempt {Attempt}); retrying in {DelaySeconds:0.0} s.")]
    private static partial void LogRetry(ILogger logger, Exception exception, string symbol, DateTimeOffset firstMissingOpenTimeUtc, int attempt, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Back-fill of {MissingCandles} {Symbol} candle(s) from {FirstMissingOpenTimeUtc:O} failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string symbol, DateTimeOffset firstMissingOpenTimeUtc, int missingCandles);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Back-filled {Symbol} candle {OpenTimeUtc:O} differs from the stored one; the stored candle was kept.")]
    private static partial void LogConflict(ILogger logger, string symbol, DateTimeOffset openTimeUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "System event {EventType} could not be stored; it is only in the logs.")]
    private static partial void LogEventNotStored(ILogger logger, Exception exception, string eventType);
}
