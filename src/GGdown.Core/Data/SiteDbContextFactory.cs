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
/// 首次访问 MigrateAsync（Migrations/Sites Init）——站点实体后续演进有前向迁移路径
/// （Final review Important 4：EnsureCreated 对已存在库是 no-op，会让下一处 schema 变更静默失效）。
/// </summary>
public sealed class SiteDbContextFactory(IAppPaths paths) : ISiteDbContextFactory
{
    private sealed class Initialization
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public bool Ready;
    }

    private readonly ConcurrentDictionary<string, Initialization> _initialization = new(StringComparer.OrdinalIgnoreCase);

    public async Task<GGdownSiteDbContext> CreateAsync(string siteId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = paths.SitesDataDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{siteId.ToLowerInvariant()}.db");
        var state = _initialization.GetOrAdd(siteId, _ => new Initialization());
        await state.Gate.WaitAsync(ct);
        try
        {
            if (!state.Ready)
            {
                // 初始化上下文与业务上下文分开；失败或取消时释放连接，下一位调用者可重试。
                await using var initializationDb = New(file);
                await initializationDb.Database.MigrateAsync(ct);
                await initializationDb.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
                ct.ThrowIfCancellationRequested();
                state.Ready = true;
            }
        }
        finally
        {
            state.Gate.Release();
        }
        ct.ThrowIfCancellationRequested();
        return New(file);
    }

    public IReadOnlyList<string> ExistingSites() =>
        Directory.Exists(paths.SitesDataDir)
            ? [.. Directory.EnumerateFiles(paths.SitesDataDir, "*.db").Select(file => Path.GetFileNameWithoutExtension(file)!)]
            : [];

    private static GGdownSiteDbContext New(string file) => new(
        // Pooling=False：池化连接 dispose 后仍持有文件句柄，会锁住平台库文件（测试删除目录/用户迁移文件时踩中）
        new DbContextOptionsBuilder<GGdownSiteDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options);
}
