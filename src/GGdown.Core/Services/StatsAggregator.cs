using GGdown.Data;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Services;

/// <summary>
/// Task 4 起按平台库聚合：ApplyJobCompletionAsync 显式带 siteId（任务归属库），
/// RecalculateDownloadCountsAsync 遍历磁盘上已存在的全部平台库。
/// </summary>
public class StatsAggregator(ISiteDbContextFactory siteFactory)
{
    public virtual async Task ApplyJobCompletionAsync(long jobId, string siteId, CancellationToken ct = default)
    {
        await using var db = await siteFactory.CreateAsync(siteId, ct);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct);
        if (job?.UserId is not long userId) return;
        if (job.Status is JobStatus.Canceled) return;

        var now = DateTime.UtcNow;
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.DownloadCount, u => u.DownloadCount + 1)
                .SetProperty(u => u.LastDownloadAt, now)
                .SetProperty(u => u.MediaCountAtDownload, u => u.MediaCount)
                .SetProperty(u => u.UpdatedAt, now), ct);
    }

    public virtual async Task RecalculateDownloadCountsAsync(CancellationToken ct = default)
    {
        foreach (var siteId in siteFactory.ExistingSites())
        {
            await using var db = await siteFactory.CreateAsync(siteId, ct);
            var counts = await db.Jobs.AsNoTracking()
                .Where(j => j.UserId != null && (j.Status == JobStatus.Completed || j.Status == JobStatus.Failed))
                .GroupBy(j => j.UserId!.Value)
                .Select(g => new { UserId = g.Key, Count = (long)g.Count() })
                .ToListAsync(ct);

            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.DownloadCount, 0L), ct);
            foreach (var c in counts)
            {
                await db.Users.Where(u => u.Id == c.UserId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.DownloadCount, c.Count), ct);
            }
        }
    }
}
