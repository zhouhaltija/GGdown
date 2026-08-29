using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Data;

public sealed class GalleryDbContext(DbContextOptions<GalleryDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<User> Users => Set<User>();
    public DbSet<DownloadJob> Jobs => Set<DownloadJob>();
    public DbSet<DownloadFile> Files => Set<DownloadFile>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

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
            e.HasIndex(j => j.Status);
            e.HasOne<Account>().WithMany().HasForeignKey(j => j.AccountId);
            e.HasOne<User>().WithMany().HasForeignKey(j => j.UserId);
        });
        b.Entity<DownloadFile>(e =>
        {
            e.HasIndex(f => f.JobId);
            e.HasIndex(f => f.UserId);
            e.HasOne<DownloadJob>().WithMany(j => j.Files).HasForeignKey(f => f.JobId); // 控制器裁定：双向导航
        });
        b.Entity<SettingEntry>(e => e.HasKey(s => s.Key));
    }
}
