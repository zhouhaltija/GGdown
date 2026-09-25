using Microsoft.EntityFrameworkCore;

namespace GGdown.Data;

/// <summary>全局库：仅应用级设置（旧 GGdownDbContext 拆分后的全局半边）。</summary>
public sealed class GGdownGlobalDbContext(DbContextOptions<GGdownGlobalDbContext> options) : DbContext(options)
{
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<SettingEntry>(e => e.HasKey(s => s.Key));
}
