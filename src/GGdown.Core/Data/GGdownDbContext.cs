using Microsoft.EntityFrameworkCore;

namespace GGdown.Data;

public sealed class GGdownDbContext(DbContextOptions<GGdownDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<User> Users => Set<User>();
    public DbSet<DownloadJob> Jobs => Set<DownloadJob>();
    public DbSet<DownloadFile> Files => Set<DownloadFile>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();
    public DbSet<FollowingCacheEntry> FollowingCache => Set<FollowingCacheEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.Property(a => a.CookiePath).IsRequired();
            e.HasIndex(a => new { a.SiteId, a.IsActive });
        });
        b.Entity<User>(e =>
        {
            // 旧合库只用于搬迁；新偏好字段仅存在于平台库。
            e.Ignore(u => u.ContentSelection);
            e.Ignore(u => u.DownloadSince);
            e.HasIndex(u => new { u.SiteId, u.RestId }).IsUnique();
            e.HasIndex(u => u.ScreenName);
            e.HasIndex(u => u.LastDownloadAt);
        });
        b.Entity<DownloadJob>(e =>
        {
            // 旧库无 SiteId 列（拆分迁移只读不写旧库），显式忽略以防误映射
            e.Ignore(j => j.SiteId);
            e.HasIndex(j => j.Status);
            e.HasOne<Account>().WithMany().HasForeignKey(j => j.AccountId);
            e.HasOne<User>().WithMany().HasForeignKey(j => j.UserId);
        });
        b.Entity<DownloadFile>(e =>
        {
            // 旧库无 SiteId 列（同上）
            e.Ignore(f => f.SiteId);
            e.HasIndex(f => f.JobId);
            e.HasIndex(f => f.UserId);
            e.HasOne<DownloadJob>().WithMany(j => j.Files).HasForeignKey(f => f.JobId); // 控制器裁定：双向导航
        });
        b.Entity<SettingEntry>(e => e.HasKey(s => s.Key));
        b.Entity<FollowingCacheEntry>(e =>
        {
            e.ToTable("FollowingCache");
            e.HasIndex(x => new { x.SiteId, x.RestId }).IsUnique();
            e.HasIndex(x => new { x.SiteId, x.SortOrder });
        });
    }
}
