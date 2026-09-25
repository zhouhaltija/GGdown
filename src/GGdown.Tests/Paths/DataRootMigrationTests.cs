using GGdown.Paths;

namespace GGdown.Tests.Paths;

public class DataRootMigrationTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "ggui-migrate-" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_sandbox, "GalleryGUI");
    private string Current => Path.Combine(_sandbox, "GGdown");

    public DataRootMigrationTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true);
    }

    [Fact]
    public void Moves_legacy_root_and_renames_database_when_current_is_absent()
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "archive"));
        File.WriteAllText(Path.Combine(Legacy, "archive", "kept.txt"), "archive");
        Directory.CreateDirectory(Path.Combine(Legacy, "data"));
        File.WriteAllText(Path.Combine(Legacy, "data", "gallery.db"), "db");
        File.WriteAllText(Path.Combine(Legacy, "data", "gallery.db-wal"), "wal");

        var result = DataRootMigration.Apply(Legacy, Current);

        Assert.True(result.MovedRoot);
        Assert.True(result.RenamedDatabase);
        Assert.False(result.LegacyLeftInPlace);
        Assert.False(Directory.Exists(Legacy));
        Assert.Equal("archive", File.ReadAllText(Path.Combine(Current, "archive", "kept.txt")));
        Assert.Equal("db", File.ReadAllText(Path.Combine(Current, "data", "ggdown.db")));
        Assert.Equal("wal", File.ReadAllText(Path.Combine(Current, "data", "ggdown.db-wal")));
        Assert.False(File.Exists(Path.Combine(Current, "data", "gallery.db")));
    }

    [Fact]
    public void Leaves_legacy_root_when_both_directories_exist()
    {
        Directory.CreateDirectory(Legacy);
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Legacy, "old.txt"), "old");
        File.WriteAllText(Path.Combine(Current, "new.txt"), "new");

        var result = DataRootMigration.Apply(Legacy, Current);

        Assert.False(result.MovedRoot);
        Assert.True(result.LegacyLeftInPlace);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Legacy, "old.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Current, "new.txt")));
    }

    [Fact]
    public void Does_not_overwrite_an_existing_ggdown_database()
    {
        Directory.CreateDirectory(Path.Combine(Current, "data"));
        File.WriteAllText(Path.Combine(Current, "data", "gallery.db"), "old");
        File.WriteAllText(Path.Combine(Current, "data", "ggdown.db"), "current");

        var result = DataRootMigration.Apply(Legacy, Current);

        Assert.False(result.RenamedDatabase);
        Assert.Equal("current", File.ReadAllText(Path.Combine(Current, "data", "ggdown.db")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Current, "data", "gallery.db")));
    }

    [Fact]
    public void Creates_nothing_when_neither_root_exists()
    {
        var result = DataRootMigration.Apply(Legacy, Current);

        Assert.False(result.MovedRoot);
        Assert.False(result.RenamedDatabase);
        Assert.False(result.LegacyLeftInPlace);
        Assert.False(Directory.Exists(Legacy));
        Assert.False(Directory.Exists(Current));
    }
}
