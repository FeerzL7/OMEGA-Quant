using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Omega.Infrastructure.Persistence.Migrations;

/// <summary>One versioned schema script embedded in this assembly.</summary>
/// <param name="Version">Sequential version, starting at 1.</param>
/// <param name="Name">Descriptive name taken from the file name.</param>
/// <param name="Sql">Script text with line endings normalized to LF.</param>
/// <param name="Checksum">SHA-256 of <paramref name="Sql"/>, lower-case hex.</param>
public sealed partial record SchemaMigration(int Version, string Name, string Sql, string Checksum)
{
    private const string ResourcePrefix = "Omega.Migrations.";

    /// <summary>All embedded migrations, ordered by version. Validates the sequence.</summary>
    public static IReadOnlyList<SchemaMigration> LoadEmbedded() => Load(typeof(SchemaMigration).Assembly);

    internal static IReadOnlyList<SchemaMigration> Load(Assembly assembly)
    {
        var migrations = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name => FromResource(assembly, name))
            .OrderBy(migration => migration.Version)
            .ToList();

        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException(
                    $"Migration versions must be contiguous from 1; expected {i + 1}, found {migrations[i].Version} ({migrations[i].Name}).");
            }
        }

        return migrations;
    }

    /// <summary>
    /// Creates a migration from raw script text. Line endings are normalized so
    /// the checksum does not change when git converts files to CRLF on Windows.
    /// </summary>
    public static SchemaMigration Create(int version, string name, string sql)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

        return new SchemaMigration(version, name, normalized, checksum);
    }

    private static SchemaMigration FromResource(Assembly assembly, string resourceName)
    {
        var match = ResourceNamePattern().Match(resourceName);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Migration resource '{resourceName}' must be named NNNN_lower_snake_case.sql.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Migration resource '{resourceName}' could not be read.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return Create(int.Parse(match.Groups["version"].ValueSpan, provider: null), match.Groups["name"].Value, reader.ReadToEnd());
    }

    [GeneratedRegex(@"^Omega\.Migrations\.(?<version>\d{4})_(?<name>[a-z0-9]+(?:_[a-z0-9]+)*)\.sql$")]
    private static partial Regex ResourceNamePattern();
}
