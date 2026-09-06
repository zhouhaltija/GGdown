using System.Collections.Concurrent;
using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalleryGUI.Services;

public sealed record JobSnapshot(
    long JobId, string SiteId, TargetKind Kind, string Title, JobStatus Status,
    long Done, long Skipped, long Failed, long Total, string? CurrentFile, string? Error,
    string KindLabel = "", long? UserId = null);

public interface IDownloadQueueService
{
    IReadOnlyList<JobSnapshot> Active { get; }
    event Action<JobSnapshot>? JobChanged;      // 新任务或状态/进度变化（含终态，随后触发 JobRemoved）
    event Action<JobSnapshot>? JobRemoved;      // 任务到达终态并从 Active 移除
    event Action<long, string>? AccountInvalid; // (accountId, reason)
    int Concurrency { get; set; }               // 默认 1
    Task<long> EnqueueUserMediaAsync(Account account, IReadOnlyList<User> users,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions,
        CancellationToken ct = default);        // 每用户一个 DownloadJob 行，返回首个 jobId
    Task<long> EnqueueUserContentAsync(Account account, IReadOnlyList<User> users, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions,
        CancellationToken ct = default);
    Task<long> EnqueueAccountContentAsync(Account account, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions,
        CancellationToken ct = default);        // likes/bookmarks：单任务，UserId 为空
    Task<long> EnqueuePermalinkAsync(Account account, string url, string title, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions,
        CancellationToken ct = default);        // 推文/列表/搜索：单任务，UserId 为空
    Task CancelAsync(long jobId, CancellationToken ct = default);
    Task RecoverOnStartupAsync(CancellationToken ct = default); // 遗留 Running/Pending → Failed("应用异常退出")
}

