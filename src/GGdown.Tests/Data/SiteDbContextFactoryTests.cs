using GGdown.Data;
using GGdown.Paths;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Data;

public class SiteDbContextFactoryTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();

    [Fact]
    public async Task CreateAsync_creates_migrates_and_reuses_site_db()
    {
        var f = new SiteDbContextFactory(_paths);
        await using (var db = await f.CreateAsync("twitter"))
        {
            Assert.True(File.Exists(Path.Combine(_paths.SitesDataDir, "twitter.db")));
            db.Users.Add(new User { SiteId = "twitter", RestId = "1", ScreenName = "a" });
            db.Accounts.Add(new Account { SiteId = "twitter", CookiePath = "cookie", AddedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        // Final review Important 4：站点库走迁移而非 EnsureCreated（后续 schema 变更有前向路径）
        await using (var hist = await f.CreateAsync("twitter"))
        {
            var applied = await hist.Database.GetAppliedMigrationsAsync();
            Assert.Contains(applied, m => m.Contains("Init"));
            Assert.Contains(applied, m => m.Contains("IgnoreFollowing"));
            hist.IgnoredFollowing.Add(new IgnoredFollowingEntry
            {
                SiteId = "twitter", AccountId = await hist.Accounts.Select(a => a.Id).SingleAsync(),
                RestId = "ignored", IgnoredAt = DateTime.UtcNow
            });
            await hist.SaveChangesAsync();
        }
        await using (var db2 = await f.CreateAsync("twitter"))
        {
            Assert.Equal(1, await db2.Users.CountAsync());
            Assert.Equal("ignored", (await db2.IgnoredFollowing.SingleAsync()).RestId);
        }
        Assert.Equal(["twitter"], f.ExistingSites());
    }

    [Fact]
    public async Task CreateAsync_is_idempotent_across_sites()
    {
        var f = new SiteDbContextFactory(_paths);
        await using var a = await f.CreateAsync("twitter");
        await using var b = await f.CreateAsync("pixiv");
        Assert.Equal(["pixiv", "twitter"], f.ExistingSites().OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task CreateAsync_retries_initialization_after_cancellation()
    {
        var factory = new SiteDbContextFactory(_paths);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.CreateAsync("twitter", canceled.Token));

        await using var retry = await factory.CreateAsync("TWITTER");
        Assert.Empty(await retry.Users.ToListAsync());
        await retry.Database.OpenConnectionAsync();
        using var command = retry.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CreateAsync_retries_after_database_file_is_repaired()
    {
        var file = Path.Combine(_paths.SitesDataDir, "twitter.db");
        await File.WriteAllTextAsync(file, "损坏的数据库");
        var factory = new SiteDbContextFactory(_paths);
        await Assert.ThrowsAnyAsync<Exception>(() => factory.CreateAsync("twitter"));
        File.Delete(file);

        await using var retry = await factory.CreateAsync("twitter");
        Assert.Empty(await retry.Users.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_first_access_returns_only_migrated_contexts()
    {
        var factory = new SiteDbContextFactory(_paths);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 24).Select(async i =>
        {
            await start.Task;
            await using var db = await factory.CreateAsync(i % 2 == 0 ? "twitter" : "TWITTER");
            Assert.Empty(await db.Users.ToListAsync());
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(callers);
    }

    public void Dispose() => Directory.Delete(_paths.Root, true);
}
