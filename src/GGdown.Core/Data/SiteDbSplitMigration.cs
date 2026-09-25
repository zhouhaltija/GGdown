using System.Text.Json;
using GGdown.Paths;
using GGdown.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Data;

/// <summary>
/// 旧单库（ggdown.db 全量数据）一次性拆分为全局库 + 各平台库（spec §3.4）。
/// 幂等语义：完成标志 = 主库不再含 Accounts 表（拆毕主库改名 .pre-split.bak）；
/// 平台库写入前先清空本库各表（wipe-then-copy），中途崩溃后下次启动整体重拆，
/// 主库在搬完前不动，永远以主库为唯一事实源。
/// </summary>
public static class SiteDbSplitMigration
{
    private const string BackupSuffix = ".pre-split.bak";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public readonly record struct Result(int Sites, long Accounts, long Users, long Jobs, long Files, long OrphanJobs);

    /// <summary>需要拆分 = ggdown.db 存在且仍含站点表（Accounts）。</summary>
    public static bool IsNeeded(string dataDir)
    {
        var db = Path.Combine(dataDir, "ggdown.db");
        if (!File.Exists(db)) return false; // 全新安装（或已拆完改名）
        using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Accounts'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public static async Task<Result> ApplyAsync(IAppPaths paths, CancellationToken ct = default)
    {
        if (!IsNeeded(paths.DataDir)) return default;

        var main = paths.DbFile;
        var backup = main + BackupSuffix;

        var legacyOptions = new DbContextOptionsBuilder<GGdownDbContext>()
            .UseSqlite($"Data Source={main};Pooling=False").Options; // 池化句柄会锁主库文件，拆分全程禁用
        using var legacy = new GGdownDbContext(legacyOptions);
        // 先 checkpoint 落盘再留档，保证 .bak 含全部已写事务（WAL 模式下 -wal 未合并时 File.Copy 会丢尾部）
        await legacy.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
        File.Copy(main, backup, overwrite: true); // 搬移期间主库不可读时的人工恢复点

        var accounts = await legacy.Accounts.AsNoTracking().ToListAsync(ct);
        var accountSite = accounts.ToDictionary(a => a.Id, a => a.SiteId);

        // 站点集合：账号/用户/关注缓存出现的站点并集
        var sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        sites.UnionWith(accounts.Select(a => a.SiteId));
        sites.UnionWith(await legacy.Users.AsNoTracking().Select(u => u.SiteId).Distinct().ToListAsync(ct));
        sites.UnionWith(await legacy.FollowingCache.AsNoTracking().Select(x => x.SiteId).Distinct().ToListAsync(ct));

        var factory = new SiteDbContextFactory(paths);
        long orphans = 0, userTotal = 0, jobTotal = 0, fileTotal = 0;
        foreach (var siteId in sites)
        {
            await using var db = await factory.CreateAsync(siteId, ct);
            // 拷贝期间关闭 FK 检查：旧库可能含悬空引用（孤儿任务），拆分以保留数据为先
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;", ct);
            // wipe-then-copy：平台库只从主库整体重建（防中途崩溃留下半拆数据）
            db.Accounts.RemoveRange(db.Accounts);
            db.Users.RemoveRange(db.Users);
            db.Jobs.RemoveRange(db.Jobs);
            db.Files.RemoveRange(db.Files);
            db.FollowingCache.RemoveRange(db.FollowingCache);
            db.SiteSettings.RemoveRange(db.SiteSettings);
            await db.SaveChangesAsync(ct);

            db.Accounts.AddRange(accounts.Where(a => a.SiteId == siteId));
            db.Users.AddRange(await legacy.Users.AsNoTracking().Where(u => u.SiteId == siteId).ToListAsync(ct));
            db.FollowingCache.AddRange(await legacy.FollowingCache.AsNoTracking()
                .Where(x => x.SiteId == siteId).ToListAsync(ct));

            // Job 归属：经 AccountId 回填（UserId 可空不作依据）；AccountId 悬空的孤儿归 twitter（当前唯一历史主站点）
            var siteAccountIds = accounts.Where(a => a.SiteId == siteId).Select(a => a.Id).ToList();
            var jobs = await legacy.Jobs.AsNoTracking()
                .Where(j => siteAccountIds.Contains(j.AccountId)).ToListAsync(ct);
            if (siteId.Equals("twitter", StringComparison.OrdinalIgnoreCase))
            {
                var knownIds = accountSite.Keys.ToList();
                var orphan = await legacy.Jobs.AsNoTracking()
                    .Where(j => !knownIds.Contains(j.AccountId)).ToListAsync(ct);
                foreach (var job in orphan) job.SiteId = "twitter";
                jobs.AddRange(orphan);
                orphans += orphan.Count;
            }
            foreach (var job in jobs) job.SiteId = siteId;
            db.Jobs.AddRange(jobs);
            await db.SaveChangesAsync(ct); // 先落 Job 取真实 Id 供 File 外键

            // File 归属经 JobId（本库 Job 的 Id 即主库 Id——自增主键原样搬运）
            var jobIds = jobs.Select(j => j.Id).ToList();
            var siteFiles = await legacy.Files.AsNoTracking()
                .Where(f => jobIds.Contains(f.JobId)).ToListAsync(ct);
            foreach (var file in siteFiles) file.SiteId = siteId;
            db.Files.AddRange(siteFiles);
            await db.SaveChangesAsync(ct);
            userTotal += await db.Users.CountAsync(ct);
            jobTotal += jobs.Count;
            fileTotal += siteFiles.Count;
        }

        // 设置键：site.<id>.options → 对应平台库 SiteSettings（值按行 JSON 化，保留 string 语义）；
        // 其余键 → 全局库（主库改名后在新 ggdown.db 重建）
        var entries = await legacy.Settings.AsNoTracking().ToListAsync(ct);
        var global = new Dictionary<string, string?>();
        foreach (var entry in entries)
        {
            if (entry.Key.StartsWith("site.", StringComparison.Ordinal) &&
                entry.Key.EndsWith(".options", StringComparison.Ordinal) &&
                entry.Key.Length > "site.".Length + ".options".Length)
            {
                var siteId = entry.Key["site.".Length..(entry.Key.Length - ".options".Length)];
                await WriteSiteOptionsAsync(factory, siteId, entry.Value, ct);
            }
            else
            {
                global[entry.Key] = entry.Value;
            }
        }

        // Final review Important 5：全局键先写入临时全局库，写成功才让位主库——
        // 原顺序（先改名后写全局键）一旦写库失败，升级路径上用户丢全部全局设置（只剩默认值，.bak 只是人工恢复点）
        var globalTmp = main + ".global-tmp";
        if (File.Exists(globalTmp)) File.Delete(globalTmp); // 上次中断的残留：整体重写（SetAsync 幂等，防半成品）
        await using (var globalDb = new GGdownGlobalDbContext(
            new DbContextOptionsBuilder<GGdownGlobalDbContext>().UseSqlite($"Data Source={globalTmp};Pooling=False").Options))
        {
            await globalDb.Database.MigrateAsync(ct); // 与启动初始化同一路径（EnsureCreated 产物会让 MigrateAsync 报「表已存在」）
            var store = new GGdownSettingsStore<GGdownGlobalDbContext>(globalDb);
            foreach (var (key, value) in global)
                if (value is not null)
                    await store.SetAsync<string>(key, value, ct);
        }

        legacy.Dispose(); // 释放主库句柄再改名
        // 清空 ADO.NET 连接池：启动早期 AppSettings/ICurrentSite（DI 工厂默认 Pooling=true）借出并归还的
        // 连接驻留池中、仍持有主库文件句柄——不清池则 File.Move 被 Windows 文件锁挡下，拆分静默失败
        // （真机冒烟两次复现：IOException at File.Move；池内无在途连接，全局清池在启动点是安全的）
        SqliteConnection.ClearAllPools();
        File.Move(main, backup, overwrite: true); // 完成标志：主库让位
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = main + suffix;
            if (File.Exists(sidecar)) File.Delete(sidecar); // checkpoint 后为空壳，随主库让位一并清理
        }
        File.Move(globalTmp, main, overwrite: true); // 全局库就位（此前主库始终是事实源）

        return new Result(sites.Count, accounts.Count, userTotal, jobTotal, fileTotal, orphans);
    }

    /// <summary>旧 blob（整字典 JSON）拆为单键行；值还原 CLR 类型后逐键 JSON 序列化。</summary>
    private static async Task WriteSiteOptionsAsync(
        SiteDbContextFactory factory, string siteId, string? blob, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blob)) return;
        Dictionary<string, object?>? dict;
        try { dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(blob, JsonOptions); }
        catch (JsonException) { return; }
        if (dict is null || dict.Count == 0) return;

        await using var db = await factory.CreateAsync(siteId, ct);
        foreach (var (key, value) in dict)
        {
            var clr = Unwrap(value);
            db.SiteSettings.Add(new SiteSettingEntry
            {
                SiteId = siteId,
                Key = key,
                Value = JsonSerializer.Serialize(clr, JsonOptions),
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private static object? Unwrap(object? value) => value switch
    {
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
            JsonValueKind.Null => null,
            _ => e,
        },
        _ => value,
    };
}
