using System.Collections.Concurrent;
using GGdown.Paths;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Data;

public interface ISiteDbContextFactory
{
    /// <summary>打开/创建平台库 data\sites\&lt;siteId&gt;.db；首次访问建目录+建表（进程内去重）。</summary>
    Task<GGdownSiteDbContext> CreateAsync(string siteId, CancellationToken ct = default);

    /// <summary>枚举磁盘上已存在的平台（*.db 文件名），供队列恢复/统计重算遍历。</summary>
    IReadOnlyList<string> ExistingSites();
}

/// <summary>
/// 平台库工厂：data\sites\&lt;siteId&gt;.db，每次返回新 context，调用方负责 await using。
/// 建表用 EnsureCreatedAsync 起步（Task 6 换正式迁移——存量拆分只搬行，不依赖迁移历史）。
/// </summary>
public sealed class SiteDbContextFactory(IAppPaths paths) : ISiteDbContextFactory
{
    private readonly ConcurrentDictionary<string, byte> _created = new(StringComparer.OrdinalIgnoreCase);

    public async Task<GGdownSiteDbContext> CreateAsync(string siteId, CancellationToken ct = default)
    {
        var dir = paths.SitesDataDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{siteId.ToLowerInvariant()}.db");
        var db = New(file);
        if (_created.TryAdd(siteId, 0))
        {
            await db.Database.EnsureCreatedAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        }
        return db;
    }

    public IReadOnlyList<string> ExistingSites() =>
        Directory.Exists(paths.SitesDataDir)
            ? [.. Directory.EnumerateFiles(paths.SitesDataDir, "*.db").Select(Path.GetFileNameWithoutExtension)]
            : [];

    private static GGdownSiteDbContext New(string file) => new(
        // Pooling=False：池化连接 dispose 后仍持有文件句柄，会锁住平台库文件（测试删除目录/用户迁移文件时踩中）
        new DbContextOptionsBuilder<GGdownSiteDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options);
}
