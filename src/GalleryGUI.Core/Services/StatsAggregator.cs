using GalleryGUI.Data;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Services;

public class StatsAggregator(IDbContextFactory<GalleryDbContext> factory)
{
    public virtual async Task ApplyJobCompletionAsync(long jobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct);
        if (job?.UserId is not long userId) return;
        if (job.Status is JobStatus.Canceled) return;

        var now = DateTime.UtcNow;
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.DownloadCount, u => u.DownloadCount + 1)
                .SetProperty(u => u.LastDownloadAt, now)
                .SetProperty(u => u.UpdatedAt, now), ct);
    }

    public virtual async Task RecalculateDownloadCountsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
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
