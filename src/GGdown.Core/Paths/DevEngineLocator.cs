namespace GGdown.Paths;

/// <summary>Locate the repository engine for a Debug build launched from its output directory.</summary>
public static class DevEngineLocator
{
    public static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "engine", "runner.py")) &&
                File.Exists(Path.Combine(dir.FullName, "src", "GGdown.App", "GGdown.App.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
