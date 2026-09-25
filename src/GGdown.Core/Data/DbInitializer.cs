using Microsoft.EntityFrameworkCore;

namespace GGdown.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(GGdownDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}
