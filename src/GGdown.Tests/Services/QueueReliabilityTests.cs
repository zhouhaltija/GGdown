using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.Services;

public sealed class QueueReliabilityTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly SiteDbContextFactory _factory;
    private readonly Account _account;
    private readonly User _user;

    public QueueReliabilityTests()
    {
        _factory = new(_paths);
        using var db = _factory.CreateAsync("twitter").GetAwaiter().GetResult();
        _account = new() { SiteId = "twitter", CookiePath = "fake", IsActive = true, Status = AccountStatus.Ok };
        _user = new() { SiteId = "twitter", RestId = "123", ScreenName = "alice" };
        db.AddRange(_account, _user);
        db.SaveChanges();
    }

    public void Dispose() => TestPaths.Delete(_paths);

    private DownloadQueueService Queue(FakeEngine engine, StatsAggregator? stats = null) =>
        new(_factory, engine, stats ?? new StatsAggregator(_factory), _paths,
            new SiteRegistry([new TwitterSiteProvider()]), NullLogger<DownloadQueueService>.Instance);

    private Task<long> Enqueue(DownloadQueueService queue, int count = 1) =>
        queue.EnqueueUserMediaAsync(_account, Enumerable.Repeat(_user, count).ToArray(), _paths.TempDir,
            new Dictionary<string, object?>());

    private static FakeEngine Successful() => new() { OnDownload = (_, _, progress, _) =>
    {
        progress.Report(new("job-done", Total: 0, Skipped: 0, Failed: 0));
        return Task.CompletedTask;
    }};

    /// <summary>
    /// 等待队列清空。CI 磁盘慢时 2500 条文件记录落库可能远超 10 秒，这里给足余量；
    /// 真出问题时只是多等一会儿，不会把慢当成失败。
    /// </summary>
    private static async Task Drain(DownloadQueueService queue, int seconds = 60)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (queue.Active.Count != 0) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task Statistics_failure_does_not_reclassify_successful_download()
    {
        var queue = Queue(Successful(), new ThrowOnceStats());
        await Enqueue(queue);
        await Drain(queue);
        await using var db = await _factory.CreateAsync("twitter");
        var job = await db.Jobs.SingleAsync();
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Null(job.ErrorMessage);
        Assert.True(job.StatisticsPending);
        await new StatsAggregator(_factory).ApplyJobCompletionAsync(job.Id, "twitter");
        await db.Entry(job).ReloadAsync();
        Assert.False(job.StatisticsPending);
        Assert.Equal(1, (await db.Users.SingleAsync()).DownloadCount);
    }

    [Fact]
    public async Task Broken_observer_does_not_prevent_enqueue_or_other_observers()
    {
        var queue = Queue(Successful());
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.JobChanged += _ => throw new InvalidOperationException("观察者故障");
        queue.JobRemoved += _ => observed.TrySetResult();
        await Enqueue(queue);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(queue.Active);
    }

    [Fact]
    public async Task Terminal_changed_notification_is_published_once()
    {
        var queue = Queue(Successful());
        int terminals = 0;
        queue.JobChanged += s => { if (s.Status == JobStatus.Completed) Interlocked.Increment(ref terminals); };
        await Enqueue(queue);
        await Drain(queue);
        Assert.Equal(1, terminals);
    }

    [Fact]
    public async Task Failed_file_insert_is_visible_and_keeps_recovery_request()
    {
        await using (var db = await _factory.CreateAsync("twitter"))
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_files BEFORE INSERT ON Files BEGIN SELECT RAISE(ABORT, 'injected file write failure'); END;");
        var engine = new FakeEngine { OnDownload = (_, _, progress, _) =>
        {
            progress.Report(new("file-done", Path: "test.jpg", ItemId: "1"));
            progress.Report(new("job-done", Total: 1, Skipped: 0, Failed: 0));
            return Task.CompletedTask;
        }};
        var queue = Queue(engine);
        JobSnapshot? removed = null;
        queue.JobRemoved += s => removed = s;
        await Enqueue(queue);
        await Drain(queue);
        await using var result = await _factory.CreateAsync("twitter");
        var job = await result.Jobs.SingleAsync();
        Assert.NotEqual(JobStatus.Completed, job.Status);
        Assert.NotNull(job.RecoveryData);
        Assert.NotNull(removed?.Error);
    }

    [Fact]
    public async Task Raising_concurrency_starts_waiting_jobs_without_new_enqueue()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        var engine = new FakeEngine { OnDownload = async (_, _, progress, _) =>
        {
            var count = Interlocked.Increment(ref started);
            first.TrySetResult();
            if (count == 3) all.TrySetResult();
            await release.Task;
            progress.Report(new("job-done", Total: 0, Skipped: 0, Failed: 0));
        }};
        var queue = Queue(engine);
        try
        {
            await Enqueue(queue, 3);
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Concurrency = 3;
            await all.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(3, started);
        }
        finally { release.TrySetResult(); await Drain(queue); }
    }

    [Fact]
    public async Task Burst_progress_is_coalesced_without_losing_the_final_snapshot()
    {
        var engine = new FakeEngine { OnDownload = (_, _, progress, _) =>
        {
            for (var i = 0; i < 10000; i++) progress.Report(new("file-start", Path: $"{i}.jpg"));
            progress.Report(new("job-done", Total: 0, Skipped: 0, Failed: 0));
            return Task.CompletedTask;
        }};
        var queue = Queue(engine);
        var updates = 0;
        JobSnapshot? final = null;
        queue.JobChanged += s => { if (s.Status == JobStatus.Running) Interlocked.Increment(ref updates); };
        queue.JobRemoved += s => final = s;
        await Enqueue(queue);
        await Drain(queue);
        Assert.InRange(updates, 1, 20);
        Assert.Equal("9999.jpg", final?.CurrentFile);
        Assert.Equal(JobStatus.Completed, final?.Status);
    }

    [Fact]
    public async Task Large_file_stream_is_fully_persisted_before_completion()
    {
        var engine = new FakeEngine { OnDownload = (_, _, progress, _) =>
        {
            for (var i = 0; i < 2500; i++)
                progress.Report(new("file-done", Path: $"{i}.jpg", ItemId: i.ToString()));
            progress.Report(new("job-done", Total: 2500, Skipped: 0, Failed: 0));
            return Task.CompletedTask;
        }};
        var queue = Queue(engine);
        await Enqueue(queue);
        await Drain(queue);
        await using var db = await _factory.CreateAsync("twitter");
        Assert.Equal(JobStatus.Completed, (await db.Jobs.SingleAsync()).Status);
        Assert.Equal(2500, await db.Files.CountAsync());
        Assert.Equal(2500, await db.Files.Select(f => f.EventSequence).Distinct().CountAsync());
    }

    [Fact]
    public async Task Lowering_concurrency_finishes_current_jobs_before_starting_another()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOthers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedThree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRemoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        var engine = new FakeEngine { OnDownload = async (_, _, progress, _) =>
        {
            var ordinal = Interlocked.Increment(ref started);
            if (ordinal == 3) startedThree.TrySetResult();
            await (ordinal == 1 ? releaseFirst.Task : releaseOthers.Task);
            progress.Report(new("job-done", Total: 0, Skipped: 0, Failed: 0));
        }};
        var queue = Queue(engine);
        queue.Concurrency = 3;
        queue.JobRemoved += _ => firstRemoved.TrySetResult();
        try
        {
            await Enqueue(queue, 4);
            await startedThree.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Concurrency = 1;
            releaseFirst.TrySetResult();
            await firstRemoved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 当前仍有两个执行者，第四个必须保持排队，不能因一个执行者结束就补位。
            Assert.Equal(3, Volatile.Read(ref started));
            Assert.Single(queue.Active, j => j.Status == JobStatus.Pending);
        }
        finally { releaseFirst.TrySetResult(); releaseOthers.TrySetResult(); await Drain(queue); }
    }

    [Fact]
    public async Task Unsupported_recovery_version_is_not_discarded()
    {
        await File.WriteAllTextAsync(AccountService.AbsoluteCookiePath(_paths, _account), "fake");
        const string request = "{\"SchemaVersion\":999,\"Request\":{}}";
        await using (var db = await _factory.CreateAsync("twitter"))
        {
            db.Jobs.Add(new DownloadJob { SiteId = "twitter", AccountId = _account.Id,
                Status = JobStatus.Failed, RecoveryData = request });
            await db.SaveChangesAsync();
        }
        var result = await Queue(Successful()).ResumeInterruptedAsync();
        Assert.Equal(0, result.Resumed);
        Assert.Equal(1, result.Unavailable);
        await using var final = await _factory.CreateAsync("twitter");
        var job = await final.Jobs.SingleAsync();
        Assert.Equal(request, job.RecoveryData);
        Assert.Contains("版本", job.ErrorMessage);
    }

    [Fact]
    public async Task Legacy_unversioned_recovery_requests_remain_readable()
    {
        await File.WriteAllTextAsync(AccountService.AbsoluteCookiePath(_paths, _account), "fake");
        var legacy = System.Text.Json.JsonSerializer.Serialize(new
        {
            JobId = 0, AccountId = _account.Id, SiteId = "twitter", Kind = TargetKind.UserMedia,
            UserId = _user.Id, TargetScreenName = "alice", TargetRestId = "123", Title = "alice",
            BaseDirectory = _paths.TempDir, SiteOptions = new Dictionary<string, object?>(),
            CookieAbsolutePath = AccountService.AbsoluteCookiePath(_paths, _account),
            ArchiveFile = DownloadQueueService.ArchivePath(_paths, _account),
        });
        await using (var db = await _factory.CreateAsync("twitter"))
        {
            db.Jobs.Add(new DownloadJob { SiteId = "twitter", AccountId = _account.Id, UserId = _user.Id,
                TargetKind = TargetKind.UserMedia, Status = JobStatus.Failed, RecoveryData = legacy });
            await db.SaveChangesAsync();
        }
        var queue = Queue(Successful());
        Assert.Equal(1, (await queue.ResumeInterruptedAsync()).Resumed);
        await Drain(queue);
        await using var result = await _factory.CreateAsync("twitter");
        Assert.Equal(1, await result.Jobs.CountAsync(j => j.Status == JobStatus.Completed));
        Assert.Equal(0, await queue.GetInterruptedCountAsync());
    }

    [Fact]
    public async Task New_requests_include_an_explicit_recovery_schema_version()
    {
        var engine = new FakeEngine { OnDownload = (_, _, _, _) => throw new AuthException("expired") };
        var queue = Queue(engine);
        await Enqueue(queue);
        await Drain(queue);
        await using var db = await _factory.CreateAsync("twitter");
        using var json = System.Text.Json.JsonDocument.Parse((await db.Jobs.SingleAsync()).RecoveryData!);
        Assert.Equal(1, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal("UserMedia", json.RootElement.GetProperty("Request").GetProperty("ContentType").GetString());
    }

    [Fact]
    public async Task Applying_same_completed_job_twice_does_not_double_count()
    {
        long id;
        await using (var db = await _factory.CreateAsync("twitter"))
        {
            var job = new DownloadJob { SiteId = "twitter", AccountId = _account.Id, UserId = _user.Id,
                Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            id = job.Id;
        }
        var stats = new StatsAggregator(_factory);
        await stats.ApplyJobCompletionAsync(id, "twitter");
        await stats.ApplyJobCompletionAsync(id, "twitter");
        await using var result = await _factory.CreateAsync("twitter");
        Assert.Equal(1, (await result.Users.SingleAsync()).DownloadCount);
    }

    [Fact]
    public async Task Cross_site_users_are_rejected_before_any_job_is_created()
    {
        var queue = Queue(Successful());
        var wrong = new User { Id = _user.Id, SiteId = "pixiv", RestId = "123", ScreenName = "other" };
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueUserMediaAsync(_account,
                [wrong], _paths.TempDir, new Dictionary<string, object?>()));
        }
        finally { await Drain(queue); }
        await using var db = await _factory.CreateAsync("twitter");
        Assert.Empty(await db.Jobs.ToListAsync());
    }

    private sealed class ThrowOnceStats : StatsAggregator
    {
        private int _calls;
        public ThrowOnceStats() : base(null!) { }
        public override Task ApplyJobCompletionAsync(long jobId, string siteId, CancellationToken ct = default) =>
            Interlocked.Increment(ref _calls) == 1
                ? Task.FromException(new InvalidOperationException("统计故障")) : Task.CompletedTask;
    }
}
