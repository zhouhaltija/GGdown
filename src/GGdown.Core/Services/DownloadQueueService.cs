using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GGdown.Services;

public sealed record JobSnapshot(
    long JobId, string SiteId, TargetKind Kind, string Title, JobStatus Status,
    long Done, long Skipped, long Failed, long Total, string? CurrentFile, string? Error,
    string KindLabel = "", long? UserId = null);

public sealed record AccountInvalidNotice(string SiteId, long AccountId, string Reason);

public interface IDownloadQueueService
{
    IReadOnlyList<JobSnapshot> Active { get; }
    event Action<JobSnapshot>? JobChanged;      // 新任务或状态/进度变化（含终态，随后触发 JobRemoved）
    event Action<JobSnapshot>? JobRemoved;      // 任务到达终态并从 Active 移除
    event Action<AccountInvalidNotice>? AccountInvalid;
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
    Task CancelAsync(long jobId, CancellationToken ct = default, string? siteId = null);
    Task RecoverOnStartupAsync(CancellationToken ct = default); // 遗留 Running/Pending → Failed("应用异常退出")
}

public sealed partial class DownloadQueueService(
    ISiteDbContextFactory siteFactory,
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
        string ArchiveFile, string? DirectUrl = null,
        UserContentSelection ContentSelection = UserContentSelection.All, DateOnly? DownloadSince = null);

    private readonly object _gate = new();
    private readonly Dictionary<(string SiteId, long JobId), JobSnapshot> _active = [];
    private readonly Dictionary<(string SiteId, long JobId), CancellationTokenSource> _cancels = [];
    private readonly LinkedList<WorkItem> _pending = [];
    private readonly HashSet<(string SiteId, long JobId)> _scheduledProgress = [];
    private int _runningWorkers;
    private int _concurrency = 1;

    public int Concurrency
    {
        get { lock (_gate) return _concurrency; }
        set
        {
            lock (_gate) _concurrency = Math.Max(1, value);
            EnsureWorkers();
        }
    }

    public IReadOnlyList<JobSnapshot> Active { get { lock (_gate) return [.. _active.Values]; } }
    public event Action<JobSnapshot>? JobChanged;
    public event Action<JobSnapshot>? JobRemoved;
    public event Action<AccountInvalidNotice>? AccountInvalid;

    public Task<long> EnqueueUserMediaAsync(Account account, IReadOnlyList<User> users,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
        => EnqueueUserContentAsync(account, users, ContentKind.UserMedia, baseDirectory, siteOptions, ct);

    public async Task<long> EnqueueUserContentAsync(Account account, IReadOnlyList<User> users, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
    {
        if (users.Count == 0) throw new ArgumentException("至少选择一个用户", nameof(users));
        if (users.Any(u => !string.Equals(u.SiteId, account.SiteId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("下载用户与账号必须属于同一平台", nameof(users));
        if (kind is not (ContentKind.UserMedia or ContentKind.UserNovels or ContentKind.UserHighlights))
            throw new ArgumentException("仅接受 UserMedia/UserNovels/UserHighlights", nameof(kind));
        var targetKind = ContentKindMapping.ToTargetKind(kind);
        long first = 0;
        var cookieAbs = AccountService.AbsoluteCookiePath(paths, account);
        var archive = ArchivePath(paths, account);
        await using var db = await siteFactory.CreateAsync(account.SiteId, ct);
        var ids = users.Select(u => u.Id).Distinct().ToList();
        if (!await db.Accounts.AnyAsync(a => a.Id == account.Id && a.SiteId == account.SiteId, ct)
            || await db.Users.CountAsync(u => ids.Contains(u.Id) && u.SiteId == account.SiteId, ct) != ids.Count)
            throw new ArgumentException("下载账号或用户不存在于指定平台");
        try
        {
            foreach (var user in users)
            {
                var name = string.IsNullOrEmpty(user.DisplayName) ? user.ScreenName : user.DisplayName!;
                var title = kind switch
                {
                    ContentKind.UserNovels => $"{name}（小说）",
                    ContentKind.UserHighlights => $"{name}（高光）",
                    _ => name,
                };
                var id = await PersistAndQueueAsync(db, new WorkItem(0, account.Id, account.SiteId, targetKind,
                    user.Id, user.ScreenName, user.RestId, title, baseDirectory, siteOptions, cookieAbs, archive,
                    ContentSelection: user.ContentSelection, DownloadSince: user.DownloadSince), ct, startWorkers: false);
                if (first == 0) first = id;
            }
        }
        finally { EnsureWorkers(); } // 部分入队后取消或失败时，已持久化的任务仍能正常执行。
        return first;
    }

    public async Task<long> EnqueueAccountContentAsync(Account account, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
    {
        if (kind is not (ContentKind.AccountLikes or ContentKind.AccountBookmarks or ContentKind.AccountNovelBookmarks))
            throw new ArgumentException("仅接受账号级内容类型", nameof(kind));
        // 适配：brief 原文将 Sites.ContentKind 与 Data.TargetKind 视为同一类型，实际为两个同名同序枚举（UserMedia/AccountLikes/AccountBookmarks），
        // 持久化/快照用 Data.TargetKind，站点计划用 Sites.ContentKind，边界处显式转换（语义一一对应）。
        var targetKind = ContentKindMapping.ToTargetKind(kind);
        var cookieAbs = AccountService.AbsoluteCookiePath(paths, account);
        var archive = ArchivePath(paths, account);
        await using var db = await siteFactory.CreateAsync(account.SiteId, ct);
        var kindLabel = sites.Get(account.SiteId).KindLabel(kind);
        var title = $"{kindLabel}（@{account.ScreenName}）";
        return await PersistAndQueueAsync(db, new WorkItem(0, account.Id, account.SiteId, targetKind,
            null, account.ScreenName ?? "me", account.RestId, title, baseDirectory, siteOptions, cookieAbs, archive), ct);
    }

    public async Task<long> EnqueuePermalinkAsync(Account account, string url, string title, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
    {
        if (kind is not (ContentKind.Permalink or ContentKind.Search))
            throw new ArgumentException("仅接受 Permalink/Search", nameof(kind));
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL 不能为空", nameof(url));
        var targetKind = ContentKindMapping.ToTargetKind(kind);
        var cookieAbs = AccountService.AbsoluteCookiePath(paths, account);
        var archive = ArchivePath(paths, account);
        await using var db = await siteFactory.CreateAsync(account.SiteId, ct);
        return await PersistAndQueueAsync(db, new WorkItem(0, account.Id, account.SiteId, targetKind,
            null, account.ScreenName ?? "me", account.RestId, title, baseDirectory, siteOptions, cookieAbs, archive, url), ct);
    }

    private static (string SiteId, long JobId) Key(string siteId, long jobId) => (siteId.ToLowerInvariant(), jobId);

    public async Task CancelAsync(long jobId, CancellationToken ct = default, string? siteId = null)
    {
        WorkItem? pending = null;
        (string SiteId, long JobId) key;
        lock (_gate)
        {
            var matches = _active.Values.Where(s => s.JobId == jobId
                && (siteId is null || string.Equals(s.SiteId, siteId, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count == 0) return;
            if (matches.Count > 1) throw new InvalidOperationException("多个平台存在同号任务，取消时必须指定平台");
            key = Key(matches[0].SiteId, jobId);
            if (_cancels.TryGetValue(key, out var cts))
            {
                cts.Cancel();
                return;
            }
            if (!_active.TryGetValue(key, out var snapshot) || snapshot.Status != JobStatus.Pending)
                return;
            for (var node = _pending.First; node is not null; node = node.Next)
            {
                if (Key(node.Value.SiteId, node.Value.JobId) != key) continue;
                pending = node.Value;
                _pending.Remove(node);
                break;
            }
        }
        if (pending is null) return;
        try
        {
            await using var db = await siteFactory.CreateAsync(pending.SiteId, ct);
            await db.Jobs.Where(j => j.Id == jobId && j.Status == JobStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Canceled)
                    .SetProperty(j => j.RecoveryData, (string?)null)
                    .SetProperty(j => j.FinishedAt, DateTime.UtcNow), ct);
        }
        catch
        {
            lock (_gate) _pending.AddFirst(pending);
            EnsureWorkers();
            throw;
        }

        JobSnapshot canceled;
        lock (_gate)
        {
            canceled = _active[key] with { Status = JobStatus.Canceled };
            _active.Remove(key);
        }
        Publish(JobChanged, canceled);
        Publish(JobRemoved, canceled);
    }

    public async Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        // Task 5：跨平台库恢复——遍历磁盘上已存在的全部站点库
        foreach (var siteId in siteFactory.ExistingSites())
        {
            await using var db = await siteFactory.CreateAsync(siteId, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var jobs = await db.Jobs.AsTracking()
                .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running).ToListAsync(ct);
            foreach (var job in jobs)
            {
                var counts = await db.Files.Where(f => f.JobId == job.Id)
                    .GroupBy(f => f.Status).Select(g => new { Status = g.Key, Count = g.LongCount() })
                    .ToDictionaryAsync(g => g.Status, g => g.Count, ct);
                job.Status = JobStatus.Failed;
                job.FinishedAt = DateTime.UtcNow;
                job.DoneFiles = counts.GetValueOrDefault(FileStatus.Downloaded);
                job.SkippedFiles = counts.GetValueOrDefault(FileStatus.Skipped);
                job.FailedFiles = counts.GetValueOrDefault(FileStatus.Failed);
                job.TotalFiles = job.DoneFiles + job.SkippedFiles + job.FailedFiles;
                job.ErrorMessage = "应用异常退出，任务中断";
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
    }

    private void EnsureWorkers()
    {
        var starting = new List<(WorkItem Item, CancellationTokenSource Cts)>();
        lock (_gate)
        {
            while (_runningWorkers < _concurrency && _pending.First is { } first)
            {
                _pending.RemoveFirst();
                var cts = new CancellationTokenSource();
                _cancels[Key(first.Value.SiteId, first.Value.JobId)] = cts;
                _runningWorkers++;
                starting.Add((first.Value, cts));
            }
        }
        foreach (var work in starting)
            _ = Task.Run(() => RunWorkerAsync(work.Item, work.Cts));
    }

    private async Task RunWorkerAsync(WorkItem item, CancellationTokenSource cts)
    {
        try { await RunJobAsync(item, cts); }
        catch (Exception ex)
        {
            // 数据库不可写时保留原请求供重启恢复，运行时显示保存故障并释放执行名额。
            log.LogError(ex, "任务 {SiteId}/{JobId} 保存状态失败，保留恢复请求", item.SiteId, item.JobId);
            RemoveActive(item, JobStatus.Failed, $"任务记录保存失败，重启后可恢复：{ex.Message}", 0, 0, 0);
        }
        finally
        {
            lock (_gate)
            {
                _cancels.Remove(Key(item.SiteId, item.JobId));
                _runningWorkers--;
            }
            cts.Dispose();
            EnsureWorkers();
        }
    }

    private async Task RunJobAsync(WorkItem item, CancellationTokenSource cts)
    {
        long done = 0, skipped = 0, failed = 0;
        long totalFinal = 0, skippedFinal = 0, failedFinal = 0;
        var hasFinal = false;
        string? currentItemId = null;
        var status = JobStatus.Failed;
        string? error = null;
        bool preserveRecovery = false, invalidAccount = false;
        JobFileWriter? writer = null;
        try
        {
            await using (var db = await siteFactory.CreateAsync(item.SiteId))
            {
                var job = await db.Jobs.SingleAsync(j => j.Id == item.JobId);
                if (job.Status is not (JobStatus.Pending or JobStatus.Running)) return;
                job.Status = JobStatus.Running;
                job.StartedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            UpdateSnapshot(item.JobId, item.SiteId, status: JobStatus.Running);
            cts.Token.ThrowIfCancellationRequested();
            var provider = sites.Get(item.SiteId);
            var target = new UserTarget(item.UserId, item.TargetScreenName, item.BaseDirectory, item.TargetRestId,
                item.DirectUrl, item.ContentSelection, item.DownloadSince);
            var plan = provider.BuildDownload(ContentKindMapping.ToContentKind(item.Kind), target, item.SiteOptions,
                new DownloadPaths(item.CookieAbsolutePath, item.ArchiveFile));
            writer = new JobFileWriter(siteFactory, item.SiteId, item.JobId, item.UserId);
            await engine.DownloadWithEventsAsync(plan, item.CookieAbsolutePath, async (ev, ct) =>
            {
                switch (ev.Event)
                {
                    case "file-start":
                        currentItemId = ev.ItemId;
                        UpdateSnapshot(item.JobId, item.SiteId, currentFile: ev.Path);
                        break;
                    case "file-done":
                        await writer.WriteAsync(ev, FileStatus.Downloaded, ev.ItemId ?? currentItemId, ct);
                        UpdateSnapshot(item.JobId, item.SiteId, done: ++done, currentFile: ev.Path);
                        break;
                    case "file-skip":
                        await writer.WriteAsync(ev, FileStatus.Skipped, ev.ItemId, ct);
                        UpdateSnapshot(item.JobId, item.SiteId, skipped: ++skipped);
                        break;
                    case "file-failed":
                        await writer.WriteAsync(ev, FileStatus.Failed, ev.ItemId ?? currentItemId, ct);
                        UpdateSnapshot(item.JobId, item.SiteId, failed: ++failed);
                        break;
                    case "job-done":
                        hasFinal = true;
                        totalFinal = ev.Total ?? done + skipped + failed;
                        skippedFinal = ev.Skipped ?? skipped;
                        failedFinal = ev.Failed ?? failed;
                        break;
                }
            }, cts.Token);
            // job-done 的 total 含成功、跳过和失败；部分失败要求至少有成功或跳过文件。
            status = !hasFinal ? JobStatus.Failed
                : failedFinal == 0 ? JobStatus.Completed
                : totalFinal > failedFinal ? JobStatus.PartialFailed : JobStatus.Failed;
            error = !hasFinal ? "引擎异常退出（未收到 job-done）"
                : failedFinal > 0 ? $"{(status == JobStatus.PartialFailed ? "部分" : "全部")}文件下载失败（{failedFinal} 个），请查看引擎错误" : null;
        }
        catch (AuthException ex)
        {
            invalidAccount = true;
            preserveRecovery = true;
            error = "登录态失效，请重新导入 Cookie";
            log.LogWarning(ex, "任务 {JobId} 认证失败", item.JobId);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            status = JobStatus.Canceled;
        }
        catch (EngineException ex) { error = ex.Message; }
        catch (Exception ex)
        {
            error = ex.Message;
            preserveRecovery = true;
            log.LogError(ex, "任务 {JobId} 执行异常", item.JobId);
        }

        if (writer is not null)
        {
            try { await writer.CompleteAsync(); }
            catch (Exception ex)
            {
                status = JobStatus.Failed;
                preserveRecovery = true;
                error = $"文件记录保存失败，重启后可恢复：{ex.Message}";
                log.LogError(ex, "任务 {JobId} 文件落库失败", item.JobId);
            }
            finally { await writer.DisposeAsync(); }
        }
        // 用户取消不取消已经接收事件的 flush 与终态提交。
        await FinishJobAsync(item, status, error, hasFinal, totalFinal, skippedFinal, failedFinal,
            done, skipped, failed, preserveRecovery, invalidAccount);
    }

    /// <summary>归档文件路径（Final review Important 6）：按 (siteId, accountId) 命名——
    /// 拆库后 account Id 仅在其站点库内唯一，两站 Id 相同不得共享归档（避免跨站「已下载」误跳过）。</summary>
    public static string ArchivePath(IAppPaths paths, Account account) =>
        Path.Combine(paths.ArchiveDir, $"{account.SiteId}-{account.Id}.txt");

    private async Task FinishJobAsync(WorkItem item, JobStatus status, string? error,
        bool hasFinal, long totalFinal, long skippedFinal, long failedFinal,
        long doneLive, long skippedLive, long failedLive, bool preserveRecovery = false, bool invalidAccount = false)
    {
        var done = hasFinal ? Math.Max(0, totalFinal - skippedFinal - failedFinal) : doneLive;
        var skipped = hasFinal ? skippedFinal : skippedLive;
        var failed = hasFinal ? failedFinal : failedLive;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var db = await siteFactory.CreateAsync(item.SiteId, cleanup.Token))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cleanup.Token);
            var job = await db.Jobs.SingleAsync(j => j.Id == item.JobId, cleanup.Token);
            if (job.Status is not (JobStatus.Pending or JobStatus.Running))
            {
                status = job.Status;
                error = job.ErrorMessage;
                (done, skipped, failed) = (job.DoneFiles, job.SkippedFiles, job.FailedFiles);
            }
            else
            {
                job.Status = status;
                job.DoneFiles = done;
                job.SkippedFiles = skipped;
                job.FailedFiles = failed;
                job.TotalFiles = done + skipped + failed;
                job.ErrorMessage = error;
                job.FinishedAt = DateTime.UtcNow;
                job.StatisticsPending = status == JobStatus.Completed && item.UserId is not null;
                if (job.StatisticsPending)
                    job.MediaCountSnapshot = await db.Users.Where(u => u.Id == item.UserId)
                        .Select(u => u.MediaCount).SingleOrDefaultAsync(cleanup.Token);
                if (!preserveRecovery) job.RecoveryData = null;
                if (invalidAccount)
                    await db.Accounts.Where(a => a.Id == item.AccountId)
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AccountStatus.Invalid), cleanup.Token);
                await db.SaveChangesAsync(cleanup.Token);
            }
            await transaction.CommitAsync(cleanup.Token);
        }
        // 统计是可重建投影，失败保留脏标记，不得更改已提交的下载结果。
        try { await stats.ApplyJobCompletionAsync(item.JobId, item.SiteId, cleanup.Token); }
        catch (Exception ex) { log.LogWarning(ex, "任务 {JobId} 统计待重试", item.JobId); }
        if (invalidAccount) Publish(AccountInvalid, new AccountInvalidNotice(item.SiteId, item.AccountId, error ?? "登录态失效"));
        RemoveActive(item, status, error, done, skipped, failed);
    }

    private void RemoveActive(WorkItem item, JobStatus status, string? error, long done, long skipped, long failed)
    {
        JobSnapshot snapshot;
        lock (_gate)
        {
            if (!_active.Remove(Key(item.SiteId, item.JobId), out var previous)) return;
            _scheduledProgress.Remove(Key(item.SiteId, item.JobId));
            snapshot = previous with { Status = status, Error = error, Done = done, Skipped = skipped,
                Failed = failed, Total = done + skipped + failed };
        }
        Publish(JobChanged, snapshot);
        Publish(JobRemoved, snapshot);
    }

    private void Publish<T>(Action<T>? subscribers, T value)
    {
        if (subscribers is null) return;
        foreach (Action<T> handler in subscribers.GetInvocationList())
        {
            try { handler(value); }
            catch (Exception ex) { log.LogWarning(ex, "队列事件订阅者处理失败"); }
        }
    }

    private JobSnapshot UpdateSnapshot(long jobId, string siteId, JobStatus? status = null, string? currentFile = null,
        string? error = null, long? done = null, long? skipped = null, long? failed = null, long? total = null)
    {
        JobSnapshot snapshot;
        var schedule = false;
        lock (_gate)
        {
            var existing = _active[Key(siteId, jobId)];
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
            _active[Key(siteId, jobId)] = existing;
            snapshot = existing;
            if (status is null) schedule = _scheduledProgress.Add(Key(siteId, jobId));
        }
        if (status is not null) Publish(JobChanged, snapshot);
        else if (schedule) _ = PublishProgressAsync(Key(siteId, jobId));
        return snapshot;
    }

    private async Task PublishProgressAsync((string SiteId, long JobId) key)
    {
        await Task.Delay(100).ConfigureAwait(false);
        JobSnapshot? current;
        lock (_gate)
        {
            _scheduledProgress.Remove(key);
            _active.TryGetValue(key, out current);
        }
        if (current is not null) Publish(JobChanged, current);
    }

}
