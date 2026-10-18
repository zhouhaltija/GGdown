using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Sites;
using GGdown.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.Services;

public sealed class PartialFailureTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly SiteDbContextFactory _factory;
    private readonly Account _account;
    private readonly User _user;
    private readonly SiteRegistry _sites = new([new TwitterSiteProvider()]);

    public PartialFailureTests()
    {
        _factory = new(_paths);
        using var db = _factory.CreateAsync("twitter").GetAwaiter().GetResult();
        _account = new() { SiteId = "twitter", CookiePath = "fake", Status = AccountStatus.Ok, IsActive = true };
        _user = new() { SiteId = "twitter", RestId = "123", ScreenName = "alice", MediaCount = 20 };
        db.AddRange(_account, _user);
        db.SaveChanges();
    }

    public void Dispose() => Directory.Delete(_paths.Root, true);

    private DownloadQueueService Queue(FakeEngine engine) => new(_factory, engine,
        new StatsAggregator(_factory), _paths, _sites, NullLogger<DownloadQueueService>.Instance);

    private async Task<JobSnapshot> Run(DownloadQueueService queue)
    {
        var removed = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.JobRemoved += snapshot => removed.TrySetResult(snapshot);
        await queue.EnqueueUserMediaAsync(_account, [_user], _paths.TempDir, new Dictionary<string, object?>());
        return await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(1, 0, 1, 5)]
    [InlineData(0, 1, 1, 5)]
    [InlineData(1, 1, 2, 5)]
    [InlineData(0, 0, 2, 3)]
    [InlineData(0, 0, 0, 2)]
    [InlineData(0, 2, 0, 2)]
    public async Task Final_file_results_distinguish_partial_and_total_failure(int done, int skipped, int failed, int expected)
    {
        var engine = new FakeEngine { OnDownload = (_, _, progress, _) =>
        {
            for (var i = 0; i < done; i++) progress.Report(new("file-done", Path: $"done-{i}.jpg"));
            for (var i = 0; i < skipped; i++) progress.Report(new("file-skip", Path: $"skip-{i}.jpg"));
            for (var i = 0; i < failed; i++) progress.Report(new("file-failed", Path: $"failed-{i}.jpg"));
            progress.Report(new("job-done", Total: done + skipped + failed, Skipped: skipped, Failed: failed));
            return Task.CompletedTask;
        }};
        var queue = Queue(engine);
        var final = await Run(queue);

        await using var db = await _factory.CreateAsync("twitter");
        var job = await db.Jobs.SingleAsync();
        Assert.Equal((JobStatus)expected, job.Status);
        Assert.Equal(job.Status, final.Status);
        Assert.Equal(done, final.Done);
        Assert.Equal(skipped, final.Skipped);
        Assert.Equal(failed, final.Failed);
        Assert.Equal(done + skipped + failed, job.TotalFiles);
        Assert.Equal(done + skipped + failed, await db.Files.CountAsync());
        Assert.Null(job.RecoveryData);
        Assert.False(job.StatisticsPending);
        Assert.Empty(queue.Active);
        if (failed > 0) Assert.Contains($"{failed}", final.Error);
        var user = await db.Users.SingleAsync();
        Assert.Equal(expected == 2 ? 1 : 0, user.DownloadCount);
        Assert.Equal(expected == 2 ? job.FinishedAt : null, user.LastDownloadAt);
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("fatal")]
    [InlineData("missing-final")]
    public async Task Fatal_or_missing_final_stays_failed_even_after_some_files_succeed(string outcome)
    {
        var engine = new FakeEngine { OnDownload = (_, _, progress, _) =>
        {
            progress.Report(new("file-done", Path: "done.jpg"));
            progress.Report(new("file-failed", Path: "failed.jpg"));
            if (outcome != "missing-final")
                progress.Report(new("job-done", Total: 2, Skipped: 0, Failed: 1));
            if (outcome == "auth") throw new AuthException("登录态失效");
            if (outcome == "fatal") throw new EngineException("进程非零退出");
            return Task.CompletedTask;
        }};
        var final = await Run(Queue(engine));
        Assert.Equal(JobStatus.Failed, final.Status);
        Assert.Equal(1, final.Done);
        Assert.Equal(1, final.Failed);
        await using var db = await _factory.CreateAsync("twitter");
        Assert.Equal(0, (await db.Users.SingleAsync()).DownloadCount);
    }

    [Fact]
    public async Task Startup_recovery_keeps_partial_failure_terminal_and_does_not_queue_it_again()
    {
        await using (var db = await _factory.CreateAsync("twitter"))
        {
            db.Jobs.Add(new() { SiteId = "twitter", AccountId = _account.Id, UserId = _user.Id,
                Status = JobStatus.PartialFailed, DoneFiles = 1, FailedFiles = 1, TotalFiles = 2,
                ErrorMessage = "一个文件失败", FinishedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var queue = Queue(new FakeEngine());
        await queue.RecoverOnStartupAsync();
        Assert.Equal(0, await queue.GetInterruptedCountAsync());
        Assert.Equal(new QueueRecoveryResult(0, 0), await queue.ResumeInterruptedAsync());
        Assert.Empty(queue.Active);
        await using var result = await _factory.CreateAsync("twitter");
        var job = await result.Jobs.SingleAsync();
        Assert.Equal(JobStatus.PartialFailed, job.Status);
        Assert.Equal("一个文件失败", job.ErrorMessage);
    }

    [Fact]
    public void Partial_failure_card_shows_terminal_state_error_and_failed_file_count()
    {
        var card = new JobCardViewModel(new(1, "twitter", TargetKind.UserMedia, "alice", JobStatus.PartialFailed,
            2, 3, 4, 9, null, "四个文件失败"), _ => Task.CompletedTask);

        Assert.Equal("部分失败", card.StatusText);
        Assert.True(card.IsFinished);
        Assert.True(card.IsFailed);
        Assert.True(card.HasError);
        Assert.Equal("四个文件失败", card.ErrorText);
        Assert.Contains("失败 4", card.ProgressText);
        Assert.Equal(100, card.ProgressPercent);
    }

    [Fact]
    public async Task Partial_failure_remains_in_recent_finished_when_switching_global_and_site_views()
    {
        await using (var pixiv = await _factory.CreateAsync("pixiv")) { }
        var engine = new FakeEngine { OnDownload = (_, _, progress, _) =>
        {
            progress.Report(new("job-done", Total: 3, Skipped: 1, Failed: 1));
            return Task.CompletedTask;
        }};
        var queue = Queue(engine);
        var current = new FakeCurrentSite();
        var vm = new DownloadsViewModel(queue, new AccountQueryService(_factory), new FakeAppSettings(),
            new SyncDispatcher(), current, _sites);
        vm.Start();
        try
        {
            var final = await Run(queue);
            Assert.Empty(vm.Jobs);
            Assert.False(vm.HasActive);
            Assert.Equal("部分失败", Assert.Single(vm.Finished).StatusText);
            await current.SelectAsync("pixiv");
            Assert.Empty(vm.Finished);
            vm.SetGlobalView(true);
            Assert.Equal(final.JobId, Assert.Single(vm.Finished).JobId);
            Assert.True(vm.HasFinished);
            Assert.False(vm.ShowEmptyState);
        }
        finally { vm.Stop(); }
    }

    [Fact]
    public async Task Partial_failure_preserves_previous_success_statistics_including_after_rebuild()
    {
        var previous = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        await using (var db = await _factory.CreateAsync("twitter"))
        {
            db.Jobs.Add(new() { SiteId = "twitter", AccountId = _account.Id, UserId = _user.Id,
                Status = JobStatus.Completed, CreatedAt = previous, FinishedAt = previous, MediaCountSnapshot = 10 });
            await db.SaveChangesAsync();
        }
        var stats = new StatsAggregator(_factory);
        await stats.RecalculateDownloadCountsAsync();
        var engine = new FakeEngine { OnDownload = (_, _, progress, _) =>
        {
            progress.Report(new("job-done", Total: 2, Skipped: 0, Failed: 1));
            return Task.CompletedTask;
        }};
        await Run(Queue(engine));

        await using (var db = await _factory.CreateAsync("twitter"))
        {
            var user = await db.Users.SingleAsync();
            Assert.Equal(1, user.DownloadCount);
            Assert.Equal(previous, user.LastDownloadAt);
            Assert.Equal(10, user.MediaCountAtDownload);
        }
        await stats.RecalculateDownloadCountsAsync();
        await using var rebuilt = await _factory.CreateAsync("twitter");
        var actual = await rebuilt.Users.SingleAsync();
        Assert.Equal(1, actual.DownloadCount);
        Assert.Equal(previous, actual.LastDownloadAt);
        Assert.Equal(10, actual.MediaCountAtDownload);
    }
}
