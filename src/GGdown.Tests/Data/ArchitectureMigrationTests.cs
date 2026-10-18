using GGdown.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GGdown.Tests.Data;

public class ArchitectureMigrationTests
{
    [Fact]
    public async Task Upgrade_preserves_history_and_normalizes_legacy_active_accounts()
    {
        var paths = TestPaths.Create();
        var options = new DbContextOptionsBuilder<GGdownSiteDbContext>()
            .UseSqlite($"Data Source={Path.Combine(paths.SitesDataDir, "twitter.db")};Pooling=False").Options;
        try
        {
            await using (var old = new GGdownSiteDbContext(options))
            {
                await old.GetService<IMigrator>().MigrateAsync("20261017092323_AddDownloadRecovery");
                await old.Database.ExecuteSqlRawAsync("""
                    INSERT INTO Accounts (SiteId,CookiePath,Status,IsActive,AddedAt) VALUES ('twitter','old',1,1,'2026-10-17');
                    INSERT INTO Accounts (SiteId,CookiePath,Status,IsActive,AddedAt) VALUES ('twitter','new',1,1,'2026-10-17');
                    INSERT INTO Jobs (SiteId,AccountId,TargetKind,Status,TotalFiles,DoneFiles,SkippedFiles,FailedFiles,CreatedAt)
                    VALUES ('twitter',1,0,2,1,1,0,0,'2026-10-17');
                    INSERT INTO Files (SiteId,JobId,Url,FilePath,Status,CreatedAt)
                    VALUES ('twitter',1,'test','kept.jpg',0,'2026-10-17');
                    """);
            }
            await using var upgraded = new GGdownSiteDbContext(options);
            await upgraded.Database.MigrateAsync();
            Assert.Equal(2, await upgraded.Accounts.CountAsync());
            Assert.Equal("new", (await upgraded.Accounts.SingleAsync(a => a.IsActive)).CookiePath);
            Assert.Equal(JobStatus.Completed, (await upgraded.Jobs.SingleAsync()).Status);
            Assert.Equal("kept.jpg", (await upgraded.Files.SingleAsync()).FilePath);
            upgraded.Accounts.Add(new Account { SiteId = "twitter", CookiePath = "third", IsActive = true });
            await Assert.ThrowsAsync<DbUpdateException>(() => upgraded.SaveChangesAsync());
        }
        finally { Directory.Delete(paths.Root, true); }
    }
}
