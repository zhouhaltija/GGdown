using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.Extensions.Logging;

namespace GalleryGUI.Engine;

public sealed class RunnerEngineOptions
{
    public string PythonExe { get; set; } = "";
    public string RunnerScript { get; set; } = "";
    public string GalleryDlPath { get; set; } = "";
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class RunnerEngine(IAppPaths paths, RunnerEngineOptions options, ILogger<RunnerEngine> log)
    : IDownloadEngine
{
    private static readonly JsonSerializerOptions JobJson = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record RunnerResult(int ExitCode, string StderrTail);

    public async Task<EngineHello> HelloAsync(CancellationToken ct = default)
    {
        EngineHello? hello = null;
        var result = await RunAsync(["hello"], ev =>
        {
            if (ev.Event == "hello" && ev.Protocol is not null)
                hello = new EngineHello(ev.Protocol.Value, ev.RunnerVersion ?? "?", ev.GalleryDlVersion);
        }, ct, killOnCancel: false);
        if (hello is null)
            throw new EngineException(
                $"runner 未输出 hello 事件（engine 未安装或 python 缺失）。退出码 {result.ExitCode}：{result.StderrTail}");
        if (hello.Protocol != 1)
            throw new EngineException($"runner 协议版本 {hello.Protocol} 与应用不兼容（需要 1）");
        return hello;
    }

    public async Task<AccountInfo> WhoAmIAsync(string cookiesFile, CancellationToken ct = default)
    {
        AccountInfo? account = null;
        var result = await RunAsync(["whoami", "--site", "twitter", "--cookies", cookiesFile],
            ev => { if (ev.Event == "account") account = new AccountInfo(ev.ScreenName ?? "?", ev.DisplayName); },
            ct, killOnCancel: false);
        return account ?? throw EngineError(result);
    }

    public async Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(string cookiesFile, CancellationToken ct = default)
    {
        var users = new List<SiteUserInfo>();
        var result = await RunAsync(["list-following", "--site", "twitter", "--cookies", cookiesFile],
            ev => { if (ev.Event == "user" && ev.RestId is not null) users.Add(ToUser(ev)); },
            ct, killOnCancel: false);
        if (users.Count == 0 && result.ExitCode != 0) throw EngineError(result);
        return users;
    }

    public async Task<SiteUserInfo> GetUserInfoAsync(string cookiesFile, string input, CancellationToken ct = default)
    {
        SiteUserInfo? user = null;
        var result = await RunAsync(["user-info", "--site", "twitter", "--cookies", cookiesFile, "--input", input],
            ev => { if (ev.Event == "user" && user is null) user = ToUser(ev); },
            ct, killOnCancel: false);
        return user ?? throw EngineError(result);
    }

    public async Task DownloadAsync(DownloadPlan plan, string cookiesFile,
        IProgress<EngineEvent> progress, CancellationToken ct = default)
    {
        paths.EnsureCreated();
        var jobFile = Path.Combine(paths.TempDir, $"job-{Guid.NewGuid():N}.json");
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["urls"] = plan.Urls,
                ["options"] = plan.Options,
            };
            await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(payload, JobJson), ct);

            var args = new List<string>
            { "download", "--site", plan.SiteId, "--cookies", cookiesFile, "--job", jobFile };
            await RunAsync(args, progress.Report, ct, killOnCancel: true);
        }
        finally
        {
            try { File.Delete(jobFile); } catch { /* 尽力清理 */ }
        }
    }

    private static SiteUserInfo ToUser(EngineEvent ev) =>
        new(ev.RestId!, ev.ScreenName ?? "?", ev.DisplayName, ev.AvatarUrl);

    private static EngineException EngineError(RunnerResult result) =>
        result.ExitCode == 2
            ? new AuthException(result.StderrTail.Length > 0 ? result.StderrTail : "runner 认证失败")
            : new EngineException($"runner 退出码 {result.ExitCode}：{result.StderrTail}");

    private async Task<RunnerResult> RunAsync(IReadOnlyList<string> args,
        Action<EngineEvent> onEvent, CancellationToken ct, bool killOnCancel)
    {
        if (string.IsNullOrEmpty(options.PythonExe))
            throw new EngineException("引擎未安装：找不到 python（首次启动会从安装目录播种 engine）");

        var psi = new ProcessStartInfo
        {
            FileName = options.PythonExe,
            WorkingDirectory = paths.EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(options.RunnerScript);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(options.GalleryDlPath))
            psi.EnvironmentVariables["PYTHONPATH"] = options.GalleryDlPath;

        var p = new Process { StartInfo = psi };
        p.Start();

        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        string? fatalMessage = null, fatalKind = null;

        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                var ev = JsonlParser.Parse(line);
                if (ev is null) continue;
                if (ev.Event == "fatal")
                {
                    fatalMessage = ev.Message ?? "runner fatal";
                    fatalKind = ev.Kind;
                }
                onEvent(ev);
            }
        }, CancellationToken.None);

        try
        {
            await stdoutTask;
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(p);
            throw;
        }

        var stderr = await stderrTask;
        var tail = stderr.Length > 2000 ? stderr[^2000..] : stderr;
        var exitCode = p.ExitCode;
        TryDispose(p);

        if (killOnCancel && ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        if (fatalMessage is not null)
            throw fatalKind == "auth"
                ? new AuthException(fatalMessage)
                : new EngineException(fatalMessage, fatalKind);
        return new RunnerResult(exitCode, tail);
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
    }

    private static void TryDispose(Process p)
    {
        try { p.Dispose(); } catch { }
    }
}
