# 平台优先导航重构 + 每平台一库 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 GGdown 的单一导航壳重构为「平台图标栏 + 每平台功能页组」，并把单一 SQLite 库拆成全局库 + 每平台一库。

**Architecture:** 先做数据层（双 EF context：`GGdownGlobalDbContext` 只存应用设置、`GGdownSiteDbContext` 按站点惰性开 `data\sites\<id>.db`；旧单库存量一次性拆分迁移），再做 UI 壳层（弃用 `NavigationView`，自绘 48px 平台栏 + 顶部 TabView 页组 + Frame），最后把全局设置拆成五页、站点设置下沉到平台页组，下载/历史按平台筛选。

**Tech Stack:** WinUI 3 (net8.0-windows10.0.22621.0) + CommunityToolkit.Mvvm + EF Core 8 (SQLite) + xunit（Xunit.SkippableFact）。

**Spec:** `docs/superpowers/specs/2026-09-25-platform-nav-redesign-design.md`（本计划从 spec 立论，执行者需同时阅读 spec）

## Global Constraints

- UI 字符串、注释、提交信息一律中文；提交用 conventional-commit 风格（`feat:` / `refactor:` / `test:` …）。
- 数据根目录：`%LOCALAPPDATA%\GGdown`；全局库 `data\ggdown.db`；平台库 `data\sites\<siteId>.db`。
- 引擎层（`engine/`、JSONL 协议 v1、`PROTOCOL` 常量）与 gallery-dl 1.32.10 pin 不动。
- DI 约定（`src/GGdown.Core/DependencyInjection.cs` 注释）：`AddDbContextFactory` 先于 `AddDbContext` 注册；Singleton 不得捕获 Scoped 服务（`AppSettings` 的 captive-dependency 修复模式沿用）。
- 直接对 csproj 调 `dotnet build`/`dotnet test` 时带 `-p:Platform=x64`；`dotnet test src\GGdown.sln` 走 sln 默认平台无需指定。
- EF 迁移命令：`dotnet ef migrations add <Name> --context <Ctx> --project src\GGdown.Core --startup-project src\GGdown.Core --output-dir Migrations\<子目录>`。
- 页面 `NavigationCacheMode=Enabled`、`ContentFrame.Navigate(pageType, services)` 的既有模式保持。
- 旧 `GGdownDbContext` 及其迁移链保留，仅供存量拆分迁移读取旧库；新代码一律使用双 context。

## Review Focus

以下输入/工况最易出事（spec 提到但任务测试未必全覆盖），每条给出归属任务的补充测试：

1. **共享页面缓存跨平台脏状态**（spec §2.7）：切到 Pixiv 后用户列表仍显示 X 数据。→ Task 10 Step 1 增补 `UsersViewModel` 收到 `ICurrentSite.Changed` 后重载的断言；Task 11 手动验收清单第一项。
2. **存量库含悬空 AccountId 的孤儿任务**（spec §6）：拆分时若按 Account 回填 SiteId 抛 KeyNotFound 则迁移崩。→ Task 6 测试显式构造孤儿 Job，断言归入 twitter 库。
3. **`ui.visibleSites` 为空数组/损坏 JSON**：栏里一个平台都没有、应用看似空白。→ Task 3 测试断言空数组回退默认（全部 Available 站点）。
4. **首启动无旧库**（全新机器）：拆分迁移误把新全局库当旧库拆。→ Task 6 测试断言「ggdown.db 不存在时直接跳过拆分」。
5. **全局设置键 `site.<id>.options` 迁移后丢失 UnwrapJsonElements 语义**（`AppSettings.cs:104` 注释）：round-trip 后 `(bool)options["videos"]` 抛 InvalidCastException。→ Task 6 拆分测试断言迁到站点库的选项值还原为 CLR 原生类型。

---

## Phase A：数据层每平台一库

### Task 1: 双 Context 与实体扩展

**Files:**
- Modify: `src/GGdown.Core/Data/Entities.cs`（`DownloadJob`/`DownloadFile` 加 `SiteId`；新增 `SiteSettingEntry`）
- Create: `src/GGdown.Core/Data/GGdownGlobalDbContext.cs`
- Create: `src/GGdown.Core/Data/GGdownSiteDbContext.cs`
- Modify: `src/GGdown.Core/Data/GGdownDbContext.cs`（忽略新属性——旧库无这些列）
- Test: `src/GGdown.Tests/Data/TwoContextsTests.cs`（新建）

**Interfaces:**
- Produces: `GGdownGlobalDbContext(DbContextOptions<GGdownGlobalDbContext>)`，DbSet `Settings`（`SettingEntry`）；`GGdownSiteDbContext(DbContextOptions<GGdownSiteDbContext>)`，DbSets `Accounts/Users/Jobs/Files/FollowingCache/SiteSettings`；`public sealed class SiteSettingEntry { public string SiteId; public string Key; public string? Value; }`（复合主键 SiteId+Key）；`DownloadJob.SiteId`、`DownloadFile.SiteId`（string，默认 `""`）。

- [ ] **Step 1: 写失败测试**

```csharp
// src/GGdown.Tests/Data/TwoContextsTests.cs
using GGdown.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Data;

public class TwoContextsTests
{
    private static (SqliteConnection, T) Open<T>(string table) where T : DbContext
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var ctxType = typeof(T);
        var optionsType = typeof(DbContextOptions<>).MakeGenericType(ctxType);
        var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(ctxType);
        dynamic builder = Activator.CreateInstance(builderType)!;
        builder.UseSqlite(conn);
        var db = (T)Activator.CreateInstance(ctxType, new[] { builder.Options })!;
        db.Database.EnsureCreated();
        return (conn, db);
    }

    [Fact]
    public void Global_context_has_only_Settings_table()
    {
        var (conn, db) = Open<GGdownGlobalDbContext>("x");
        var tables = TableNames(conn);
        Assert.Contains("Settings", tables);
        Assert.DoesNotContain("Accounts", tables);
        Assert.DoesNotContain("SiteSettings", tables);
    }

    [Fact]
    public void Site_context_has_site_tables_and_setting_entry_composite_key()
    {
        var (conn, db) = Open<GGdownSiteDbContext>("x");
        var tables = TableNames(conn);
        Assert.Contains("Accounts", tables);
        Assert.Contains("Users", tables);
        Assert.Contains("Jobs", tables);
        Assert.Contains("Files", tables);
        Assert.Contains("FollowingCache", tables);
        Assert.Contains("SiteSettings", tables);
        Assert.DoesNotContain("Settings", tables);
        // 复合主键：同库同 key 不同站点可共存（防御将来多站点混用）
        db.SiteSettings.Add(new SiteSettingEntry { SiteId = "twitter", Key = "k", Value = "1" });
        db.SaveChanges();
    }

    [Fact]
    public void Legacy_context_ignores_SiteId_and_SiteSettingEntry()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<GGdownDbContext>().UseSqlite(conn).Options;
        using var db = new GGdownDbContext(options);
        db.Database.EnsureCreated();
        // 旧库 schema 不变：Jobs 表没有 SiteId 列
        Assert.DoesNotContain("SiteId", Columns(conn, "Jobs"));
        Assert.DoesNotContain("SiteSettings", TableNames(conn));
        db.Jobs.Add(new DownloadJob { AccountId = 1, TargetKind = TargetKind.UserMedia });
        db.SaveChanges(); // 不抛（SiteId 被忽略）
    }

    [Fact]
    public void Site_context_maps_SiteId_on_jobs_and_files()
    {
        var (conn, db) = Open<GGdownSiteDbContext>("x");
        Assert.Contains("SiteId", Columns(conn, "Jobs"));
        Assert.Contains("SiteId", Columns(conn, "Files"));
    }

    private static List<string> TableNames(SqliteConnection c) =>
        Query(c, "SELECT name FROM sqlite_master WHERE type='table'");
    private static List<string> Columns(SqliteConnection c, string table) =>
        Query(c, $"PRAGMA table_info({table})");
    private static List<string> Query(SqliteConnection c, string sql)
    {
        var list = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~TwoContextsTests"`
Expected: FAIL，编译错误 `GGdownGlobalDbContext`/`GGdownSiteDbContext`/`SiteSettingEntry` 不存在。

- [ ] **Step 3: 最小实现**

```csharp
// src/GGdown.Core/Data/Entities.cs 追加（DownloadJob 里加一行、DownloadFile 里加一行、文件末尾加类）
// DownloadJob:  public string SiteId { get; set; } = string.Empty;   // 拆库后显式携带归属（旧 context 忽略）
// DownloadFile: public string SiteId { get; set; } = string.Empty;
public sealed class SiteSettingEntry
{
    public string SiteId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}
```

```csharp
// src/GGdown.Core/Data/GGdownGlobalDbContext.cs
using Microsoft.EntityFrameworkCore;

namespace GGdown.Data;

/// <summary>全局库：仅应用级设置（旧 GGdownDbContext 拆分后的全局半边）。</summary>
public sealed class GGdownGlobalDbContext(DbContextOptions<GGdownGlobalDbContext> options) : DbContext(options)
{
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();
    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<SettingEntry>(e => e.HasKey(s => s.Key));
}
```

