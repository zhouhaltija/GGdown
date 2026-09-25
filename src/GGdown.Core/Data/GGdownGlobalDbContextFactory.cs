using GGdown.Paths;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GGdown.Data;

/// <summary>全局库设计时工厂（dotnet ef migrations add --context GGdownGlobalDbContext）。</summary>
public sealed class GGdownGlobalDbContextFactory : IDesignTimeDbContextFactory<GGdownGlobalDbContext>
{
    public GGdownGlobalDbContext CreateDbContext(string[] args)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataRootMigration.CurrentFolderName);
        var options = new DbContextOptionsBuilder<GGdownGlobalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "data", DataRootMigration.CurrentDbFileName)}").Options;
        return new GGdownGlobalDbContext(options);
    }
}
