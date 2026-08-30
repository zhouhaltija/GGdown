using System.Diagnostics;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalleryGUI.Tests.Engine;

public class RunnerEngineTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private RunnerEngine CreateEngine(string python, string? stubPath = null)
    {
        var stub = stubPath ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "stub_runner.py");
        return new RunnerEngine(_paths,
            new RunnerEngineOptions { PythonExe = python, RunnerScript = stub, GalleryDlPath = "" },
            NullLogger<RunnerEngine>.Instance);
    }

    private static string? LocatePython()
    {
        foreach (var exe in new[] { "python", "python3", "py" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--version")
                { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
                p.WaitForExit(5000);
                if (p is { HasExited: true, ExitCode: 0 }) return exe;
            }
            catch { /* 未安装 */ }
        }
        return null;
    }

    [SkippableFact]
    public async Task Hello_returns_protocol_1()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var hello = await CreateEngine(python!).HelloAsync();
        Assert.Equal(1, hello.Protocol);
    }

    [SkippableFact]
    public async Task WhoAmI_returns_account()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var info = await CreateEngine(python!).WhoAmIAsync("good.txt");
        Assert.Equal("stub_user", info.ScreenName);
    }

    [SkippableFact]
    public async Task WhoAmI_auth_failure_throws_AuthException()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        await Assert.ThrowsAsync<AuthException>(
            () => CreateEngine(python!).WhoAmIAsync("bad.txt"));
    }

    [SkippableFact]
    public async Task ListFollowing_returns_users()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var users = await CreateEngine(python!).ListFollowingAsync("good.txt");
        Assert.Equal(2, users.Count);
        Assert.Equal("alice", users[0].ScreenName);
        Assert.Equal("bob", users[1].ScreenName);
    }

    [SkippableFact]
    public async Task Download_streams_events_and_completes()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var plan = new DownloadPlan("twitter", ["https://x.com/alice/media"],
            _paths.Root, new Dictionary<string, object?>());
        var events = new List<EngineEvent>();
        await CreateEngine(python!).DownloadAsync(plan, "good.txt",
            new Progress<EngineEvent>(events.Add));
        Assert.Contains(events, e => e.Event == "url-start");
        Assert.Contains(events, e => e.Event == "file-done" && e.Size == 100);
        var done = events.Single(e => e.Event == "job-done");
        Assert.Equal(2, done.Total);
    }

    [SkippableFact]
    public async Task Download_fatal_throws_EngineException()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var plan = new DownloadPlan("twitter", ["https://x.com/alice/media"],
            _paths.Root, new Dictionary<string, object?> { ["fail"] = true });
        await Assert.ThrowsAsync<EngineException>(() =>
            CreateEngine(python!).DownloadAsync(plan, "good.txt", new Progress<EngineEvent>()));
    }

    [SkippableFact]
    public async Task Download_nonzero_exit_without_fatal_throws_EngineException()
    {
        // 审查 Important-1 回归覆盖：runner 非零退出且未发 fatal/job-done 时不得静默当成功
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var plan = new DownloadPlan("twitter", ["https://x.com/alice/media"],
            _paths.Root, new Dictionary<string, object?> { ["crash"] = true });
        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            CreateEngine(python!).DownloadAsync(plan, "good.txt", new Progress<EngineEvent>()));
        Assert.Contains("runner 异常退出", ex.Message);
    }

    [SkippableFact]
    public async Task Download_cancel_kills_process()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var plan = new DownloadPlan("twitter", ["https://x.com/alice/media"],
            _paths.Root, new Dictionary<string, object?> { ["hang"] = true });
        using var cts = new CancellationTokenSource(1000);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateEngine(python!).DownloadAsync(plan, "good.txt",
                new Progress<EngineEvent>(), cts.Token));
    }
}
