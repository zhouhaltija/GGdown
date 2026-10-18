using GGdown.Data;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Services;

/// <summary>用户统计是已提交任务的幂等投影，任务脏标记与投影在同一平台库内处理。</summary>
public class StatsAggregator(ISiteDbContextFactory siteFactory)
{
    public virtual async Task ApplyJobCompletionAsync(long jobId, string siteId, CancellationToken ct = default)
    {
        await using var db = await siteFactory.CreateAsync(siteId, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct);
        if (job?.UserId is long userId && job.Status == JobStatus.Completed)
            await RebuildUserAsync(db, userId, ct);
        await transaction.CommitAsync(ct);
    }

    public virtual async Task RecalculateDownloadCountsAsync(CancellationToken ct = default)
    {
        foreach (var siteId in siteFactory.ExistingSites())
        {
            await using var db = await siteFactory.CreateAsync(siteId, ct);
            var userIds = await db.Users.AsNoTracking().Select(u => u.Id).ToListAsync(ct);
            foreach (var userId in userIds)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await RebuildUserAsync(db, userId, ct);
                await transaction.CommitAsync(ct);
            }
        }
    }

    private static async Task RebuildUserAsync(GGdownSiteDbContext db, long userId, CancellationToken ct)
    {
        var completed = db.Jobs.AsNoTracking().Where(j => j.UserId == userId && j.Status == JobStatus.Completed);
        var count = await completed.LongCountAsync(ct);
        var latest = await completed.OrderByDescending(j => j.FinishedAt ?? j.StartedAt ?? j.CreatedAt)
            .ThenByDescending(j => j.Id).FirstOrDefaultAsync(ct);
        DateTime? last = latest is null ? null : latest.FinishedAt ?? latest.StartedAt ?? latest.CreatedAt;
        var mediaSnapshot = latest?.MediaCountSnapshot;
        var now = DateTime.UtcNow;
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.DownloadCount, count)
            .SetProperty(u => u.LastDownloadAt, last)
            .SetProperty(u => u.MediaCountAtDownload, u => mediaSnapshot ?? u.MediaCountAtDownload)
            .SetProperty(u => u.UpdatedAt, now), ct);
        await db.Jobs.Where(j => j.UserId == userId && j.StatisticsPending)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.StatisticsPending, false), ct);
    }
}
