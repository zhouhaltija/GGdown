namespace GGdown.Paths;

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string root) => Root = root;

    public static AppPaths CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DataRootMigration.CurrentFolderName));

    public string Root { get; }
    public string DataDir => Path.Combine(Root, "data");
    public string DbFile => Path.Combine(DataDir, DataRootMigration.CurrentDbFileName);
    public string SitesDataDir => Path.Combine(DataDir, "sites");
    public string EngineDir => Path.Combine(Root, "engine");
    public string PythonExe => Path.Combine(EngineDir, "python", "python.exe");
    public string RunnerScript => Path.Combine(EngineDir, "runner.py");
    public string AccountsDir => Path.Combine(Root, "accounts");
    public string ArchiveDir => Path.Combine(Root, "archive");
    public string LogsDir => Path.Combine(Root, "logs");
    public string TempDir => Path.Combine(Root, "temp");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { DataDir, SitesDataDir, EngineDir, AccountsDir, ArchiveDir, LogsDir, TempDir })
            Directory.CreateDirectory(dir);
    }
}
