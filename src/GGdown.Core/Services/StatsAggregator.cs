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
        // 下载次数、末次下载时间和作品数快照只记录成功完成的任务。
        if (job.Status != JobStatus.Completed) return;

        var now = DateTime.UtcNow;
        var lastDownloadAt = job.FinishedAt ?? job.StartedAt ?? job.CreatedAt;
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.DownloadCount, u => u.DownloadCount + 1)
                .SetProperty(u => u.LastDownloadAt, u => u.LastDownloadAt == null || u.LastDownloadAt < lastDownloadAt
                    ? lastDownloadAt : u.LastDownloadAt)
                .SetProperty(u => u.MediaCountAtDownload, u => u.MediaCount)
                .SetProperty(u => u.UpdatedAt, now), ct);
    }

    public virtual async Task RecalculateDownloadCountsAsync(CancellationToken ct = default)
    {
        foreach (var siteId in siteFactory.ExistingSites())
        {
            await using var db = await siteFactory.CreateAsync(siteId, ct);
            // 与实时更新使用相同口径，同时清除旧版本误计的失败任务。
            var counts = await db.Jobs.AsNoTracking()
                .Where(j => j.UserId != null && j.Status == JobStatus.Completed)
                .GroupBy(j => j.UserId!.Value)
                .Select(g => new { UserId = g.Key, Count = (long)g.Count(),
                    LastDownloadAt = g.Max(j => j.FinishedAt ?? j.StartedAt ?? j.CreatedAt) })
                .ToListAsync(ct);

            // 次数与日期取自同一批历史任务，事务避免只重建一半的统计。
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.DownloadCount, 0L)
                .SetProperty(u => u.LastDownloadAt, (DateTime?)null), ct);
            foreach (var c in counts)
            {
                await db.Users.Where(u => u.Id == c.UserId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.DownloadCount, c.Count)
                        .SetProperty(u => u.LastDownloadAt, c.LastDownloadAt), ct);
            }
            await transaction.CommitAsync(ct);
        }
    }
}
