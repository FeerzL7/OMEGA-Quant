using Npgsql;

namespace Omega.Integration.Tests.Persistence;

/// <summary>
/// A throw-away PostgreSQL database for one test, created on the server given
/// by the <c>OMEGA_TEST_POSTGRES</c> environment variable and dropped afterwards.
/// </summary>
internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    public const string EnvironmentVariable = "OMEGA_TEST_POSTGRES";

    private readonly string _adminConnectionString;
    private readonly string _databaseName;

    private PostgresTestDatabase(string adminConnectionString, string databaseName, NpgsqlDataSource dataSource)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        DataSource = dataSource;
    }

    public static string? AdminConnectionString => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public NpgsqlDataSource DataSource { get; }

    public static async Task<PostgresTestDatabase> CreateAsync()
    {
        var admin = AdminConnectionString
            ?? throw new InvalidOperationException($"{EnvironmentVariable} is not set.");

        // Hex GUID: safe as an unquoted identifier.
        var databaseName = $"omega_test_{Guid.NewGuid():N}";

        await using (var adminSource = NpgsqlDataSource.Create(admin))
        await using (var create = adminSource.CreateCommand($"CREATE DATABASE {databaseName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = databaseName }.ConnectionString;
        return new PostgresTestDatabase(admin, databaseName, NpgsqlDataSource.Create(connectionString));
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();

        await using var adminSource = NpgsqlDataSource.Create(_adminConnectionString);
        await using var drop = adminSource.CreateCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// A test that needs PostgreSQL. It is reported as skipped (never as passed)
/// when <c>OMEGA_TEST_POSTGRES</c> is not set.
/// </summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgresTestDatabase.AdminConnectionString))
        {
            Skip = $"Requires PostgreSQL: set {PostgresTestDatabase.EnvironmentVariable} (see README).";
        }
    }
}

/// <summary>Base class: every test gets its own empty database.</summary>
public abstract class DatabaseTest : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    protected NpgsqlDataSource DataSource =>
        _database?.DataSource ?? throw new InvalidOperationException("The test database was not created.");

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    protected async Task<object?> ScalarAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    protected async Task ExecuteAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
