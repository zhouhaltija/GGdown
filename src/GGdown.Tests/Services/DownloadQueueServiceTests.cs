using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Sites;
using Microsoft.Data.Sqlite; // 适配：字段元组类型 SqliteConnection 需要（同 StatsAndRecoveryTests/UserServiceTests，brief 文件原文未含此 using）
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.Services;

public class DownloadQueueServiceTests : IDisposable
{
    private readonly (SqliteConnection, GGdownSiteDbContext) _t;
    private readonly AppPaths _paths;
    private readonly FakeEngine _engine = new();
    private readonly FakeStats _stats = new();
    private readonly GGdownSiteDbContext _db;
    private readonly Account _account;
    private readonly User _alice;
    private readonly DownloadQueueService _queue;

    public DownloadQueueServiceTests()
    {
        _t = TestDb.CreateSite();
        _db = _t.Item2;
        _paths = TestPaths.Create();
        _account = new Account
        { SiteId = "twitter", CookiePath = "twitter\\c\\cookies.txt", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _alice = new User
        { SiteId = "twitter", RestId = "1", ScreenName = "alice", Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Accounts.Add(_account);
        _db.Users.Add(_alice);
        _db.SaveChanges();
        // Task 5：队列改走 ISiteDbContextFactory（站点库；预播 twitter 供恢复遍历 ExistingSites）
        _queue = new DownloadQueueService(new SingleSiteDbContextFactory(_db, "twitter"), _engine, _stats, _paths,
            new SiteRegistry([new TwitterSiteProvider()]),
            NullLogger<DownloadQueueService>.Instance);
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private static IReadOnlyDictionary<string, object?> Opts() => new Dictionary<string, object?> { ["videos"] = true };

    // ---- Task 5：拆库后的归属与跨库恢复 ----

    [Fact]
    public async Task Enqueue_persists_job_with_site_id_in_site_db()
    {
        var jobId = await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _queue.Active.Count == 0);
        var job = await _db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(jobId, job.Id);
        Assert.Equal("twitter", job.SiteId); // 任务显式携带归属（spec §3.2）
    }

    [Fact]
    public async Task Recover_marks_pending_running_failed_across_site_dbs()
    {
        var paths = TestPaths.Create();
        var factory = new SiteDbContextFactory(paths);
        long twId, pxId;
        await using (var tw = await factory.CreateAsync("twitter"))
        {
            var acc = new Account
            { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
            tw.Accounts.Add(acc);
            tw.SaveChanges();
            var job = new DownloadJob
            { SiteId = "twitter", AccountId = acc.Id, TargetKind = TargetKind.UserMedia, Status = JobStatus.Running, CreatedAt = DateTime.UtcNow };
            tw.Jobs.Add(job);
            tw.SaveChanges();
            twId = job.Id;
        }
        await using (var px = await factory.CreateAsync("pixiv"))
        {
            var acc = new Account
            { SiteId = "pixiv", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
            px.Accounts.Add(acc);
            px.SaveChanges();
            var job = new DownloadJob
            { SiteId = "pixiv", AccountId = acc.Id, TargetKind = TargetKind.UserMedia, Status = JobStatus.Pending, CreatedAt = DateTime.UtcNow };
            px.Jobs.Add(job);
            px.SaveChanges();
            pxId = job.Id;
        }

        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), paths,
            new SiteRegistry([new TwitterSiteProvider()]), NullLogger<DownloadQueueService>.Instance);
        await queue.RecoverOnStartupAsync();

        await using (var tw = await factory.CreateAsync("twitter"))
        {
            var j = await tw.Jobs.AsNoTracking().SingleAsync(x => x.Id == twId);
            Assert.Equal(JobStatus.Failed, j.Status);
            Assert.Contains("应用异常退出", j.ErrorMessage);
        }
        await using (var px = await factory.CreateAsync("pixiv"))
        {
            var j = await px.Jobs.AsNoTracking().SingleAsync(x => x.Id == pxId);
            Assert.Equal(JobStatus.Failed, j.Status);
            Assert.Contains("应用异常退出", j.ErrorMessage);
        }
        Directory.Delete(paths.Root, true);
    }

    [Fact]
    public async Task Enqueue_user_runs_job_and_records_files()
    {
        _engine.OnDownload = (plan, cookies, progress, _) =>
        {
            progress.Report(new EngineEvent("url-start", Url: plan.Urls[0]));
            progress.Report(new EngineEvent("file-start", Path: "D:/x/1_a_1.jpg", ItemId: "1"));
            progress.Report(new EngineEvent("file-done", Path: "D:/x/1_a_1.jpg", Size: 100));
            progress.Report(new EngineEvent("file-skip", Path: "D:/x/2_b_1.jpg"));
            progress.Report(new EngineEvent("job-done", Total: 2, Skipped: 1, Failed: 0));
            return Task.CompletedTask;
        };

        var jobId = await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _queue.Active.Count == 0);

        var job = await _db.Jobs.Include(j => j.Files).SingleAsync();
        Assert.Equal(jobId, job.Id);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.DoneFiles);
        Assert.Equal(1, job.SkippedFiles);
        Assert.Equal(2, job.Files.Count);
        Assert.Contains(job.Files, f => f.Status == FileStatus.Downloaded && f.SourceItemId == "1");
        var (plan, cookies, _) = _engine.Downloads.Single();
        Assert.Equal(["https://x.com/alice/media"], plan.Urls);
        Assert.Equal(@"D:\dl", plan.BaseDirectory);
        Assert.EndsWith("cookies.txt", cookies);
        Assert.Equal([jobId], _stats.AppliedJobIds);
    }

