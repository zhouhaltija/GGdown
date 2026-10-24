using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GGdown.Paths;
using GGdown.Settings;
using GGdown.Sites;
using Microsoft.Extensions.Logging;

namespace GGdown.Engine;

public sealed class RunnerEngineOptions
{
    public string PythonExe { get; set; } = "";
    public string RunnerScript { get; set; } = "";
    public string GalleryDlPath { get; set; } = "";
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public Task Initialization { get; set; } = Task.CompletedTask;
}

public sealed class RunnerEngine(
    IAppPaths paths, RunnerEngineOptions options, ILogger<RunnerEngine> log, IAppSettings? settings = null)
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

    public async Task<AccountInfo> WhoAmIAsync(string siteId, string cookiesFile, CancellationToken ct = default)
    {
        AccountInfo? account = null;
        var result = await RunAsync(["whoami", "--site", siteId, "--cookies", cookiesFile],
            ev =>
            {
                if (ev.Event == "account")
                    account = new AccountInfo(ev.ScreenName ?? "?", ev.DisplayName, ev.RestId);
            },
            ct, killOnCancel: false);
        return account ?? throw EngineError(result);
    }

    public async Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(string siteId, string cookiesFile, CancellationToken ct = default)
    {
        var users = new List<SiteUserInfo>();
        var result = await RunAsync(["list-following", "--site", siteId, "--cookies", cookiesFile],
            ev => { if (ev.Event == "user" && ev.RestId is not null) users.Add(ToUser(ev)); },
            ct, killOnCancel: false);
        if (users.Count == 0 && result.ExitCode != 0) throw EngineError(result);
        return users;
    }

    public async Task<SiteUserInfo> GetUserInfoAsync(string siteId, string cookiesFile, string input, CancellationToken ct = default)
    {
        SiteUserInfo? user = null;
        var result = await RunAsync(["user-info", "--site", siteId, "--cookies", cookiesFile, "--input", input],
            ev => { if (ev.Event == "user" && user is null) user = ToUser(ev); },
            ct, killOnCancel: false);
        return user ?? throw EngineError(result);
    }

    public Task DownloadAsync(DownloadPlan plan, string cookiesFile,
        IProgress<EngineEvent> progress, CancellationToken ct = default) =>
        DownloadWithEventsAsync(plan, cookiesFile, (ev, _) =>
        {
            progress.Report(ev);
            return ValueTask.CompletedTask;
        }, ct);

    public async Task DownloadWithEventsAsync(DownloadPlan plan, string cookiesFile,
        Func<EngineEvent, CancellationToken, ValueTask> onEvent, CancellationToken ct = default)
    {
        paths.EnsureCreated();
        var jobFile = Path.Combine(paths.TempDir, $"job-{Guid.NewGuid():N}.json");
        try
        {
            var planOptions = plan.Options;
            if (settings is not null)
            {
                planOptions = ProxyConfig.MergeIntoOptions(planOptions, (await settings.GetProxyAsync(ct)).ToUrl());
                planOptions = new Dictionary<string, object?>(planOptions)
                {
                    ["ggdown-rate-limit"] = new Dictionary<string, object?>
                    {
                        ["version"] = 1,
                        ["settings-db"] = paths.DbFile,
                        ["state-db"] = Path.Combine(paths.TempDir, "download-rate.db"),
                    },
                };
            }
            var payload = new Dictionary<string, object?>
            {
                ["urls"] = plan.Urls,
                ["options"] = planOptions,
            };
            await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(payload, JobJson), ct);

            var args = new List<string>
            { "download", "--site", plan.SiteId, "--cookies", cookiesFile, "--job", jobFile };
            // 审查 Important-1：runner 非零退出且未发 fatal（如被系统 OOM 杀掉）时不能静默当成功，
            // 未收到 job-done 的下载必须抛异常交给队列标 Failed
            var hasFinal = false;
            var result = await RunAsync(args, async (ev, token) =>
            {
                await onEvent(ev, token);
                if (ev.Event == "job-done") hasFinal = true;
            }, ct, killOnCancel: true);
            if (result.ExitCode != 0)
                throw new EngineException($"runner 异常退出（退出码 {result.ExitCode}）：{result.StderrTail}");
            if (!hasFinal)
                throw new EngineException($"runner 未输出 job-done，下载结果不完整：{result.StderrTail}");
        }
        finally
        {
            try { File.Delete(jobFile); } catch { /* 尽力清理 */ }
        }
    }

    private static SiteUserInfo ToUser(EngineEvent ev) =>
        new(ev.RestId!, ev.ScreenName ?? "?", ev.DisplayName, ev.AvatarUrl,
            ev.BannerUrl, ev.Bio, ev.FollowersCount, ev.MediaCount);

    private static EngineException EngineError(RunnerResult result) =>
        result.ExitCode == 2
            ? new AuthException(result.StderrTail.Length > 0 ? result.StderrTail : "runner 认证失败")
            : new EngineException($"runner 退出码 {result.ExitCode}：{result.StderrTail}");

    private Task<RunnerResult> RunAsync(IReadOnlyList<string> args,
        Action<EngineEvent> onEvent, CancellationToken ct, bool killOnCancel) =>
        RunAsync(args, (ev, _) =>
        {
            onEvent(ev);
            return ValueTask.CompletedTask;
        }, ct, killOnCancel);

    private async Task<RunnerResult> RunAsync(IReadOnlyList<string> args,
        Func<EngineEvent, CancellationToken, ValueTask> onEvent, CancellationToken ct, bool killOnCancel)
    {
        // 只取消本次等待，不取消所有操作共享的引擎初始化；完成后才读取运行路径。
        await options.Initialization.WaitAsync(ct);
        // B8 警告清理：CS9113（log 主构造参数未读）——Debug 级记录起止（默认 MinimumLevel=Information 不落盘）
        log.LogDebug("runner 启动：{Args}", string.Join(" ", args));
        // 先判定「引擎未安装」，再读运行路径：未播种时 RunnerScript 为空，GetFullPath 会抛空路径异常，
        // 用户就看不到「引擎未安装」这条可操作的原因。
        if (string.IsNullOrEmpty(options.PythonExe))
            throw new EngineException("引擎未安装：找不到 python（首次启动会从安装目录播种 engine）");
        var pythonIsPath = Path.IsPathRooted(options.PythonExe)
            || options.PythonExe.Contains(Path.DirectorySeparatorChar)
            || options.PythonExe.Contains(Path.AltDirectorySeparatorChar);
        if (pythonIsPath && !File.Exists(options.PythonExe))
            throw new EngineException($"引擎未安装：找不到 {options.PythonExe}（首次启动会从安装目录播种 engine）");
        // 裸命令名（如 "python"）由 Process.Start 走 PATH 解析，File.Exists 无法判定，
        // 缺失时仍由 p.Start() 抛 Win32Exception → 队列兜底 catch 标 Failed。

        // 受管理的版本目录持读租约，覆盖进程创建到完全退出；开发源码路径与旧式目录不持租约。
        var runtimeDirectory = Path.GetDirectoryName(Path.GetFullPath(options.RunnerScript));
        using var versionLease = runtimeDirectory is not null
            && EngineVersionMaintenance.IsManagedVersion(paths.EngineDir, runtimeDirectory)
                ? EngineVersionLease.Acquire(runtimeDirectory) : null;

        var psi = new ProcessStartInfo
        {
            FileName = options.PythonExe,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.RunnerScript)) ?? paths.EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(options.RunnerScript);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(options.GalleryDlPath))
            psi.EnvironmentVariables["PYTHONPATH"] = options.GalleryDlPath;
        // Windows 控制台默认 GBK，用户昵称含 ⋆ 等字符会在 runner/gallery-dl 打印时炸
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        var proxyUrl = settings is null ? null : (await settings.GetProxyAsync(ct)).ToUrl();
        ProxyConfig.ApplyTo(psi, proxyUrl);

        using var p = new Process { StartInfo = psi };
        p.Start();
        using var lifetime = RunnerProcessLifetime.Attach(p);

        var stderrTask = ReadStderrTailAsync(p.StandardError);
        string? fatalMessage = null, fatalKind = null;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(options.HandshakeTimeout);
        var hasHello = false;

        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(handshake.Token)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var ev = JsonlParser.Parse(line);
                if (!hasHello)
                {
                    if (ev?.Event != "hello" || ev.Protocol is null)
                        throw new EngineException("runner 协议握手失败：首条消息必须是有效的 hello");
                    if (ev.Protocol != 1)
                        throw new EngineException($"runner 协议版本 {ev.Protocol} 与应用不兼容（需要 1）");
                    hasHello = true;
                    // 计时器只约束握手；后续下载和可等待的事件接收不受此期限限制。
                    handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                else if (ev?.Event == "hello")
                    throw new EngineException("runner 协议顺序错误：重复 hello");
                if (ev is null) continue;
                if (ev.Event == "fatal")
                {
                    fatalMessage = ev.Message ?? "runner fatal";
                    fatalKind = ev.Kind;
                }
                await onEvent(ev, ct);
            }
            if (!hasHello) throw new EngineException("runner 协议握手失败：未收到 hello");
        }, CancellationToken.None);

        try
        {
            await stdoutTask;
            await p.WaitForExitAsync(ct);
        }
        catch (Exception error)
        {
            TryKill(p);
            // Kill 发出终止请求后并不等待退出，取消完成前应确认工作目录和流均已释放。
            try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException ex) { log.LogWarning(ex, "取消后 runner 未在 5 秒内退出"); }
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { log.LogDebug(ex, "清理 runner 输出流（保留原始异常）"); }
            var diagnostic = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
            if (error is OperationCanceledException && !ct.IsCancellationRequested && !hasHello)
                throw new EngineException($"runner 协议握手超时：{diagnostic}");
            if (error is EngineException protocolError && !hasHello && diagnostic.Length > 0)
                throw new EngineException($"{protocolError.Message}：{diagnostic}", protocolError.Kind);
            throw;
        }

        var tail = await stderrTask;
        var exitCode = p.ExitCode;
        log.LogDebug("runner 退出：退出码 {ExitCode}", exitCode);

        if (killOnCancel && ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        if (fatalMessage is not null)
            throw fatalKind == "auth"
                ? new AuthException(fatalMessage)
                : new EngineException(fatalMessage, fatalKind);
        return new RunnerResult(exitCode, tail);
    }

    private static async Task<string> ReadStderrTailAsync(StreamReader reader)
    {
        const int capacity = 2000;
        var tail = new StringBuilder(capacity);
        var buffer = new char[4096];
        int count;
        // 不按行读取，单条超长诊断也不能产生与日志总量成比例的分配。
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            if (count >= capacity)
            {
                tail.Clear();
                tail.Append(buffer, count - capacity, capacity);
            }
            else
            {
                var excess = tail.Length + count - capacity;
                if (excess > 0) tail.Remove(0, excess);
                tail.Append(buffer, 0, count);
            }
        }
        return tail.ToString();
    }

    private void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "终止 runner 进程树失败，尝试终止主进程");
            try { if (!p.HasExited) p.Kill(); }
            catch (Exception fallbackEx) { log.LogWarning(fallbackEx, "终止 runner 主进程失败"); }
        }
    }

}
