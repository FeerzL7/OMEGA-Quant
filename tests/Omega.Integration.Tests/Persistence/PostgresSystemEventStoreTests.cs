using Microsoft.Extensions.Logging.Abstractions;
using Omega.Core.SystemEvents;
using Omega.Infrastructure.Persistence;
using Omega.Infrastructure.Persistence.Migrations;

namespace Omega.Integration.Tests.Persistence;

public class PostgresSystemEventStoreTests : DatabaseTest
{
    private static readonly DateTimeOffset Time = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [DatabaseFact]
    public async Task Events_round_trip_newest_first_with_details()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync(new SystemEvent(Time, "MarketDataIngestion", SystemEventTypes.IngestionStarted, SystemEventSeverity.Information, "Started."), CancellationToken.None);
        await store.AppendAsync(new SystemEvent(
            Time.AddMinutes(1), "MarketDataIngestion", SystemEventTypes.MarketDataGapDetected, SystemEventSeverity.Warning,
            "Gap: señal sin datos.", new Dictionary<string, string> { ["missingCandles"] = "3", ["note"] = "señal" }), CancellationToken.None);

        var events = await store.GetRecentAsync(10, CancellationToken.None);

        Assert.Equal([SystemEventTypes.MarketDataGapDetected, SystemEventTypes.IngestionStarted], events.Select(e => e.EventType));
        var gap = events[0];
        Assert.Equal(Time.AddMinutes(1), gap.OccurredAtUtc);
        Assert.Equal(SystemEventSeverity.Warning, gap.Severity);
        Assert.Equal("Gap: señal sin datos.", gap.Message);
        Assert.Equal("3", gap.Details["missingCandles"]);
        Assert.Equal("señal", gap.Details["note"]);
        Assert.Empty(events[1].Details);
    }

    [DatabaseFact]
    public async Task Limit_is_applied_and_validated()
    {
        var store = await CreateStoreAsync();
        for (var i = 0; i < 3; i++)
        {
            await store.AppendAsync(new SystemEvent(Time.AddSeconds(i), "Test", "TEST_EVENT", SystemEventSeverity.Information, $"Event {i}"), CancellationToken.None);
        }

        Assert.Equal(2, (await store.GetRecentAsync(2, CancellationToken.None)).Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetRecentAsync(0, CancellationToken.None));
    }

    private async Task<PostgresSystemEventStore> CreateStoreAsync()
    {
        await new DatabaseMigrator(DataSource, NullLogger<DatabaseMigrator>.Instance)
            .EnsureUpToDateAsync(applyPending: true, CancellationToken.None);
        return new PostgresSystemEventStore(DataSource);
    }
}
