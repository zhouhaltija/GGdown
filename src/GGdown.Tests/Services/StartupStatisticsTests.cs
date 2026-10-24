using GGdown.Data;
using GGdown.Paths;
using GGdown.Services;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Services;

public sealed class StartupStatisticsTests : IDisposable
{
    private const string VersionKey = "ggdown.stats-projection.version";
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly SiteDbContextFactory _factory;
    private static readonly DateTime Finished = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    public StartupStatisticsTests() => _factory = new(_paths);

    private async Task SeedAsync(string site = "twitter")
    {
        await using var db = await _factory.CreateAsync(site);
        var account = new Account { SiteId = site, CookiePath = "synthetic" };
        var user = new User { SiteId = site, RestId = "1", ScreenName = "synthetic", DownloadCount = 99 };
        db.AddRange(account, user);
        await db.SaveChangesAsync();
        db.Jobs.Add(new DownloadJob { SiteId = site, AccountId = account.Id, UserId = user.Id,
            Status = JobStatus.Completed, CreatedAt = Finished.AddHours(-1), FinishedAt = Finished,
            MediaCountSnapshot = 42 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task First_start_corrects_legacy_counts_before_marking_initialized()
    {
        await SeedAsync();
        await SeedAsync("pixiv");
        await new StatsAggregator(_factory).RepairOnStartupAsync();
        foreach (var site in _factory.ExistingSites())
        {
            await using var db = await _factory.CreateAsync(site);
            var user = await db.Users.SingleAsync();
            Assert.Equal(1, user.DownloadCount);
            Assert.Equal(Finished, user.LastDownloadAt);
            Assert.Equal(42, user.MediaCountAtDownload);
            Assert.Equal("1", (await db.SiteSettings.SingleAsync(s => s.Key == VersionKey)).Value);
        }
    }

    [Fact]
    public async Task Clean_restart_does_not_rewrite_users()
    {
        await SeedAsync();
        var stats = new StatsAggregator(_factory);
        await stats.RepairOnStartupAsync();
        await using (var db = await _factory.CreateAsync("twitter"))
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.UpdatedAt, Finished));
        await stats.RepairOnStartupAsync();
        await using var result = await _factory.CreateAsync("twitter");
        var user = await result.Users.SingleAsync();
        Assert.Equal(Finished, user.UpdatedAt);
        Assert.Equal(1, user.DownloadCount);
        Assert.Equal(42, user.MediaCountAtDownload);
    }

    [Fact]
    public async Task Restart_repairs_only_distinct_dirty_users()
    {
        await SeedAsync();
        await SeedAsync("pixiv");
        var stats = new StatsAggregator(_factory);
        await stats.RepairOnStartupAsync();
        await using (var db = await _factory.CreateAsync("twitter"))
        {
            db.Users.Add(new User { SiteId = "twitter", RestId = "2", ScreenName = "clean", UpdatedAt = Finished });
            var job = await db.Jobs.SingleAsync();
            job.StatisticsPending = true;
            db.Jobs.Add(new DownloadJob { SiteId = "twitter", AccountId = job.AccountId,
                UserId = job.UserId, Status = JobStatus.Completed, StatisticsPending = true,
                CreatedAt = Finished, FinishedAt = Finished.AddDays(1), MediaCountSnapshot = 43 });
            await db.SaveChangesAsync();
        }
        await using (var db = await _factory.CreateAsync("pixiv"))
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.UpdatedAt, Finished));
        await stats.RepairOnStartupAsync();
        await using var result = await _factory.CreateAsync("twitter");
        var dirty = await result.Users.SingleAsync(u => u.RestId == "1");
        Assert.Equal(2, dirty.DownloadCount);
        Assert.Equal(Finished.AddDays(1), dirty.LastDownloadAt);
        Assert.Equal(43, dirty.MediaCountAtDownload);
        Assert.Equal(Finished, (await result.Users.SingleAsync(u => u.RestId == "2")).UpdatedAt);
        Assert.False(await result.Jobs.AnyAsync(j => j.StatisticsPending));
        await using var other = await _factory.CreateAsync("pixiv");
        Assert.Equal(Finished, (await other.Users.SingleAsync()).UpdatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_or_failed_initial_rebuild_can_retry(bool failWrite)
    {
        await SeedAsync();
        var stats = new StatsAggregator(_factory);
        if (failWrite)
        {
            await using var db = await _factory.CreateAsync("twitter");
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_stats BEFORE UPDATE ON Users BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");
            await Assert.ThrowsAnyAsync<Exception>(() => stats.RepairOnStartupAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_stats;");
        }
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stats.RepairOnStartupAsync(new CancellationToken(true)));
        await using (var db = await _factory.CreateAsync("twitter"))
            Assert.False(await db.SiteSettings.AnyAsync(s => s.Key == VersionKey));
        await stats.RepairOnStartupAsync();
        await using var result = await _factory.CreateAsync("twitter");
        Assert.Equal(1, (await result.Users.SingleAsync()).DownloadCount);
        Assert.Equal("1", (await result.SiteSettings.SingleAsync(s => s.Key == VersionKey)).Value);
    }

    [Fact]
    public async Task Full_rebuild_remains_available()
    {
        await SeedAsync();
        var stats = new StatsAggregator(_factory);
        await stats.RepairOnStartupAsync();
        await using (var db = await _factory.CreateAsync("twitter"))
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.DownloadCount, 88L));
        await stats.RecalculateDownloadCountsAsync();
        await using var result = await _factory.CreateAsync("twitter");
        Assert.Equal(1, (await result.Users.SingleAsync()).DownloadCount);
    }

    public void Dispose() => Directory.Delete(_paths.Root, true);
}
