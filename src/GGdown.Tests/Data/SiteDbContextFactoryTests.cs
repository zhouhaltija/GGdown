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

    public void Dispose() => Directory.Delete(_paths.Root, true);
}
