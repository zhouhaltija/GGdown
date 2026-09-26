using GGdown.Paths;

namespace GGdown.Tests.Paths;

public class DevEngineLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ggdown-dev-engine-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Finds_repository_engine_without_gallery_dl_checkout()
    {
        var engine = Path.Combine(_root, "engine");
        var project = Path.Combine(_root, "src", "GGdown.App");
        var output = Path.Combine(project, "bin", "x64", "Debug", "net8.0-windows10.0.22621.0");
        Directory.CreateDirectory(engine);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(engine, "runner.py"), "");
        File.WriteAllText(Path.Combine(project, "GGdown.App.csproj"), "");

        Assert.Equal(_root, DevEngineLocator.FindRepoRoot(output));
    }
}
