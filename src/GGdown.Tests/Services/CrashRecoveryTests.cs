using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Sites;
using GGdown.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.Services;

public sealed class CrashRecoveryTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly SiteDbContextFactory _factory;

    public CrashRecoveryTests() => _factory = new SiteDbContextFactory(_paths);

    public void Dispose() => Directory.Delete(_paths.Root, true);

    private DownloadQueueService Queue(FakeEngine? engine = null) => new(_factory,
        engine ?? new FakeEngine(), new StatsAggregator(_factory), _paths,
        new SiteRegistry([new TwitterSiteProvider(), new PixivSiteProvider()]),
        NullLogger<DownloadQueueService>.Instance);

    private async Task<(long JobId, Account Account, DownloadPlan Plan)> InterruptAsync(
        TargetKind kind = TargetKind.Permalink, string siteId = "twitter")
    {
        Account account;
        User user;
        await using (var db = await _factory.CreateAsync(siteId))
        {
            account = new Account { SiteId = siteId, CookiePath = siteId + "/cookies.txt", Status = AccountStatus.Ok,
                IsActive = true, RestId = "99", ScreenName = "owner" };
            user = new User { SiteId = siteId, RestId = "123", ScreenName = "alice",
                ContentSelection = UserContentSelection.PixivArtworks, DownloadSince = new DateOnly(2025, 1, 2) };
            db.Accounts.Add(account);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        var cookie = AccountService.AbsoluteCookiePath(_paths, account);
        Directory.CreateDirectory(Path.GetDirectoryName(cookie)!);
        await File.WriteAllTextAsync(cookie, "fictional-cookie");
        string? request = null;
        DownloadPlan? original = null;
        var engine = new FakeEngine
        {
            OnDownload = async (plan, _, progress, _) =>
            {
                original = plan;
                await using var db = await _factory.CreateAsync(siteId);
                request = (await db.Jobs.SingleAsync(j => j.Status == JobStatus.Running)).RecoveryData;
                progress.Report(new EngineEvent("job-done", Total: 0, Skipped: 0, Failed: 0));
            },
        };
        var queue = Queue(engine);
        var options = new Dictionary<string, object?> { ["videos"] = false, ["size"] = "small" };
        var id = kind switch
        {
            TargetKind.UserMedia => await queue.EnqueueUserMediaAsync(account, [user], _paths.Root, options),
            TargetKind.AccountLikes => await queue.EnqueueAccountContentAsync(account, ContentKind.AccountLikes, _paths.Root, options),
            _ => await queue.EnqueuePermalinkAsync(account, "https://x.com/alice/status/456", "测试链接",
                ContentKind.Permalink, _paths.Root, options),
        };
        await WaitAsync(queue);
        Assert.NotNull(request);
        await using (var db = await _factory.CreateAsync(siteId))
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == id);
            Assert.Null(job.RecoveryData); // 正常终态不进入下次恢复。
            job.Status = JobStatus.Running;
            job.FinishedAt = null;
            job.RecoveryData = request;
            await db.SaveChangesAsync();
        }
        return (id, account, original!);
    }

    private static async Task WaitAsync(DownloadQueueService queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (queue.Active.Count > 0) await Task.Delay(10, timeout.Token);
    }

    [Theory]
    [InlineData(TargetKind.UserMedia)]
    [InlineData(TargetKind.AccountLikes)]
    [InlineData(TargetKind.Permalink)]
    public async Task Restart_preserves_request_and_resumes_only_once_after_confirmation(TargetKind kind)
    {
        var interrupted = await InterruptAsync(kind);
        var engine = new FakeEngine();
        var queue = Queue(engine);
        await queue.RecoverOnStartupAsync();
        Assert.Equal(1, await queue.GetInterruptedCountAsync());
        Assert.Empty(engine.Downloads);
        // 暂不恢复后再次重启，请求仍然可用。
        queue = Queue(engine);
        await queue.RecoverOnStartupAsync();
        var result = await queue.ResumeInterruptedAsync();
        Assert.Equal(1, result.Resumed);
        Assert.Equal(0, result.Unavailable);
        await WaitAsync(queue);
        var actual = Assert.Single(engine.Downloads).Plan;
        Assert.Equal(interrupted.Plan.Urls, actual.Urls);
        Assert.Equal(interrupted.Plan.BaseDirectory, actual.BaseDirectory);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(interrupted.Plan.Options),
            System.Text.Json.JsonSerializer.Serialize(actual.Options));
        Assert.Equal(0, (await queue.ResumeInterruptedAsync()).Resumed);
        await using var db = await _factory.CreateAsync("twitter");
        Assert.Equal(2, await db.Jobs.CountAsync());
        Assert.Null((await db.Jobs.SingleAsync(j => j.Id == interrupted.JobId)).RecoveryData);
    }

    [Fact]
    public async Task Discard_preserves_download_history_but_does_not_offer_it_again()
    {
        await InterruptAsync();
        var queue = Queue();
        await queue.RecoverOnStartupAsync();
        await queue.DiscardInterruptedAsync();
        Assert.Equal(0, await Queue().GetInterruptedCountAsync());
        await using var db = await _factory.CreateAsync("twitter");
        Assert.Equal(JobStatus.Failed, (await db.Jobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Missing_credentials_keep_request_for_later_without_starting_engine()
    {
        var interrupted = await InterruptAsync();
        File.Delete(AccountService.AbsoluteCookiePath(_paths, interrupted.Account));
        var engine = new FakeEngine();
        var queue = Queue(engine);
        await queue.RecoverOnStartupAsync();
        var result = await queue.ResumeInterruptedAsync();
        Assert.Equal(0, result.Resumed);
        Assert.Equal(1, result.Unavailable);
        Assert.Empty(engine.Downloads);
        Assert.Equal(1, await Queue().GetInterruptedCountAsync());
    }

    [Fact]
    public async Task Recovery_keeps_jobs_with_the_same_id_in_different_sites_separate()
    {
        await InterruptAsync();
        await InterruptAsync(TargetKind.UserMedia, "pixiv");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine { OnDownload = async (_, _, progress, ct) =>
        {
            await release.Task.WaitAsync(ct);
            progress.Report(new EngineEvent("job-done", Total: 0, Skipped: 0, Failed: 0));
        } };
        var queue = Queue(engine);
        try
        {
            await queue.RecoverOnStartupAsync();
            Assert.Equal(2, (await queue.ResumeInterruptedAsync()).Resumed);
            Assert.Equal(2, queue.Active.Count);
            Assert.Contains(queue.Active, s => s.SiteId == "twitter");
            Assert.Contains(queue.Active, s => s.SiteId == "pixiv");
        }
        finally
        {
            release.TrySetResult();
            await WaitAsync(queue);
        }
    }

    [Fact]
    public async Task Corrupt_request_does_not_block_other_interrupted_jobs()
    {
        var interrupted = await InterruptAsync();
        await using (var db = await _factory.CreateAsync("twitter"))
        {
            db.Jobs.Add(new DownloadJob { SiteId = "twitter", AccountId = interrupted.Account.Id,
                Status = JobStatus.Running, RecoveryData = "{broken" });
            await db.SaveChangesAsync();
        }
        var queue = Queue();
        await queue.RecoverOnStartupAsync();
        var result = await queue.ResumeInterruptedAsync();
        await WaitAsync(queue);
        Assert.Equal(1, result.Resumed);
        Assert.Equal(1, result.Unavailable);
        Assert.Equal(0, await queue.GetInterruptedCountAsync());
        await using var check = await _factory.CreateAsync("twitter");
        Assert.Contains(await check.Jobs.ToListAsync(), j => j.ErrorMessage != null && j.ErrorMessage.Contains("恢复参数损坏"));
    }

    [Fact]
    public async Task Simultaneous_resume_requests_do_not_duplicate_jobs()
    {
        await InterruptAsync();
        var queue = Queue();
        await queue.RecoverOnStartupAsync();
        var results = await Task.WhenAll(Task.Run(() => queue.ResumeInterruptedAsync()),
            Task.Run(() => queue.ResumeInterruptedAsync()));
        await WaitAsync(queue);
        Assert.Equal(1, results.Sum(r => r.Resumed));
        await using var db = await _factory.CreateAsync("twitter");
        Assert.Equal(2, await db.Jobs.CountAsync());
    }

    [Fact]
    public async Task Expired_cookie_discovered_by_engine_preserves_request_until_reimport()
    {
        var interrupted = await InterruptAsync();
        var engine = new FakeEngine { OnDownload = (_, _, _, _) => throw new AuthException("Cookie 已过期") };
        var queue = Queue(engine);
        await queue.RecoverOnStartupAsync();
        Assert.Equal(1, (await queue.ResumeInterruptedAsync()).Resumed);
        await WaitAsync(queue);
        Assert.Equal(1, await Queue().GetInterruptedCountAsync());
        await using (var db = await _factory.CreateAsync("twitter"))
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == interrupted.Account.Id);
            Assert.Equal(AccountStatus.Invalid, account.Status);
            account.Status = AccountStatus.Ok; // 模拟重新导入并验证凭据。
            await db.SaveChangesAsync();
        }
        var retry = Queue();
        Assert.Equal(1, (await retry.ResumeInterruptedAsync()).Resumed);
        await WaitAsync(retry);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reimport_uses_same_accounts_new_credentials_and_keeps_original_archive(bool sameAccount)
    {
        var interrupted = await InterruptAsync();
        await using (var db = await _factory.CreateAsync("twitter"))
        {
            (await db.Accounts.SingleAsync()).Status = AccountStatus.Invalid;
            await db.SaveChangesAsync();
        }
        var importEngine = new FakeEngine { WhoAmI = new AccountInfo("owner", "Owner", sameAccount ? "99" : "other") };
        var accounts = new AccountService(_factory, importEngine, _paths,
            new SiteRegistry([new TwitterSiteProvider()]), NullLogger<AccountService>.Instance);
        var imported = await accounts.ImportCookiesAsync("twitter", AccountService.AbsoluteCookiePath(_paths, interrupted.Account));
        Assert.True(imported.Ok);
        Assert.NotEqual(interrupted.Account.Id, imported.Account.Id);
        var engine = new FakeEngine();
        var queue = Queue(engine);
        await queue.RecoverOnStartupAsync();
        var result = await queue.ResumeInterruptedAsync();
        await WaitAsync(queue);
        if (!sameAccount)
        {
            Assert.Equal(0, result.Resumed);
            Assert.Equal(1, result.Unavailable);
            Assert.Empty(engine.Downloads);
            return;
        }
        Assert.Equal(1, result.Resumed);
        var download = Assert.Single(engine.Downloads);
        Assert.Equal(AccountService.AbsoluteCookiePath(_paths, imported.Account), download.CookiesFile);
        using var options = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(download.Plan.Options));
        Assert.Equal(DownloadQueueService.ArchivePath(_paths, interrupted.Account),
            options.RootElement.GetProperty("extractor").GetProperty("twitter").GetProperty("archive").GetString());
        await using var check = await _factory.CreateAsync("twitter");
        Assert.Equal(imported.Account.Id, (await check.Jobs.OrderByDescending(j => j.Id).FirstAsync()).AccountId);
    }

    [Fact]
    public async Task Account_likes_recovery_uses_new_username_for_the_same_account_identity()
    {
        var interrupted = await InterruptAsync(TargetKind.AccountLikes);
        var importEngine = new FakeEngine { WhoAmI = new AccountInfo("renamed", "Owner", "99") };
        var accounts = new AccountService(_factory, importEngine, _paths,
            new SiteRegistry([new TwitterSiteProvider()]), NullLogger<AccountService>.Instance);
        Assert.True((await accounts.ImportCookiesAsync("twitter",
            AccountService.AbsoluteCookiePath(_paths, interrupted.Account))).Ok);
        var engine = new FakeEngine();
        var queue = Queue(engine);
        await queue.RecoverOnStartupAsync();
        Assert.Equal(1, (await queue.ResumeInterruptedAsync()).Resumed);
        await WaitAsync(queue);
        Assert.Equal("https://x.com/renamed/likes", Assert.Single(Assert.Single(engine.Downloads).Plan.Urls));
    }

    [Fact]
    public async Task Recovering_another_site_does_not_mark_same_id_user_as_downloading()
    {
        await using (var db = await _factory.CreateAsync("twitter"))
        {
            db.Users.Add(new User { SiteId = "twitter", RestId = "1", ScreenName = "alice" });
            await db.SaveChangesAsync();
        }
        await InterruptAsync(TargetKind.UserMedia, "pixiv");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine { OnDownload = (_, _, _, ct) => release.Task.WaitAsync(ct) };
        var queue = Queue(engine);
        try
        {
            await queue.RecoverOnStartupAsync();
            await queue.ResumeInterruptedAsync();
            var sites = new SiteRegistry([new TwitterSiteProvider(), new PixivSiteProvider()]);
            var vm = new UsersViewModel(new UserQueryService(_factory), new AccountQueryService(_factory),
                new UserService(_factory, engine, _paths, sites, NullLogger<UserService>.Instance), queue,
                new FakeAppSettings(), new SyncDispatcher(), sites, new FakeCurrentSite(), new HistoryQueryService(_factory));
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.False(Assert.Single(vm.Users).IsDownloading);
        }
        finally
        {
            release.TrySetResult();
            await WaitAsync(queue);
        }
    }

    [Fact]
    public async Task Recovery_rebuilds_interrupted_progress_from_durable_file_records()
    {
        await using (var db = await _factory.CreateAsync("twitter"))
        {
            var account = new Account { SiteId = "twitter", CookiePath = "c" };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            var job = new DownloadJob { AccountId = account.Id, SiteId = "twitter", Status = JobStatus.Running };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            db.Files.AddRange(
                new DownloadFile { JobId = job.Id, Status = FileStatus.Downloaded },
                new DownloadFile { JobId = job.Id, Status = FileStatus.Skipped });
            await db.SaveChangesAsync();
        }

        await Queue().RecoverOnStartupAsync();

        await using var result = await _factory.CreateAsync("twitter");
        var recovered = await result.Jobs.SingleAsync();
        Assert.Equal(JobStatus.Failed, recovered.Status);
        Assert.NotNull(recovered.FinishedAt);
        Assert.Equal(1, recovered.DoneFiles);
        Assert.Equal(1, recovered.SkippedFiles);
        Assert.Equal(2, recovered.TotalFiles);
        var finishedAt = recovered.FinishedAt;
        await Queue().RecoverOnStartupAsync();
        await result.Entry(recovered).ReloadAsync();
        Assert.Equal(finishedAt, recovered.FinishedAt);
    }
}
