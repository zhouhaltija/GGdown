using GGdown.Paths;

namespace GGdown.Tests.Paths;

public class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ggui-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public AppPathsTests() => _paths = new AppPaths(_root);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Paths_derive_from_root()
    {
        Assert.Equal(_root, _paths.Root);
        Assert.Equal(Path.Combine(_root, "data", "ggdown.db"), _paths.DbFile);
        Assert.Equal(Path.Combine(_root, "engine", "python", "python.exe"), _paths.PythonExe);
        Assert.Equal(Path.Combine(_root, "engine", "runner.py"), _paths.RunnerScript);
        Assert.Equal(Path.Combine(_root, "accounts"), _paths.AccountsDir);
        Assert.Equal(Path.Combine(_root, "archive"), _paths.ArchiveDir);
        Assert.Equal(Path.Combine(_root, "logs"), _paths.LogsDir);
        Assert.Equal(Path.Combine(_root, "temp"), _paths.TempDir);
    }

    [Fact]
    public void EnsureCreated_creates_directories()
    {
        _paths.EnsureCreated();
        Assert.True(Directory.Exists(_paths.DataDir));
        Assert.True(Directory.Exists(_paths.AccountsDir));
        Assert.True(Directory.Exists(_paths.TempDir));
    }

    [Fact]
    public void CreateDefault_uses_local_appdata_ggdown()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GGdown");
        Assert.Equal(expected, AppPaths.CreateDefault().Root);
    }
}