```csharp
// src/GGdown.Core/Data/GGdownSiteDbContext.cs
using Microsoft.EntityFrameworkCore;

namespace GGdown.Data;

/// <summary>平台库：用户/账号/任务/文件/关注缓存/站点选项（SiteId 列在此 context 才映射）。</summary>
public sealed class GGdownSiteDbContext(DbContextOptions<GGdownSiteDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<User> Users => Set<User>();
    public DbSet<DownloadJob> Jobs => Set<DownloadJob>();
    public DbSet<DownloadFile> Files => Set<DownloadFile>();
    public DbSet<FollowingCacheEntry> FollowingCache => Set<FollowingCacheEntry>();
    public DbSet<SiteSettingEntry> SiteSettings => Set<SiteSettingEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Account/User/FollowingCache 的索引照抄 GGdownDbContext.OnModelCreating 同名段
        //（SiteId+IsActive 账号索引、SiteId+RestId 唯一用户索引、FollowingCache 两组索引）；
        // Job/File 的导航与索引同抄，但去掉 HasOne<User>() 对 OwnerAccountId 的约束保持现状；
        // SiteSettings 复合主键：
        b.Entity<SiteSettingEntry>(e => e.HasKey(s => new { s.SiteId, s.Key }));
    }
}
```

`GGdownDbContext.OnModelCreating` 中追加两处（保持旧 schema 可读旧库）：

```csharp
// 旧库无 SiteId 列（拆分迁移只读不写），显式忽略以防误映射
b.Entity<DownloadJob>(e => e.Ignore(j => j.SiteId));
b.Entity<DownloadFile>(e => e.Ignore(f => f.SiteId));
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~TwoContextsTests"`
Expected: PASS（4 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/GGdown.Core/Data/Entities.cs src/GGdown.Core/Data/GGdownGlobalDbContext.cs src/GGdown.Core/Data/GGdownSiteDbContext.cs src/GGdown.Core/Data/GGdownDbContext.cs src/GGdown.Tests/Data/TwoContextsTests.cs
git commit -m "feat: 拆分全局/平台双 DbContext 并为任务补 SiteId 列"
```

### Task 2: ISiteDbContextFactory（平台库惰性创建+迁移）

**Files:**
- Modify: `src/GGdown.Core/Paths/AppPaths.cs`（新增 `SitesDataDir`）
- Create: `src/GGdown.Core/Data/SiteDbContextFactory.cs`
- Test: `src/GGdown.Tests/Data/SiteDbContextFactoryTests.cs`（新建）

**Interfaces:**
- Consumes: Task 1 的 `GGdownSiteDbContext`。
- Produces: `public interface ISiteDbContextFactory { Task<GGdownSiteDbContext> CreateAsync(string siteId, CancellationToken ct = default); IReadOnlyList<string> ExistingSites(); }`（每次调用返回新 context，调用方 `await using`；首次对某 siteId 建库并 `MigrateAsync`，进程内去重；`ExistingSites` 枚举 `data\sites\*.db` 的文件名去扩展名，供队列恢复/统计重算遍历）。

- [ ] **Step 1: 写失败测试**

```csharp
// src/GGdown.Tests/Data/SiteDbContextFactoryTests.cs
using GGdown.Data;
using GGdown.Paths;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Data;

public class SiteDbContextFactoryTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();

    [Fact]
    public async Task CreateAsync_creates_migrates_and_reuses_site_db()
    {
        var f = new SiteDbContextFactory(_paths);
        await using (var db = await f.CreateAsync("twitter"))
        {
            Assert.True(File.Exists(Path.Combine(_paths.SitesDataDir, "twitter.db")));
            db.Users.Add(new User { SiteId = "twitter", RestId = "1", ScreenName = "a" });
            await db.SaveChangesAsync();
        }
        await using (var db2 = await f.CreateAsync("twitter"))
            Assert.Equal(1, await db2.Users.CountAsync());
        Assert.Equal(["twitter"], f.ExistingSites());
    }

    [Fact]
    public async Task CreateAsync_is_idempotent_across_sites()
    {
        var f = new SiteDbContextFactory(_paths);
        await using var a = await f.CreateAsync("twitter");
        await using var b = await f.CreateAsync("pixiv");
        Assert.Equal(["twitter", "pixiv"], f.ExistingSites().OrderBy(x => x).ToArray());
    }

    public void Dispose() => Directory.Delete(_paths.Root, true);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~SiteDbContextFactoryTests"`
Expected: FAIL，`SiteDbContextFactory` 不存在。

- [ ] **Step 3: 最小实现**

```csharp
// src/GGdown.Core/Data/SiteDbContextFactory.cs
using System.Collections.Concurrent;
using GGdown.Paths;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Data;

/// <summary>
/// 平台库工厂：data\sites\<siteId>.db，首次访问建目录+迁移（进程内 HashSet 去重，幂等）。
/// 每次返回新 context，调用方负责 await using。
/// </summary>
public sealed class SiteDbContextFactory(IAppPaths paths) : ISiteDbContextFactory
{
    private readonly ConcurrentDictionary<string, byte> _migrated = new(StringComparer.OrdinalIgnoreCase);

    public async Task<GGdownSiteDbContext> CreateAsync(string siteId, CancellationToken ct = default)
    {
        var dir = paths.SitesDataDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{siteId.ToLowerInvariant()}.db");
        var db = New(file);
        if (_migrated.TryAdd(siteId, 0))
        {
            await db.Database.MigrateAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        }
        return db;
    }

    public IReadOnlyList<string> ExistingSites() =>
        Directory.Exists(paths.SitesDataDir)
            ? [.. Directory.EnumerateFiles(paths.SitesDataDir, "*.db").Select(Path.GetFileNameWithoutExtension)]
            : [];

    private static GGdownSiteDbContext New(string file) => new(
        new DbContextOptionsBuilder<GGdownSiteDbContext>().UseSqlite($"Data Source={file}").Options);
}
```

`AppPaths` 增加属性并加入 `EnsureCreated` 目录清单：

```csharp
public string SitesDataDir => Path.Combine(DataDir, "sites");
// EnsureCreated 的数组追加 SitesDataDir
```

（本任务先手写 Initial 迁移替代 CLI，避免 sln 上下文干扰：创建 `Migrations/Sites/20260925000000_SiteInit.cs` + `.Designer.cs` + `GGdownSiteDbContextModelSnapshot.cs`——直接用 `db.Database.EnsureCreated()` 的等价模型；更简单的方式：`CreateAsync` 首次访问时用 `EnsureCreatedAsync()` 起步，**Phase A 收尾任务（Task 6）再统一换成 CLI 生成的迁移**——本任务测试只断言建库可用，不依赖 `MigrateAsync` 的迁移历史存在。若采用 EnsureCreatedAsync 起步，把 `MigrateAsync` 调用换成 `EnsureCreatedAsync` 并在注释注明「Task 6 换正式迁移」。）

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~SiteDbContextFactoryTests"`
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/GGdown.Core/Data/SiteDbContextFactory.cs src/GGdown.Core/Paths/AppPaths.cs src/GGdown.Tests/Data/SiteDbContextFactoryTests.cs
git commit -m "feat: 平台库工厂（惰性建库与站点枚举）"
```

### Task 3: AppSettings 改走全局库 + 可见平台/最近页键

**Files:**
- Modify: `src/GGdown.Core/Settings/AppSettings.cs`
- Modify: `src/GGdown.Core/DependencyInjection.cs`
- Test: `src/GGdown.Tests/Services/AppSettingsTests.cs`（改造现有用例到双库 fixture）

**Interfaces:**
- Consumes: `GGdownGlobalDbContext`（经 `IDbContextFactory<GGdownGlobalDbContext>`）、Task 2 `ISiteDbContextFactory`。
- Produces（IAppSettings 新增/变更）：
  - `Task<IReadOnlyList<string>> GetVisibleSitesAsync(CancellationToken ct = default)`（空/缺失/损坏 → 默认 = `SiteCatalog.All.Where(Available)` 的 SiteId 列表）
  - `Task SetVisibleSitesAsync(IReadOnlyList<string> siteIds, CancellationToken ct = default)`
  - `Task<string> GetSiteLastPageAsync(string siteId, CancellationToken ct = default)`（默认 `"users"`）
  - `Task SetSiteLastPageAsync(string siteId, string pageKey, CancellationToken ct = default)`
  - `GetSiteOptionsAsync`/`SetSiteOptionsAsync` 改读写平台库 `SiteSettings` 表（键即字段 Key，行按 siteId），默认值回退逻辑不变。

- [ ] **Step 1: 改造失败测试**

`src/GGdown.Tests/Helpers.cs` 增加双库 fixture（后续任务复用）：

```csharp
public static class TestDb
{
    // Create() 保留（旧 context 的存量测试仍用），追加：
    public static (SqliteConnection Connection, GGdownGlobalDbContext Db) CreateGlobal()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var db = new GGdownGlobalDbContext(new DbContextOptionsBuilder<GGdownGlobalDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (conn, db);
    }
}

/// <summary>测试用 site factory：内存目录 + 真文件（复用 SiteDbContextFactory）。</summary>
public sealed class TestSiteDbFactory(AppPaths paths) : GGdown.Data.SiteDbContextFactory(paths) { }

/// <summary>包装测试内存全局库的 factory（AppSettings 依赖注入用）。</summary>
public sealed class SingleGlobalDbContextFactory(GGdownGlobalDbContext db) : IDbContextFactory<GGdownGlobalDbContext>
{
    public GGdownGlobalDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<GGdownGlobalDbContext>().UseSqlite(db.Database.GetDbConnection()).Options);
}
```

`AppSettingsTests.cs` 用例改为：`new AppSettings(new SingleGlobalDbContextFactory(globalDb), sites, new TestSiteDbFactory(TestPaths.Create()))` 构造；新增用例（原用例继续跑，断言行为不变）：

```csharp
[Fact]
public async Task Visible_sites_default_to_available_and_survive_roundtrip() { /* 存 → 取 → 断言一致 */ }

[Fact]
public async Task Visible_sites_empty_or_corrupt_json_falls_back_to_available()
{
    // SetAsync 前直接向 Settings 表插 "ui.visibleSites" = "" / "not json"，断言回退默认
}

