namespace GalleryGUI.Paths;

public interface IAppPaths
{
    string Root { get; }
    string DataDir { get; }
    string DbFile { get; }
    string EngineDir { get; }
    string PythonExe { get; }
    string RunnerScript { get; }
    string AccountsDir { get; }
    string ArchiveDir { get; }
    string LogsDir { get; }
    string TempDir { get; }
    void EnsureCreated();
}
