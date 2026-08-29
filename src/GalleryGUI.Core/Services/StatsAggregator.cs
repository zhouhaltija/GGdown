using GalleryGUI.Data;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Services;

public class StatsAggregator(IDbContextFactory<GalleryDbContext> factory)
{
    public virtual async Task ApplyJobCompletionAsync(long jobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var counts = await db.Files.AsNoTracking()
            .Where(f => f.JobId == jobId && f.UserId != null && f.Status == FileStatus.Downloaded)
            .GroupBy(f => f.UserId!.Value)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var c in counts)
        {
            await db.Users.Where(u => u.Id == c.UserId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.DownloadCount, u => u.DownloadCount + c.Count)
                    .SetProperty(u => u.LastDownloadAt, now)
                    .SetProperty(u => u.UpdatedAt, now), ct);
        }
        await tx.CommitAsync(ct);
    }
}
