using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GalleryGUI.Data;

public sealed class GalleryDbContextFactory : IDesignTimeDbContextFactory<GalleryDbContext>
{
    public GalleryDbContext CreateDbContext(string[] args)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalleryGUI");
        var options = new DbContextOptionsBuilder<GalleryDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "data", "gallery.db")}").Options;
        return new GalleryDbContext(options);
    }
}
