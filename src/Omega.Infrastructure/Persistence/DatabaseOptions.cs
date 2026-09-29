using Npgsql;

namespace Omega.Infrastructure.Persistence;

/// <summary>Options bound from the <c>Database</c> configuration section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Npgsql connection string. Contains a password: supply it through
    /// user-secrets (development) or the <c>Database__ConnectionString</c>
    /// environment variable, never through appsettings files or git.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Apply pending schema migrations when the process starts. When false and
    /// migrations are pending, start-up fails instead of running on an old schema.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; }

    /// <summary>Returns every configuration problem found. Messages never include the connection string.</summary>
    public IReadOnlyList<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return ["Database:ConnectionString is required. Set it with 'dotnet user-secrets' or the Database__ConnectionString environment variable."];
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(builder.Host))
            {
                errors.Add("Database:ConnectionString must specify Host.");
            }

            if (string.IsNullOrWhiteSpace(builder.Database))
            {
                errors.Add("Database:ConnectionString must specify Database.");
            }

            return errors;
        }
        catch (ArgumentException)
        {
            return ["Database:ConnectionString is not a valid PostgreSQL connection string."];
        }
    }
}
