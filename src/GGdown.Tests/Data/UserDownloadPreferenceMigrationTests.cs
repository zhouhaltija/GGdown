using GGdown.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GGdown.Tests.Data;

public class UserDownloadPreferenceMigrationTests
{
    [Fact]
    public async Task Existing_pixiv_users_keep_single_type_site_choice()
    {
        var root = Path.Combine(Path.GetTempPath(), "ggdown-pref-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<GGdownSiteDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "pixiv.db")};Pooling=False").Options;
            await using (var oldDb = new GGdownSiteDbContext(options))
            {
                await oldDb.GetService<IMigrator>().MigrateAsync("20260925135148_IgnoreFollowing");
                await oldDb.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Users" ("SiteId","RestId","ScreenName","Source","IsPinned","IsSkipped","InDownloadList","DownloadCount","AddedAt","UpdatedAt")
                    VALUES ('pixiv','12345','12345',1,0,0,0,0,'2026-01-01','2026-01-01');
                    """);
                await oldDb.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "SiteSettings" ("SiteId","Key","Value") VALUES ('pixiv','download_novels','false');
                    """);
            }
            await using (var upgraded = new GGdownSiteDbContext(options))
            {
                await upgraded.Database.MigrateAsync();
                var user = await upgraded.Users.SingleAsync();
                Assert.Equal(UserContentSelection.PixivArtworks, user.ContentSelection);
                Assert.Null(user.DownloadSince);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
