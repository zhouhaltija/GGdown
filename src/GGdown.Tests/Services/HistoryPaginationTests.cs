using GGdown.Data;
using GGdown.Services;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Services;

public class HistoryPaginationTests
{
    [Fact]
    public async Task Cursor_visits_same_timestamp_and_same_ids_in_different_sites_once()
    {
        var paths = TestPaths.Create();
        try
        {
            var factory = new SiteDbContextFactory(paths);
            var stamp = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            foreach (var site in new[] { "twitter", "pixiv" })
            {
                await using var db = await factory.CreateAsync(site);
                var account = new Account { SiteId = site, CookiePath = "test", IsActive = true };
                db.Accounts.Add(account);
                await db.SaveChangesAsync();
                var job = new DownloadJob { SiteId = site, AccountId = account.Id, Status = JobStatus.Completed };
                db.Jobs.Add(job);
                await db.SaveChangesAsync();
                db.Files.AddRange(Enumerable.Range(1, 3).Select(n => new DownloadFile
                {
                    SiteId = site, JobId = job.Id, Url = "test", FilePath = $"{site}-{n}.jpg",
                    Status = FileStatus.Downloaded, CreatedAt = stamp,
                }));
                await db.SaveChangesAsync();
            }
            var query = new HistoryQueryService(factory);
            var seen = new List<string>();
            HistoryCursor? cursor = null;
            for (var i = 0; i < 5; i++)
            {
                var page = await query.ListAsync(new HistoryFilter(Cursor: cursor, PageSize: 2));
                Assert.InRange(page.Count, 0, 2);
                if (page.Count == 0) break;
                seen.AddRange(page.Select(r => r.FilePath));
                var last = page[^1];
                cursor = new HistoryCursor(last.CreatedAt, last.FileId, last.SiteId);
                if (i == 0)
                {
                    await using var db = await factory.CreateAsync("twitter");
                    db.Files.Add(new DownloadFile { SiteId = "twitter", JobId = 1, Url = "test", FilePath = "new.jpg",
                        Status = FileStatus.Downloaded, CreatedAt = stamp.AddMinutes(1) });
                    await db.SaveChangesAsync();
                }
            }
            Assert.Equal(new[] { "twitter-3.jpg", "pixiv-3.jpg", "twitter-2.jpg", "pixiv-2.jpg", "twitter-1.jpg", "pixiv-1.jpg" }, seen);
        }
        finally { Directory.Delete(paths.Root, true); }
    }
}
