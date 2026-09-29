namespace Omega.Core.SystemEvents;

/// <summary>
/// An operational event worth keeping beyond the process logs: connection
/// changes, data-integrity problems, start and stop of pipeline stages.
/// </summary>
public sealed record SystemEvent
{
    public SystemEvent(
        DateTimeOffset occurredAtUtc,
        string source,
        string eventType,
        SystemEventSeverity severity,
        string message,
        IReadOnlyDictionary<string, string>? details = null)
    {
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Occurrence time must be expressed in UTC.", nameof(occurredAtUtc));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown severity.");
        }

        OccurredAtUtc = occurredAtUtc;
        Source = source;
        EventType = eventType;
        Severity = severity;
        Message = message;
        Details = details ?? new Dictionary<string, string>();
    }

    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Component that produced the event, for example <c>MarketDataIngestion</c>.</summary>
    public string Source { get; }

    /// <summary>Machine-readable type; see <see cref="SystemEventTypes"/>.</summary>
    public string EventType { get; }

    public SystemEventSeverity Severity { get; }

    public string Message { get; }

    /// <summary>Structured context. Never put secrets here.</summary>
    public IReadOnlyDictionary<string, string> Details { get; }
}

public enum SystemEventSeverity
{
    Information = 1,
    Warning = 2,
    Error = 3,
    Critical = 4,
}

/// <summary>Known system event types (upper snake case).</summary>
public static class SystemEventTypes
{
    public const string IngestionStarted = "INGESTION_STARTED";
    public const string IngestionStopped = "INGESTION_STOPPED";
    public const string MarketDataConnected = "MARKET_DATA_CONNECTED";
    public const string MarketDataDisconnected = "MARKET_DATA_DISCONNECTED";
    public const string MarketDataGapDetected = "MARKET_DATA_GAP_DETECTED";
    public const string CandleConflict = "CANDLE_CONFLICT";
    public const string MarketDataGapFilled = "MARKET_DATA_GAP_FILLED";
    public const string MarketDataGapFillFailed = "MARKET_DATA_GAP_FILL_FAILED";
    public const string MarketDataStale = "MARKET_DATA_STALE";
    public const string MarketDataFresh = "MARKET_DATA_FRESH";
    public const string ClockSkewDetected = "CLOCK_SKEW_DETECTED";
}

/// <summary>Durable, append-only log of <see cref="SystemEvent"/>.</summary>
/// <remarks>Implementations throw <see cref="Persistence.PersistenceException"/> on storage failures.</remarks>
public interface ISystemEventStore
{
    Task AppendAsync(SystemEvent systemEvent, CancellationToken cancellationToken);

    /// <summary>Most recent events first.</summary>
    Task<IReadOnlyList<SystemEvent>> GetRecentAsync(int limit, CancellationToken cancellationToken);
}
