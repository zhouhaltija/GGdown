using GGdown.Paths;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GGdown.Data;

/// <summary>平台库设计时工厂（dotnet ef migrations add --context GGdownSiteDbContext）。</summary>
public sealed class GGdownSiteDbContextFactory : IDesignTimeDbContextFactory<GGdownSiteDbContext>
{
    public GGdownSiteDbContext CreateDbContext(string[] args)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataRootMigration.CurrentFolderName);
        var options = new DbContextOptionsBuilder<GGdownSiteDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "data", "sites", "twitter.db")}").Options;
        return new GGdownSiteDbContext(options);
    }
}
