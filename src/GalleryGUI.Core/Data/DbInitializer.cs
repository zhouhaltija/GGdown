using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(GalleryDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}
