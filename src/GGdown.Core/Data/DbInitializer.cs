using Microsoft.EntityFrameworkCore;

namespace GGdown.Data;

public static class DbInitializer
{
    /// <summary>Task 6：仅全局库在启动时迁移；平台库由 ISiteDbContextFactory 惰性建表。</summary>
    public static async Task InitializeGlobalAsync(GGdownGlobalDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}
