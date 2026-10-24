using GGdown.Data;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Services;

/// <summary>用户统计是已提交任务的幂等投影，任务脏标记与投影在同一平台库内处理。</summary>
public class StatsAggregator(ISiteDbContextFactory siteFactory)
{
    // 内部键与站点选项共用 SiteSettings 表，键定义集中在 SiteInternalSettings，避免两处各写一份而漂移。
    private const string ProjectionVersionKey = SiteInternalSettings.StatsProjectionVersionKey;
    private const string ProjectionVersion = "1";

    /// <summary>旧库校正一次，之后启动只修复未提交完统计投影的作者。</summary>
    public virtual async Task RepairOnStartupAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var siteId in siteFactory.ExistingSites())
        {
            await using var db = await siteFactory.CreateAsync(siteId, ct);
            var version = await db.SiteSettings.SingleOrDefaultAsync(s =>
                s.SiteId == siteId && s.Key == ProjectionVersionKey, ct);
            var initialize = version?.Value != ProjectionVersion;
            var userIds = initialize
                ? await db.Users.AsNoTracking().Select(u => u.Id).ToListAsync(ct)
                : await db.Jobs.AsNoTracking().Where(j => j.StatisticsPending && j.UserId != null)
                    .Select(j => j.UserId!.Value).Distinct().ToListAsync(ct);
            foreach (var userId in userIds)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await RebuildUserAsync(db, userId, ct);
                await transaction.CommitAsync(ct);
            }
            if (!initialize) continue;
            // 只有整个库校正成功才记版本，失败或取消后的启动可以幂等重试。
            if (version is null)
                db.SiteSettings.Add(new SiteSettingEntry { SiteId = siteId, Key = ProjectionVersionKey, Value = ProjectionVersion });
            else
                version.Value = ProjectionVersion;
            await db.SaveChangesAsync(ct);
        }
    }

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
