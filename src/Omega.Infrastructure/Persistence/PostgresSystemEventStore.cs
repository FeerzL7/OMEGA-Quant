using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Omega.Core.SystemEvents;

namespace Omega.Infrastructure.Persistence;

/// <summary><see cref="ISystemEventStore"/> on PostgreSQL (table <c>system_events</c>).</summary>
public sealed class PostgresSystemEventStore(NpgsqlDataSource dataSource) : ISystemEventStore
{
    /// <summary>Upper bound for <see cref="GetRecentAsync"/>.</summary>
    public const int MaxLimit = 1000;

    private const string InsertSql = """
        INSERT INTO system_events (occurred_at, source, event_type, severity, message, details)
        VALUES ($1, $2, $3, $4, $5, $6)
        """;

    private const string SelectRecentSql = """
        SELECT occurred_at, source, event_type, severity, message, details
        FROM system_events
        ORDER BY occurred_at DESC, id DESC
        LIMIT $1
        """;

    public Task AppendAsync(SystemEvent systemEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);

        return NpgsqlErrors.TranslateAsync("Appending system event", async () =>
        {
            await using var command = dataSource.CreateCommand(InsertSql);
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = systemEvent.OccurredAtUtc, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = systemEvent.Source });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = systemEvent.EventType });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = systemEvent.Severity.ToString() });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = systemEvent.Message });
            command.Parameters.Add(new NpgsqlParameter
            {
                Value = systemEvent.Details.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(systemEvent.Details),
                NpgsqlDbType = NpgsqlDbType.Jsonb,
            });

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        });
    }

    public Task<IReadOnlyList<SystemEvent>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);

        return NpgsqlErrors.TranslateAsync("Reading system events", async () =>
        {
            await using var command = dataSource.CreateCommand(SelectRecentSql);
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });

            var events = new List<SystemEvent>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var details = reader.IsDBNull(5)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5));

                events.Add(new SystemEvent(
                    reader.GetFieldValue<DateTimeOffset>(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Enum.Parse<SystemEventSeverity>(reader.GetString(3)),
                    reader.GetString(4),
                    details));
            }

            IReadOnlyList<SystemEvent> result = events;
            return result;
        });
    }
}