public sealed class DownloadQueueService(
    IDbContextFactory<GalleryDbContext> factory,
    IDownloadEngine engine,
    StatsAggregator stats,
    IAppPaths paths,
    SiteRegistry sites,
    ILogger<DownloadQueueService> log) : IDownloadQueueService
{
    private sealed record WorkItem(
        long JobId, long AccountId, string SiteId, TargetKind Kind, long? UserId,
        string TargetScreenName, string? TargetRestId, string Title, string BaseDirectory,
        IReadOnlyDictionary<string, object?> SiteOptions, string CookieAbsolutePath,
        string ArchiveFile, string? DirectUrl = null);

    private readonly object _gate = new();
    private readonly Dictionary<long, JobSnapshot> _active = [];
    private readonly Dictionary<long, CancellationTokenSource> _cancels = [];
    private readonly ConcurrentQueue<WorkItem> _pending = [];
    private int _runningWorkers;
    private int _concurrency = 1;

    public int Concurrency { get => _concurrency; set => _concurrency = Math.Max(1, value); }

    public IReadOnlyList<JobSnapshot> Active { get { lock (_gate) return [.. _active.Values]; } }
    public event Action<JobSnapshot>? JobChanged;
    public event Action<JobSnapshot>? JobRemoved;
    public event Action<long, string>? AccountInvalid;

    public Task<long> EnqueueUserMediaAsync(Account account, IReadOnlyList<User> users,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
        => EnqueueUserContentAsync(account, users, ContentKind.UserMedia, baseDirectory, siteOptions, ct);

    public async Task<long> EnqueueUserContentAsync(Account account, IReadOnlyList<User> users, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
    {
        if (users.Count == 0) throw new ArgumentException("至少选择一个用户", nameof(users));
        if (kind is not (ContentKind.UserMedia or ContentKind.UserNovels or ContentKind.UserHighlights))
            throw new ArgumentException("仅接受 UserMedia/UserNovels/UserHighlights", nameof(kind));
        var targetKind = (TargetKind)kind;
        long first = 0;
        var cookieAbs = AccountService.AbsoluteCookiePath(paths, account);
        var archive = Path.Combine(paths.ArchiveDir, account.Id + ".txt");
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var user in users)
        {
            var job = new DownloadJob
            {
                AccountId = account.Id, TargetKind = targetKind, UserId = user.Id,
                Status = JobStatus.Pending, CreatedAt = DateTime.UtcNow,
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            if (first == 0) first = job.Id;
            var name = string.IsNullOrEmpty(user.DisplayName) ? user.ScreenName : user.DisplayName!;
            var title = kind switch
            {
                ContentKind.UserNovels => $"{name}（小说）",
                ContentKind.UserHighlights => $"{name}（高光）",
                _ => name,
            };
            AddActive(job.Id, account.SiteId, targetKind, title, JobStatus.Pending, user.Id);
            _pending.Enqueue(new WorkItem(job.Id, account.Id, account.SiteId, targetKind,
                user.Id, user.ScreenName, user.RestId, title, baseDirectory, siteOptions, cookieAbs, archive));
        }
        EnsureWorkers();
        return first;
    }

    public async Task<long> EnqueueAccountContentAsync(Account account, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
    {
        if (kind is not (ContentKind.AccountLikes or ContentKind.AccountBookmarks or ContentKind.AccountNovelBookmarks))
            throw new ArgumentException("仅接受账号级内容类型", nameof(kind));
        // 适配：brief 原文将 Sites.ContentKind 与 Data.TargetKind 视为同一类型，实际为两个同名同序枚举（UserMedia/AccountLikes/AccountBookmarks），
        // 持久化/快照用 Data.TargetKind，站点计划用 Sites.ContentKind，边界处显式转换（语义一一对应）。
        var targetKind = (TargetKind)kind;
        var cookieAbs = AccountService.AbsoluteCookiePath(paths, account);
        var archive = Path.Combine(paths.ArchiveDir, account.Id + ".txt");
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = new DownloadJob
        {
            AccountId = account.Id, TargetKind = targetKind, UserId = null,
            Status = JobStatus.Pending, CreatedAt = DateTime.UtcNow,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        var kindLabel = sites.Get(account.SiteId).KindLabel(kind);
        var title = $"{kindLabel}（@{account.ScreenName}）";
        AddActive(job.Id, account.SiteId, targetKind, title, JobStatus.Pending);
        _pending.Enqueue(new WorkItem(job.Id, account.Id, account.SiteId, targetKind,
            null, account.ScreenName ?? "me", account.RestId, title, baseDirectory, siteOptions, cookieAbs, archive));
        EnsureWorkers();
        return job.Id;
    }

    public async Task<long> EnqueuePermalinkAsync(Account account, string url, string title, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
    {
        if (kind is not (ContentKind.Permalink or ContentKind.Search))
            throw new ArgumentException("仅接受 Permalink/Search", nameof(kind));
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL 不能为空", nameof(url));
        var targetKind = (TargetKind)kind;
        var cookieAbs = AccountService.AbsoluteCookiePath(paths, account);
        var archive = Path.Combine(paths.ArchiveDir, account.Id + ".txt");
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = new DownloadJob
        {
            AccountId = account.Id, TargetKind = targetKind, UserId = null,
            Status = JobStatus.Pending, CreatedAt = DateTime.UtcNow,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        AddActive(job.Id, account.SiteId, targetKind, title, JobStatus.Pending);
        _pending.Enqueue(new WorkItem(job.Id, account.Id, account.SiteId, targetKind,
            null, account.ScreenName ?? "me", account.RestId, title, baseDirectory, siteOptions, cookieAbs, archive, url));
        EnsureWorkers();
        return job.Id;
    }

    public Task CancelAsync(long jobId, CancellationToken ct = default)
    {
        lock (_gate) { if (_cancels.TryGetValue(jobId, out var cts)) cts.Cancel(); }
        return Task.CompletedTask;
    }

    public async Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Jobs.Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.ErrorMessage, "应用异常退出，任务中断"), ct);
    }

    private void EnsureWorkers()
    {
        lock (_gate)
        {
            while (_runningWorkers < _concurrency && !_pending.IsEmpty)
            {
                _runningWorkers++;
                _ = Task.Run(RunWorkerLoopAsync);
            }
        }
    }

    private async Task RunWorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                WorkItem item;
                lock (_gate)
                {
                    if (!_pending.TryDequeue(out item!)) break;
                }
                try { await RunJobAsync(item); }
                catch (Exception ex) { log.LogError(ex, "任务 {JobId} 工作循环异常", item.JobId); }
            }
        }
        finally
        {
            lock (_gate) _runningWorkers--;
            if (!_pending.IsEmpty) EnsureWorkers();
        }
    }

    private async Task RunJobAsync(WorkItem item)
    {
        var cts = new CancellationTokenSource();
        lock (_gate) _cancels[item.JobId] = cts;

        await using (var db = await factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == item.JobId);
            job.Status = JobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        UpdateSnapshot(item.JobId, status: JobStatus.Running);

        // 审查 Important-2：pendingInserts/hasFinal/计数器声明在 try 之前，保证兜底 catch 可见；
        // provider/target/plan 构造移入 try，BuildDownload/sites.Get 抛异常时兜底 catch 把任务标 Failed（此时 hasFinal=false、计数器为 0）
        long done = 0, skipped = 0, failed = 0;
        long totalFinal = 0, skippedFinal = 0, failedFinal = 0;
        var hasFinal = false;
        // 适配：协议仅 file-start 携带 item_id（stub_runner.py 与 JsonlParserTests 一致），file-done/file-skip
        // 落库的 SourceItemId 用事件自身 item_id，缺省回退到最近一次 file-start 的 item_id（测试即此语义）。
        string? currentItemId = null;
        // 审查 Important-2：文件落库不逐条 await（不回退事件吞吐），但每个 finish 路径在进入终态前必须
        // 排干在途插入，否则 FinishJobAsync 的统计聚合可能读到缺行快照，用户 DownloadCount 永久少计。
        // InsertFileAsync 内部 try/catch 不抛，Task.WhenAll 安全。
        var pendingInserts = new List<Task>();

        try
        {
            var provider = sites.Get(item.SiteId);
            var target = new UserTarget(item.UserId, item.TargetScreenName, item.BaseDirectory, item.TargetRestId, item.DirectUrl);
            var plan = provider.BuildDownload((ContentKind)item.Kind, target, item.SiteOptions,
                new DownloadPaths(item.CookieAbsolutePath, item.ArchiveFile));

            var progress = new DelegateProgress<EngineEvent>(ev =>
            {
                switch (ev.Event)
                {
                    case "file-start":
                        currentItemId = ev.ItemId;
                        UpdateSnapshot(item.JobId, currentFile: ev.Path);
                        break;
                    case "file-done":
                        done++;
                        pendingInserts.Add(InsertFileAsync(item.JobId, item.UserId, ev, FileStatus.Downloaded, ev.ItemId ?? currentItemId));
                        UpdateSnapshot(item.JobId, done: done);
                        break;
                    case "file-skip":
                        skipped++;
                        pendingInserts.Add(InsertFileAsync(item.JobId, item.UserId, ev, FileStatus.Skipped, ev.ItemId ?? currentItemId));
                        UpdateSnapshot(item.JobId, skipped: skipped);
                        break;
                    case "log" when ev.Level == "error":
                        failed++;
                        UpdateSnapshot(item.JobId, failed: failed);
                        break;
                    case "job-done":
                        hasFinal = true;
                        totalFinal = ev.Total ?? done + skipped + failed;
                        skippedFinal = ev.Skipped ?? skipped;
                        failedFinal = ev.Failed ?? failed;
                        break;
                }
            });

            await engine.DownloadAsync(plan, item.CookieAbsolutePath, progress, cts.Token);
            await Task.WhenAll(pendingInserts); // 统计聚合前排干在途文件落库（审查 Important-2）
            // 双保险（审查 Important-1）：引擎未抛异常但也没发 job-done（理论上被 FIX-1 的退出码检查拦截，
            // 防御 FakeEngine/未来引擎实现漏发）时不标 Completed
            await FinishJobAsync(item, hasFinal ? JobStatus.Completed : JobStatus.Failed,
                hasFinal ? null : "引擎异常退出（未收到 job-done）",
                hasFinal, totalFinal, skippedFinal, failedFinal, done, skipped, failed);
        }
        catch (AuthException e)
        {
            await Task.WhenAll(pendingInserts); // 统计聚合前排干在途文件落库（审查 Important-2）
            await FinishJobAsync(item, JobStatus.Failed, "登录态失效，请重新导入 Cookie", hasFinal,
                totalFinal, skippedFinal, failedFinal, done, skipped, failed);
            await using var db = await factory.CreateDbContextAsync();
            await db.Accounts.Where(a => a.Id == item.AccountId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AccountStatus.Invalid));
            AccountInvalid?.Invoke(item.AccountId, e.Message);
        }
        catch (OperationCanceledException)
        {
            await Task.WhenAll(pendingInserts); // 统计聚合前排干在途文件落库（审查 Important-2）
            await FinishJobAsync(item, JobStatus.Canceled, null, hasFinal,
                totalFinal, skippedFinal, failedFinal, done, skipped, failed);
        }
        catch (EngineException e)
        {
            await Task.WhenAll(pendingInserts); // 统计聚合前排干在途文件落库（审查 Important-2）
            await FinishJobAsync(item, JobStatus.Failed, e.Message, hasFinal,
                totalFinal, skippedFinal, failedFinal, done, skipped, failed);
        }
        catch (Exception e)
        {
            // 审查 Important-2 兜底：内层 try 之前的异常（如未知站点 sites.Get/BuildDownload 抛出）
            // 原先直接穿透导致任务永久卡 Running；此处先排干插入、标 Failed 再上抛交工作循环记日志
            await Task.WhenAll(pendingInserts);
            await FinishJobAsync(item, JobStatus.Failed, e.Message, hasFinal,
                totalFinal, skippedFinal, failedFinal, done, skipped, failed);
            throw; // 交 RunWorkerLoopAsync 记日志
        }
        finally
        {
            lock (_gate) _cancels.Remove(item.JobId, out _);
        }
    }

    private async Task InsertFileAsync(long jobId, long? userId, EngineEvent ev, FileStatus status, string? itemId)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            db.Files.Add(new DownloadFile
            {
                JobId = jobId, UserId = userId, SourceItemId = itemId,
                Url = ev.Url ?? "", FilePath = ev.Path ?? "",
                FileSize = ev.Size, Status = status, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { log.LogWarning(ex, "文件记录落库失败"); }
    }

    private async Task FinishJobAsync(WorkItem item, JobStatus status, string? error,
        bool hasFinal, long totalFinal, long skippedFinal, long failedFinal,
        long doneLive, long skippedLive, long failedLive)
    {
        var done = hasFinal ? Math.Max(0, totalFinal - skippedFinal - failedFinal) : doneLive;
        var skipped = hasFinal ? skippedFinal : skippedLive;
        var failed = hasFinal ? failedFinal : failedLive;

        await using var db = await factory.CreateDbContextAsync();
        var job = await db.Jobs.SingleAsync(j => j.Id == item.JobId);
        job.Status = status;
        job.DoneFiles = done;
        job.SkippedFiles = skipped;
        job.FailedFiles = failed;
        job.TotalFiles = done + skipped + failed;
        job.ErrorMessage = error;
        job.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // 审查 Important-4（控制器裁定）：终态一律聚合。规格 §4.2 DownloadCount 定义不限定任务终态，
        // 只排除 Failed 会让认证失效中断的文件永不计数（单向门：重下时 archive 命中只算 Skipped）
        if (status is JobStatus.Completed or JobStatus.Canceled or JobStatus.Failed)
            await stats.ApplyJobCompletionAsync(item.JobId);

        var snapshot = UpdateSnapshot(item.JobId, status: status, error: error,
            done: done, skipped: skipped, failed: failed, total: job.TotalFiles);
        lock (_gate) _active.Remove(item.JobId);
        JobChanged?.Invoke(snapshot);
        JobRemoved?.Invoke(snapshot);
    }

    private JobSnapshot UpdateSnapshot(long jobId, JobStatus? status = null, string? currentFile = null,
        string? error = null, long? done = null, long? skipped = null, long? failed = null, long? total = null)
    {
        JobSnapshot snapshot;
        lock (_gate)
        {
            var existing = _active[jobId];
            existing = existing with
            {
                Status = status ?? existing.Status,
                CurrentFile = currentFile ?? existing.CurrentFile,
                Error = error ?? existing.Error,
                Done = done ?? existing.Done,
                Skipped = skipped ?? existing.Skipped,
                Failed = failed ?? existing.Failed,
                Total = total ?? existing.Total,
            };
            _active[jobId] = existing;
            snapshot = existing;
        }
        JobChanged?.Invoke(snapshot);
        return snapshot;
    }

    private void AddActive(long jobId, string siteId, TargetKind kind, string title, JobStatus status, long? userId = null)
    {
        var kindLabel = sites.IsRegistered(siteId)
            ? sites.Get(siteId).KindLabel((ContentKind)kind)
            : kind.ToString();
        JobSnapshot snapshot = new(jobId, siteId, kind, title, status, 0, 0, 0, 0, null, null, kindLabel, userId);
        lock (_gate) _active[jobId] = snapshot;
        JobChanged?.Invoke(snapshot);
    }

    private sealed class DelegateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
