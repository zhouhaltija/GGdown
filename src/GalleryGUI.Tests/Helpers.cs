using GalleryGUI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Tests;

public static class TestDb
{
    public static (SqliteConnection Connection, GalleryDbContext Db) Create()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<GalleryDbContext>()
            .UseSqlite(conn).Options;
        var db = new GalleryDbContext(options);
        db.Database.EnsureCreated();
        return (conn, db);
    }
}

public static class TestPaths
{
    public static GalleryGUI.Paths.AppPaths Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "ggui-" + Guid.NewGuid().ToString("N"));
        var p = new GalleryGUI.Paths.AppPaths(root); // 限定：GalleryGUI.Tests.Paths（AppPathsTests.cs）会遮蔽短写 Paths
        p.EnsureCreated();
        return p;
    }
}
