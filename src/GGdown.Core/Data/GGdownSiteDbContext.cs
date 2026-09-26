using Microsoft.EntityFrameworkCore;

namespace GGdown.Data;

/// <summary>
/// 平台库：用户/账号/任务/文件/关注缓存/站点选项。
/// 索引与导航沿用旧 GGdownDbContext 的同名配置；SiteId 列在此 context 才映射（旧库无此列，旧 context 忽略）。
/// </summary>
public sealed class GGdownSiteDbContext(DbContextOptions<GGdownSiteDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<User> Users => Set<User>();
    public DbSet<DownloadJob> Jobs => Set<DownloadJob>();
    public DbSet<DownloadFile> Files => Set<DownloadFile>();
    public DbSet<FollowingCacheEntry> FollowingCache => Set<FollowingCacheEntry>();
    public DbSet<IgnoredFollowingEntry> IgnoredFollowing => Set<IgnoredFollowingEntry>();
    public DbSet<SiteSettingEntry> SiteSettings => Set<SiteSettingEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.Property(a => a.CookiePath).IsRequired();
            e.HasIndex(a => new { a.SiteId, a.IsActive });
        });
        b.Entity<User>(e =>
        {
            e.HasIndex(u => new { u.SiteId, u.RestId }).IsUnique();
            e.HasIndex(u => u.ScreenName);
            e.HasIndex(u => u.LastDownloadAt);
        });
        b.Entity<DownloadJob>(e =>
        {
            e.Property(j => j.SiteId).IsRequired().HasDefaultValue(""); // 默认空串：同连接上的旧模型插入（队列/测试）不写该列不违约
            e.HasIndex(j => j.SiteId); // 跨库恢复/站点筛选的主路径
            e.HasIndex(j => j.Status);
            e.HasOne<Account>().WithMany().HasForeignKey(j => j.AccountId);
            e.HasOne<User>().WithMany().HasForeignKey(j => j.UserId);
        });
        b.Entity<DownloadFile>(e =>
        {
            e.Property(f => f.SiteId).IsRequired().HasDefaultValue(""); // 同上
            e.HasIndex(f => f.JobId);
            e.HasIndex(f => f.UserId);
            e.HasOne<DownloadJob>().WithMany(j => j.Files).HasForeignKey(f => f.JobId); // 控制器裁定：双向导航
        });
        b.Entity<FollowingCacheEntry>(e =>
        {
            e.ToTable("FollowingCache");
            e.HasIndex(x => new { x.SiteId, x.RestId }).IsUnique();
            e.HasIndex(x => new { x.SiteId, x.SortOrder });
        });
        b.Entity<IgnoredFollowingEntry>(e =>
        {
            e.ToTable("IgnoredFollowing");
            e.HasIndex(x => new { x.SiteId, x.AccountId, x.RestId }).IsUnique();
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SiteSettingEntry>(e =>
        {
            e.HasKey(s => new { s.SiteId, s.Key });
            e.Property(s => s.SiteId).IsRequired();
            e.Property(s => s.Key).IsRequired();
        });
    }
}
