using GGdown.Paths;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GGdown.Data;

public sealed class GGdownDbContextFactory : IDesignTimeDbContextFactory<GGdownDbContext>
{
    public GGdownDbContext CreateDbContext(string[] args)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataRootMigration.CurrentFolderName);
        var options = new DbContextOptionsBuilder<GGdownDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "data", DataRootMigration.CurrentDbFileName)}").Options;
        return new GGdownDbContext(options);
    }
}
