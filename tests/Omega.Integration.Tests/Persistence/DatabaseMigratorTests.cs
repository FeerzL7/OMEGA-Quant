using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Omega.Infrastructure.Persistence.Migrations;

namespace Omega.Integration.Tests.Persistence;

public class DatabaseMigratorTests : DatabaseTest
{
    private static readonly IReadOnlyList<SchemaMigration> Embedded = SchemaMigration.LoadEmbedded();

    [DatabaseFact]
    public async Task Empty_database_is_migrated_to_the_latest_version()
    {
        await Migrator().EnsureUpToDateAsync(applyPending: true, CancellationToken.None);

        var status = await Migrator().GetStatusAsync(CancellationToken.None);
        Assert.True(status.IsUpToDate);
        Assert.Equal(Embedded.Count, status.CurrentVersion);
        Assert.True(await ScalarAsync("SELECT to_regclass('candles') IS NOT NULL AND to_regclass('system_events') IS NOT NULL") is true);
    }

    [DatabaseFact]
    public async Task Running_again_changes_nothing()
    {
        await Migrator().EnsureUpToDateAsync(applyPending: true, CancellationToken.None);
        var firstAppliedAt = await ScalarAsync("SELECT max(applied_at) FROM schema_migrations");

        await Migrator().EnsureUpToDateAsync(applyPending: true, CancellationToken.None);

        Assert.Equal((long)Embedded.Count, await ScalarAsync("SELECT count(*) FROM schema_migrations"));
        Assert.Equal(firstAppliedAt, await ScalarAsync("SELECT max(applied_at) FROM schema_migrations"));
    }

    [DatabaseFact]
    public async Task Pending_migrations_stop_start_up_when_automatic_application_is_disabled()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Migrator().EnsureUpToDateAsync(applyPending: false, CancellationToken.None));

        Assert.Contains("0001_initial_schema", error.Message, StringComparison.Ordinal);
        Assert.True(await ScalarAsync("SELECT to_regclass('candles') IS NOT NULL") is false);
    }

    [DatabaseFact]
    public async Task Modified_applied_migration_stops_start_up()
    {
        await Migrator().EnsureUpToDateAsync(applyPending: true, CancellationToken.None);
        await ExecuteAsync("UPDATE schema_migrations SET checksum = 'tampered' WHERE version = 1");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Migrator().EnsureUpToDateAsync(applyPending: true, CancellationToken.None));

        Assert.Contains("checksum mismatch", error.Message, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Database_newer_than_the_code_stops_start_up()
    {
        await Migrator().EnsureUpToDateAsync(applyPending: true, CancellationToken.None);
        await ExecuteAsync("INSERT INTO schema_migrations (version, name, checksum) VALUES (9999, 'from_the_future', 'x')");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Migrator().EnsureUpToDateAsync(applyPending: true, CancellationToken.None));

        Assert.Contains("unknown to this build", error.Message, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Concurrent_migrators_apply_each_migration_once()
    {
        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => Migrator().EnsureUpToDateAsync(applyPending: true, CancellationToken.None))));

        Assert.Equal((long)Embedded.Count, await ScalarAsync("SELECT count(*) FROM schema_migrations"));
    }

    [DatabaseFact]
    public async Task Failing_migration_is_rolled_back_completely()
    {
        var failing = SchemaMigration.Create(Embedded.Count + 1, "broken", "CREATE TABLE half_done (id int);\nSELECT 1 / 0;");
        var migrator = new DatabaseMigrator(DataSource, NullLogger<DatabaseMigrator>.Instance, [.. Embedded, failing]);

        await Assert.ThrowsAsync<PostgresException>(() => migrator.EnsureUpToDateAsync(applyPending: true, CancellationToken.None));

        Assert.True(await ScalarAsync("SELECT to_regclass('half_done') IS NOT NULL") is false);
        Assert.Equal((long)Embedded.Count, await ScalarAsync("SELECT count(*) FROM schema_migrations"));
    }

    private DatabaseMigrator Migrator() => new(DataSource, NullLogger<DatabaseMigrator>.Instance);
}
