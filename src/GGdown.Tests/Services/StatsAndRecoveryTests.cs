using GGdown.Data;
using GGdown.Paths;
using GGdown.Services;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Services;

/// <summary>
/// Task 4：统计聚合按平台库隔离。真实 SiteDbContextFactory + 临时目录（twitter/pixiv 各一库）。
/// 恢复测试在 DownloadQueueServiceTests（Task 5 改造）。
/// </summary>
public class StatsAndRecoveryTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly SiteDbContextFactory _factory;
    private readonly User _alice;
    private readonly DownloadJob _job;

    public StatsAndRecoveryTests()
    {
        _factory = new SiteDbContextFactory(_paths);
        using var db = _factory.CreateAsync("twitter").GetAwaiter().GetResult();
        var account = new Account
        { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _alice = new User
        { SiteId = "twitter", RestId = "1", ScreenName = "alice", AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Accounts.Add(account);
        db.Users.Add(_alice);
        db.SaveChanges();
        _job = new DownloadJob
        { SiteId = "twitter", AccountId = account.Id, TargetKind = TargetKind.UserMedia, UserId = _alice.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
        db.Jobs.Add(_job);
        db.SaveChanges(); // 适配：SQLite 不在 Add 时分配临时键值（job.Id=0），先落 Job 取真实 Id 再挂 Files，否则 FK 约束失败
        db.Files.AddRange(
            F(_job.Id, FileStatus.Downloaded, _alice.Id, "1"), F(_job.Id, FileStatus.Downloaded, _alice.Id, "2"),
            F(_job.Id, FileStatus.Downloaded, _alice.Id, "3"), F(_job.Id, FileStatus.Skipped, _alice.Id, "4"),
            F(_job.Id, FileStatus.Downloaded, null, "L1"));  // likes 文件：UserId 为空，不计入
        db.SaveChanges();
        _alice = db.Users.AsNoTracking().Single(u => u.Id == _alice.Id);
        _job = db.Jobs.AsNoTracking().Single(j => j.Id == _job.Id);
    }
    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private static DownloadFile F(long jobId, FileStatus status, long? userId, string itemId) => new()
    { JobId = jobId, SiteId = "twitter", UserId = userId, SourceItemId = itemId, Url = "u", FilePath = "p", Status = status, CreatedAt = DateTime.UtcNow };

    private async Task<GGdownSiteDbContext> Twitter() => await _factory.CreateAsync("twitter");

    [Fact]
    public async Task Apply_counts_one_session_not_downloaded_files()
    {
        var stats = new StatsAggregator(_factory);

        await stats.ApplyJobCompletionAsync(_job.Id, "twitter");

        await using var db = await Twitter();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id); // 适配：ExecuteUpdate 绕过变更跟踪器，AsNoTracking 读库中真实状态
        Assert.Equal(1, user.DownloadCount);
        Assert.NotNull(user.LastDownloadAt);
    }

    [Fact]
    public async Task Apply_is_incremental_across_jobs()
    {
        var stats = new StatsAggregator(_factory);
        await stats.ApplyJobCompletionAsync(_job.Id, "twitter");

        await using (var db = await Twitter())
        {
            var job2 = new DownloadJob
            { SiteId = "twitter", AccountId = _job.AccountId, TargetKind = TargetKind.UserMedia, UserId = _alice.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
            db.Jobs.Add(job2);
            db.SaveChanges(); // 适配：同构造器，先落 job2 取真实 Id 再挂 File
            db.Files.Add(new DownloadFile
            { JobId = job2.Id, SiteId = "twitter", UserId = _alice.Id, SourceItemId = "9", Url = "u", FilePath = "p", Status = FileStatus.Downloaded, CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        var job2Id = await (await Twitter()).Jobs.AsNoTracking()
            .Where(j => j.Id != _job.Id).Select(j => j.Id).SingleAsync();
        await stats.ApplyJobCompletionAsync(job2Id, "twitter");
        await using var db2 = await Twitter();
        Assert.Equal(2, (await db2.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id)).DownloadCount); // 适配：同上
    }

    [Fact]
    public async Task Apply_ignores_jobs_without_user()
    {
        long likesId;
        await using (var db = await Twitter())
        {
            var likes = new DownloadJob
            { SiteId = "twitter", AccountId = _job.AccountId, TargetKind = TargetKind.AccountLikes, UserId = null, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
            db.Jobs.Add(likes);
            db.SaveChanges();
            likesId = likes.Id;
        }

        var stats = new StatsAggregator(_factory);
        await stats.ApplyJobCompletionAsync(likesId, "twitter");

        await using var db2 = await Twitter();
        Assert.Equal(0, (await db2.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id)).DownloadCount);
    }

    [Fact]
    public async Task Recalculate_replaces_file_counts_with_session_counts()
    {
        await using (var db = await Twitter())
        {
            var alice = db.Users.Single(u => u.Id == _alice.Id);
            alice.DownloadCount = 876;
            var job2 = new DownloadJob
            { SiteId = "twitter", AccountId = _job.AccountId, TargetKind = TargetKind.UserMedia, UserId = _alice.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
            db.Jobs.Add(job2);
            db.SaveChanges();
        }

        var stats = new StatsAggregator(_factory);
        await stats.RecalculateDownloadCountsAsync();

        await using var db2 = await Twitter();
        Assert.Equal(2, (await db2.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id)).DownloadCount);
    }

    // ---- Task 4 新增：跨库隔离 ----

    [Fact]
    public async Task Recalculate_restores_latest_success_date_and_excludes_failed_and_canceled_jobs()
    {
        var completedAt = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
        var interruptedAt = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);
        await using (var db = await Twitter())
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == _job.Id);
            job.CreatedAt = completedAt.AddDays(-2);
            job.StartedAt = completedAt.AddDays(-1);
            job.FinishedAt = completedAt;
            db.Jobs.AddRange(
                new DownloadJob { SiteId = "twitter", AccountId = _job.AccountId, UserId = _alice.Id,
                    Status = JobStatus.Failed, CreatedAt = interruptedAt.AddHours(-1), StartedAt = interruptedAt },
                new DownloadJob { SiteId = "twitter", AccountId = _job.AccountId, UserId = _alice.Id,
                    Status = JobStatus.Failed, CreatedAt = interruptedAt.AddDays(1) },
                new DownloadJob { SiteId = "twitter", AccountId = _job.AccountId, UserId = _alice.Id,
                    Status = JobStatus.Canceled, CreatedAt = interruptedAt.AddDays(2), FinishedAt = interruptedAt.AddDays(2) });
            await db.SaveChangesAsync();
        }

        var stats = new StatsAggregator(_factory);
        await stats.RecalculateDownloadCountsAsync();
        await stats.RecalculateDownloadCountsAsync(); // 重启重算不会重复累加。

        await using var result = await Twitter();
        var user = await result.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id);
        Assert.Equal(1, user.DownloadCount);
        Assert.Equal(completedAt, user.LastDownloadAt);
    }

    [Theory]
    [InlineData(JobStatus.Completed, 1L)]
    [InlineData(JobStatus.Failed, 0L)]
    [InlineData(JobStatus.Canceled, 0L)]
    [InlineData(JobStatus.Pending, 0L)]
    [InlineData(JobStatus.Running, 0L)]
    public async Task Recalculate_handles_missing_job_times_without_inventing_downloads(JobStatus status, long count)
    {
        var createdAt = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
        await using (var db = await Twitter())
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == _job.Id);
            job.Status = status;
            job.CreatedAt = createdAt;
            var user = await db.Users.SingleAsync(u => u.Id == _alice.Id);
            user.DownloadCount = 99;
            user.LastDownloadAt = createdAt.AddDays(-1);
            await db.SaveChangesAsync();
        }
        await new StatsAggregator(_factory).RecalculateDownloadCountsAsync();
        await using var result = await Twitter();
        var actual = await result.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id);
        Assert.Equal(count, actual.DownloadCount);
        Assert.Equal(count == 1 ? createdAt : (DateTime?)null, actual.LastDownloadAt);
    }

    [Fact]
    public async Task Apply_uses_persisted_completion_time()
    {
        var finishedAt = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
        await using (var db = await Twitter())
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == _job.Id);
            job.FinishedAt = finishedAt;
            await db.SaveChangesAsync();
        }
        await new StatsAggregator(_factory).ApplyJobCompletionAsync(_job.Id, "twitter");
        await using var result = await Twitter();
        var user = await result.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id);
        Assert.Equal(finishedAt, user.LastDownloadAt);
    }

    [Theory]
    [InlineData(JobStatus.Failed, false)]
    [InlineData(JobStatus.Failed, true)]
    [InlineData(JobStatus.Canceled, true)]
    [InlineData(JobStatus.Pending, false)]
    [InlineData(JobStatus.Running, true)]
    public async Task Apply_ignores_unsuccessful_jobs(JobStatus status, bool started)
    {
        await using (var db = await Twitter())
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == _job.Id);
            job.Status = status;
            job.StartedAt = started ? DateTime.UtcNow.AddMinutes(-1) : null;
            job.FinishedAt = started ? DateTime.UtcNow : null;
            await db.SaveChangesAsync();
        }
        await new StatsAggregator(_factory).ApplyJobCompletionAsync(_job.Id, "twitter");
        await using var result = await Twitter();
        var user = await result.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id);
        Assert.Equal(0, user.DownloadCount);
        Assert.Null(user.LastDownloadAt);
    }

    [Theory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Canceled)]
    public async Task Recalculate_clears_stale_statistics_when_only_unsuccessful_jobs_exist(JobStatus status)
    {
        await using (var db = await Twitter())
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == _job.Id);
            job.Status = status;
            job.StartedAt = DateTime.UtcNow.AddMinutes(-1);
            job.FinishedAt = DateTime.UtcNow;
            var user = await db.Users.SingleAsync(u => u.Id == _alice.Id);
            user.DownloadCount = 2;
            user.LastDownloadAt = job.FinishedAt;
            await db.SaveChangesAsync();
        }

        await new StatsAggregator(_factory).RecalculateDownloadCountsAsync();

        await using var result = await Twitter();
        var actual = await result.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id);
        Assert.Equal(0, actual.DownloadCount);
        Assert.Null(actual.LastDownloadAt);
    }

    [Fact]
    public async Task Apply_does_not_move_last_download_backwards_when_jobs_finish_concurrently()
    {
        var later = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);
        long laterJobId;
        await using (var db = await Twitter())
        {
            var earlierJob = await db.Jobs.SingleAsync(j => j.Id == _job.Id);
            earlierJob.FinishedAt = later.AddHours(-1);
            var laterJob = new DownloadJob { SiteId = "twitter", AccountId = _job.AccountId,
                UserId = _alice.Id, Status = JobStatus.Completed, CreatedAt = later, FinishedAt = later };
            db.Jobs.Add(laterJob);
            await db.SaveChangesAsync();
            laterJobId = laterJob.Id;
        }
        var stats = new StatsAggregator(_factory);
        await stats.ApplyJobCompletionAsync(laterJobId, "twitter");
        await stats.ApplyJobCompletionAsync(_job.Id, "twitter");
        await using var result = await Twitter();
        var user = await result.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id);
        Assert.Equal(2, user.DownloadCount);
        Assert.Equal(later, user.LastDownloadAt);
    }

    [Fact]
    public async Task Recalculate_covers_all_site_dbs()
    {
        // pixiv 库独立播种 user+job
        long pixivUserId;
        await using (var px = await _factory.CreateAsync("pixiv"))
        {
            var account = new Account
            { SiteId = "pixiv", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
            var user = new User
            { SiteId = "pixiv", RestId = "p1", ScreenName = "pico", DownloadCount = 999, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            px.Accounts.Add(account);
            px.Users.Add(user);
            px.SaveChanges();
            var job = new DownloadJob
            { SiteId = "pixiv", AccountId = account.Id, TargetKind = TargetKind.UserMedia, UserId = user.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
            px.Jobs.Add(job);
            px.SaveChanges();
            pixivUserId = user.Id;
        }

        var stats = new StatsAggregator(_factory);
        await stats.RecalculateDownloadCountsAsync();

        await using var tw = await Twitter();
        Assert.Equal(1, (await tw.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id)).DownloadCount);
        await using var px2 = await _factory.CreateAsync("pixiv");
        Assert.Equal(1, (await px2.Users.AsNoTracking().SingleAsync(u => u.Id == pixivUserId)).DownloadCount);
    }

    [Fact]
    public async Task ApplyJobCompletion_uses_site_of_job()
    {
        // twitter 的 job 完成聚合只加 twitter 库用户计数；pixiv 库用户不受影响
        long pixivUserId;
        await using (var px = await _factory.CreateAsync("pixiv"))
        {
            var user = new User
            { SiteId = "pixiv", RestId = "p1", ScreenName = "pico", AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            px.Users.Add(user);
            px.SaveChanges();
            pixivUserId = user.Id;
        }

        var stats = new StatsAggregator(_factory);
        await stats.ApplyJobCompletionAsync(_job.Id, "twitter");

        await using var px2 = await _factory.CreateAsync("pixiv");
        Assert.Equal(0, (await px2.Users.AsNoTracking().SingleAsync(u => u.Id == pixivUserId)).DownloadCount);
        await using var tw = await Twitter();
        Assert.Equal(1, (await tw.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id)).DownloadCount);
    }
}
