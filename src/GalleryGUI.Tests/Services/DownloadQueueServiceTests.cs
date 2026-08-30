using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using Microsoft.Data.Sqlite; // 适配：字段元组类型 SqliteConnection 需要（同 StatsAndRecoveryTests/UserServiceTests，brief 文件原文未含此 using）
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalleryGUI.Tests.Services;

public class DownloadQueueServiceTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly AppPaths _paths;
    private readonly FakeEngine _engine = new();
    private readonly FakeStats _stats = new();
    private readonly GalleryDbContext _db;
    private readonly Account _account;
    private readonly User _alice;
    private readonly DownloadQueueService _queue;

    public DownloadQueueServiceTests()
    {
        _t = TestDb.Create();
        _db = _t.Item2;
        _paths = TestPaths.Create();
        _account = new Account
        { SiteId = "twitter", CookiePath = "twitter\\c\\cookies.txt", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _alice = new User
        { SiteId = "twitter", RestId = "1", ScreenName = "alice", Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Accounts.Add(_account);
        _db.Users.Add(_alice);
        _db.SaveChanges();
        // 控制器裁定：实现签名为 IDbContextFactory<GalleryDbContext>，测试传 SingleDbContextFactory（Task 11 已落位 Helpers.cs，勿重复添加）
        _queue = new DownloadQueueService(new SingleDbContextFactory(_db), _engine, _stats, _paths,
            new SiteRegistry([new TwitterSiteProvider()]),
            NullLogger<DownloadQueueService>.Instance);
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private static IReadOnlyDictionary<string, object?> Opts() => new Dictionary<string, object?> { ["videos"] = true };

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
    public async Task RecoverOnStartup_marks_stale_jobs_failed()
    {
        _db.Jobs.Add(new DownloadJob
        { AccountId = _account.Id, TargetKind = TargetKind.UserMedia, UserId = _alice.Id, Status = JobStatus.Running, CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        await _queue.RecoverOnStartupAsync();
        Assert.All(_db.Jobs.AsNoTracking().ToList(), j => Assert.Equal(JobStatus.Failed, j.Status));
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
