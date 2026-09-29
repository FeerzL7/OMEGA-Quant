using Microsoft.Extensions.Logging;
using Npgsql;

namespace Omega.Infrastructure.Persistence.Migrations;

/// <summary>
/// Applies the embedded, versioned SQL scripts to PostgreSQL.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Applied versions are recorded in <c>schema_migrations</c> with the script checksum.</item>
/// <item>An applied script whose checksum changed, or an applied version this build does not know,
/// stops start-up: the schema and the code no longer match.</item>
/// <item>A PostgreSQL advisory lock serializes concurrent migrators (for example two processes starting together).</item>
/// <item>Each script runs in its own transaction together with its bookkeeping row.</item>
/// </list>
/// </remarks>
public sealed partial class DatabaseMigrator
{
    // Arbitrary but fixed key ("OMEGAMIG" in ASCII) for pg_advisory_lock.
    private const long AdvisoryLockKey = 0x4F4D4547414D4947;

    private const string CreateHistoryTableSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version     integer     NOT NULL PRIMARY KEY,
            name        text        NOT NULL,
            checksum    text        NOT NULL,
            applied_at  timestamptz NOT NULL DEFAULT now()
        )
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DatabaseMigrator> _logger;
    private readonly IReadOnlyList<SchemaMigration> _migrations;

    public DatabaseMigrator(NpgsqlDataSource dataSource, ILogger<DatabaseMigrator> logger)
        : this(dataSource, logger, SchemaMigration.LoadEmbedded())
    {
    }

    /// <summary>Uses an explicit list of migrations instead of the embedded ones (tooling and tests).</summary>
    public DatabaseMigrator(NpgsqlDataSource dataSource, ILogger<DatabaseMigrator> logger, IReadOnlyList<SchemaMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(migrations);

        _dataSource = dataSource;
        _logger = logger;
        _migrations = migrations;
    }

    /// <summary>Compares the database with the embedded migrations without changing anything.</summary>
    public async Task<MigrationStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStatusAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes sure the schema matches this build. Applies pending migrations when
    /// <paramref name="applyPending"/> is true; otherwise pending migrations are an error.
    /// </summary>
    /// <exception cref="InvalidOperationException">Schema and code do not match and cannot be reconciled automatically.</exception>
    public async Task EnsureUpToDateAsync(bool applyPending, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, $"SELECT pg_advisory_lock({AdvisoryLockKey})", cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await ReadStatusAsync(connection, cancellationToken).ConfigureAwait(false);
            status.ThrowIfInconsistent();

            if (status.Pending.Count == 0)
            {
                LogUpToDate(_logger, status.CurrentVersion);
                return;
            }

            if (!applyPending)
            {
                throw new InvalidOperationException(
                    $"The database schema is at version {status.CurrentVersion} but this build needs version {_migrations.Count}. " +
                    $"Pending: {string.Join(", ", status.Pending.Select(m => $"{m.Version:0000}_{m.Name}"))}. " +
                    "Enable Database:ApplyMigrationsOnStartup or apply them explicitly.");
            }

            await ExecuteAsync(connection, CreateHistoryTableSql, cancellationToken).ConfigureAwait(false);

            foreach (var migration in status.Pending)
            {
                await ApplyAsync(connection, migration, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Unlock even if cancelled, so the session does not keep the lock while pooled.
            await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ApplyAsync(NpgsqlConnection connection, SchemaMigration migration, CancellationToken cancellationToken)
    {
        LogApplying(_logger, migration.Version, migration.Name);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var script = new NpgsqlCommand(migration.Sql, connection, transaction))
        {
            await script.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var record = new NpgsqlCommand(
            "INSERT INTO schema_migrations (version, name, checksum) VALUES ($1, $2, $3)", connection, transaction))
        {
            record.Parameters.Add(new NpgsqlParameter<int> { TypedValue = migration.Version });
            record.Parameters.Add(new NpgsqlParameter<string> { TypedValue = migration.Name });
            record.Parameters.Add(new NpgsqlParameter<string> { TypedValue = migration.Checksum });
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        LogApplied(_logger, migration.Version, migration.Name);
    }

    private async Task<MigrationStatus> ReadStatusAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<int, (string Name, string Checksum)>();

        await using (var exists = new NpgsqlCommand("SELECT to_regclass('schema_migrations') IS NOT NULL", connection))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return new MigrationStatus([], _migrations, []);
            }
        }

        await using (var read = new NpgsqlCommand("SELECT version, name, checksum FROM schema_migrations ORDER BY version", connection))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                applied[reader.GetInt32(0)] = (reader.GetString(1), reader.GetString(2));
            }
        }

        var problems = new List<string>();
        var known = _migrations.ToDictionary(m => m.Version);

        foreach (var (version, record) in applied)
        {
            if (!known.TryGetValue(version, out var migration))
            {
                problems.Add($"Applied migration {version:0000}_{record.Name} is unknown to this build (database is newer than the code).");
            }
            else if (!string.Equals(migration.Checksum, record.Checksum, StringComparison.Ordinal))
            {
                problems.Add($"Applied migration {version:0000}_{record.Name} was modified after being applied (checksum mismatch).");
            }
        }

        var appliedMigrations = _migrations.Where(m => applied.ContainsKey(m.Version)).ToList();
        var pending = _migrations.Where(m => !applied.ContainsKey(m.Version)).ToList();

        return new MigrationStatus(appliedMigrations, pending, problems);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema is up to date (version {Version}).")]
    private static partial void LogUpToDate(ILogger logger, int version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying migration {Version:0000}_{Name}.")]
    private static partial void LogApplying(ILogger logger, int version, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied migration {Version:0000}_{Name}.")]
    private static partial void LogApplied(ILogger logger, int version, string name);
}

/// <summary>Result of comparing the database with the embedded migrations.</summary>
public sealed record MigrationStatus(
    IReadOnlyList<SchemaMigration> Applied,
    IReadOnlyList<SchemaMigration> Pending,
    IReadOnlyList<string> Problems)
{
    /// <summary>Highest applied version, 0 for an empty database.</summary>
    public int CurrentVersion => Applied.Count == 0 ? 0 : Applied.Max(m => m.Version);

    public bool IsUpToDate => Pending.Count == 0 && Problems.Count == 0;

    public void ThrowIfInconsistent()
    {
        if (Problems.Count > 0)
        {
            throw new InvalidOperationException($"Database schema does not match this build: {string.Join(" ", Problems)}");
        }
    }
}