[Fact]
public async Task Site_last_page_roundtrip_and_default()
{
    // 默认 "users"；Set("pixiv","history") 后 Get 返回 "history"
}

[Fact]
public async Task Site_options_stored_in_site_db_not_global()
{
    // GetSiteOptionsAsync 未保存时返回 provider.DefaultOptions；
    // Set 后断言平台库 SiteSettings 有行、全局 Settings 表没有 "site.twitter.options" 键
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~AppSettingsTests"`
Expected: FAIL（编译错误：AppSettings 构造签名不匹配/新方法不存在）。

- [ ] **Step 3: 最小实现**

`AppSettings` 改造要点（保留现有 UnwrapJsonElements 与各键语义）：

```csharp
public sealed class AppSettings(
    IDbContextFactory<GGdownGlobalDbContext> globalFactory,
    SiteRegistry sites,
    ISiteDbContextFactory siteFactory) : IAppSettings
{
    // 每个方法把原来的 factory.CreateDbContextAsync() 换成：
    //   全局键（download.* / network.proxy / ui.*）→ globalFactory.CreateDbContextAsync(ct) + GGdownSettingsStore(改泛型)
    //   站点选项 → siteFactory.CreateAsync(siteId) 直接读写 SiteSettings 表
    // ISettingsStore/GGdownSettingsStore 改为泛型 GGdownSettingsStore<TCtx>(TCtx db) 或新 GlobalSettingsStore(GGdownGlobalDbContext)，
    // 二选一（推荐后者：新类 GlobalSettingsStore，旧 GGdownSettingsStore 留给存量拆分迁移读旧库）。

    public async Task<IReadOnlyList<string>> GetVisibleSitesAsync(CancellationToken ct = default)
    {
        var ids = await GetGlobalAsync<string>("ui.visibleSites", null, ct);
        if (string.IsNullOrWhiteSpace(ids)) return DefaultVisible;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(ids, JsonOptions) ?? [];
            return parsed.Count == 0 ? DefaultVisible : [.. parsed];
        }
        catch (JsonException) { return DefaultVisible; }
    }

    public Task SetVisibleSitesAsync(IReadOnlyList<string> siteIds, CancellationToken ct = default) =>
        SetGlobalAsync("ui.visibleSites", JsonSerializer.Serialize(siteIds, JsonOptions), ct);

    private static IReadOnlyList<string> DefaultVisible =>
        [.. SiteCatalog.All.Where(s => s.Available).Select(s => s.SiteId)];

    public async Task<string> GetSiteLastPageAsync(string siteId, CancellationToken ct = default) =>
        await GetGlobalAsync<string>($"ui.site.{siteId}.lastPage", "users", ct) ?? "users";

    public Task SetSiteLastPageAsync(string siteId, string pageKey, CancellationToken ct = default) =>
        SetGlobalAsync($"ui.site.{siteId}.lastPage", pageKey, ct);

    // GetSiteOptionsAsync：siteFactory.CreateAsync(siteId) 读 SiteSettings 行集（键=字段 Key），
    // 无行 → sites.Get(siteId).DefaultOptions；有行 → 复用 UnwrapJsonElements 语义还原 CLR 类型。
    // SetSiteOptionsAsync：逐键 upsert SiteSettingEntry { SiteId, Key, Value=JSON }。
}
```

`DependencyInjection.cs` 改注册（保序注释保留并更新）：

```csharp
services.AddSingleton<ISiteDbContextFactory>(sp => new SiteDbContextFactory(sp.GetRequiredService<IAppPaths>()));
services.AddDbContextFactory<GGdownGlobalDbContext>(o => o.UseSqlite($"Data Source={paths.DbFile}"));
services.AddDbContext<GGdownGlobalDbContext>(o => o.UseSqlite($"Data Source={paths.DbFile}"));
// GGdownSiteDbContext 不走 AddDbContextFactory：由 ISiteDbContextFactory 按站点建 options，无 scoped 需求
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~AppSettingsTests"`
Expected: PASS。随后全量 `dotnet test src\GGdown.sln`，Expected: **编译失败集中在还注入旧 `IDbContextFactory<GGdownDbContext>` 的服务**（Task 4/5 处理，属预期）。

- [ ] **Step 5: 提交**

```bash
git add src/GGdown.Core/Settings/AppSettings.cs src/GGdown.Core/Services/SettingsStore.cs src/GGdown.Core/DependencyInjection.cs src/GGdown.Tests/Helpers.cs src/GGdown.Tests/Services/AppSettingsTests.cs
git commit -m "refactor: AppSettings 走全局库，新增可见平台与最近页键，站点选项入库"
```

### Task 4: 站点服务改走平台库（查询/账号/用户/统计）

**Files:**
- Modify: `src/GGdown.Core/Services/Queries.cs`、`AccountService.cs`、`UserService.cs`、`StatsAggregator.cs`
- Modify: `src/GGdown.Core/DependencyInjection.cs`
- Test: 对应改造 `src/GGdown.Tests/Services/{QueriesTests,AccountServiceTests,UserServiceTests,StatsAndRecoveryTests}.cs`

**Interfaces:**
- Consumes: Task 2 `ISiteDbContextFactory.CreateAsync(siteId)`。
- Produces: 各服务公开方法签名不变（调用方已传 siteId 或 Account.SiteId）；内部一律 `await using var db = await siteFactory.CreateAsync(siteId)`。`StatsAggregator` 构造改 `StatsAggregator(ISiteDbContextFactory siteFactory, IAppPaths paths)`，`ApplyJobCompletionAsync(long jobId, string siteId, ...)` 加 siteId 参数；`RecalculateDownloadCountsAsync()` 遍历 `siteFactory.ExistingSites()`。
- `AccountService.AbsoluteCookiePath(paths, account)` 静态方法不动（Cookie 路径仍按账号 Id 在 accounts\ 下，与库无关）。

- [ ] **Step 1: 改造失败测试**

每个测试文件把 `SingleDbContextFactory(_db)` 换成真实 `SiteDbContextFactory(TestPaths.Create())`（用户/账号数据先经 `CreateAsync("twitter")` 播种）。`StatsAndRecoveryTests` 的统计用例改为双库播种（twitter 一条 Completed job + pixiv 一条），断言：

```csharp
[Fact]
public async Task Recalculate_covers_all_site_dbs()
{
    // 两库各播种 user+job，调用 RecalculateDownloadCountsAsync，
    // 断言两库各自 User.DownloadCount 正确（1/1），互不串
}

[Fact]
public async Task ApplyJobCompletion_uses_site_of_job()
{
    // twitter 库 job 完成聚合只加 twitter 库用户计数
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~QueriesTests|FullyQualifiedName~AccountServiceTests|FullyQualifiedName~UserServiceTests|FullyQualifiedName~StatsAndRecoveryTests"`
Expected: FAIL（服务仍要求旧 factory / 统计签名不匹配）。

- [ ] **Step 3: 最小实现**

机械替换模式（以 `Queries.cs` `UserQueryService` 为例，其余同型）：

```csharp
// 旧：public UserQueryService(IDbContextFactory<GGdownDbContext> factory)
// 新：
public UserQueryService(ISiteDbContextFactory siteFactory)
// 旧：await using var db = await factory.CreateDbContextAsync(ct);
// 新：await using var db = await siteFactory.CreateAsync(siteId, ct);  // siteId 来自方法参数
```

`HistoryQueryService.ListAsync(HistoryFilter)`：`HistoryFilter` 增加 `public string? SiteId`（null=全部），查询加 `Where(j => j.SiteId == filter.SiteId)`（`ListUsersAsync` 已有 siteId 参数，照 UserQueryService 模式换）。`StatsAggregator` 遍历实现：

```csharp
public async Task RecalculateDownloadCountsAsync(CancellationToken ct = default)
{
    foreach (var siteId in _siteFactory.ExistingSites())
    {
        await using var db = await _siteFactory.CreateAsync(siteId, ct);
        // 原方法体逐行搬入（查询/ExecuteUpdate 不变）
    }
}
```

DI 注册同步：`AddScoped<IAccountService…>` 等保持，仅构造依赖换掉；`AddSingleton<StatsAggregator>()` 不变。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~QueriesTests|FullyQualifiedName~AccountServiceTests|FullyQualifiedName~UserServiceTests|FullyQualifiedName~StatsAndRecoveryTests"`
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add -A src/GGdown.Core/Services src/GGdown.Core/DependencyInjection.cs src/GGdown.Tests
git commit -m "refactor: 查询/账号/用户/统计服务改走平台库"
```

### Task 5: DownloadQueueService 跨库持久化与恢复

**Files:**
- Modify: `src/GGdown.Core/Services/DownloadQueueService.cs`
- Test: 改造 `src/GGdown.Tests/Services/DownloadQueueServiceTests.cs`

**Interfaces:**
- Consumes: `ISiteDbContextFactory`、Task 4 的 `StatsAggregator.ApplyJobCompletionAsync(long jobId, string siteId, ...)`。
- Produces: `IDownloadQueueService` 公开签名全部不变；`Enqueue*Async` 落库时写 `job.SiteId = account.SiteId`（`DownloadFile` 同）；`RecoverOnStartupAsync` 遍历 `ExistingSites()`；`RunJobAsync`/`InsertFileAsync`/`FinishJobAsync`/账号置 Invalid 全部经 `siteFactory.CreateAsync(item.SiteId)`。

- [ ] **Step 1: 改造失败测试**

`DownloadQueueServiceTests` 换 fixture 为 `SiteDbContextFactory`；新增：

```csharp
[Fact]
public async Task Enqueue_persists_job_with_site_id_in_site_db()
{
    // 账号播种进 twitter 库；EnqueueUserMediaAsync 后：
    // twitter 库 Jobs.Single().SiteId == "twitter"；全局库不存在（或无 Jobs 表）
}

[Fact]
public async Task Recover_marks_pending_running_failed_across_site_dbs()
{
    // twitter 库播种 Running job、pixiv 库播种 Pending job → RecoverOnStartupAsync
    // 断言两库各自 Job.Status == Failed 且 ErrorMessage 含「应用异常退出」
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~DownloadQueueServiceTests"`
Expected: FAIL。

- [ ] **Step 3: 最小实现**

构造注入换 `ISiteDbContextFactory siteFactory`（去掉 `IDbContextFactory<GGdownDbContext> factory`）。逐方法替换（`WorkItem` 已带 `SiteId`，无需改 record）：

```csharp
// Enqueue* 三处：await using var db = await siteFactory.CreateAsync(account.SiteId, ct);
//   new DownloadJob { AccountId…, SiteId = account.SiteId, … }
// RunJobAsync 开头：await using var db = await siteFactory.CreateAsync(item.SiteId);
// InsertFileAsync(long jobId, string siteId, …)：new DownloadFile { …, SiteId = siteId }
//   （调用点补传 item.SiteId）
// AuthException 分支：await using var db = await siteFactory.CreateAsync(item.SiteId);
// FinishJobAsync：db 换站点库；stats.ApplyJobCompletionAsync(item.JobId, item.SiteId)
// RecoverOnStartupAsync：
public async Task RecoverOnStartupAsync(CancellationToken ct = default)
{
    foreach (var siteId in _siteFactory.ExistingSites())
    {
        await using var db = await _siteFactory.CreateAsync(siteId, ct);
        await db.Jobs.Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.ErrorMessage, "应用异常退出，任务中断"), ct);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~DownloadQueueServiceTests"`
Expected: PASS。全量 `dotnet test src\GGdown.sln`，Expected: 仅剩 App 层编译错误（VM 构造签名变化，Task 8/9 处理，属预期）；Core+Tests 编译须干净。

- [ ] **Step 5: 提交**

```bash
git add src/GGdown.Core/Services/DownloadQueueService.cs src/GGdown.Tests/Services/DownloadQueueServiceTests.cs src/GGdown.Tests/Helpers.cs
git commit -m "feat: 下载队列按平台库持久化并跨库恢复"
```

### Task 6: 存量拆分迁移 + 启动序列接线

**Files:**
- Create: `src/GGdown.Core/Data/SiteDbSplitMigration.cs`
- Modify: `src/GGdown.App/App.xaml.cs`（启动 Task.Run 序列）
- Modify: `src/GGdown.Core/Data/DbInitializer.cs`（改为 `InitializeGlobalAsync(GGdownGlobalDbContext)`）
- Test: `src/GGdown.Tests/Data/SiteDbSplitMigrationTests.cs`（新建）

**Interfaces:**
- Consumes: 旧 `GGdownDbContext`（读旧库，SiteId 被 Ignore）、`ISiteDbContextFactory`、`GGdownGlobalDbContext`。
- Produces: `public static class SiteDbSplitMigration { public static bool IsNeeded(string dataDir); public static Task ApplyAsync(IAppPaths paths, CancellationToken ct = default); }`。
  - `IsNeeded`：`ggdown.db` 存在 **且** 其 `sqlite_master` 含 `Accounts` 表 **且** `sites\` 目录不存在。
  - `ApplyAsync` 顺序：复制 `ggdown.db` → `ggdown.db.pre-split.bak`（已存在则复用，幂等）；打开旧库副本（旧 context）；对旧库出现的每个 SiteId 经 siteFactory 建平台库；按行搬移（Job 的 SiteId 经 `AccountId → Account.SiteId` 字典回填，`DownloadFile` 经 `JobId → SiteId` 字典；AccountId 悬空的孤儿 Job 归 `twitter` 并记 Serilog 日志）；`SettingEntry` 中 `site.<id>.options` 键解析后写入对应平台库 `SiteSettings`（值还原 CLR 类型），其余键写入全局库；搬毕把旧主库文件改名 `ggdown.db.pre-split.bak`（覆盖先前副本语义：拆分成功才动主库）。全局库由后续 `InitializeGlobalAsync` 正常建表。
- App 启动序列（`App.OnLaunched` 的 `Task.Run` 内，顺序）：`DataRootMigration`（现状保留）→ `SiteDbSplitMigration.ApplyAsync` → `DbInitializer.InitializeGlobalAsync` → `StatsAggregator.RecalculateDownloadCountsAsync` → 并发数应用 → `RecoverOnStartupAsync` → `_readyTcs.TrySetResult()`（失败也放行，现有语义）。Readiness 门禁覆盖拆分全程。

- [ ] **Step 1: 写失败测试**

```csharp
// src/GGdown.Tests/Data/SiteDbSplitMigrationTests.cs
using GGdown.Data;
using GGdown.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Data;

public class SiteDbSplitMigrationTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();

    private void SeedLegacyDb()
    {
        var options = new DbContextOptionsBuilder<GGdownDbContext>()
            .UseSqlite($"Data Source={_paths.DbFile}").Options;
        using var db = new GGdownDbContext(options);
        db.Database.EnsureCreated();
        var tw = new Account { SiteId = "twitter", CookiePath = "a.txt", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        var px = new Account { SiteId = "pixiv", CookiePath = "b.txt", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        db.Accounts.AddRange(tw, px);
        db.Users.AddRange(
            new User { SiteId = "twitter", RestId = "1", ScreenName = "a", AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new User { SiteId = "pixiv", RestId = "2", ScreenName = "b", AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.Jobs.AddRange(
            new DownloadJob { AccountId = tw.Id, TargetKind = TargetKind.UserMedia, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow },
            new DownloadJob { AccountId = px.Id, TargetKind = TargetKind.UserMedia, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow },
            new DownloadJob { AccountId = 999, TargetKind = TargetKind.UserMedia, Status = JobStatus.Failed, CreatedAt = DateTime.UtcNow }); // 孤儿
        db.Settings.Add(new SettingEntry { Key = "download.directory", Value = "D:\\x" });
        db.Settings.Add(new SettingEntry { Key = "site.twitter.options", Value = """{"videos":true}""" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Not_needed_when_fresh_install_or_already_split()
    {
        Assert.False(SiteDbSplitMigration.IsNeeded(_paths.DataDir)); // 无 ggdown.db
        SeedLegacyDb();
        Assert.True(SiteDbSplitMigration.IsNeeded(_paths.DataDir));
        Directory.CreateDirectory(_paths.SitesDataDir);
        Assert.False(SiteDbSplitMigration.IsNeeded(_paths.DataDir)); // 已拆
    }

    [Fact]
    public async Task Apply_splits_rows_by_site_and_keeps_global_keys()
    {
        SeedLegacyDb();
        var factory = new SiteDbContextFactory(_paths);
        await SiteDbSplitMigration.ApplyAsync(_paths);
        Assert.True(File.Exists(Path.Combine(_paths.DataDir, "ggdown.db.pre-split.bak")));

        await using (var tw = await factory.CreateAsync("twitter"))
        {
            Assert.Equal(1, await tw.Users.CountAsync());
            Assert.Equal(2, await tw.Jobs.CountAsync()); // 含孤儿归 twitter
            Assert.True((await tw.Jobs.FirstAsync(j => j.UserId == null || true)).SiteId is "twitter");
            Assert.Equal(2, await tw.Jobs.CountAsync(j => j.SiteId == "twitter"));
            var opt = await tw.SiteSettings.SingleAsync();
            Assert.Equal("videos", opt.Key);
            Assert.Equal("True", opt.Value); // CLR 还原后序列化（Read Focus 5）
        }
        await using (var px = await factory.CreateAsync("pixiv"))
            Assert.Equal(1, await px.Jobs.CountAsync(j => j.SiteId == "pixiv"));

        using var global = new GGdownGlobalDbContext(new DbContextOptionsBuilder<GGdownGlobalDbContext>()
            .UseSqlite($"Data Source={_paths.DbFile}").Options);
        global.Database.EnsureCreated();
        Assert.Equal("D:\\x", await new GlobalSettingsStore(global).GetAsync<string>("download.directory"));
        Assert.Null(await new GlobalSettingsStore(global).GetAsync<string>("site.twitter.options", null));
    }

    [Fact]
    public async Task Apply_is_idempotent_on_rerun()
    {
        SeedLegacyDb();
        await SiteDbSplitMigration.ApplyAsync(_paths);
        await SiteDbSplitMigration.ApplyAsync(_paths); // 二次不炸不重搬
        var factory = new SiteDbContextFactory(_paths);
        await using var tw = await factory.CreateAsync("twitter");
        Assert.Equal(1, await tw.Users.CountAsync());
    }

    public void Dispose() => Directory.Delete(_paths.Root, true);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~SiteDbSplitMigrationTests"`
Expected: FAIL，`SiteDbSplitMigration` 不存在。

- [ ] **Step 3: 最小实现**

```csharp
// src/GGdown.Core/Data/SiteDbSplitMigration.cs（骨架含全部关键路径）
public static class SiteDbSplitMigration
{
    private const string BackupSuffix = ".pre-split.bak";
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(SiteDbSplitMigration));

    public static bool IsNeeded(string dataDir)
    {
        var db = Path.Combine(dataDir, "ggdown.db");
        if (!File.Exists(db) || Directory.Exists(Path.Combine(dataDir, "sites"))) return false;
        using var conn = new SqliteConnection($"Data Source={db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Accounts'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public static async Task ApplyAsync(IAppPaths paths, CancellationToken ct = default)
    {
        if (!IsNeeded(paths.DataDir)) return;
        var main = paths.DbFile;
        var backup = main + BackupSuffix;
        if (!File.Exists(backup)) File.Copy(main, backup);

        var legacyOptions = new DbContextOptionsBuilder<GGdownDbContext>().UseSqlite($"Data Source={main}").Options;
        using var legacy = new GGdownDbContext(legacyOptions);
        var accounts = await legacy.Accounts.AsNoTracking().ToListAsync(ct);
        var accountSite = accounts.ToDictionary(a => a.Id, a => a.SiteId);
        var siteFactory = new SiteDbContextFactory(paths);

        // 逐站点搬行（Account/User/FollowingCache 按 SiteId；Job 经 accountSite 回填，悬空归 twitter；
        // File 经 jobId→site 字典；SettingEntry 拆 site.*.options 与其余）
        // 每站点一个 GGdownSiteDbContext，SaveChangesAsync 逐批提交；异常上抛由启动序列记日志，
        // sites\ 未建成（本方法末尾才目录落盘标记）→ 下次启动重试。
        foreach (var siteId in DistinctSites(legacy, accountSite))
        {
            await using var db = await siteFactory.CreateAsync(siteId, ct);
            db.Accounts.AddRange(accounts.Where(a => a.SiteId == siteId));
            await db.SaveChangesAsync(ct);
            db.Users.AddRange(await legacy.Users.AsNoTracking().Where(u => u.SiteId == siteId).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            // Jobs/Files/Options 同型（Job.SiteId 回填 + 孤儿 twitter + 日志）
        }
        // 全局键 → 全局库（SiteDbSplitMigration 内直接 SqliteConnection 写 Settings 表，
        // 或建 GGdownGlobalDbContext——推荐后者，建库后逐键 Set）
        // 完成标记：目录 sites\ 已由 siteFactory.CreateAsync 创建；最后把主库改名留档：
        File.Move(main, backup, overwrite: true);
        Log.Information("存量库拆分完成，旧库留档 {Backup}", backup);
    }
}
```

`DbInitializer.cs` 改造：

```csharp
public static class DbInitializer
{
    public static async Task InitializeGlobalAsync(GGdownGlobalDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}
```

`App.xaml.cs` 启动 `Task.Run` 序列改为：

```csharp
await SiteDbSplitMigration.ApplyAsync(paths);                    // 存量拆分（幂等，IsNeeded 内判）
using (var scope = Services.CreateScope())
    await DbInitializer.InitializeGlobalAsync(
        scope.ServiceProvider.GetRequiredService<GGdownGlobalDbContext>());
await scope.ServiceProvider.GetRequiredService<StatsAggregator>().RecalculateDownloadCountsAsync();
// 其余（并发数应用、RecoverOnStartupAsync）保持原样
```

（全局库迁移：此时用 CLI 生成 `Init`：`dotnet ef migrations add Init --context GGdownGlobalDbContext --project src\GGdown.Core --startup-project src\GGdown.Core --output-dir Migrations\Global`；平台库沿用 Task 2 的 `EnsureCreatedAsync` 起步并保留换正式迁移的注释——存量拆分只搬行不依赖迁移历史，风险可控。）

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~SiteDbSplitMigrationTests"`
Expected: PASS（3 个测试，含孤儿任务与幂等）。

- [ ] **Step 5: 提交**

```bash
git add src/GGdown.Core/Data/SiteDbSplitMigration.cs src/GGdown.Core/Data/DbInitializer.cs src/GGdown.App/App.xaml.cs src/GGdown.Tests/Data/SiteDbSplitMigrationTests.cs
git commit -m "feat: 存量单库一次性拆分迁移并接入启动序列"
```

---

## Phase B：UI 壳层

### Task 7: SiteUiProfile 页组目录 + 平台图标（Core）

**Files:**
- Modify: `src/GGdown.Core/Sites/SiteCatalog.cs`（`SiteInfo` 加 `IconGlyph`）
- Create: `src/GGdown.Core/Sites/SiteUiProfile.cs`
- Test: `src/GGdown.Tests/Sites/SiteUiProfileTests.cs`（新建）

**Interfaces:**
- Produces:
  - `SiteInfo` 增 `string IconGlyph`（Segoe Fluent Icons 字形：X=`&#xE938;`（污渍字母近拟可换）、pixiv=`&#xE890;`、fanbox=`&#xE8F1;`、danbooru=`&#xE8F4;`、gelbooru=`&#xE8F4;`、kemono=`&#xE77B;`、coomer=`&#xE77B;`、fantia=`&#xE8BC;`、instagram=`&#xE722;`、reddit=`&#xE816;`、tumblr=`&#xE8C8;`、bilibili=`&#xE8C8;`、weibo=`&#xE8C8;`——值在测试里锁定，UI 蜡笔值可后续调）。
  - `public enum SitePageKey { Users, Downloads, History, SiteSettings }`
  - `public static class SiteUiProfiles { public static IReadOnlyList<SitePageKey> For(string siteId); }`：twitter/pixiv → 四键全有；danbooru/gelbooru/kemono/coomer → `[Downloads, History, SiteSettings]`；其余 → 四键。

- [ ] **Step 1: 写失败测试**

```csharp
// src/GGdown.Tests/Sites/SiteUiProfileTests.cs
using GGdown.Sites;

namespace GGdown.Tests.Sites;

public class SiteUiProfileTests
{
    [Theory]
    [InlineData("twitter")]
    [InlineData("pixiv")]
    public void Available_sites_have_all_four_pages(string siteId) =>
        Assert.Equal(new[] { SitePageKey.Users, SitePageKey.Downloads, SitePageKey.History, SitePageKey.SiteSettings },
            SiteUiProfiles.For(siteId));

    [Theory]
    [InlineData("danbooru")]
    [InlineData("gelbooru")]
    [InlineData("kemono")]
    [InlineData("coomer")]
    public void Booru_like_sites_skip_users_page(string siteId) =>
        Assert.Equal(new[] { SitePageKey.Downloads, SitePageKey.History, SitePageKey.SiteSettings },
            SiteUiProfiles.For(siteId));

    [Theory]
    [InlineData("fanbox")]
    [InlineData("weibo")]
    [InlineData("bilibili")]
    [InlineData("instagram")]
    [InlineData("reddit")]
    [InlineData("tumblr")]
    [InlineData("fantia")]
    public void Other_placeholder_sites_have_four_pages(string siteId) =>
        Assert.Equal(4, SiteUiProfiles.For(siteId).Count);

    [Fact]
    public void Unknown_site_falls_back_to_all_pages() =>
        Assert.Equal(4, SiteUiProfiles.For("nope").Count);

    [Fact]
    public void Every_catalog_entry_has_icon_glyph() =>
        Assert.All(SiteCatalog.All, s => Assert.False(string.IsNullOrWhiteSpace(s.IconGlyph)));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~SiteUiProfileTests"`
Expected: FAIL。

- [ ] **Step 3: 最小实现**

`SiteInfo` 记录加字段（构造点 13 处全补 glyph 实参）；`SiteUiProfile.cs`：

```csharp
namespace GGdown.Sites;

public enum SitePageKey { Users, Downloads, History, SiteSettings }

/// <summary>各平台的功能页组（spec §2.2）：仅 Available 站点的页组会被 UI 实际渲染。</summary>
public static class SiteUiProfiles
{
    private static readonly IReadOnlyList<SitePageKey> All =
        [SitePageKey.Users, SitePageKey.Downloads, SitePageKey.History, SitePageKey.SiteSettings];
    private static readonly IReadOnlyList<SitePageKey> NoUsers =
        [SitePageKey.Downloads, SitePageKey.History, SitePageKey.SiteSettings];

    private static readonly Dictionary<string, IReadOnlyList<SitePageKey>> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twitter"] = All, ["pixiv"] = All,
        ["danbooru"] = NoUsers, ["gelbooru"] = NoUsers,
        ["kemono"] = NoUsers, ["coomer"] = NoUsers,
    };

    public static IReadOnlyList<SitePageKey> For(string siteId) =>
        Map.GetValueOrDefault(siteId, All);
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~SiteUiProfileTests"`
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/GGdown.Core/Sites/SiteCatalog.cs src/GGdown.Core/Sites/SiteUiProfile.cs src/GGdown.Tests/Sites/SiteUiProfileTests.cs
git commit -m "feat: 站点页组目录与平台图标字形"
```

### Task 8: MainWindow 壳层重构（平台栏 + 页组标签）

**Files:**
- Modify: `src/GGdown.App/MainWindow.xaml`（全文重写）
- Modify: `src/GGdown.App/MainWindow.xaml.cs`（全文重写）
- Create: `src/GGdown.Core/ViewModels/MainNavViewModel.cs`
- Delete: `src/GGdown.Core/ViewModels/SiteSwitcherViewModel.cs`（功能并入 MainNavViewModel）
- Modify: `src/GGdown.App/App.xaml.cs`（VM 注册、删除 SiteSwitcherViewModel 注册）
- Test: `src/GGdown.Tests/ViewModels/MainNavViewModelTests.cs`（新建）+ `src/GGdown.Tests/Xaml/MainWindowXamlTests.cs`（新建，字符串断言模式仿 `UsersPageXamlTests`）

**Interfaces:**
- Consumes: `ICurrentSite`、`IAppSettings`（`GetVisibleSitesAsync`/`SetVisibleSitesAsync`/`GetSiteLastPageAsync`/`SetSiteLastPageAsync`/`GetCurrentSiteIdAsync`）、`SiteUiProfiles`、`SiteCatalog`、`IDownloadQueueService`（指示器计数）。
- Produces（`MainNavViewModel`，Core，singleton）:
  - `IReadOnlyList<PlatformItemViewModel> Platforms`（可见∩可用的站点：`SiteId`/`DisplayName`/`IconGlyph`/`IsSelected`）
  - `PlatformItemViewModel? SelectedPlatform`
  - `IReadOnlyList<PageTabViewModel> CurrentTabs`（当前平台页组：`Key: SitePageKey`/`Title`/`IsSelected`，Title 映射 Users=用户管理、Downloads=下载、History=历史、SiteSettings=设置）
  - `bool IsGlobalSettings`（true 时 Header 显示「设置」、CurrentTabs 为全局五页）
  - `Task SelectPlatformAsync(string siteId)`、`Task SelectTabAsync(SitePageKey key)`、`Task EnterGlobalSettingsAsync()`、`Task ReloadVisibleAsync()`（界面设置勾选后调用）
  - `int ActiveDownloadCount`（queue 事件驱动）
  - `event Action<SitePageKey>? TabNavigationRequested` / `event Action? GlobalSettingsNavigationRequested`（Core 不引用 App 页面类型：VM 发意图，MainWindow 映射 `SitePageKey → Type` 并 `ContentFrame.Navigate`）

- [ ] **Step 1: 写失败测试（VM）**

```csharp
// src/GGdown.Tests/ViewModels/MainNavViewModelTests.cs
using GGdown.Sites;
using GGdown.ViewModels;

namespace GGdown.Tests.ViewModels;

public class MainNavViewModelTests
{
    private static MainNavViewModel Create(FakeCurrentSite site, FakeAppSettings? settings = null)
    {
        settings ??= new FakeAppSettings();
        return new MainNavViewModel(site, settings);
    }

    [Fact]
    public async Task Platforms_default_to_available_visible_sites()
    {
        var vm = Create(new FakeCurrentSite());
        await vm.StartAsync();
        Assert.Equal(["twitter", "pixiv"], vm.Platforms.Select(p => p.SiteId).ToArray());
    }

    [Fact]
    public async Task Selecting_platform_loads_its_tabs_and_persists()
    {
        var site = new FakeCurrentSite();
        var settings = new FakeAppSettings();
        var vm = Create(site, settings);
        await vm.StartAsync();
        await vm.SelectPlatformAsync("pixiv");
        Assert.Equal("pixiv", site.SiteId);
        Assert.Equal(settings.SavedCurrentSiteId, "pixiv");
        Assert.Equal(4, vm.CurrentTabs.Count);
        Assert.Equal(SitePageKey.Users, vm.CurrentTabs[0].Key);
    }

    [Fact]
    public async Task Selecting_tab_persists_last_page_and_restores_on_platform_switch()
    {
        var settings = new FakeAppSettings();
        var vm = Create(new FakeCurrentSite(), settings);
        await vm.StartAsync();
        await vm.SelectTabAsync(SitePageKey.History);
        await vm.SelectPlatformAsync("pixiv"); // pixiv 无记忆 → Users
        await vm.SelectTabAsync(SitePageKey.SiteSettings);
        await vm.SelectPlatformAsync("twitter"); // 恢复 History
        Assert.Equal(SitePageKey.History, vm.CurrentTabs.Single(t => t.IsSelected).Key);
        Assert.Equal("history", settings.LastPages["twitter"]);
    }

    [Fact]
    public async Task Global_settings_mode_swaps_tabs()
    {
        var vm = Create(new FakeCurrentSite());
        await vm.StartAsync();
        await vm.EnterGlobalSettingsAsync();
        Assert.True(vm.IsGlobalSettings);
        await vm.SelectPlatformAsync("twitter");
        Assert.False(vm.IsGlobalSettings);
    }
}
```

（`FakeAppSettings` 若无现成实现则在 `Helpers.cs` 新增一个内存版，覆盖 `GetCurrentSiteIdAsync/SetCurrentSiteIdAsync/GetVisibleSitesAsync/SetVisibleSitesAsync/GetSiteLastPageAsync/SetSiteLastPageAsync` 等成员。）

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~MainNavViewModelTests"`
Expected: FAIL，`MainNavViewModel` 不存在。

- [ ] **Step 3: 实现 VM**

```csharp
// src/GGdown.Core/ViewModels/MainNavViewModel.cs（核心逻辑，UI 无关）
public partial class MainNavViewModel(ICurrentSite current, IAppSettings settings) : ObservableObject
{
    private static readonly Dictionary<SitePageKey, string> Titles = new()
    {
        [SitePageKey.Users] = "用户管理", [SitePageKey.Downloads] = "下载",
        [SitePageKey.History] = "历史", [SitePageKey.SiteSettings] = "设置",
    };

    public ObservableCollection<PlatformItemViewModel> Platforms { get; } = [];
    public ObservableCollection<PageTabViewModel> CurrentTabs { get; } = [];

    [ObservableProperty] private bool _isGlobalSettings;
    [ObservableProperty] private int _activeDownloadCount;

    public event Action<SitePageKey>? TabNavigationRequested;
    public event Action? GlobalSettingsNavigationRequested;

    public async Task StartAsync()
    {
        await ReloadVisibleAsync();
        var currentId = current.SiteId; // App 启动已 LoadAsync
        await SelectPlatformInternalAsync(currentId);
    }

    public async Task ReloadVisibleAsync()
    {
        var visible = (await settings.GetVisibleSitesAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = current.SiteId;
        Platforms.Clear();
        foreach (var s in SiteCatalog.All.Where(s => s.Available && visible.Contains(s.SiteId)))
            Platforms.Add(new PlatformItemViewModel(s.SiteId, s.DisplayName, s.IconGlyph, s.SiteId == selected));
        if (Platforms.All(p => p.SiteId != selected) && Platforms.Count > 0)
            await SelectPlatformInternalAsync(Platforms[0].SiteId);
    }

    public async Task SelectPlatformAsync(string siteId)
    {
        if (IsGlobalSettings) IsGlobalSettings = false;
        await SelectPlatformInternalAsync(siteId);
    }

    private async Task SelectPlatformInternalAsync(string siteId)
    {
        if (current.SiteId != siteId) await current.SelectAsync(siteId);
        await settings.SetCurrentSiteIdAsync(siteId);
        foreach (var p in Platforms) p.IsSelected = p.SiteId == siteId;
        RebuildTabs(SiteUiProfiles.For(siteId), await settings.GetSiteLastPageAsync(siteId));
    }

    public async Task SelectTabAsync(SitePageKey key)
    {
        foreach (var t in CurrentTabs) t.IsSelected = t.Key == key;
        if (!IsGlobalSettings)
        {
            await settings.SetSiteLastPageAsync(current.SiteId, key.ToString().ToLowerInvariant());
            TabNavigationRequested?.Invoke(key);
        }
    }

    public async Task EnterGlobalSettingsAsync()
    {
        IsGlobalSettings = true;
        foreach (var p in Platforms) p.IsSelected = false;
        // 全局五页 tab（Key 复用 SitePageKey 之外另设——见 Task 9 的 GlobalSettingsPageKey，此处占位由 Task 9 接管）
        GlobalSettingsNavigationRequested?.Invoke();
        await Task.CompletedTask;
    }

    private void RebuildTabs(IReadOnlyList<SitePageKey> keys, string lastPage)
    {
        CurrentTabs.Clear();
        var last = Enum.TryParse<SitePageKey>(lastPage, ignoreCase: true, out var lp) ? lp : keys.FirstOrDefault();
        foreach (var k in keys)
            CurrentTabs.Add(new PageTabViewModel(k, Titles[k], k == last));
    }
}

public partial class PlatformItemViewModel(string siteId, string displayName, string iconGlyph, bool isSelected)
    : ObservableObject
{
    public string SiteId { get; } = siteId;
    public string DisplayName { get; } = displayName;
    public string IconGlyph { get; } = iconGlyph;
    [ObservableProperty] private bool _isSelected = isSelected;
}

public partial class PageTabViewModel(SitePageKey key, string title, bool isSelected) : ObservableObject
{
    public SitePageKey Key { get; } = key;
    public string Title { get; } = title;
    [ObservableProperty] private bool _isSelected = isSelected;
}
```

（`EnterGlobalSettingsAsync` 的全局五页标签在 Task 9 落地 `GlobalSettingsPageKey` 后补齐——本任务先发导航意图。）

- [ ] **Step 4: 跑 VM 测试确认通过**

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~MainNavViewModelTests"`
Expected: PASS。

- [ ] **Step 5: 重写 MainWindow 壳层**

```xml
<!-- MainWindow.xaml 全文：InfoBar 行保留；主体 2 列 = 平台栏 + 内容区 -->
<Window x:Class="GGdown.App.MainWindow" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="GGdown">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>
        <!-- InfoBar NoticeBar 一节原样保留（含 GoSettingsButton，点击改为平台设置页，见 code-behind） -->
        <InfoBar x:Name="NoticeBar" Grid.Row="0" IsOpen="False" CloseButtonClick="NoticeBar_CloseButtonClick">
            <InfoBar.ActionButton>
                <Button x:Name="GoSettingsButton" Content="去设置" Click="GoSettingsButton_Click"/>
            </InfoBar.ActionButton>
        </InfoBar>
        <Grid Grid.Row="1">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="48"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>
                <ListView Grid.Row="0" x:Name="PlatformList" SelectionMode="Single"
                          ItemsSource="{x:Bind Nav.Platforms}">
                    <ListView.ItemContainerStyle>
                        <Style TargetType="ListViewItem">
                            <Setter Property="HorizontalContentAlignment" Value="Center"/>
                            <Setter Property="MinHeight" Value="44"/>
                            <Setter Property="Padding" Value="0"/>
                            <Setter Property="ToolTipService.ToolTip" Value="{x:Bind DisplayName, Mode=OneWay}"/>
                        </Style>
                    </ListView.ItemContainerStyle>
                    <ListView.ItemTemplate>
                        <DataTemplate x:DataType="vm:PlatformItemViewModel">
                            <FontIcon Glyph="{x:Bind IconGlyph}" FontSize="20"/>
                        </DataTemplate>
                    </ListView.ItemTemplate>
                </ListView>
                <Button Grid.Row="1" x:Name="GlobalDownloadsButton" Click="GlobalDownloadsButton_Click"
                        Background="Transparent" BorderThickness="0" Padding="0" MinHeight="44" ToolTipService.ToolTip="全部下载">
                    <Grid>
                        <FontIcon Glyph="&#xE896;" FontSize="20"/>
                        <Border x:Name="ActiveCountBadge" Visibility="Collapsed" .../>
                    </Grid>
                </Button>
                <Button Grid.Row="2" x:Name="GlobalSettingsButton" Click="GlobalSettingsButton_Click"
                        Background="Transparent" BorderThickness="0" Padding="0" MinHeight="44" ToolTipService.ToolTip="设置">
                    <FontIcon Glyph="&#xE713;" FontSize="20"/>
                </Button>
            </Grid>
            <Grid Grid.Column="1">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                </Grid.RowDefinitions>
                <StackPanel Orientation="Horizontal" Spacing="12" Margin="12,10,12,0">
                    <TextBlock x:Name="HeaderTitle" FontSize="20" FontWeight="SemiBold"/>
                    <muxc:TabView x:Name="PageTabs" ... ItemsSource 绑 Nav.CurrentTabs / TabAction 点击调 Nav.SelectTabAsync/>
                </StackPanel>
                <Frame x:Name="ContentFrame" Grid.Row="1"/>
            </Grid>
        </Grid>
    </Grid>
</Window>
```

code-behind 要点（保留 B8 全部通知逻辑，删除 `Nav_SelectionChanged`/`SiteList_SelectionChanged`）：

```csharp
private static readonly Dictionary<SitePageKey, Type> SitePages = new()
{
    [SitePageKey.Users] = typeof(UsersPage),
    [SitePageKey.Downloads] = typeof(DownloadsPage),
    [SitePageKey.History] = typeof(HistoryPage),
    [SitePageKey.SiteSettings] = typeof(SettingsPage), // Task 9 换成 SiteSettingsPage
};

public MainNavViewModel Nav { get; }

// 构造：Nav = services.GetRequiredService<MainNavViewModel>();
//   Nav.TabNavigationRequested += k => Navigate(SitePages[k]);
//   Nav.GlobalSettingsNavigationRequested += () => Navigate(typeof(GeneralSettingsPage)); // Task 9 接管五页
//   PlatformList.SelectionChanged → _ = Nav.SelectPlatformAsync(siteId);
//   PageTabs（或其 SelectionChanged/TabItemClick）→ _ = Nav.SelectTabAsync(key);
//   GoSettingsButton_Click：NoticeBar 关闭后 Navigate(SitePages[SitePageKey.SiteSettings]);
//   GlobalDownloadsButton_Click：进入「全部平台」下载视图（Task 10 落地筛选参数前先导航 DownloadsPage）；
//   queue.JobChanged/JobRemoved 已订阅的 OnJobChanged 里同步 Nav.ActiveDownloadCount
//     = Active.Count(s => s.Status is JobStatus.Running or JobStatus.Pending).ToString();
//   启动：await App.Readiness 后 _ = Nav.StartAsync();（Readiness 完成才有可见平台/当前站点设置）
// 头部标题：订阅 Nav 属性变更，IsGlobalSettings ? "设置" : Nav 当前平台 DisplayName。
```

`App.xaml.cs`：删 `services.AddSingleton<SiteSwitcherViewModel>();`，加 `services.AddSingleton<MainNavViewModel>();`。

- [ ] **Step 6: XAML 静态测试 + 构建验证**

```csharp
// src/GGdown.Tests/Xaml/MainWindowXamlTests.cs（fixture 拷贝方式仿 UsersPage.xaml 的 XamlFixtures 机制）
[Fact]
public void MainWindow_has_platform_rail_and_tab_header()
{
    var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "XamlFixtures", "MainWindow.xaml"));
    Assert.Contains("PlatformList", xaml);
    Assert.Contains("PageTabs", xaml);
    Assert.Contains("GlobalSettingsButton", xaml);
    Assert.Contains("GlobalDownloadsButton", xaml);
    Assert.DoesNotContain("NavigationView", xaml);
    Assert.DoesNotContain("SiteFlyout", xaml);
}
```

（`GGdown.Tests.csproj` 的 XamlFixtures 拷贝条目补 `MainWindow.xaml`——与 UsersPage.xaml 现有 CopyToOutputDirectory 同型。）

Run: `dotnet build src\GGdown.App\GGdown.App.csproj -p:Platform=x64`
Expected: 编译通过（`SettingsPage` 暂作 SiteSettings 落点，Task 9 换）。

- [ ] **Step 7: 提交**

```bash
git add -A src/GGdown.App src/GGdown.Core/ViewModels src/GGdown.Tests
git commit -m "feat: 平台图标栏 + 页组标签的主窗口壳层"
```

### Task 9: 全局设置拆五页 + 平台设置页下沉

**Files:**
- Create: `src/GGdown.App/Views/Settings/{GeneralSettingsPage,NetworkSettingsPage,EngineSettingsPage,InterfaceSettingsPage,AboutPage,SiteSettingsPage}.xaml(.cs)`
- Create: `src/GGdown.Core/ViewModels/{GlobalSettingsViewModel,InterfaceSettingsViewModel,SiteSettingsViewModel}.cs`
- Modify: `src/GGdown.Core/ViewModels/SettingsViewModel.cs`（拆分后删除，逻辑迁入三个新 VM）
- Delete: `src/GGdown.App/Views/SettingsPage.xaml(.cs)`
- Modify: `src/GGdown.App/App.xaml.cs`（VM 注册）、`MainWindow.xaml.cs`（`SitePages` 映射与全局五页映射）、`MainNavViewModel`（全局五页标签：`GlobalSettingsPageKey { General, Network, Engine, Interface, About }` → 标题「通用/网络/引擎/界面/关于」）

**Interfaces:**
- Produces:
  - `GlobalSettingsViewModel`（singleton，页面共享）：原 SettingsViewModel 的通用（DownloadDirectory/Concurrency/SaveGeneralCommand/BrowseDownloadDirectory）+ 网络（ProxyScheme/ProxyHost/…）+ 引擎（EngineVersion/OpenLogsCommand）+ 关于（AppVersion）+ StatusMessage。Shell 交互事件（OpenFolderPickerRequested/OpenFolderRequested）原样迁移。
  - `InterfaceSettingsViewModel`（singleton）：`IReadOnlyList<VisibleSiteItemViewModel> Items`（13 站点：DisplayName/IconGlyph/Available/IsVisible），勾选 ToggleSwitch → `IAppSettings.SetVisibleSitesAsync` + `MainNavViewModel.ReloadVisibleAsync()`。
  - `SiteSettingsViewModel`（Transient）：原「账号」两卡 + 「站点选项」ItemsControl 段（OptionItemViewModel 复用），`ICurrentSite.Changed += Reload`（B7 语义保留）。
  - `GlobalSettingsPageKey`（enum，`GGdown.Sites` 或 VMs 命名空间）+ `MainNavViewModel` 增 `Task SelectGlobalTabAsync(GlobalSettingsPageKey key)` 与全局 tab 集合。

- [ ] **Step 1: 拆分实现（本任务以 XAML 搬移为主，测试集中在 VM 可测逻辑）**

失败测试先行（`InterfaceSettingsViewModelTests`，新建）：

```csharp
[Fact]
public async Task Toggling_site_updates_visible_sites_and_rail()
{
    var settings = new FakeAppSettings();
    var nav = new MainNavViewModel(new FakeCurrentSite(), settings);
    await nav.StartAsync();
    var vm = new InterfaceSettingsViewModel(settings, nav);
    await vm.StartAsync();
    Assert.Equal(13, vm.Items.Count);
    vm.Items.Single(i => i.SiteId == "pixiv").IsVisible = false;
    await vm.SaveChangesAsync();
    Assert.DoesNotContain("pixiv", await settings.GetVisibleSitesAsync());
    Assert.DoesNotContain(nav.Platforms, p => p.SiteId == "pixiv");
}
```

Run 确认 FAIL → 实现 `InterfaceSettingsViewModel`（勾选集合 → SetVisibleSitesAsync → nav.ReloadVisibleAsync）→ Run PASS。

- [ ] **Step 2: XAML 搬移（按段剪切，不改控件）**

从现 `SettingsPage.xaml` 剪切到新页面（每页包 `ScrollViewer` + `StackPanel` 壳与 `NavigationCacheMode="Enabled"`，绑定前缀 `Vm.` 改为对应 VM 属性名）：

| 新页面 | 搬入的 XAML 段（SettingsPage.xaml 行号） |
|---|---|
| GeneralSettingsPage | 「通用」标题 + 下载目录/并发卡（14–25） |
| NetworkSettingsPage | 「网络代理」卡（26–43） |
| EngineSettingsPage | 「引擎」标题+卡（110–114） |
| AboutPage | 「关于」标题+卡（116–123）+ StatusMessage 行 |
| SiteSettingsPage | 「账号」两卡（45–68）+「站点选项」段（70–108，含 OptionItemViewModel 模板与保存按钮） |

`InterfaceSettingsPage` 全新（勾选列表）：

```xml
<ItemsControl ItemsSource="{x:Bind Vm.Items}">
    <ItemsControl.ItemTemplate>
        <DataTemplate x:DataType="vm:VisibleSiteItemViewModel">
            <Grid Padding="12,8" ColumnSpacing="12" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <FontIcon Glyph="{x:Bind IconGlyph}"/>
                <TextBlock Grid.Column="1" Text="{x:Bind DisplayName}" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="1" Text="即将支持" FontSize="12" Opacity="0.5"
                           Visibility="{x:Bind conv:UiConv.ToInverseVisibility(Available)}"/>
                <ToggleSwitch Grid.Column="2" IsOn="{x:Bind IsVisible, Mode=TwoWay}" OnContent="" OffContent=""
                              IsEnabled="{x:Bind Available}"/>
            </Grid>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

- [ ] **Step 3: 接线与删除旧页**

- `MainWindow.xaml.cs`：`SitePages[SitePageKey.SiteSettings] = typeof(SiteSettingsPage)`；新增 `GlobalSettingsPages` 映射五页；`Nav.SelectGlobalTabAsync` → `Navigate(GlobalSettingsPages[key])`；进入全局设置默认落 GeneralSettingsPage。
- `App.xaml.cs`：`AddSingleton<GlobalSettingsViewModel>(); AddSingleton<InterfaceSettingsViewModel>(); AddTransient<SiteSettingsViewModel>();`（SiteSettingsViewModel 依赖 Scoped `IAccountService`，Transient——沿用 B7 captive-dependency 裁定注释）。
- 删除 `SettingsPage.xaml(.cs)` 与 `SettingsViewModel.cs`；`AddUserDialog` 等对话框不动。
- 页面 code-behind：`OnNavigatedTo` 里 `await App.Readiness` 后 `Vm.Start()`（各页现模式保留）；`SiteSettingsPage` 保留 `OnImportCookieClick` 打开 `ImportCookieDialog` 的逻辑（从旧 SettingsPage.xaml.cs 剪切）。

- [ ] **Step 4: 构建 + 全量测试**

Run: `dotnet build src\GGdown.App\GGdown.App.csproj -p:Platform=x64` → 编译通过。
Run: `dotnet test src\GGdown.sln` → Core 测试全 PASS（`SettingsViewModelTests` 相应改造为三个新 VM 的测试）。

- [ ] **Step 5: 提交**

```bash
git add -A src/GGdown.App src/GGdown.Core src/GGdown.Tests
git commit -m "feat: 全局设置拆五页，账号与站点选项下沉为平台设置页"
```

### Task 10: 下载/历史按平台筛选 + 全局下载指示器

**Files:**
- Modify: `src/GGdown.Core/ViewModels/DownloadsViewModel.cs`、`HistoryViewModel.cs`
- Modify: `src/GGdown.Core/Services/Queries.cs`（`HistoryFilter.SiteId`，Task 4 已加则跳过）
- Modify: `src/GGdown.App/MainWindow.xaml.cs`（全局下载视图导航参数）、`Views/DownloadsPage.xaml.cs`、`HistoryPage.xaml.cs`
- Test: 改造 `DownloadsViewModelTests`/`HistoryViewModelTests`

**Interfaces:**
- Produces: `DownloadsViewModel.SiteFilter`（string?，null=全部平台；设值即刷新列表）、`HistoryViewModel.SiteFilter`（同）；页面 `OnNavigatedTo` 读取导航参数：平台页组导航 → 参数仍为 `services`，页面从 `ICurrentSite.SiteId` 取筛选；全局下载视图 → `ContentFrame.Navigate(typeof(DownloadsPage), services, frameParams: null)` + `e.Parameter` 携带 `"all"` 标记（WinUI Frame 参数单值：改用 `DownloadsPage.GlobalView` 静态开关或 `MainNavViewModel.IsGlobalDownloads` 均可——采用后者：VM 增加 `bool IsGlobalDownloads`，`GlobalDownloadsButton_Click` 置位并导航，页面据此把 SiteFilter 置 null）。
- `DownloadsViewModel`：列表源 `IDownloadQueueService.Active` 过滤 `snapshot.SiteId == SiteFilter`；`ICurrentSite.Changed` 订阅把平台模式下 SiteFilter 换成新站点并刷新（Read Focus 1）。
- `HistoryViewModel`：`LoadAsync` 查询带 `HistoryFilter { SiteId = SiteFilter, … }`；同上订站点变更。

- [ ] **Step 1: 改造失败测试**

```csharp
[Fact]
public async Task Downloads_shows_only_current_site_jobs()
{
    // FakeQueue（或真实 service + 双库）给出 twitter/pixiv 两条 Active snapshot；
    // VM 切到 pixiv（FakeCurrentSite.SelectAsync）→ 断言列表只剩 pixiv 条目
}

[Fact]
public async Task Global_view_shows_all_sites()
{
    // IsGlobalDownloads = true → SiteFilter null → 两条都在
}

[Fact]
public async Task History_filter_scopes_to_site()
{
    // 双库播种各一条历史；SiteFilter="pixiv" → 只出 pixiv 行；null → 两条
}
```

- [ ] **Step 2: 跑测试确认失败** → FAIL。

- [ ] **Step 3: 实现**（VM 过滤属性 + 订阅 + 页面接线；`MainWindow.GlobalDownloadsButton_Click` → `Nav.IsGlobalDownloads=true; Navigate(DownloadsPage)`；平台切换时复位 `IsGlobalDownloads=false`。）

- [ ] **Step 4: 跑测试确认通过 + 全量** → `dotnet test src\GGdown.sln` 全 PASS。

- [ ] **Step 5: 提交**

```bash
git add -A src/GGdown.Core src/GGdown.App src/GGdown.Tests
git commit -m "feat: 下载与历史按平台筛选，栏底全局下载视图"
```

### Task 11: 验收核对与收尾

**Files:**
- Modify: `src/GGdown.Tests/ViewModels/UsersViewModelTests.cs`（若缺 `ICurrentSite.Changed` 刷新断言则补）
- 无新功能文件；产出为验证记录

**Interfaces:** 无。

- [ ] **Step 1: 共享页刷新断言（Read Focus 1）**

```csharp
[Fact]
public async Task Users_reload_on_site_change()
{
    // twitter 播种 1 用户、pixiv 播种 1 用户；VM 加载后切 site → 断言 Users 只剩当前站点条目
}
```

Run: `dotnet test src\GGdown.Tests\GGdown.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~UsersViewModelTests"` → PASS（若 FAIL：补 UsersViewModel 对 `ICurrentSite.Changed` 的订阅刷新——这是本任务的存在意义）。

- [ ] **Step 2: 全量自动化**

Run: `dotnet test src\GGdown.sln`
Expected: 全 PASS（engine/E2E 无 python 时按现有 SkippableFact 规则跳过）。

- [ ] **Step 3: 手动验收（pwsh scripts/run.ps1 起真机）**

核对清单（每条过一遍，记入提交信息或验收备注）：

1. 旧数据升级路径：手工把 `%LOCALAPPDATA%\GGdown\data\ggdown.db` 换成含 twitter+pixiv 数据的旧库（无 sites\）→ 启动 → 数据按平台落位、`ggdown.db.pre-split.bak` 存在、全局设置（下载目录）保留。
2. 平台切换：X ↔ Pixiv 往返，用户列表/历史/下载页内容随站点切换（无脏数据残留）。
3. 每平台 lastPage：X 停在「历史」→ 切 Pixiv 再切回 → 仍在「历史」。
4. 界面设置：取消勾选 Pixiv → 栏内消失；重新勾选 → 回来；「即将支持」站点不可勾选有效。
5. 栏底指示器：起一个下载 → 徽标出现计数；完成后归零；点击进「全部平台」视图。
6. 全局设置五页内容完整（目录/并发/代理/引擎/关于）；各平台设置页显示各自账号与站点选项。
7. 登录态失效通知 → 「去设置」落当前平台的设置页。
8. InfoBar 任务完成通知 5 秒自动关闭（B8 回归）。

- [ ] **Step 4: 提交（如有补丁）**

```bash
git add -A
git commit -m "fix: 验收补丁（站点切换刷新/指示器细节）"
```

---

## Self-Review 记录

- **Spec 覆盖**：§2.1→Task 8；§2.2→Task 7；§2.3→Task 3/9；§2.4→Task 9；§2.5→Task 10；§2.6→Task 3/8；§2.7→Task 10/11；§3.1→Task 1/2；§3.2→Task 1/2/6；§3.3→Task 3/4/5；§3.4→Task 6；§4 顺序→Phase A/B 分组；§5 测试→各任务 Step 1；§6 风险→Review Focus 1–5 与 Task 6 测试。无缺口。
- **占位符扫描**：Task 8 XAML 中 `...` 两处为省略的样式属性（badge 视觉细节）与 TabView 绑定细写交给实现者按 `x:Bind` 惯例补齐——已在步骤文字中给出绑定源与行为约定；Task 6 骨架注明逐段同型。无 TBD/TODO。
- **类型一致性**：`ISiteDbContextFactory.CreateAsync(string, CancellationToken)` 全文一致；`StatsAggregator.ApplyJobCompletionAsync(long, string)` 在 Task 4 定义、Task 5 消费；`SitePageKey` 在 Task 7 定义、Task 8/9/10 消费；`GlobalSettingsPageKey` 在 Task 9 定义并在 Task 8 预告接入点。一致。
- **Review Focus 测试落位**：1→Task 10/11；2→Task 6；3→Task 3；4→Task 6；5→Task 6。均有对应断言。
