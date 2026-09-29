using Omega.Infrastructure.Persistence.Migrations;

namespace Omega.Integration.Tests.Persistence;

public class SchemaMigrationTests
{
    [Fact]
    public void Embedded_migrations_are_numbered_from_one_without_gaps()
    {
        var migrations = SchemaMigration.LoadEmbedded();

        Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(m => m.Version));
        Assert.Equal("initial_schema", migrations[0].Name);
    }

    [Fact]
    public void Checksum_does_not_depend_on_line_endings()
    {
        var unix = SchemaMigration.Create(1, "sample", "CREATE TABLE a (id int);\nCREATE TABLE b (id int);\n");
        var windows = SchemaMigration.Create(1, "sample", "CREATE TABLE a (id int);\r\nCREATE TABLE b (id int);\r\n");

        Assert.Equal(unix.Checksum, windows.Checksum);
        Assert.Equal(64, unix.Checksum.Length);
    }

    [Fact]
    public void Checksum_changes_when_the_script_changes()
    {
        var original = SchemaMigration.Create(1, "sample", "CREATE TABLE a (id int);");
        var edited = SchemaMigration.Create(1, "sample", "CREATE TABLE a (id bigint);");

        Assert.NotEqual(original.Checksum, edited.Checksum);
    }
}
