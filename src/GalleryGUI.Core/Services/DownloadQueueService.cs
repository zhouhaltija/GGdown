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
    long Done, long Skipped, long Failed, long Total, string? CurrentFile, string? Error);

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
    Task<long> EnqueueAccountContentAsync(Account account, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions,
        CancellationToken ct = default);        // likes/bookmarks：单任务，UserId 为空
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
        string TargetScreenName, string Title, string BaseDirectory,
        IReadOnlyDictionary<string, object?> SiteOptions, string CookieAbsolutePath,
        string ArchiveFile);

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

    public async Task<long> EnqueueUserMediaAsync(Account account, IReadOnlyList<User> users,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
    {
        if (users.Count == 0) throw new ArgumentException("至少选择一个用户", nameof(users));
        long first = 0;
        var cookieAbs = AccountService.AbsoluteCookiePath(paths, account);
        var archive = Path.Combine(paths.ArchiveDir, account.Id + ".txt");
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var user in users)
        {
            var job = new DownloadJob
            {
                AccountId = account.Id, TargetKind = TargetKind.UserMedia, UserId = user.Id,
                Status = JobStatus.Pending, CreatedAt = DateTime.UtcNow,
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            if (first == 0) first = job.Id; // 接口契约"返回首个 jobId"：循环内无条件覆盖会在多用户时返回最后一个（审查 Important-1）
            var title = string.IsNullOrEmpty(user.DisplayName) ? user.ScreenName : user.DisplayName!;
            AddActive(job.Id, account.SiteId, TargetKind.UserMedia, title, JobStatus.Pending);
            _pending.Enqueue(new WorkItem(job.Id, account.Id, account.SiteId, TargetKind.UserMedia,
                user.Id, user.ScreenName, title, baseDirectory, siteOptions, cookieAbs, archive));
        }
        EnsureWorkers();
        return first;
    }

    public async Task<long> EnqueueAccountContentAsync(Account account, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
    {
        if (kind is not (ContentKind.AccountLikes or ContentKind.AccountBookmarks))
            throw new ArgumentException("仅接受 AccountLikes/AccountBookmarks", nameof(kind));
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
        var title = kind == ContentKind.AccountLikes ? $"喜欢（@{account.ScreenName}）" : $"书签（@{account.ScreenName}）";
        AddActive(job.Id, account.SiteId, targetKind, title, JobStatus.Pending);
        _pending.Enqueue(new WorkItem(job.Id, account.Id, account.SiteId, targetKind,
            null, account.ScreenName ?? "me", title, baseDirectory, siteOptions, cookieAbs, archive));
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

        var provider = sites.Get(item.SiteId);
        var target = item.Kind switch
        {
            TargetKind.UserMedia => new UserTarget(item.UserId, item.TargetScreenName, item.BaseDirectory),
            _ => new UserTarget(null, item.TargetScreenName, item.BaseDirectory),
        };
        var plan = provider.BuildDownload((ContentKind)item.Kind, target, item.SiteOptions,
            new DownloadPaths(item.CookieAbsolutePath, item.ArchiveFile));

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
            await FinishJobAsync(item, JobStatus.Completed, null, hasFinal,
                totalFinal, skippedFinal, failedFinal, done, skipped, failed);
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

        if (status is JobStatus.Completed or JobStatus.Canceled)
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

    private void AddActive(long jobId, string siteId, TargetKind kind, string title, JobStatus status)
    {
        JobSnapshot snapshot = new(jobId, siteId, kind, title, status, 0, 0, 0, 0, null, null);
        lock (_gate) _active[jobId] = snapshot;
        JobChanged?.Invoke(snapshot);
    }

    private sealed class DelegateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
