using System.Diagnostics;
using System.Text.Json;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Sites;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GGdown.Tests.Engine;

[CollectionDefinition("RunnerMemory", DisableParallelization = true)]
public sealed class RunnerMemoryCollection;

[Collection("RunnerMemory")]
public class RunnerEngineTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private RunnerEngine CreateEngine(string python, string? stubPath = null, TimeSpan? handshakeTimeout = null)
    {
        var stub = stubPath ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "stub_runner.py");
        return new RunnerEngine(_paths,
            new RunnerEngineOptions { PythonExe = python, RunnerScript = stub, GalleryDlPath = "", HandshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(30) },
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
    public async Task Download_passes_same_shared_rate_state_to_all_platforms()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var capture = Path.Combine(_paths.TempDir, "captured.json");
        var options = new Dictionary<string, object?> { ["capture_spec"] = capture, ["base-directory"] = _paths.Root };
        var engine = new RunnerEngine(_paths,
            new RunnerEngineOptions { PythonExe = python!, RunnerScript = Path.Combine(AppContext.BaseDirectory, "Fixtures", "stub_runner.py") },
            NullLogger<RunnerEngine>.Instance, new FakeAppSettings());
        foreach (var siteId in new[] { "twitter", "pixiv", "douyin" })
        {
            await engine.DownloadAsync(new DownloadPlan(siteId, ["https://example.com/video"], _paths.Root, options),
                "fictional-cookie", new Progress<EngineEvent>());
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(capture));
            var saved = doc.RootElement.GetProperty("options");
            var config = saved.GetProperty("ggdown-rate-limit");
            Assert.Equal(1, config.GetProperty("version").GetInt32());
            Assert.Equal(_paths.DbFile, config.GetProperty("settings-db").GetString());
            Assert.Equal(Path.Combine(_paths.TempDir, "download-rate.db"), config.GetProperty("state-db").GetString());
            Assert.Equal(_paths.Root, saved.GetProperty("base-directory").GetString());
        }
        Assert.False(options.ContainsKey("ggdown-rate-limit"));
    }

    [SkippableFact]
    public async Task Hello_returns_protocol_1()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var hello = await CreateEngine(python!).HelloAsync();
        Assert.Equal(1, hello.Protocol);
    }

    private async Task<string> WriteRunnerAsync(string body)
    {
        var script = Path.Combine(_paths.TempDir, $"runner-{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(script, "import json, sys, time\n" + body);
        return script;
    }

    [SkippableTheory]
    [InlineData("hello")]
    [InlineData("whoami")]
    [InlineData("following")]
    [InlineData("user-info")]
    [InlineData("download")]
    public async Task Every_operation_rejects_incompatible_handshake(string command)
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var script = await WriteRunnerAsync("print(json.dumps({'ev':'hello','protocol':2}), flush=True)\n" +
            "print(json.dumps({'ev':'account','screen_name':'wrong'}), flush=True)\n" +
            "print(json.dumps({'ev':'user','rest_id':'1'}), flush=True)\n");
        var engine = CreateEngine(python!, script);
        Task Invoke() => command switch
        {
            "hello" => engine.HelloAsync(),
            "whoami" => engine.WhoAmIAsync("twitter", "cookies"),
            "following" => engine.ListFollowingAsync("twitter", "cookies"),
            "user-info" => engine.GetUserInfoAsync("twitter", "cookies", "alice"),
            _ => engine.DownloadAsync(new DownloadPlan("twitter", ["url"], _paths.Root, new Dictionary<string, object?>()), "cookies", new Progress<EngineEvent>()),
        };
        var error = await Assert.ThrowsAsync<EngineException>(Invoke);
        Assert.Contains("协议", error.Message);
    }

    [SkippableTheory]
    [InlineData("print(json.dumps({'ev':'account','screen_name':'wrong'}), flush=True)")]
    [InlineData("print(json.dumps({'ev':'hello'}), flush=True)")]
    [InlineData("print('ordinary stdout noise', flush=True)")]
    public async Task First_message_must_be_a_valid_hello(string output)
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var script = await WriteRunnerAsync(output + "\n");
        await Assert.ThrowsAsync<EngineException>(() => CreateEngine(python!, script).WhoAmIAsync("twitter", "cookies"));
    }

    [SkippableFact]
    public async Task Missing_hello_times_out_and_terminates_runner()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var script = await WriteRunnerAsync("time.sleep(60)\n");
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var error = await Assert.ThrowsAsync<EngineException>(() =>
            CreateEngine(python!, script, TimeSpan.FromMilliseconds(500)).HelloAsync(guard.Token));
        Assert.Contains("握手", error.Message);
    }

    [SkippableFact]
    public async Task Handshake_deadline_does_not_limit_the_download_or_async_receiver()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var script = await WriteRunnerAsync("print(json.dumps({'ev':'hello','protocol':1}), flush=True)\n" +
            "time.sleep(1.2)\nprint(json.dumps({'ev':'file-done','path':'a.jpg'}), flush=True)\n" +
            "print(json.dumps({'ev':'job-done','total':1,'failed':0}), flush=True)\n");
        IDownloadEngine engine = CreateEngine(python!, script, TimeSpan.FromSeconds(1));
        var events = new List<string>();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await engine.DownloadWithEventsAsync(new DownloadPlan("twitter", ["url"], _paths.Root, new Dictionary<string, object?>()),
            "cookies", async (ev, ct) =>
            {
                if (ev.Event == "file-done") await Task.Delay(1200, ct);
                events.Add(ev.Event);
            }, guard.Token);
        Assert.Equal(new[] { "hello", "file-done", "job-done" }, events);
    }

    [SkippableFact]
    public async Task Download_without_job_done_is_not_successful()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var script = await WriteRunnerAsync("print(json.dumps({'ev':'hello','protocol':1}), flush=True)\n");
        var error = await Assert.ThrowsAsync<EngineException>(() => CreateEngine(python!, script).DownloadAsync(
            new DownloadPlan("twitter", ["url"], _paths.Root, new Dictionary<string, object?>()), "cookies", new Progress<EngineEvent>()));
        Assert.Contains("job-done", error.Message);
    }

    [SkippableFact]
    public async Task Large_stderr_is_drained_and_only_the_tail_is_reported()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var script = await WriteRunnerAsync("print(json.dumps({'ev':'hello','protocol':1}), flush=True)\n" +
            "sys.stderr.write('sensitive-prefix' + 'x' * 4000000 + 'tail-marker')\nsys.stderr.flush()\nsys.exit(3)\n");
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var allocatedBefore = GC.GetTotalAllocatedBytes();
        var error = await Assert.ThrowsAsync<EngineException>(() => CreateEngine(python!, script).DownloadAsync(
            new DownloadPlan("twitter", ["url"], _paths.Root, new Dictionary<string, object?>()), "cookies", new Progress<EngineEvent>(), guard.Token));
        Assert.EndsWith("tail-marker", error.Message);
        Assert.DoesNotContain("sensitive-prefix", error.Message);
        Assert.InRange(error.Message.Length, 11, 2200);
        Assert.InRange(GC.GetTotalAllocatedBytes() - allocatedBefore, 0, 4_000_000);
    }

    [SkippableFact]
    public async Task WhoAmI_returns_account()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var info = await CreateEngine(python!).WhoAmIAsync("twitter", "good.txt");
        Assert.Equal("stub_user", info.ScreenName);
        Assert.Equal("99", info.RestId);
    }

    [SkippableFact]
    public async Task WhoAmI_passes_site_id_and_returns_pixiv_account()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var info = await CreateEngine(python!).WhoAmIAsync("pixiv", "good.txt");
        Assert.Equal("pixiv_user", info.ScreenName);
        Assert.Equal("12345", info.RestId);
        Assert.Equal("Pixiv User", info.DisplayName);
    }

    [SkippableFact]
    public async Task WhoAmI_auth_failure_throws_AuthException()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        await Assert.ThrowsAsync<AuthException>(
            () => CreateEngine(python!).WhoAmIAsync("twitter", "bad.txt"));
    }

    [SkippableFact]
    public async Task ListFollowing_returns_users()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var users = await CreateEngine(python!).ListFollowingAsync("twitter", "good.txt");
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
        var readyFile = Path.Combine(_paths.TempDir, "runner-ready.txt");
        var plan = new DownloadPlan("twitter", ["https://x.com/alice/media"],
            _paths.Root, new Dictionary<string, object?> { ["hang"] = true, ["ready_file"] = readyFile });
        using var cts = new CancellationTokenSource();
        var download = CreateEngine(python!).DownloadAsync(plan, "good.txt", new Progress<EngineEvent>(), cts.Token);
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(readyFile) && timeout.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(20);
            Assert.True(File.Exists(readyFile), "测试引擎未就绪");
            var processId = int.Parse(await File.ReadAllTextAsync(readyFile));
            using var child = Process.GetProcessById(processId);
            _ = child.Handle; // 先取得句柄，退出检查不会受 PID 回收或复用影响。
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            Assert.True(child.HasExited, "取消返回后 Python 进程仍占用工作目录");
        }
        finally
        {
            cts.Cancel();
            try { await download; }
            catch (OperationCanceledException) { }
        }
    }

    [SkippableFact]
    public async Task Download_callback_failure_does_not_leave_an_orphan_runner()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var script = Path.Combine(_paths.TempDir, "callback-failure.py");
        var pidFile = Path.Combine(_paths.TempDir, "callback-pid.txt");
        await File.WriteAllTextAsync(script, """
            import os, sys, json, time
            with open(sys.argv[1], 'w') as f:
                f.write(str(os.getpid()))
            print(json.dumps({'ev': 'hello', 'protocol': 1}), flush=True)
            print(json.dumps({'ev': 'file-start', 'path': 'test.jpg'}), flush=True)
            time.sleep(60)
            """);
        await File.WriteAllTextAsync(script, (await File.ReadAllTextAsync(script))
            .Replace("sys.argv[1]", "'" + pidFile.Replace("\\", "/") + "'"));
        var plan = new DownloadPlan("twitter", ["https://x.com/alice/media"], _paths.Root,
            new Dictionary<string, object?>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateEngine(python!, script)
            .DownloadAsync(plan, "good.txt", new ThrowingProgress()));
        Process process;
        try { process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile))); }
        catch (ArgumentException) { return; } // 引擎已退出且系统已回收进程。
        using var processHandle = process;
        try
        {
            Assert.True(process.HasExited, "回调失败后 runner 仍在后台运行");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private sealed class ThrowingProgress : IProgress<EngineEvent>
    {
        public void Report(EngineEvent value) => throw new InvalidOperationException("测试回调失败");
    }
}