    [Fact]
    public async Task Enqueue_permalink_runs_direct_url()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, ct) => await gate.Task.WaitAsync(ct);
        var jobId = await _queue.EnqueuePermalinkAsync(_account, "https://x.com/alice/status/9", "推文 9",
            ContentKind.Permalink, @"D:\dl", Opts());
        await WaitUntil(() => _queue.Active.Count == 1);
        Assert.Equal("推文 9", _queue.Active[0].Title);
        Assert.Null(_queue.Active[0].UserId);
        gate.SetResult();
        await WaitUntil(() => _queue.Active.Count == 0);
        Assert.Equal(jobId, _db.Jobs.Single().Id);
        Assert.Equal(TargetKind.Permalink, _db.Jobs.Single().TargetKind);
        Assert.Equal(["https://x.com/alice/status/9"], _engine.Downloads.Single().Plan.Urls);
    }

    [Fact]
    public async Task Enqueue_user_snapshot_includes_user_id()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, ct) => await gate.Task.WaitAsync(ct);
        await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _queue.Active.Count == 1);
        Assert.Equal(_alice.Id, _queue.Active[0].UserId);
        gate.SetResult();
        await WaitUntil(() => _queue.Active.Count == 0);
    }

    [Fact]
    public async Task Enqueue_multiple_users_returns_first_job_id()
    {
        // 审查 Important-1 回归覆盖：接口契约"每用户一个 DownloadJob 行，返回首个 jobId"
        var bob = new User
        { SiteId = "twitter", RestId = "2", ScreenName = "bob", Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Users.Add(bob);
        _db.SaveChanges();

        var first = await _queue.EnqueueUserMediaAsync(_account, [_alice, bob], @"D:\dl", Opts());
        await WaitUntil(() => _queue.Active.Count == 0);

        var firstJob = _db.Jobs.AsNoTracking().OrderBy(j => j.Id).First();
        Assert.Equal(firstJob.Id, first);
        Assert.Equal(_alice.Id, firstJob.UserId);
    }

    [Fact]
    public async Task Cancel_moves_job_to_canceled()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (plan, cookies, progress, ct) =>
        {
            progress.Report(new EngineEvent("url-start", Url: plan.Urls[0]));
            progress.Report(new EngineEvent("file-start", Path: "D:/x/1_a_1.jpg"));
            await gate.Task.WaitAsync(ct); // 适配：brief 原文 `await gate.Task` 永不被完成，取消后 OnDownload 永不返回、任务到不了 Canceled（已实证超时失败）；
                                           // 改为可被取消唤醒的等待，与真实引擎响应 CancellationToken 的行为一致
            ct.ThrowIfCancellationRequested();
        };
        var jobId = await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _db.Jobs.AsNoTracking().Single().Status == JobStatus.Running);
        await _queue.CancelAsync(jobId);
        await WaitUntil(() => _queue.Active.Count == 0);

        Assert.Equal(JobStatus.Canceled, (await _db.Jobs.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Auth_failure_fails_job_and_flags_account()
    {
        long? flagged = null;
        _queue.AccountInvalid += (id, _) => flagged = id;
        _engine.OnDownload = (_, _, _, _) => throw new AuthException("cookie 无效");

        await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _queue.Active.Count == 0);

        var job = await _db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("登录态", job.ErrorMessage);
        Assert.Equal(_account.Id, flagged);
        Assert.Equal(AccountStatus.Invalid, (await _db.Accounts.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Download_without_job_done_marks_job_failed()
    {
        // 审查 Important-1 回归覆盖（队列侧双保险）：引擎正常返回但未发 job-done → 不得标 Completed
        _engine.OnDownload = (plan, cookies, progress, _) =>
        {
            progress.Report(new EngineEvent("file-start", Path: "D:/x/1_a_1.jpg", ItemId: "1"));
            progress.Report(new EngineEvent("file-done", Path: "D:/x/1_a_1.jpg", Size: 100));
            return Task.CompletedTask;
        };

        await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _queue.Active.Count == 0);

        var job = await _db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("未收到 job-done", job.ErrorMessage);
    }

    [Fact]
    public async Task Unknown_site_fails_job_and_fires_job_removed()
    {
        // 审查 Important-2 回归覆盖：sites.Get 在内层 try 之前抛异常，兜底 catch 须把任务标 Failed
        var removed = new List<long>();
        _queue.JobRemoved += s => removed.Add(s.JobId);
        var account = new Account
        { SiteId = "nope", CookiePath = "nope\\c\\cookies.txt", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        var user = new User
        { SiteId = "nope", RestId = "9", ScreenName = "nope_user", Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Accounts.Add(account);
        _db.Users.Add(user);
        _db.SaveChanges();

        await _queue.EnqueueUserMediaAsync(account, [user], @"D:\dl", Opts());
        await WaitUntil(() => _queue.Active.Count == 0);

        var job = await _db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("未知站点", job.ErrorMessage);
        Assert.Contains(job.Id, removed);
        Assert.DoesNotContain(_engine.Downloads, d => d.Plan.SiteId == "nope"); // FakeEngine 未被调到
    }

    [Fact]
    public async Task Engine_failure_still_aggregates_stats()
    {
        // 审查 Important-4 回归覆盖（控制器裁定：终态一律聚合）：非 auth 引擎失败、已有 file-done 落库，
        // 任务 Failed 后统计聚合仍须执行，否则 DownloadCount 永久少计
        _engine.OnDownload = (plan, cookies, progress, _) =>
        {
            progress.Report(new EngineEvent("file-done", Path: "D:/x/1_a_1.jpg", Size: 100));
            throw new EngineException("boom");
        };

        var jobId = await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _queue.Active.Count == 0);

        var job = await _db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains(jobId, _stats.AppliedJobIds);
    }

    [Fact]
    public async Task RecoverOnStartup_marks_stale_jobs_failed()
    {
        _db.Jobs.Add(new DownloadJob
        { AccountId = _account.Id, TargetKind = TargetKind.UserMedia, UserId = _alice.Id, Status = JobStatus.Running, CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        await _queue.RecoverOnStartupAsync();
        Assert.All(_db.Jobs.AsNoTracking().ToList(), j => Assert.Equal(JobStatus.Failed, j.Status));
    }

    [Fact]
    public async Task Enqueue_pixiv_novels_uses_numeric_id_url()
    {
        var pixivAccount = new Account
        {
            SiteId = "pixiv", CookiePath = "pixiv\\c\\cookies.txt", RestId = "1",
            ScreenName = "me", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow,
        };
        var artist = new User
        {
            SiteId = "pixiv", RestId = "12345", ScreenName = "foo_bar", DisplayName = "Foo",
            Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Accounts.Add(pixivAccount);
        _db.Users.Add(artist);
        await _db.SaveChangesAsync();
        var queue = new DownloadQueueService(new SingleSiteDbContextFactory(_db), _engine, _stats, _paths,
            new SiteRegistry([new PixivSiteProvider()]),
            NullLogger<DownloadQueueService>.Instance);

        await queue.EnqueueUserContentAsync(pixivAccount, [artist], ContentKind.UserNovels, @"D:\dl",
            new Dictionary<string, object?>());
        await WaitUntil(() => queue.Active.Count == 0);

        var plan = _engine.Downloads.Last().Plan;
        Assert.Equal(["https://www.pixiv.net/users/12345/novels"], plan.Urls);
        var job = _db.Jobs.AsNoTracking().OrderByDescending(j => j.Id).First();
        Assert.Equal(TargetKind.UserNovels, job.TargetKind);
    }

    [Fact]
    public async Task Enqueue_pixiv_bookmarks_include_private()
    {
        var pixivAccount = new Account
        {
            SiteId = "pixiv", CookiePath = "pixiv\\c\\cookies.txt", RestId = "99",
            ScreenName = "me", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow,
        };
        _db.Accounts.Add(pixivAccount);
        await _db.SaveChangesAsync();
        var queue = new DownloadQueueService(new SingleSiteDbContextFactory(_db), _engine, _stats, _paths,
            new SiteRegistry([new PixivSiteProvider()]),
            NullLogger<DownloadQueueService>.Instance);

        await queue.EnqueueAccountContentAsync(pixivAccount, ContentKind.AccountBookmarks, @"D:\dl",
            new Dictionary<string, object?>());
        await WaitUntil(() => queue.Active.Count == 0);

        Assert.Equal(
        [
            "https://www.pixiv.net/users/99/bookmarks/artworks",
            "https://www.pixiv.net/users/99/bookmarks/artworks?rest=hide",
        ], _engine.Downloads.Last().Plan.Urls);
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 5000)
    {
        var start = Environment.TickCount;
        while (!cond())
        {
            if (Environment.TickCount - start > timeoutMs) throw new TimeoutException("条件等待超时");
            await Task.Delay(20);
        }
    }
}
