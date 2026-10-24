using GGdown.Engine;

namespace GGdown.Tests.Engine;

public sealed class EngineVersionMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ggdown-maintenance-" + Guid.NewGuid().ToString("N"));
    private string Versions => Path.Combine(_root, "versions");
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private string Version(int age)
    {
        var path = Path.Combine(Versions, age.ToString("x").PadLeft(64, '0') + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "python"));
        Directory.CreateDirectory(Path.Combine(path, "site-packages"));
        File.WriteAllText(Path.Combine(path, "python", "python.exe"), "synthetic");
        File.WriteAllText(Path.Combine(path, "runner.py"), "synthetic");
        File.WriteAllText(Path.Combine(path, "engine.json"), "{}");
        File.WriteAllText(Path.Combine(path, "site-packages", "dependency.py"), "# 测试依赖");
        Directory.SetCreationTimeUtc(path, Now.AddDays(-age));
        return path;
    }

    [Fact]
    public void Keeps_active_and_latest_complete_fallback()
    {
        var active = Version(1);
        var fallback = Version(2);
        var obsolete = Version(3);
        Assert.Equal(1, EngineVersionMaintenance.Cleanup(_root, active, Now));
        Assert.True(Directory.Exists(active));
        Assert.True(Directory.Exists(fallback));
        Assert.False(Directory.Exists(obsolete));
    }

    [Fact]
    public void Active_old_version_is_untouched_until_all_leases_release()
    {
        var active = Version(1);
        Version(2);
        var obsolete = Version(3);
        using var first = EngineVersionLease.Acquire(obsolete);
        var second = EngineVersionLease.Acquire(obsolete);
        Assert.Equal(0, EngineVersionMaintenance.Cleanup(_root, active, Now));
        second.Dispose();
        Assert.Equal(0, EngineVersionMaintenance.Cleanup(_root, active, Now));
        Assert.True(File.Exists(Path.Combine(obsolete, "runner.py")));
        first.Dispose();
        Assert.Equal(1, EngineVersionMaintenance.Cleanup(_root, active, Now));
    }

    [Fact]
    public void Maintenance_failure_does_not_partially_remove_busy_legacy_version()
    {
        var active = Version(1);
        Version(2);
        var obsolete = Version(3);
        using var running = File.Open(Path.Combine(obsolete, "runner.py"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(0, EngineVersionMaintenance.Cleanup(_root, active, Now));
        Assert.True(File.Exists(Path.Combine(obsolete, "python", "python.exe")));
        Assert.True(File.Exists(Path.Combine(obsolete, "engine.json")));
    }

    [Fact]
    public void Only_stale_idle_staging_is_removed()
    {
        var active = Version(1);
        var stale = Path.Combine(Versions, ".install-" + Guid.NewGuid().ToString("N"));
        var recent = Path.Combine(Versions, ".install-" + Guid.NewGuid().ToString("N"));
        var busy = Path.Combine(Versions, ".install-" + Guid.NewGuid().ToString("N"));
        foreach (var path in new[] { stale, recent, busy }) Directory.CreateDirectory(path);
        Directory.SetCreationTimeUtc(stale, Now.AddHours(-25));
        Directory.SetCreationTimeUtc(busy, Now.AddHours(-25));
        Directory.SetCreationTimeUtc(recent, Now.AddHours(-24));
        using var lease = EngineVersionLease.Acquire(busy);
        Assert.Equal(1, EngineVersionMaintenance.Cleanup(_root, active, Now));
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(busy));
    }

    [Fact]
    public void Unknown_directories_and_outside_active_path_are_preserved()
    {
        var active = Version(1);
        var unknown = Path.Combine(Versions, "user-notes");
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "keep");
        Assert.Equal(0, EngineVersionMaintenance.Cleanup(_root, Path.GetTempPath(), Now));
        Assert.Equal(0, EngineVersionMaintenance.Cleanup(_root, active, Now));
        Assert.True(File.Exists(Path.Combine(unknown, "keep.txt")));
    }

    [SkippableFact]
    public void Does_not_follow_links_anywhere_in_candidate_tree()
    {
        var active = Version(1);
        Version(2);
        var obsolete = Version(3);
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var link = Path.Combine(obsolete, "linked");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { Skip.If(true, "当前权限不允许创建目录链接"); }
        try
        {
            Assert.Equal(0, EngineVersionMaintenance.Cleanup(_root, active, Now));
            Assert.True(File.Exists(sentinel));
            Assert.True(File.Exists(Path.Combine(obsolete, "runner.py")));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    [Fact]
    public async Task Lease_does_not_change_content_version()
    {
        var bundle = Version(1);
        var engineRoot = Path.Combine(_root, "installed");
        var installer = new EngineInstaller((_, _) => Task.CompletedTask);
        var first = await installer.InstallAsync(bundle, engineRoot);
        using var lease = EngineVersionLease.Acquire(first.Directory);
        var next = await installer.InstallAsync(bundle, engineRoot);
        Assert.False(next.Updated);
        Assert.Equal(first.Directory, next.Directory);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
