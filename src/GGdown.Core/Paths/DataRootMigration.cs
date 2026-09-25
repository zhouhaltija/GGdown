namespace GGdown.Paths;

/// <summary>
/// 把旧的 GalleryGUI 用户目录迁到 GGdown，并把 gallery.db 改名为 ggdown.db。
/// 不创建目录。两个根目录都存在时保留旧目录，避免覆盖已有数据。
/// </summary>
public static class DataRootMigration
{
    public const string LegacyFolderName = "GalleryGUI";
    public const string CurrentFolderName = "GGdown";
    public const string LegacyDbFileName = "gallery.db";
    public const string CurrentDbFileName = "ggdown.db";

    public readonly record struct Result(bool MovedRoot, bool RenamedDatabase, bool LegacyLeftInPlace);

    public static Result Apply(string legacyRoot, string currentRoot)
    {
        var moved = false;
        var left = false;
        var legacyExists = Directory.Exists(legacyRoot);
        var currentExists = Directory.Exists(currentRoot);
        if (legacyExists && !currentExists)
        {
            Directory.Move(legacyRoot, currentRoot);
            moved = true;
            currentExists = true;
        }
        else if (legacyExists && currentExists)
        {
            left = true;
        }

        var renamed = false;
        if (currentExists)
            renamed = RenameDatabase(Path.Combine(currentRoot, "data"));

        return new Result(moved, renamed, left);
    }

    private static bool RenameDatabase(string dataDir)
    {
        var legacyDb = Path.Combine(dataDir, LegacyDbFileName);
        var currentDb = Path.Combine(dataDir, CurrentDbFileName);
        if (!File.Exists(legacyDb) || File.Exists(currentDb))
            return false;

        File.Move(legacyDb, currentDb);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var from = legacyDb + suffix;
            var to = currentDb + suffix;
            if (File.Exists(from) && !File.Exists(to))
                File.Move(from, to);
        }

        return true;
    }
}
