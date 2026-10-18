using GGdown.Engine;
using GGdown.Paths;

namespace GGdown.Tests.Engine;

public sealed class EngineInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ggdown-install-" + Guid.NewGuid().ToString("N"));
    private string Bundle => Path.Combine(_root, "bundle");
    private string Installed => Path.Combine(_root, "engine");

    public EngineInstallerTests() => WriteBundle("original", "first-build");

    [Fact]
    public async Task Same_content_with_different_build_timestamp_skips_installation()
    {
        var first = await Installer().InstallAsync(Bundle, Installed);
        WriteBundle("original", "second-build");
        var second = await new EngineInstaller((_, _) => throw new InvalidOperationException("不应重新验证同版本"))
            .InstallAsync(Bundle, Installed);
        Assert.Equal(first.Directory, second.Directory);
        Assert.False(second.Updated);
        Assert.Null(second.UpdateError);
    }

    [Fact]
    public async Task Upgrade_switches_path_without_overwriting_old_runtime_or_retaining_removed_files()
    {
        await File.WriteAllTextAsync(Path.Combine(Bundle, "obsolete.py"), "old");
        var first = await Installer().InstallAsync(Bundle, Installed);
        using var running = File.Open(Path.Combine(first.Directory, "runner.py"), FileMode.Open, FileAccess.Read, FileShare.Read);
        File.Delete(Path.Combine(Bundle, "obsolete.py"));
        WriteBundle("new", "second-build");

        var next = await Installer().InstallAsync(Bundle, Installed);
        Assert.True(next.Updated);
        Assert.NotEqual(first.Directory, next.Directory);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(first.Directory, "runner.py")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(next.Directory, "runner.py")));
        Assert.False(File.Exists(Path.Combine(next.Directory, "obsolete.py")));
    }

    [Fact]
    public async Task Failed_hello_keeps_previous_active_runtime_and_next_start_can_retry()
    {
        var first = await Installer().InstallAsync(Bundle, Installed);
        WriteBundle("new", "second-build");
        var failed = await new EngineInstaller((_, _) => throw new InvalidDataException("hello 不兼容"))
            .InstallAsync(Bundle, Installed);
        Assert.Equal(first.Directory, failed.Directory);
        Assert.IsType<InvalidDataException>(failed.UpdateError);
        Assert.False(failed.Updated);
        var retry = await Installer().InstallAsync(Bundle, Installed);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(retry.Directory, "runner.py")));
    }

    [Fact]
    public async Task Canceled_upgrade_keeps_active_pointer()
    {
        var first = await Installer().InstallAsync(Bundle, Installed);
        WriteBundle("new", "second-build");
        using var cancel = new CancellationTokenSource();
        var installer = new EngineInstaller((_, ct) => { cancel.Cancel(); return Task.FromCanceled(ct); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(Bundle, Installed, cancel.Token));
        Directory.Delete(Bundle, true);
        var next = await Installer().InstallAsync(Bundle, Installed);
        Assert.Equal(first.Directory, next.Directory);
    }

    [Fact]
    public async Task Validation_timeout_falls_back_when_caller_did_not_cancel()
    {
        var first = await Installer().InstallAsync(Bundle, Installed);
        WriteBundle("new", "second-build");
        var result = await new EngineInstaller((_, _) => throw new OperationCanceledException(new CancellationToken(true)))
            .InstallAsync(Bundle, Installed);
        Assert.Equal(first.Directory, result.Directory);
        Assert.IsType<OperationCanceledException>(result.UpdateError);
    }

    [Fact]
    public async Task Corrupt_active_manifest_is_replaced_from_the_valid_bundle()
    {
        var first = await Installer().InstallAsync(Bundle, Installed);
        await File.WriteAllTextAsync(Path.Combine(first.Directory, "engine.json"), "{broken");
        var repaired = await Installer().InstallAsync(Bundle, Installed);
        Assert.True(repaired.Updated);
        Assert.Null(repaired.UpdateError);
        Assert.NotEqual(first.Directory, repaired.Directory);
    }

    [Fact]
    public async Task Copy_failure_preserves_previous_runtime()
    {
        var first = await Installer().InstallAsync(Bundle, Installed);
        WriteBundle("new", "second-build");
        using var locked = File.Open(Path.Combine(Bundle, "runner.py"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await Installer().InstallAsync(Bundle, Installed);
        Assert.Equal(first.Directory, result.Directory);
        Assert.NotNull(result.UpdateError);
    }

    [Fact]
    public async Task Incomplete_first_install_is_rejected_without_activating_it()
    {
        File.Delete(Path.Combine(Bundle, "python", "python.exe"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer().InstallAsync(Bundle, Installed));
        Assert.False(File.Exists(Path.Combine(Installed, "active.json")));
    }

    [Fact]
    public async Task Missing_bundle_reuses_legacy_engine_directory()
    {
        Directory.Move(Bundle, Installed);
        var result = await Installer().InstallAsync(Bundle, Installed);
        Assert.Equal(Installed, result.Directory);
        Assert.False(result.Updated);
    }

    [Fact]
    public async Task Missing_bundle_and_runtime_reports_no_available_engine()
    {
        Directory.Delete(Bundle, true);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Installer().InstallAsync(Bundle, Installed));
    }

    [SkippableFact]
    public async Task Packaged_runtime_passes_real_hello_before_activation()
    {
        var repo = DevEngineLocator.FindRepoRoot(AppContext.BaseDirectory);
        var source = repo is null ? "" : Path.Combine(repo, "dist", "engine");
        Skip.If(!File.Exists(Path.Combine(source, "python", "python.exe")), "未组装发行引擎");
        var installer = new EngineInstaller();
        var result = await installer.InstallAsync(source, Installed);
        Assert.True(result.Updated);
        Assert.Null(result.UpdateError);
        var next = await installer.InstallAsync(source, Installed);
        Assert.False(next.Updated);
        Assert.Equal(result.Directory, next.Directory);
    }

    private static EngineInstaller Installer() => new((_, _) => Task.CompletedTask);

    private void WriteBundle(string runner, string builtAt)
    {
        Directory.CreateDirectory(Path.Combine(Bundle, "python"));
        Directory.CreateDirectory(Path.Combine(Bundle, "site-packages"));
        File.WriteAllText(Path.Combine(Bundle, "python", "python.exe"), "python");
        File.WriteAllText(Path.Combine(Bundle, "site-packages", "dependency.py"), "package");
        File.WriteAllText(Path.Combine(Bundle, "runner.py"), runner);
        File.WriteAllText(Path.Combine(Bundle, "engine.json"),
            $$"""{"python":"3.13.7","gallery_dl":"1.32.10","built_at":"{{builtAt}}"}""");
    }

    public void Dispose() => Directory.Delete(_root, true);
}
