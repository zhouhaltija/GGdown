using GGdown.Data;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Sites;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Data;

public class SiteDbSplitMigrationTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();

    private void SeedLegacyDb()
    {
        var options = new DbContextOptionsBuilder<GGdownDbContext>()
            .UseSqlite($"Data Source={_paths.DbFile};Pooling=False").Options; // 池化句柄锁文件，测试收尾删目录需要
        using var db = new GGdownDbContext(options);
        db.Database.EnsureCreated();
        var tw = new Account { SiteId = "twitter", CookiePath = "a.txt", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        var px = new Account { SiteId = "pixiv", CookiePath = "b.txt", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        db.Accounts.AddRange(tw, px);
        db.Users.AddRange(
            new User { SiteId = "twitter", RestId = "1", ScreenName = "a", AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new User { SiteId = "pixiv", RestId = "2", ScreenName = "b", AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges(); // 先落账号取真实 Id（SQLite 不在 Add 时分配临时键值，Job 外键需要）
        db.Jobs.AddRange(
            new DownloadJob { AccountId = tw.Id, TargetKind = TargetKind.UserMedia, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow },
            new DownloadJob { AccountId = tw.Id, TargetKind = TargetKind.UserMedia, Status = JobStatus.Failed, CreatedAt = DateTime.UtcNow },
            new DownloadJob { AccountId = px.Id, TargetKind = TargetKind.UserMedia, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow });
        db.Settings.Add(new SettingEntry { Key = "download.directory", Value = "D:\\x" });
        db.Settings.Add(new SettingEntry { Key = "site.twitter.options", Value = """{"videos":true}""" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Not_needed_when_fresh_install_or_already_split()
    {
        Assert.False(SiteDbSplitMigration.IsNeeded(_paths.DataDir)); // 全新机器：无 ggdown.db
        SeedLegacyDb();
        Assert.True(SiteDbSplitMigration.IsNeeded(_paths.DataDir));
        await SiteDbSplitMigration.ApplyAsync(_paths);
        Assert.False(SiteDbSplitMigration.IsNeeded(_paths.DataDir)); // 已拆：主库让位、新全局库无站点表
    }

    [Fact]
    public async Task Apply_splits_rows_by_site_and_keeps_global_keys()
    {
        SeedLegacyDb();
        var factory = new SiteDbContextFactory(_paths);
        await SiteDbSplitMigration.ApplyAsync(_paths);
        Assert.True(File.Exists(Path.Combine(_paths.DataDir, "ggdown.db.pre-split.bak")));

        await using (var tw = await factory.CreateAsync("twitter"))
        {
            Assert.Equal(1, await tw.Users.CountAsync());
            Assert.Equal(2, await tw.Jobs.CountAsync()); // 两条 twitter 任务
            Assert.Equal(2, await tw.Jobs.CountAsync(j => j.SiteId == "twitter"));
            // 选项经 AppSettings round-trip 还原 CLR 类型（Review Focus 5）
            var settings = new AppSettings(new SingleGlobalDbContextFactory(TestDb.CreateGlobal().Item2),
                new SiteRegistry([new TwitterSiteProvider()]), factory);
            var opts = await settings.GetSiteOptionsAsync("twitter");
            Assert.True((bool)opts["videos"]!);
            Assert.Equal(1, await tw.SiteSettings.CountAsync(s => s.SiteId == "twitter")); // 旧 blob 只存了一个键
        }
        await using (var px = await factory.CreateAsync("pixiv"))
        {
            Assert.Equal(1, await px.Users.CountAsync());
            Assert.Equal(1, await px.Jobs.CountAsync(j => j.SiteId == "pixiv"));
        }

        // 全局库（拆分产物 data\ggdown.db）保留应用级键、不再有 site.*.options
        await using var global = new GGdownGlobalDbContext(new DbContextOptionsBuilder<GGdownGlobalDbContext>()
            .UseSqlite($"Data Source={_paths.DbFile};Pooling=False").Options);
        await global.Database.EnsureCreatedAsync();
        var store = new GGdownSettingsStore<GGdownGlobalDbContext>(global);
        Assert.Equal("D:\\x", await store.GetAsync<string>("download.directory"));
        Assert.Null(await store.GetAsync<string>("site.twitter.options", null));
    }

    [Fact]
    public async Task Apply_is_idempotent_on_rerun()
    {
        SeedLegacyDb();
        await SiteDbSplitMigration.ApplyAsync(_paths);
        await SiteDbSplitMigration.ApplyAsync(_paths); // 二次不炸不重搬（IsNeeded 已为 false，直接返回）
        var factory = new SiteDbContextFactory(_paths);
        await using var tw = await factory.CreateAsync("twitter");
        Assert.Equal(1, await tw.Users.CountAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }
}
