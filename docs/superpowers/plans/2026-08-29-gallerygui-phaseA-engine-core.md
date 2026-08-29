# GalleryGUI Phase A（引擎与核心层）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 搭建 GalleryGUI 的领域核心（C#）与 gallery-dl runner 适配器（Python），完成"用户/账号/队列/统计"全部业务逻辑与测试，不含 UI 与打包。

**Architecture:** C# Core 通过 `IDownloadEngine` 面向 runner 进程通信（stdout JSONL 事件流）；站点差异收敛在 `ISiteProvider`（Twitter 为首个实现）；数据用 EF Core 8 + SQLite（WAL）。runner 复用 gallery-dl 内部 API（关注列表、认证、下载），通过猴子补丁 `gallery_dl.output.select` 输出结构化事件。

**Tech Stack:** .NET 8 / C# 12、EF Core 8 + Microsoft.Data.Sqlite、CommunityToolkit.Mvvm 8.3.x、xUnit 2.9、Python 3.12 + gallery-dl（仓库根 `gallery-dl\` 上游源码）、pytest。

**Spec:** `docs/superpowers/specs/2026-08-29-gallery-gui-design.md`（本计划依据规格 §2/§3/§4/§5/§7/§8；执行者需同时阅读规格）

## Global Constraints

- 目标框架 `net8.0`（App 项目在 Phase B 覆盖为 `net8.0-windows10.0.19041.0`）。
- gallery-dl 上游源码在仓库根 `gallery-dl\`，**不修改**；runner 通过 `PYTHONPATH` 引用。
- runner 协议版本 `protocol: 1`；事件名固定集合：`hello / account / user / end / url-start / file-start / file-done / file-skip / log / job-done / fatal`；stdout 每行一个 JSON、逐行 flush；诊断输出走 stderr。
- runner 子命令固定：`hello / whoami / list-following / user-info / download`。
- `fatal` 事件带 `kind` 字段：`"auth"`（cookie 失效）或缺省（一般错误）。
- 应用数据根：`%LOCALAPPDATA%\GalleryGUI\`（`IAppPaths` 支持注入测试根目录）。
- SQLite WAL 模式；`users` 表 `UNIQUE(SiteId, RestId)`。
- 下载并发默认 1，全局队列属性；V1 队列操作只有取消（杀进程树）。
- 去重第一道闸：每账号一个 gallery-dl `download-archive` 文件。
- `DownloadCount` 口径：该用户 user_media 任务中 `Status=Downloaded` 的文件累计数；likes/bookmarks 文件 UserId 为空，不计入。
- 命名模板默认 `{tweet_id}_{author[name]}_{num}.{extension}`；`item_id` 事件字段由文件名前缀 `\d+` 推导（模板受我方控制）。
- 所有业务代码在 `GalleryGUI.Core`（可单测），不引用 UI 框架；测试用 xUnit，日志用 `Microsoft.Extensions.Logging.Abstractions`（Serilog 在 Phase B 接入）。
- 提交信息用约定式（`feat:`/`test:`/`chore:`），每任务至少一次提交。

## 文件结构总览

```
src/
├── GalleryGUI.sln
├── Directory.Build.props               # net8.0/Nullable/ImplicitUsings
├── GalleryGUI.Core/
│   ├── GalleryGUI.Core.csproj
│   ├── DependencyInjection.cs          # AddGalleryCore(IServiceCollection, IAppPaths)
│   ├── Paths/AppPaths.cs               # IAppPaths + AppPaths（目录布局）
│   ├── Data/
│   │   ├── Entities.cs                 # Account/User/DownloadJob/DownloadFile + 枚举
│   │   ├── GalleryDbContext.cs         # EF Core DbContext + 索引/唯一约束
│   │   ├── GalleryDbContextFactory.cs  # IDesignTimeDbContextFactory（迁移用）
│   │   └── DbInitializer.cs            # Migrate + WAL pragma
│   ├── Engine/
│   │   ├── EngineEvent.cs              # 事件模型
│   │   ├── JsonlParser.cs              # 行 → EngineEvent（宽容解析）
│   │   ├── IDownloadEngine.cs          # 引擎抽象 + Hello/AccountInfo/SiteUserInfo/异常
│   │   └── RunnerEngine.cs             # 进程管理 + JSONL 接线 + 取消
│   ├── Sites/
│   │   ├── ISiteProvider.cs            # 抽象 + ContentKind/Option*/DownloadPlan
│   │   ├── TwitterSiteProvider.cs
│   │   └── SiteRegistry.cs
│   ├── Services/
│   │   ├── SettingsStore.cs            # ISettingsStore + GallerySettingsStore
│   │   ├── AccountService.cs
│   │   ├── UserService.cs
│   │   ├── DownloadQueueService.cs     # 队列调度/取消/事件落库/启动恢复
│   │   └── StatsAggregator.cs          # 任务完成聚合统计
│   └── ViewModels/                     # （Phase B 填充）
└── GalleryGUI.Tests/
    ├── GalleryGUI.Tests.csproj
    ├── Helpers.cs                       # TestDb/TestPaths/CollectEvents
    ├── SmokeTests.cs
    ├── Paths/AppPathsTests.cs
    ├── Data/GalleryDbContextTests.cs
    ├── Services/SettingsStoreTests.cs
    ├── Engine/JsonlParserTests.cs
    ├── Engine/RunnerEngineTests.cs
    ├── Fixtures/stub_runner.py          # 进程测试桩
    ├── Sites/TwitterSiteProviderTests.cs
    ├── Services/AccountServiceTests.cs
    ├── Services/UserServiceTests.cs
    ├── Services/DownloadQueueServiceTests.cs
    ├── Services/StatsAndRecoveryTests.cs
    └── E2E/EngineSmokeTests.cs
engine/
├── runner.py                            # CLI 适配器（argparse + 事件发射）
├── sites/__init__.py
├── sites/twitter.py                     # make_api/whoami/list_following/user_info
└── tests/
    ├── test_runner.py                   # walk_config/parse_screen_name/emit
    └── test_twitter.py                  # map_transformed 纯函数
```

---

### Task 1: 解决方案与项目脚手架

**Files:**
- Create: `src/GalleryGUI.sln`、`src/Directory.Build.props`、`.gitignore`
- Create: `src/GalleryGUI.Core/GalleryGUI.Core.csproj`（空类库）
- Create: `src/GalleryGUI.Tests/GalleryGUI.Tests.csproj` + `SmokeTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: 三个项目可编译、`dotnet test` 绿；后续所有任务在此基础上追加文件

- [ ] **Step 1: 初始化 .gitignore 与解决方案**

`.gitignore`（仓库根，覆盖 .NET + Python + 引擎产物）：

```gitignore
bin/
obj/
*.user
.vs/
*.db
__pycache__/
*.pyc
.pytest_cache/
dist/
out/
engine/dist/
.artifacts/
```

命令：

```bash
cd "E:\Projects\gallery-gui"
mkdir -p src
dotnet new sln -o src -n GalleryGUI
dotnet new classlib -o src/GalleryGUI.Core -n GalleryGUI.Core -f net8.0
dotnet new xunit -o src/GalleryGUI.Tests -n GalleryGUI.Tests -f net8.0
dotnet sln src/GalleryGUI.sln add src/GalleryGUI.Core/GalleryGUI.Core.csproj src/GalleryGUI.Tests/GalleryGUI.Tests.csproj
rm src/GalleryGUI.Core/Class1.cs src/GalleryGUI.Tests/UnitTest1.cs
```

- [ ] **Step 2: 写 Directory.Build.props 与 csproj**

`src/Directory.Build.props`：

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

`src/GalleryGUI.Core/GalleryGUI.Core.csproj`（类库本体 + 包引用）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.3.2" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.0.11" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.2" />
  </ItemGroup>
</Project>
```

`src/GalleryGUI.Tests/GalleryGUI.Tests.csproj`（追加项目引用）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" PrivateAssets="all" />
    <PackageReference Include="Xunit.SkippableFact" Version="1.5.23" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\GalleryGUI.Core\GalleryGUI.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: 写冒烟测试**

`src/GalleryGUI.Tests/SmokeTests.cs`：

```csharp
namespace GalleryGUI.Tests;

public class SmokeTests
{
    [Fact]
    public void Test_infrastructure_works() => Assert.True(true);
}
```

- [ ] **Step 4: 还原并运行测试**

Run: `dotnet test src/GalleryGUI.sln`
Expected: PASS（1 个测试）

- [ ] **Step 5: Commit**

```bash
git add .gitignore src/Directory.Build.props src/GalleryGUI.sln src/GalleryGUI.Core src/GalleryGUI.Tests
git commit -m "chore: 搭建 GalleryGUI 解决方案（Core + Tests）"
```

---

### Task 2: IAppPaths 应用数据目录布局

**Files:**
- Create: `src/GalleryGUI.Core/Paths/AppPaths.cs`
- Test: `src/GalleryGUI.Tests/Paths/AppPathsTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `IAppPaths`（后续所有需要磁盘位置的任务使用；`PythonExe`/`RunnerScript` 供 Task 7；`DbFile` 供 Task 3；`TempDir` 供 Task 7）

```csharp
namespace GalleryGUI.Paths;

public interface IAppPaths
{
    string Root { get; }
    string DataDir { get; }
    string DbFile { get; }
    string EngineDir { get; }
    string PythonExe { get; }
    string RunnerScript { get; }
    string AccountsDir { get; }
    string ArchiveDir { get; }
    string LogsDir { get; }
    string TempDir { get; }
    void EnsureCreated();
}
```

- [ ] **Step 1: 写失败测试**

`src/GalleryGUI.Tests/Paths/AppPathsTests.cs`：

```csharp
using GalleryGUI.Paths;

namespace GalleryGUI.Tests.Paths;

public class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ggui-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public AppPathsTests() => _paths = new AppPaths(_root);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Paths_derive_from_root()
    {
        Assert.Equal(_root, _paths.Root);
        Assert.Equal(Path.Combine(_root, "data", "gallery.db"), _paths.DbFile);
        Assert.Equal(Path.Combine(_root, "engine", "python", "python.exe"), _paths.PythonExe);
        Assert.Equal(Path.Combine(_root, "engine", "runner.py"), _paths.RunnerScript);
        Assert.Equal(Path.Combine(_root, "accounts"), _paths.AccountsDir);
        Assert.Equal(Path.Combine(_root, "archive"), _paths.ArchiveDir);
        Assert.Equal(Path.Combine(_root, "logs"), _paths.LogsDir);
        Assert.Equal(Path.Combine(_root, "temp"), _paths.TempDir);
    }

    [Fact]
    public void EnsureCreated_creates_directories()
    {
        _paths.EnsureCreated();
        Assert.True(Directory.Exists(_paths.DataDir));
        Assert.True(Directory.Exists(_paths.AccountsDir));
        Assert.True(Directory.Exists(_paths.TempDir));
    }

    [Fact]
    public void CreateDefault_uses_local_appdata_gallerygui()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalleryGUI");
        Assert.Equal(expected, AppPaths.CreateDefault().Root);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test src/GalleryGUI.sln --filter AppPathsTests`
Expected: FAIL（`AppPaths` 不存在，编译错误）

- [ ] **Step 3: 实现**

`src/GalleryGUI.Core/Paths/AppPaths.cs`：

```csharp
namespace GalleryGUI.Paths;

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string root) => Root = root;

    public static AppPaths CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GalleryGUI"));

    public string Root { get; }
    public string DataDir => Path.Combine(Root, "data");
    public string DbFile => Path.Combine(DataDir, "gallery.db");
    public string EngineDir => Path.Combine(Root, "engine");
    public string PythonExe => Path.Combine(EngineDir, "python", "python.exe");
    public string RunnerScript => Path.Combine(EngineDir, "runner.py");
    public string AccountsDir => Path.Combine(Root, "accounts");
    public string ArchiveDir => Path.Combine(Root, "archive");
    public string LogsDir => Path.Combine(Root, "logs");
    public string TempDir => Path.Combine(Root, "temp");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { DataDir, EngineDir, AccountsDir, ArchiveDir, LogsDir, TempDir })
            Directory.CreateDirectory(dir);
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test src/GalleryGUI.sln --filter AppPathsTests`
Expected: PASS（3 个测试）

- [ ] **Step 5: Commit**

```bash
git add src/GalleryGUI.Core/Paths src/GalleryGUI.Tests/Paths
git commit -m "feat(core): IAppPaths 应用数据目录布局与测试"
```

---

### Task 3: 实体、GalleryDbContext 与迁移

**Files:**
- Create: `src/GalleryGUI.Core/Data/Entities.cs`、`GalleryDbContext.cs`、`GalleryDbContextFactory.cs`、`DbInitializer.cs`
- Modify: `src/GalleryGUI.Core/GalleryGUI.Core.csproj`（添加 `Microsoft.EntityFrameworkCore.Design`）
- Test: `src/GalleryGUI.Tests/Data/GalleryDbContextTests.cs`、`src/GalleryGUI.Tests/Helpers.cs`

**Interfaces:**
- Consumes: `IAppPaths.DbFile`（Task 2）
- Produces: 实体与枚举（Task 9/10/11 直接使用）：

```csharp
public enum AccountStatus { Unverified, Ok, Invalid }
public enum UserSource { Following, Manual, Link }
public enum TargetKind { UserMedia, AccountLikes, AccountBookmarks }
public enum JobStatus { Pending, Running, Completed, Failed, Canceled }
public enum FileStatus { Downloaded, Skipped, Failed }

public sealed class Account { long Id; string SiteId; string? DisplayName; string? ScreenName;
    string CookiePath; AccountStatus Status; bool IsActive; DateTime AddedAt; DateTime? VerifiedAt; }
public sealed class User { long Id; string SiteId; string RestId; string ScreenName; string? DisplayName;
    string? AvatarUrl; string? ProfileUrl; UserSource Source; long? OwnerAccountId; bool IsPinned;
    long DownloadCount; DateTime? LastDownloadAt; DateTime AddedAt; DateTime UpdatedAt; }
public sealed class DownloadJob { long Id; long AccountId; TargetKind TargetKind; long? UserId;
    JobStatus Status; long TotalFiles; long DoneFiles; long SkippedFiles; long FailedFiles;
    string? ErrorMessage; DateTime CreatedAt; DateTime? StartedAt; DateTime? FinishedAt; }
public sealed class DownloadFile { long Id; long JobId; long? UserId; string? SourceItemId;
    string Url; string FilePath; long? FileSize; FileStatus Status; DateTime CreatedAt; }
```

- [ ] **Step 1: 写测试辅助（内存 SQLite）**

`src/GalleryGUI.Tests/Helpers.cs`：

```csharp
using GalleryGUI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Tests;

public static class TestDb
{
    public static (SqliteConnection Connection, GalleryDbContext Db) Create()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<GalleryDbContext>()
            .UseSqlite(conn).Options;
        var db = new GalleryDbContext(options);
        db.Database.EnsureCreated();
        return (conn, db);
    }
}

public static class TestPaths
{
    public static Paths.AppPaths Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "ggui-" + Guid.NewGuid().ToString("N"));
        var p = new Paths.AppPaths(root);
        p.EnsureCreated();
        return p;
    }
}
```

- [ ] **Step 2: 写失败测试**

`src/GalleryGUI.Tests/Data/GalleryDbContextTests.cs`：

```csharp
using GalleryGUI.Data;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Tests.Data;

public class GalleryDbContextTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private GalleryDbContext Db => _t.Item2;

    public GalleryDbContextTests() => _t = TestDb.Create();
    public void Dispose() => _t.Item1.Dispose();

    [Fact]
    public async Task Can_save_and_read_user()
    {
        Db.Users.Add(new User
        {
            SiteId = "twitter", RestId = "44196397", ScreenName = "elonmusk",
            Source = UserSource.Manual, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var found = await Db.Users.SingleAsync(u => u.RestId == "44196397");
        Assert.Equal("elonmusk", found.ScreenName);
    }

    [Fact]
    public async Task SiteId_RestId_is_unique()
    {
        Db.Users.Add(NewUser("u1"));
        await Db.SaveChangesAsync();
        Db.Users.Add(NewUser("u1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());
    }

    private static User NewUser(string restId) => new()
    {
        SiteId = "twitter", RestId = restId, ScreenName = "u" + restId,
        Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };
}
```

- [ ] **Step 3: 运行确认失败**

Run: `dotnet test src/GalleryGUI.sln --filter GalleryDbContextTests`
Expected: FAIL（编译错误，实体不存在）

- [ ] **Step 4: 实现实体与 DbContext**

`src/GalleryGUI.Core/Data/Entities.cs`（按 Produces 签名；实体全部 `public sealed class`，属性 `get; set;`，`DateTime` 用 UTC 约定）。

`src/GalleryGUI.Core/Data/GalleryDbContext.cs`：

```csharp
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
            e.HasOne<DownloadJob>().WithMany().HasForeignKey(f => f.JobId);
        });
        b.Entity<SettingEntry>(e => e.HasKey(s => s.Key));
    }
}
```

`SettingEntry`（加入 Entities.cs）：`public sealed class SettingEntry { public required string Key { get; set; } public string? Value { get; set; } }`

- [ ] **Step 5: 运行确认通过**

Run: `dotnet test src/GalleryGUI.sln --filter GalleryDbContextTests`
Expected: PASS（2 个测试）

- [ ] **Step 6: 建立迁移**

添加包：`dotnet add src/GalleryGUI.Core package Microsoft.EntityFrameworkCore.Design --version 8.0.11`

安装工具（若无）：`dotnet tool install --global dotnet-ef --version 8.0.11`

`src/GalleryGUI.Core/Data/GalleryDbContextFactory.cs`（设计期工厂，使类库项目可独立生成迁移）：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GalleryGUI.Data;

public sealed class GalleryDbContextFactory : IDesignTimeDbContextFactory<GalleryDbContext>
{
    public GalleryDbContext CreateDbContext(string[] args)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalleryGUI");
        var options = new DbContextOptionsBuilder<GalleryDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "data", "gallery.db")}").Options;
        return new GalleryDbContext(options);
    }
}
```

命令：

```bash
dotnet ef migrations add InitialCreate --project src/GalleryGUI.Core
```

- [ ] **Step 7: DbInitializer（迁移 + WAL）**

`src/GalleryGUI.Core/Data/DbInitializer.cs`：

```csharp
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(GalleryDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}
```

测试断言迁移可执行（追加到 `GalleryDbContextTests`）：

```csharp
    [Fact]
    public async Task Initialize_applies_migration_without_error()
    {
        // 内存库上 Migrate 需要先 EnsureDeleted 以清空 EnsureCreated 的痕迹
        await Db.Database.EnsureDeletedAsync();
        var ex = await Record.ExceptionAsync(() => DbInitializer.InitializeAsync(Db));
        Assert.Null(ex);
    }
```

Run: `dotnet test src/GalleryGUI.sln`
Expected: 全部 PASS

- [ ] **Step 8: Commit**

```bash
git add src/GalleryGUI.Core src/GalleryGUI.Tests
git commit -m "feat(core): 实体/GalleryDbContext/迁移与 DbInitializer（WAL）"
```

---

### Task 4: SettingsStore

**Files:**
- Create: `src/GalleryGUI.Core/Services/SettingsStore.cs`
- Test: `src/GalleryGUI.Tests/Services/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: `GalleryDbContext.Settings`（Task 3）
- Produces:

```csharp
public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
}
```

- [ ] **Step 1: 写失败测试**

`src/GalleryGUI.Tests/Services/SettingsStoreTests.cs`：

```csharp
using GalleryGUI.Services;

namespace GalleryGUI.Tests.Services;

public class SettingsStoreTests : IDisposable
{
    private readonly (SqliteConnection, Data.GalleryDbContext) _t;
    private readonly GallerySettingsStore _store;

    public SettingsStoreTests()
    {
        _t = TestDb.Create();
        _store = new GallerySettingsStore(_t.Item2);
    }
    public void Dispose() => _t.Item1.Dispose();

    [Fact]
    public async Task Missing_key_returns_fallback()
    {
        Assert.Equal(7, await _store.GetAsync("nope", 7));
        Assert.Null(await _store.GetAsync<string>("nope"));
    }

    [Fact]
    public async Task Roundtrip_and_overwrite()
    {
        await _store.SetAsync("download.dir", @"D:\Downloads");
        Assert.Equal(@"D:\Downloads", await _store.GetAsync<string>("download.dir"));
        await _store.SetAsync("download.dir", @"E:\Media");
        Assert.Equal(@"E:\Media", await _store.GetAsync<string>("download.dir"));
    }

    [Fact]
    public async Task Complex_value_survives_json()
    {
        await _store.SetAsync("x.options", new { videos = true, sleep = "2" });
        var json = await _store.GetAsync<string>("x.options");
        Assert.Contains("\"videos\":true", json);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test src/GalleryGUI.sln --filter SettingsStoreTests`
Expected: FAIL（编译错误）

- [ ] **Step 3: 实现**

`src/GalleryGUI.Core/Services/SettingsStore.cs`：

```csharp
using System.Text.Json;
using GalleryGUI.Data;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Services;

public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
}

public sealed class GallerySettingsStore(GalleryDbContext db) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var entry = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct);
        if (entry?.Value is null) return default;
        if (typeof(T) == typeof(string)) return (T)(object)entry.Value;
        return JsonSerializer.Deserialize<T>(entry.Value, JsonOptions);
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var json = typeof(T) == typeof(string) ? value as string : JsonSerializer.Serialize(value, JsonOptions);
        var entry = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (entry is null) db.Settings.Add(new SettingEntry { Key = key, Value = json });
        else entry.Value = json;
        await db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test src/GalleryGUI.sln --filter SettingsStoreTests`
Expected: PASS（3 个测试）

- [ ] **Step 5: Commit**

```bash
git add src/GalleryGUI.Core/Services/SettingsStore.cs src/GalleryGUI.Tests/Services/SettingsStoreTests.cs
git commit -m "feat(core): SettingsStore（settings 表 JSON 存取）"
```

---

### Task 5: EngineEvent 与 JsonlParser

**Files:**
- Create: `src/GalleryGUI.Core/Engine/EngineEvent.cs`、`JsonlParser.cs`
- Test: `src/GalleryGUI.Tests/Engine/JsonlParserTests.cs`

**Interfaces:**
- Consumes: 无
- Produces（Task 7/10 全部依赖此模型）：

```csharp
public sealed record EngineEvent(
    string Event,
    string? Url = null, string? Path = null, string? ItemId = null, string? User = null,
    long? Size = null, string? Reason = null, string? Level = null, string? Message = null, string? Kind = null,
    long? Total = null, long? Skipped = null, long? Failed = null,
    int? Protocol = null, string? RunnerVersion = null, string? GalleryDlVersion = null,
    string? RestId = null, string? ScreenName = null, string? DisplayName = null, string? AvatarUrl = null);

public static class JsonlParser { public static EngineEvent? Parse(string line); }
```

解析规则：空行/空白 → null；无 `ev` 键或非法 JSON → null（宽容，不抛异常）；未知附加键忽略；已知键集合：`ev,url,path,item_id,user,size,reason,level,msg,kind,total,skipped,failed,protocol,runner,gallery_dl,rest_id,screen_name,display_name,avatar_url`。

- [ ] **Step 1: 写失败测试**

`src/GalleryGUI.Tests/Engine/JsonlParserTests.cs`：

```csharp
using GalleryGUI.Engine;

namespace GalleryGUI.Tests.Engine;

public class JsonlParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"no_ev\":1}")]
    public void Malformed_lines_return_null(string line)
        => Assert.Null(JsonlParser.Parse(line));

    [Fact]
    public void Parses_hello()
    {
        var ev = JsonlParser.Parse(
            """{"ev":"hello","protocol":1,"runner":"1.0.0","gallery_dl":"1.28.5"}""");
        Assert.NotNull(ev);
        Assert.Equal("hello", ev.Event);
        Assert.Equal(1, ev.Protocol);
        Assert.Equal("1.0.0", ev.RunnerVersion);
        Assert.Equal("1.28.5", ev.GalleryDlVersion);
    }

    [Fact]
    public void Parses_file_events()
    {
        var start = JsonlParser.Parse(
            """{"ev":"file-start","path":"D:/x/1234_user_1.jpg","item_id":"1234"}""");
        Assert.Equal("file-start", start!.Event);
        Assert.Equal("D:/x/1234_user_1.jpg", start.Path);
        Assert.Equal("1234", start.ItemId);

        var done = JsonlParser.Parse(
            """{"ev":"file-done","path":"D:/x/1234_user_1.jpg","size":48213}""");
        Assert.Equal(48213L, done!.Size);

        var skip = JsonlParser.Parse("""{"ev":"file-skip","path":"D:/x/9.jpg"}""");
        Assert.Equal("file-skip", skip!.Event);
    }

    [Fact]
    public void Parses_fatal_with_kind()
    {
        var ev = JsonlParser.Parse("""{"ev":"fatal","msg":"invalid cookie","kind":"auth"}""");
        Assert.Equal("fatal", ev!.Event);
        Assert.Equal("invalid cookie", ev.Message);
        Assert.Equal("auth", ev.Kind);
    }

    [Fact]
    public void Parses_job_done_counters()
    {
        var ev = JsonlParser.Parse(
            """{"ev":"job-done","total":120,"skipped":80,"failed":0}""");
        Assert.Equal(120L, ev!.Total);
        Assert.Equal(80L, ev.Skipped);
        Assert.Equal(0L, ev.Failed);
    }

    [Fact]
    public void Parses_user_event()
    {
        var ev = JsonlParser.Parse(
            """{"ev":"user","rest_id":"44196397","screen_name":"elonmusk","display_name":"Elon Musk","avatar_url":"https://pbs.twimg.com/a.jpg"}""");
        Assert.Equal("user", ev!.Event);
        Assert.Equal("44196397", ev.RestId);
        Assert.Equal("elonmusk", ev.ScreenName);
        Assert.Equal("Elon Musk", ev.DisplayName);
        Assert.Equal("https://pbs.twimg.com/a.jpg", ev.AvatarUrl);
    }

    [Fact]
    public void Unknown_event_name_is_preserved()
    {
        var ev = JsonlParser.Parse("""{"ev":"future-event","x":1}""");
        Assert.Equal("future-event", ev!.Event);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test src/GalleryGUI.sln --filter JsonlParserTests`
Expected: FAIL（编译错误）

- [ ] **Step 3: 实现**

`src/GalleryGUI.Core/Engine/EngineEvent.cs`：按 Produces 声明的 record 原样实现。

`src/GalleryGUI.Core/Engine/JsonlParser.cs`：

```csharp
using System.Text.Json;

namespace GalleryGUI.Engine;

public static class JsonlParser
{
    public static EngineEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        JsonElement json;
        try { json = JsonSerializer.Deserialize<JsonElement>(line); }
        catch (JsonException) { return null; }
        if (json.ValueKind != JsonValueKind.Object) return null;
        if (!json.TryGetProperty("ev", out var evEl) || evEl.ValueKind != JsonValueKind.String)
            return null;

        string? S(string name) => json.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;
        long? L(string name) => json.TryGetProperty(name, out var e) &&
            (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v)) ? v : null;
        int? I(string name) => json.TryGetProperty(name, out var e) &&
            (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v)) ? v : null;

        return new EngineEvent(
            Event: evEl.GetString()!,
            Url: S("url"), Path: S("path"), ItemId: S("item_id"), User: S("user"),
            Size: L("size"), Reason: S("reason"), Level: S("level"), Message: S("msg"), Kind: S("kind"),
            Total: L("total"), Skipped: L("skipped"), Failed: L("failed"),
            Protocol: I("protocol"), RunnerVersion: S("runner"), GalleryDlVersion: S("gallery_dl"),
            RestId: S("rest_id"), ScreenName: S("screen_name"),
            DisplayName: S("display_name"), AvatarUrl: S("avatar_url"));
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test src/GalleryGUI.sln --filter JsonlParserTests`
Expected: PASS（7 个测试）

- [ ] **Step 5: Commit**

```bash
git add src/GalleryGUI.Core/Engine src/GalleryGUI.Tests/Engine
git commit -m "feat(core): EngineEvent 模型与 JSONL 宽容解析器"
```

---

### Task 6: engine/runner.py 与 sites/twitter.py

**Files:**
- Create: `engine/runner.py`、`engine/sites/__init__.py`、`engine/sites/twitter.py`
- Test: `engine/tests/test_runner.py`、`engine/tests/test_twitter.py`

**Interfaces:**
- Consumes: gallery-dl（`PYTHONPATH` 指向仓库根 `gallery-dl\`，不修改上游）
- Produces（Task 7 的 C# 端依赖此 CLI 契约）：

| 子命令 | 参数 | stdout 事件序列 |
|---|---|---|
| `hello` | — | `hello` |
| `whoami` | `--site --cookies` | `hello` → `account`（或 `fatal kind=auth`） |
| `list-following` | `--site --cookies` | `hello` → `user`×N → `end` |
| `user-info` | `--site --cookies --input` | `hello` → `user`（或 `fatal`） |
| `download` | `--site --cookies --job <json>` | `hello` → `url-start`/`file-*`/`log`×… → `job-done` |

退出码：0 成功、1 一般错误（伴随 `fatal`）、2 认证失败（`fatal kind=auth`）。

已验证的 gallery-dl 内部用法（来自仓库 `gallery-dl/gallery_dl/extractor/twitter.py`，版本以此为准）：
- `gallery_dl.extractor.find(url)` 构造提取器实例（cookie 经 `config.set(("extractor","twitter","cookies"), path)` 在 find 之前设置）。
- `TwitterAPI(extractor)`：`extractor.cookies` 提供 auth_token/ct0。
- `api.user_following(screen_name)` 产出**原始** user dict（含 `rest_id`）；经 `extractor._transform_user(raw)` 归一为 `{id:int, name:screen_name, nick:显示名, profile_image:头像}`。
- `api.user_by_screen_name(screen_name)` 返回原始 user dict（同样经 transform）。
- `job.DownloadJob(url).run()` 前猴子补丁 `gallery_dl.output.select` 换成事件发射器（`start/skip/success/progress` 四方法接口，见 `output.py` NullOutput）。
- whoami 走 `GET https://api.x.com/1.1/account/settings.json`，请求头复用 `api.headers`（内含公共 web Bearer 与 csrf）。

- [ ] **Step 1: 写共享基座 sites/__init__.py**

模块结构定稿：共享基座（协议常量、事件发射、配置展开、输入解析、日志桥接）在 `engine/sites/__init__.py`；`runner.py` 只留 CLI 与 `EventOutput`；`sites/twitter.py` 为站点实现。导入关系：`runner.py` 顶部 `from sites import ...`（基座不触发 gallery-dl 导入），子命令内部懒加载 `from sites import twitter`（间接触发 gallery-dl 大量导入）；`sites/twitter.py` 顶部 `from sites import AuthError, parse_screen_name`。无循环导入。

`engine/sites/__init__.py`：

```python
"""runner 共享基座：事件发射、配置展开、输入解析、日志桥接。"""
import json
import logging
import re
import sys

PROTOCOL = 1
RUNNER_VERSION = "1.0.0"


class AuthError(Exception):
    """Cookie 无效或登录态失效。"""


def emit(ev, **kw):
    kw["ev"] = ev
    sys.stdout.write(json.dumps(kw, ensure_ascii=False))
    sys.stdout.write("\n")
    sys.stdout.flush()


def emit_hello():
    try:
        from gallery_dl import version as gdl_version
        ver = gdl_version.__version__
    except Exception:
        ver = None
    emit("hello", protocol=PROTOCOL, runner=RUNNER_VERSION, gallery_dl=ver)


def walk_config(d, base=()):
    """把嵌套 dict 展开为 (path_tuple, value) 序列，供 gallery_dl.config.set 使用。"""
    for k, v in d.items():
        path = base + (k,)
        if isinstance(v, dict):
            yield from walk_config(v, path)
        else:
            yield path, v


def apply_options(options):
    from gallery_dl import config
    for path, value in walk_config(options or {}):
        config.set(path, value)


def parse_screen_name(value):
    """接受 裸用户名 / @user / x.com/user / twitter.com/user(/任意后缀)。"""
    v = (value or "").strip()
    if not v:
        raise ValueError("输入为空")
    m = re.search(r"(?:https?://)?(?:www\.|mobile\.)?(?:twitter|x)\.com/@?([A-Za-z0-9_]{1,15})", v)
    if m:
        return m.group(1)
    m = re.fullmatch(r"@?([A-Za-z0-9_]{1,15})", v)
    if m:
        return m.group(1)
    raise ValueError(f"无法识别用户名或链接: {value!r}")


class EventHandler(logging.Handler):
    """gallery-dl 日志 → log 事件（默认 WARNING 及以上）。"""

    def __init__(self, level=logging.WARNING):
        super().__init__(level=level)

    def emit(self, record):
        if record.exc_info:
            msg = f"{record.getMessage()}: {record.exc_info[0].__name__}"
        else:
            msg = record.getMessage()
        emit("log", level=record.levelname.lower(), msg=msg)
```

- [ ] **Step 2: 写 runner.py 主程序**

`engine/runner.py`：

```python
#!/usr/bin/env python3
"""GalleryGUI 引擎适配器。stdout 输出 JSONL 事件（协议 v1），诊断走 stderr。"""
import argparse
import json
import logging
import os
import re
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from sites import AuthError, EventHandler, emit, emit_hello


class EventOutput:
    """替代 gallery_dl.output 的下载事件发射器（接口同 output.NullOutput）。"""

    def __init__(self, counters):
        self.counters = counters

    def start(self, path):
        m = re.match(r"^(\d+)_", os.path.basename(path))
        self.counters["started"] += 1
        emit("file-start", path=path, item_id=m.group(1) if m else None)

    def skip(self, path):
        self.counters["skipped"] += 1
        emit("file-skip", path=path)

    def success(self, path):
        self.counters["done"] += 1
        emit("file-done", path=path)

    def progress(self, bytes_total, bytes_downloaded, bytes_per_second):
        pass


def cmd_whoami(args):
    from sites import twitter
    emit_hello()
    info = twitter.whoami(args.cookies)
    emit("account", screen_name=info["screen_name"], display_name=info.get("display_name"))


def cmd_list_following(args):
    from sites import twitter
    emit_hello()
    me = twitter.whoami(args.cookies)["screen_name"]
    n = 0
    for user in twitter.list_following(args.cookies, me):
        emit("user", **user)
        n += 1
    emit("end", total=n)


def cmd_user_info(args):
    from sites import twitter
    emit_hello()
    user = twitter.user_info(args.cookies, args.input)
    emit("user", **user)
    emit("end", total=1)


def cmd_download(args):
    from gallery_dl import config, job, output
    from sites import apply_options
    emit_hello()
    with open(args.job, encoding="utf-8") as f:
        spec = json.load(f)
    apply_options(spec.get("options", {}))
    config.set(("extractor", args.site, "cookies"), args.cookies)  # CLI 参数覆盖，双保险
    counters = {"started": 0, "done": 0, "skipped": 0, "failed": 0}
    output.select = lambda: EventOutput(counters)
    logging.getLogger("gallery_dl").addHandler(EventHandler(logging.WARNING))
    for url in spec["urls"]:
        emit("url-start", url=url)
        job.DownloadJob(url).run()
    total = counters["done"] + counters["skipped"] + counters["failed"]
    emit("job-done", total=total, skipped=counters["skipped"], failed=counters["failed"])


def main(argv=None):
    p = argparse.ArgumentParser(prog="runner")
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("hello")
    for name in ("whoami", "list-following", "user-info", "download"):
        sp = sub.add_parser(name)
        sp.add_argument("--site", default="twitter")
        sp.add_argument("--cookies", required=True)
        if name == "user-info":
            sp.add_argument("--input", required=True)
        if name == "download":
            sp.add_argument("--job", required=True)
    args = p.parse_args(argv)

    if args.cmd == "hello":
        emit_hello()
        return 0
    try:
        {"whoami": cmd_whoami,
         "list-following": cmd_list_following,
         "user-info": cmd_user_info,
         "download": cmd_download}[args.cmd](args)
        return 0
    except AuthError as e:
        emit("fatal", msg=str(e), kind="auth")
        return 2
    except Exception as e:
        emit("fatal", msg=f"{type(e).__name__}: {e}")
        traceback.print_exc(file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 3: 写 sites/twitter.py**

`engine/sites/twitter.py`：

```python
"""X (Twitter) 站点命令实现，复用 gallery-dl 内部 API。"""
import requests

from sites import AuthError, parse_screen_name

SETTINGS_URL = "https://api.x.com/1.1/account/settings.json"


def make_api(cookies_path):
    from gallery_dl import config, extractor
    from gallery_dl.extractor.twitter import TwitterAPI
    config.set(("extractor", "twitter", "cookies"), cookies_path)
    # URL 中的 handle 无意义：user_following/user_by_screen_name 都显式传入用户名
    ext = extractor.find("https://x.com/__gallerygui__/following")
    return TwitterAPI(ext), ext


def map_transformed(u):
    """gallery-dl _transform_user 结果 -> 协议 user 事件字段（纯函数，可测）。"""
    return {
        "rest_id": str(u["id"]),
        "screen_name": u.get("name"),
        "display_name": u.get("nick"),
        "avatar_url": u.get("profile_image"),
    }


def _raw_to_user(ext, raw):
    return map_transformed(ext._transform_user(raw))


def whoami(cookies_path):
    api, _ = make_api(cookies_path)
    try:
        resp = requests.get(SETTINGS_URL, headers=api.headers, timeout=30)
    except requests.RequestException as e:
        raise RuntimeError(f"网络请求失败: {e}") from e
    if resp.status_code in (401, 403):
        raise AuthError(f"cookie 无效或已过期（HTTP {resp.status_code}）")
    resp.raise_for_status()
    d = resp.json()
    return {"screen_name": d.get("screen_name"), "display_name": d.get("name")}


def list_following(cookies_path, screen_name):
    api, ext = make_api(cookies_path)
    for raw in api.user_following(screen_name):
        if "rest_id" not in raw:
            continue
        yield _raw_to_user(ext, raw)


def user_info(cookies_path, input_value):
    api, ext = make_api(cookies_path)
    screen = parse_screen_name(input_value)
    return _raw_to_user(ext, api.user_by_screen_name(screen))
```

- [ ] **Step 4: 写 pytest 纯函数测试**

`engine/tests/test_runner.py`（实际导入点为 `sites`）：

```python
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from sites import parse_screen_name, walk_config
import pytest


def test_walk_config_flattens_nested_dicts():
    nested = {
        "extractor": {"twitter": {"videos": True, "filename": "{tweet_id}.jpg"}},
        "base-directory": "D:/x",
    }
    paths = {p: v for p, v in walk_config(nested)}
    assert paths[("extractor", "twitter", "videos")] is True
    assert paths[("extractor", "twitter", "filename")] == "{tweet_id}.jpg"
    assert paths[("base-directory",)] == "D:/x"


@pytest.mark.parametrize("value,expected", [
    ("elonmusk", "elonmusk"),
    ("@elonmusk", "elonmusk"),
    ("https://x.com/elonmusk", "elonmusk"),
    ("https://twitter.com/elonmusk/media", "elonmusk"),
    ("x.com/elonmusk?foo=1", "elonmusk"),
])
def test_parse_screen_name_accepts(value, expected):
    assert parse_screen_name(value) == expected


@pytest.mark.parametrize("value", ["", "https://google.com/x", "!!bad!!", "https://x.com/"])
def test_parse_screen_name_rejects(value):
    with pytest.raises(ValueError):
        parse_screen_name(value)
```

`engine/tests/test_twitter.py`：

```python
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from sites.twitter import map_transformed


def test_map_transformed_fields():
    u = map_transformed({
        "id": 44196397, "name": "elonmusk", "nick": "Elon Musk",
        "profile_image": "https://pbs.twimg.com/profile/a.jpg",
    })
    assert u == {
        "rest_id": "44196397",
        "screen_name": "elonmusk",
        "display_name": "Elon Musk",
        "avatar_url": "https://pbs.twimg.com/profile/a.jpg",
    }
```

- [ ] **Step 5: 运行 pytest**

Run（Git Bash）: `PYTHONPATH="E:\Projects\gallery-gui\gallery-dl" python -m pytest engine/tests -q`
Expected: PASS（6 个测试）。若本机无 Python 3.10+ 或无 pytest，记录并跳过——CI（Phase C）强制执行。

- [ ] **Step 6: 手动验证 hello（有 Python 时）**

Run: `PYTHONPATH="E:\Projects\gallery-gui\gallery-dl" python engine/runner.py hello`
Expected stdout 首行: `{"ev": "hello", "protocol": 1, "runner": "1.0.0", "gallery_dl": "..."}`

- [ ] **Step 7: Commit**

```bash
git add engine
git commit -m "feat(engine): runner.py 协议 v1 + twitter 站点命令（whoami/关注列表/用户信息/下载）"
```

---

### Task 7: RunnerEngine（进程管理与事件接线）

**Files:**
- Create: `src/GalleryGUI.Core/Engine/IDownloadEngine.cs`、`RunnerEngine.cs`
- Modify: `src/GalleryGUI.Tests/GalleryGUI.Tests.csproj`（拷贝 stub 到输出目录）
- Create: `src/GalleryGUI.Tests/Fixtures/stub_runner.py`
- Test: `src/GalleryGUI.Tests/Engine/RunnerEngineTests.cs`

**Interfaces:**
- Consumes: `EngineEvent`/`JsonlParser`（Task 5）、`IAppPaths.TempDir/PythonExe/RunnerScript`（Task 2）、runner CLI 契约（Task 6）
- Produces（Task 9/10/12 全部依赖）：

```csharp
namespace GalleryGUI.Engine;

public sealed record EngineHello(int Protocol, string RunnerVersion, string? GalleryDlVersion);
public sealed record AccountInfo(string ScreenName, string? DisplayName);
public sealed record SiteUserInfo(string RestId, string ScreenName, string? DisplayName, string? AvatarUrl);

public interface IDownloadEngine
{
    Task<EngineHello> HelloAsync(CancellationToken ct = default);
    Task<AccountInfo> WhoAmIAsync(string cookiesFile, CancellationToken ct = default);
    Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(string cookiesFile, CancellationToken ct = default);
    Task<SiteUserInfo> GetUserInfoAsync(string cookiesFile, string input, CancellationToken ct = default);
    Task DownloadAsync(DownloadPlan plan, string cookiesFile, IProgress<EngineEvent> progress, CancellationToken ct = default);
}

public class EngineException(string message, string? kind = null) : Exception(message)
{ public string? Kind { get; } = kind; }
public sealed class AuthException(string message) : EngineException(message, "auth");
```

`DownloadPlan` 来自 Task 8（`SiteId/Urls/BaseDirectory/Options`）。RunnerEngine 构造签名：`RunnerEngine(IAppPaths paths, RunnerEngineOptions options, ILogger<RunnerEngine> logger)`；`RunnerEngineOptions { string PythonExe; string RunnerScript; string GalleryDlPath; TimeSpan HandshakeTimeout = 30s }`（测试可整体替换）。

- [ ] **Step 1: 写 stub runner（测试桩）**

`src/GalleryGUI.Tests/Fixtures/stub_runner.py`（模拟 runner 契约，不依赖 gallery-dl）：

```python
#!/usr/bin/env python3
"""测试桩：按 runner 协议输出事件。"""
import json
import sys
import time


def emit(ev, **kw):
    kw["ev"] = ev
    sys.stdout.write(json.dumps(kw) + "\n")
    sys.stdout.flush()


def main():
    emit("hello", protocol=1, runner="stub-1.0.0", gallery_dl="stub-0.0.1")
    args = sys.argv[1:]
    cmd = args[0]
    if cmd == "hello":
        return 0
    if cmd == "whoami":
        if "bad" in args:
            emit("fatal", msg="cookie 无效", kind="auth")
            return 2
        emit("account", screen_name="stub_user", display_name="Stub User")
        return 0
    if cmd == "list-following":
        emit("user", rest_id="1", screen_name="alice", display_name="Alice",
             avatar_url="https://x/a.png")
        emit("user", rest_id="2", screen_name="bob", display_name="Bob",
             avatar_url="https://x/b.png")
        emit("end", total=2)
        return 0
    if cmd == "user-info":
        emit("user", rest_id="42", screen_name="carol", display_name="Carol",
             avatar_url="https://x/c.png")
        emit("end", total=1)
        return 0
    if cmd == "download":
        job_path = args[args.index("--job") + 1]
        with open(job_path, encoding="utf-8") as f:
            spec = json.load(f)
        if spec.get("fail"):
            emit("fatal", msg="boom")
            return 1
        if spec.get("hang"):
            time.sleep(60)
        for url in spec["urls"]:
            emit("url-start", url=url)
            emit("file-start", path="D:/x/1_a_1.jpg", item_id="1")
            emit("file-done", path="D:/x/1_a_1.jpg", size=100)
            emit("file-skip", path="D:/x/2_b_1.jpg")
        emit("job-done", total=2, skipped=1, failed=0)
        return 0
    return 1


if __name__ == "__main__":
    sys.exit(main())
```

`GalleryGUI.Tests.csproj` 追加：

```xml
  <ItemGroup>
    <None Include="Fixtures\stub_runner.py" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: 写失败测试**

`src/GalleryGUI.Tests/Engine/RunnerEngineTests.cs`：

```csharp
using System.Diagnostics;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalleryGUI.Tests.Engine;

public class RunnerEngineTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private RunnerEngine CreateEngine(string python, string? stubPath = null)
    {
        var stub = stubPath ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "stub_runner.py");
        return new RunnerEngine(_paths,
            new RunnerEngineOptions { PythonExe = python, RunnerScript = stub, GalleryDlPath = "" },
            NullLogger<RunnerEngine>.Instance);
    }

    private static string? LocatePython()
    {
        foreach (var exe in new[] { "python", "python3", "py" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--version")
                { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
                p.WaitForExit(5000);
                if (p is { HasExited: true, ExitCode: 0 }) return exe;
            }
            catch { /* 未安装 */ }
        }
        return null;
    }

    [SkippableFact]
    public async Task Hello_returns_protocol_1()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var hello = await CreateEngine(python!).HelloAsync();
        Assert.Equal(1, hello.Protocol);
    }

    [SkippableFact]
    public async Task WhoAmI_returns_account()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var info = await CreateEngine(python!).WhoAmIAsync("good.txt");
        Assert.Equal("stub_user", info.ScreenName);
    }

    [SkippableFact]
    public async Task WhoAmI_auth_failure_throws_AuthException()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        await Assert.ThrowsAsync<AuthException>(
            () => CreateEngine(python!).WhoAmIAsync("bad.txt"));
    }

    [SkippableFact]
    public async Task ListFollowing_returns_users()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var users = await CreateEngine(python!).ListFollowingAsync("good.txt");
        Assert.Equal(2, users.Count);
        Assert.Equal("alice", users[0].ScreenName);
        Assert.Equal("bob", users[1].ScreenName);
    }

    [SkippableFact]
    public async Task Download_streams_events_and_completes()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var plan = new DownloadPlan("twitter", ["https://x.com/alice/media"],
            _paths.Root, new Dictionary<string, object?>());
        var events = new List<EngineEvent>();
        await CreateEngine(python!).DownloadAsync(plan, "good.txt",
            new Progress<EngineEvent>(events.Add));
        Assert.Contains(events, e => e.Event == "url-start");
        Assert.Contains(events, e => e.Event == "file-done" && e.Size == 100);
        var done = events.Single(e => e.Event == "job-done");
        Assert.Equal(2, done.Total);
    }

    [SkippableFact]
    public async Task Download_fatal_throws_EngineException()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var plan = new DownloadPlan("twitter", ["https://x.com/alice/media"],
            _paths.Root, new Dictionary<string, object?> { ["fail"] = true });
        await Assert.ThrowsAsync<EngineException>(() =>
            CreateEngine(python!).DownloadAsync(plan, "good.txt", new Progress<EngineEvent>()));
    }

    [SkippableFact]
    public async Task Download_cancel_kills_process()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var plan = new DownloadPlan("twitter", ["https://x.com/alice/media"],
            _paths.Root, new Dictionary<string, object?> { ["hang"] = true });
        using var cts = new CancellationTokenSource(1000);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateEngine(python!).DownloadAsync(plan, "good.txt",
                new Progress<EngineEvent>(), cts.Token));
    }
}
```

- [ ] **Step 3: 运行确认失败**

Run: `dotnet test src/GalleryGUI.sln --filter RunnerEngineTests`
Expected: FAIL（编译错误，RunnerEngine 不存在）

- [ ] **Step 4: 实现 RunnerEngine**

`src/GalleryGUI.Core/Engine/IDownloadEngine.cs`：按 Produces 实现（record + interface + 两个异常类）。

`src/GalleryGUI.Core/Engine/RunnerEngine.cs`：

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GalleryGUI.Paths;
using Microsoft.Extensions.Logging;

namespace GalleryGUI.Engine;

public sealed class RunnerEngineOptions
{
    public string PythonExe { get; set; } = "";
    public string RunnerScript { get; set; } = "";
    public string GalleryDlPath { get; set; } = "";
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class RunnerEngine(IAppPaths paths, RunnerEngineOptions options, ILogger<RunnerEngine> log)
    : IDownloadEngine
{
    private static readonly JsonSerializerOptions JobJson = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record RunnerResult(int ExitCode, string StderrTail);

    public async Task<EngineHello> HelloAsync(CancellationToken ct = default)
    {
        EngineHello? hello = null;
        var result = await RunAsync(["hello"], ev =>
        {
            if (ev.Event == "hello" && ev.Protocol is not null)
                hello = new EngineHello(ev.Protocol.Value, ev.RunnerVersion ?? "?", ev.GalleryDlVersion);
        }, ct, killOnCancel: false);
        if (hello is null)
            throw new EngineException(
                $"runner 未输出 hello 事件（engine 未安装或 python 缺失）。退出码 {result.ExitCode}：{result.StderrTail}");
        if (hello.Protocol != 1)
            throw new EngineException($"runner 协议版本 {hello.Protocol} 与应用不兼容（需要 1）");
        return hello;
    }

    public async Task<AccountInfo> WhoAmIAsync(string cookiesFile, CancellationToken ct = default)
    {
        AccountInfo? account = null;
        var result = await RunAsync(["whoami", "--site", "twitter", "--cookies", cookiesFile],
            ev => { if (ev.Event == "account") account = new AccountInfo(ev.ScreenName ?? "?", ev.DisplayName); },
            ct, killOnCancel: false);
        return account ?? throw EngineError(result);
    }

    public async Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(string cookiesFile, CancellationToken ct = default)
    {
        var users = new List<SiteUserInfo>();
        var result = await RunAsync(["list-following", "--site", "twitter", "--cookies", cookiesFile],
            ev => { if (ev.Event == "user" && ev.RestId is not null) users.Add(ToUser(ev)); },
            ct, killOnCancel: false);
        if (users.Count == 0 && result.ExitCode != 0) throw EngineError(result);
        return users;
    }

    public async Task<SiteUserInfo> GetUserInfoAsync(string cookiesFile, string input, CancellationToken ct = default)
    {
        SiteUserInfo? user = null;
        var result = await RunAsync(["user-info", "--site", "twitter", "--cookies", cookiesFile, "--input", input],
            ev => { if (ev.Event == "user" && user is null) user = ToUser(ev); },
            ct, killOnCancel: false);
        return user ?? throw EngineError(result);
    }

    public async Task DownloadAsync(DownloadPlan plan, string cookiesFile,
        IProgress<EngineEvent> progress, CancellationToken ct = default)
    {
        paths.EnsureCreated();
        var jobFile = Path.Combine(paths.TempDir, $"job-{Guid.NewGuid():N}.json");
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["urls"] = plan.Urls,
                ["options"] = plan.Options,
            };
            await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(payload, JobJson), ct);

            var args = new List<string>
            { "download", "--site", plan.SiteId, "--cookies", cookiesFile, "--job", jobFile };
            await RunAsync(args, progress.Report, ct, killOnCancel: true);
        }
        finally
        {
            try { File.Delete(jobFile); } catch { /* 尽力清理 */ }
        }
    }

    private static SiteUserInfo ToUser(EngineEvent ev) =>
        new(ev.RestId!, ev.ScreenName ?? "?", ev.DisplayName, ev.AvatarUrl);

    private static EngineException EngineError(RunnerResult result) =>
        result.ExitCode == 2
            ? new AuthException(result.StderrTail.Length > 0 ? result.StderrTail : "runner 认证失败")
            : new EngineException($"runner 退出码 {result.ExitCode}：{result.StderrTail}");

    private async Task<RunnerResult> RunAsync(IReadOnlyList<string> args,
        Action<EngineEvent> onEvent, CancellationToken ct, bool killOnCancel)
    {
        if (string.IsNullOrEmpty(options.PythonExe))
            throw new EngineException("引擎未安装：找不到 python（首次启动会从安装目录播种 engine）");

        var psi = new ProcessStartInfo
        {
            FileName = options.PythonExe,
            WorkingDirectory = paths.EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(options.RunnerScript);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(options.GalleryDlPath))
            psi.EnvironmentVariables["PYTHONPATH"] = options.GalleryDlPath;

        var p = new Process { StartInfo = psi };
        p.Start();

        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        string? fatalMessage = null, fatalKind = null;

        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                var ev = JsonlParser.Parse(line);
                if (ev is null) continue;
                if (ev.Event == "fatal")
                {
                    fatalMessage = ev.Message ?? "runner fatal";
                    fatalKind = ev.Kind;
                }
                onEvent(ev);
            }
        }, CancellationToken.None);

        try
        {
            await stdoutTask;
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(p);
            throw;
        }

        var stderr = await stderrTask;
        var tail = stderr.Length > 2000 ? stderr[^2000..] : stderr;
        TryDispose(p);

        if (killOnCancel && ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        if (fatalMessage is not null)
            throw fatalKind == "auth"
                ? new AuthException(fatalMessage)
                : new EngineException(fatalMessage, fatalKind);
        return new RunnerResult(p.ExitCode, tail);
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
    }

    private static void TryDispose(Process p)
    {
        try { p.Dispose(); } catch { }
    }
}
```

实现注意：
- `ReadLineAsync(ct)` 用 .NET 8 的 `StreamReader.ReadLineAsync(CancellationToken)` 重载；stdout 读取循环用 `CancellationToken.None` 保底，取消靠杀进程触发流结束。
- fatal 事件抛 `AuthException`（kind=auth）或 `EngineException`；取消（killOnCancel 且 ct 已取消）抛 `OperationCanceledException`；非零退出且无 fatal 抛含 stderr 尾部摘要的 `EngineException`（ExitCode==2 → `AuthException`）。

- [ ] **Step 5: 运行确认通过**

Run: `dotnet test src/GalleryGUI.sln --filter RunnerEngineTests`
Expected: PASS（7 个测试；本机无 Python 时全部 SKIP 并注明原因）

- [ ] **Step 6: Commit**

```bash
git add src/GalleryGUI.Core/Engine src/GalleryGUI.Tests
git commit -m "feat(core): RunnerEngine 进程管理/事件流/取消与 stub 测试"
```

---

### Task 8: ISiteProvider 抽象与 TwitterSiteProvider

**Files:**
- Create: `src/GalleryGUI.Core/Sites/ISiteProvider.cs`、`TwitterSiteProvider.cs`、`SiteRegistry.cs`
- Test: `src/GalleryGUI.Tests/Sites/TwitterSiteProviderTests.cs`

**Interfaces:**
- Consumes: 无（Sites 层是纯函数式的，不依赖引擎与数据库）
- Produces（Task 7 `DownloadPlan`、Task 9/10 消费）：

```csharp
namespace GalleryGUI.Sites;

public enum ContentKind { UserMedia, AccountLikes, AccountBookmarks }
public enum OptionKind { Boolean, Text, Choice }

public sealed record UserTarget(long? UserId, string? ScreenName, string? BaseDirectory = null);
public sealed record UserInputParseResult(bool Ok, string? ScreenName = null, string? Error = null);
public sealed record DownloadPaths(string CookiesFile, string ArchiveFile);
public sealed record DownloadPlan(
    string SiteId, IReadOnlyList<string> Urls, string BaseDirectory,
    IReadOnlyDictionary<string, object?> Options);
public sealed record OptionField(string Key, OptionKind Kind, object? Default,
    string DisplayName, IReadOnlyList<string>? Choices = null);
public sealed record OptionSchema(IReadOnlyList<OptionField> Fields);

public interface ISiteProvider
{
    string SiteId { get; }
    string DisplayName { get; }
    IReadOnlyList<ContentKind> SupportedKinds { get; }
    OptionSchema OptionsSchema { get; }
    IReadOnlyDictionary<string, object?> DefaultOptions { get; }
    UserInputParseResult ParseInput(string input);
    string BuildProfileUrl(string screenName);
    DownloadPlan BuildDownload(ContentKind kind, UserTarget target,
        IReadOnlyDictionary<string, object?> options, DownloadPaths paths);
}

public sealed class SiteRegistry
{
    public SiteRegistry(IEnumerable<ISiteProvider> providers);
    public ISiteProvider Get(string siteId);          // 未知站点抛 KeyNotFoundException
    public IReadOnlyList<ISiteProvider> All { get; }
}
```

- [ ] **Step 1: 写失败测试**

`src/GalleryGUI.Tests/Sites/TwitterSiteProviderTests.cs`：

```csharp
using GalleryGUI.Sites;

namespace GalleryGUI.Tests.Sites;

public class TwitterSiteProviderTests
{
    private readonly TwitterSiteProvider _site = new();

    [Theory]
    [InlineData("elonmusk", "elonmusk")]
    [InlineData("@elonmusk", "elonmusk")]
    [InlineData("https://x.com/elonmusk", "elonmusk")]
    [InlineData("https://twitter.com/elonmusk/media", "elonmusk")]
    [InlineData("https://mobile.x.com/elonmusk?foo=1", "elonmusk")]
    public void ParseInput_accepts(string input, string expected)
    {
        var r = _site.ParseInput(input);
        Assert.True(r.Ok);
        Assert.Equal(expected, r.ScreenName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://x.com/i/status/123")]     // 保留路径非用户名
    [InlineData("https://x.com/i/bookmarks")]
    [InlineData("https://x.com/home")]
    [InlineData("https://google.com/foo")]
    [InlineData("!!bad!!")]
    public void ParseInput_rejects(string input)
        => Assert.False(_site.ParseInput(input).Ok);

    [Fact]
    public void BuildDownload_user_media_maps_options()
    {
        var plan = _site.BuildDownload(ContentKind.UserMedia,
            new UserTarget(7, "alice", @"D:\dl"),
            new Dictionary<string, object?> { ["videos"] = true, ["retweets"] = false, ["sleep"] = "2" },
            new DownloadPaths(@"C:\a\cookies.txt", @"C:\arc\1.txt"));
        Assert.Equal("twitter", plan.SiteId);
        Assert.Equal(["https://x.com/alice/media"], plan.Urls);
        Assert.Equal(@"D:\dl", plan.BaseDirectory);
        var ex = (IReadOnlyDictionary<string, object?>)plan.Options["extractor"]!["twitter"]!;
        Assert.True((bool)ex["videos"]!);
        Assert.False((bool)ex["retweets"]!);
        Assert.Equal(@"C:\a\cookies.txt", ex["cookies"]);
        Assert.Equal("2", ex["sleep-request"]);
        Assert.Equal(@"C:\arc\1.txt", plan.Options["download-archive"]);
        Assert.Equal(@"D:\dl", plan.Options["base-directory"]);
    }

    [Fact]
    public void BuildDownload_likes_and_bookmarks()
    {
        var likes = _site.BuildDownload(ContentKind.AccountLikes,
            new UserTarget(null, "me", @"D:\dl"), _site.DefaultOptions,
            new DownloadPaths("c", "a"));
        Assert.Equal(["https://x.com/me/likes"], likes.Urls);

        var bookmarks = _site.BuildDownload(ContentKind.AccountBookmarks,
            new UserTarget(null, "me", @"D:\dl"), _site.DefaultOptions, new DownloadPaths("c", "a"));
        Assert.Equal(["https://x.com/i/bookmarks"], bookmarks.Urls);
    }

    [Fact]
    public void Schema_defaults_and_profile_url()
    {
        Assert.True((bool)_site.DefaultOptions["videos"]!);
        Assert.False((bool)_site.DefaultOptions["retweets"]!);
        Assert.Equal("https://x.com/elonmusk", _site.BuildProfileUrl("elonmusk"));
        Assert.Contains(_site.OptionsSchema.Fields, f => f.Key == "filename" && f.Kind == OptionKind.Text);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test src/GalleryGUI.sln --filter TwitterSiteProviderTests`
Expected: FAIL（编译错误）

- [ ] **Step 3: 实现**

`src/GalleryGUI.Core/Sites/ISiteProvider.cs`：按 Produces 实现。

`src/GalleryGUI.Core/Sites/TwitterSiteProvider.cs`：

```csharp
using System.Text.RegularExpressions;

namespace GalleryGUI.Sites;

public sealed partial class TwitterSiteProvider : ISiteProvider
{
    public const string Id = "twitter";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "home", "explore", "notifications", "messages", "settings", "search",
        "intent", "hashtag", "share", "compose", "bookmarks",
    };

    [GeneratedRegex(
        @"^(?:https?://)?(?:www\.|mobile\.)?(?:twitter|x)\.com/@?(?<name>[A-Za-z0-9_]{1,15})(?:[/?#].*)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^@?(?<name>[A-Za-z0-9_]{1,15})$")]
    private static partial Regex NameRegex();

    public string SiteId => Id;
    public string DisplayName => "X (Twitter)";
    public IReadOnlyList<ContentKind> SupportedKinds { get; } =
        [ContentKind.UserMedia, ContentKind.AccountLikes, ContentKind.AccountBookmarks];

    public OptionSchema OptionsSchema { get; } = new(
    [
        new OptionField("videos", OptionKind.Boolean, true, "同时下载视频和动图"),
        new OptionField("retweets", OptionKind.Boolean, false, "包含转推"),
        new OptionField("quoted", OptionKind.Boolean, false, "包含引用推文"),
        new OptionField("replies", OptionKind.Boolean, false, "包含回复"),
        new OptionField("filename", OptionKind.Text, "{tweet_id}_{author[name]}_{num}.{extension}", "文件命名模板"),
        new OptionField("sleep", OptionKind.Text, "1", "请求间隔（秒）"),
    ]);

    public IReadOnlyDictionary<string, object?> DefaultOptions { get; } =
        new Dictionary<string, object?>
        {
            ["videos"] = true, ["retweets"] = false, ["quoted"] = false, ["replies"] = false,
            ["filename"] = "{tweet_id}_{author[name]}_{num}.{extension}", ["sleep"] = "1",
        };

    public UserInputParseResult ParseInput(string input)
    {
        var v = (input ?? "").Trim();
        if (v.Length == 0) return new(false, Error: "输入为空");
        var m = UrlRegex().Match(v);
        if (m.Success)
        {
            var name = m.Groups["name"].Value;
            return Reserved.Contains(name)
                ? new(false, Error: $"'{name}' 不是用户主页链接")
                : new(true, name);
        }
        m = NameRegex().Match(v);
        if (m.Success) return new(true, m.Groups["name"].Value);
        return new(false, Error: $"无法识别的用户名或链接：{v}");
    }

    public string BuildProfileUrl(string screenName) => $"https://x.com/{screenName}";

    public DownloadPlan BuildDownload(ContentKind kind, UserTarget target,
        IReadOnlyDictionary<string, object?> options, DownloadPaths paths)
    {
        var url = kind switch
        {
            ContentKind.UserMedia => $"https://x.com/{target.ScreenName}/media",
            ContentKind.AccountLikes => $"https://x.com/{target.ScreenName}/likes",
            ContentKind.AccountBookmarks => "https://x.com/i/bookmarks",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        bool B(string k, bool d) => options.TryGetValue(k, out var v) && v is bool b ? b : d;
        string S(string k, string d) => options.TryGetValue(k, out var v) && v is string s ? s : d;

        var extractorOptions = new Dictionary<string, object?>
        {
            ["cookies"] = paths.CookiesFile,
            ["videos"] = B("videos", true),
            ["retweets"] = B("retweets", false),
            ["quoted"] = B("quoted", false),
            ["replies"] = B("replies", false),
            ["filename"] = S("filename", "{tweet_id}_{author[name]}_{num}.{extension}"),
            ["sleep-request"] = S("sleep", "1"),
        };
        var nested = new Dictionary<string, object?>
        {
            ["extractor"] = new Dictionary<string, object?> { [Id] = extractorOptions },
            ["download-archive"] = paths.ArchiveFile,
            ["base-directory"] = target.BaseDirectory ?? "",
        };
        return new DownloadPlan(Id, [url], target.BaseDirectory ?? "", nested);
    }
}
```

`src/GalleryGUI.Core/Sites/SiteRegistry.cs`：

```csharp
namespace GalleryGUI.Sites;

public sealed class SiteRegistry
{
    private readonly Dictionary<string, ISiteProvider> _byId;

    public SiteRegistry(IEnumerable<ISiteProvider> providers) =>
        _byId = providers.ToDictionary(p => p.SiteId, StringComparer.OrdinalIgnoreCase);

    public ISiteProvider Get(string siteId) =>
        _byId.TryGetValue(siteId, out var p)
            ? p
            : throw new KeyNotFoundException($"未知站点：{siteId}");

    public IReadOnlyList<ISiteProvider> All => [.. _byId.Values];
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test src/GalleryGUI.sln`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add src/GalleryGUI.Core/Sites src/GalleryGUI.Tests/Sites
git commit -m "feat(core): ISiteProvider 抽象与 TwitterSiteProvider（输入解析/下载计划/选项模式）"
```

---

### Task 9: AccountService 与 UserService

**Files:**
- Create: `src/GalleryGUI.Core/Services/AccountService.cs`、`UserService.cs`
- Create: `src/GalleryGUI.Core/DependencyInjection.cs`
- Test: `src/GalleryGUI.Tests/Services/AccountServiceTests.cs`、`UserServiceTests.cs`

**Interfaces:**
- Consumes: `IDownloadEngine`（Task 7）、`ISiteProvider`/`SiteRegistry`（Task 8）、`GalleryDbContext`（Task 3）、`IAppPaths`（Task 2）
- Produces（Task 10/Phase B 依赖）：

```csharp
namespace GalleryGUI.Services;

public sealed record ImportCookiesResult(Account Account, string? Error = null)
{ public bool Ok => Error is null; }

public interface IAccountService
{
    Task<ImportCookiesResult> ImportCookiesAsync(string siteId, string cookiesFilePath, CancellationToken ct = default);
    Task<ImportCookiesResult> VerifyAsync(Account account, CancellationToken ct = default);
    Task DeleteAsync(Account account, CancellationToken ct = default);
}

public interface IUserService
{
    Task<int> ImportFollowingAsync(Account account, CancellationToken ct = default);
    Task<User> AddUserAsync(Account account, string input, CancellationToken ct = default);
    Task RemoveAsync(IReadOnlyList<long> userIds, CancellationToken ct = default);
    Task SetPinnedAsync(long userId, bool pinned, CancellationToken ct = default);
}
```

`AccountService` 构造：`(GalleryDbContext db, IDownloadEngine engine, IAppPaths paths, SiteRegistry sites, ILogger<AccountService> log)`；`UserService` 构造：`(GalleryDbContext db, IDownloadEngine engine, IAppPaths paths, SiteRegistry sites, ILogger<UserService> log)`。cookie 相对路径换算：`AccountService.AbsoluteCookiePath(IAppPaths, Account)`（public static，Task 10 复用）。

- [ ] **Step 1: 写失败测试**

`src/GalleryGUI.Tests/Services/AccountServiceTests.cs`：

```csharp
using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalleryGUI.Tests.Services;

public class AccountServiceTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly AppPaths _paths;
    private readonly FakeEngine _engine;
    private readonly AccountService _svc;

    public AccountServiceTests()
    {
        _t = TestDb.Create();
        _paths = TestPaths.Create();
        _engine = new FakeEngine();
        _svc = new AccountService(_t.Item2, _engine, _paths,
            new SiteRegistry([new TwitterSiteProvider()]),
            NullLogger<AccountService>.Instance);
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private string MakeCookiesFile()
    {
        var file = Path.Combine(_paths.TempDir, $"cookies-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "# Netscape HTTP Cookie File\n");
        return file;
    }

    [Fact]
    public async Task Import_copies_file_creates_account_and_verifies()
    {
        var result = await _svc.ImportCookiesAsync("twitter", MakeCookiesFile());
        Assert.True(result.Ok);
        Assert.Equal(AccountStatus.Ok, result.Account.Status);
        Assert.Equal("stub_user", result.Account.ScreenName);
        Assert.True(result.Account.IsActive);
        // 文件已复制到 accounts 目录（不再引用原路径）
        Assert.True(File.Exists(AccountService.AbsoluteCookiePath(_paths, result.Account)));
        Assert.StartsWith($"twitter{Path.DirectorySeparatorChar}", result.Account.CookiePath);
    }

    [Fact]
    public async Task Import_with_invalid_cookie_marks_Invalid()
    {
        _engine.WhoAmIError = new AuthException("cookie 无效");
        var result = await _svc.ImportCookiesAsync("twitter", MakeCookiesFile());
        Assert.False(result.Ok);
        Assert.Equal(AccountStatus.Invalid, result.Account.Status);
        Assert.Contains("Cookie", result.Error);
    }

    [Fact]
    public async Task New_import_deactivates_previous_account()
    {
        await _svc.ImportCookiesAsync("twitter", MakeCookiesFile());
        var second = await _svc.ImportCookiesAsync("twitter", MakeCookiesFile());
        var all = _t.Item2.Accounts.Where(a => a.SiteId == "twitter").ToList();
        Assert.Equal(2, all.Count);
        Assert.Single(all.Where(a => a.IsActive));
        Assert.Equal(second.Account.Id, all.Single(a => a.IsActive).Id);
    }
}
```

`src/GalleryGUI.Tests/Services/UserServiceTests.cs`：

```csharp
using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalleryGUI.Tests.Services;

public class UserServiceTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine;
    private readonly UserService _svc;
    private readonly Account _account;

    public UserServiceTests()
    {
        _t = TestDb.Create();
        _engine = new FakeEngine();
        _svc = new UserService(_t.Item2, _engine, _paths,
            new SiteRegistry([new TwitterSiteProvider()]),
            NullLogger<UserService>.Instance);
        _account = new Account
        { SiteId = "twitter", CookiePath = "twitter\\c\\cookies.txt", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _t.Item2.Accounts.Add(_account);
        _t.Item2.SaveChanges();
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [Fact]
    public async Task ImportFollowing_upserts_users()
    {
        var count = await _svc.ImportFollowingAsync(_account);
        Assert.Equal(2, count);

        // 二次导入不产生重复，且资料被刷新
        _engine.NextFollowing = [new SiteUserInfo("1", "alice2", "Alice Renamed", "https://x/a2.png")];
        var count2 = await _svc.ImportFollowingAsync(_account);
        Assert.Equal(1, count2);
        var users = _t.Item2.Users.ToList();
        Assert.Equal(2, users.Count);
        var alice = users.Single(u => u.RestId == "1");
        Assert.Equal("alice2", alice.ScreenName);
        Assert.Equal(UserSource.Following, alice.Source);
        Assert.Equal("https://x.com/alice2", alice.ProfileUrl);
    }

    [Fact]
    public async Task AddUser_accepts_url_and_username()
    {
        var byUrl = await _svc.AddUserAsync(_account, "https://x.com/carol");
        Assert.Equal("carol", byUrl.ScreenName);
        Assert.Equal(UserSource.Link, byUrl.Source);

        var byName = await _svc.AddUserAsync(_account, "@carol");
        Assert.Equal(byUrl.Id, byName.Id);   // 同一 rest_id 幂等
        Assert.Equal(UserSource.Manual, byName.Source);
    }

    [Fact]
    public async Task AddUser_invalid_input_throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _svc.AddUserAsync(_account, "!!bad!!"));
    }

    [Fact]
    public async Task Remove_deletes_users()
    {
        var u1 = await _svc.AddUserAsync(_account, "carol");
        var u2 = await _svc.AddUserAsync(_account, "dave");
        await _svc.RemoveAsync([u1.Id, u2.Id]);
        Assert.Empty(_t.Item2.Users);
    }

    [Fact]
    public async Task SetPinned_toggles()
    {
        var u = await _svc.AddUserAsync(_account, "carol");
        await _svc.SetPinnedAsync(u.Id, true);
        Assert.True(_t.Item2.Users.Single(x => x.Id == u.Id).IsPinned);
    }
}
```

`FakeEngine`（加入测试 Helpers.cs）：

```csharp
using GalleryGUI.Engine;
using GalleryGUI.Sites;

namespace GalleryGUI.Tests;

public sealed class FakeEngine : IDownloadEngine
{
    public EngineHello Hello { get; set; } = new(1, "fake", "fake");
    public AccountInfo WhoAmI { get; set; } = new("stub_user", "Stub User");
    public Exception? WhoAmIError { get; set; }
    public IReadOnlyList<SiteUserInfo> NextFollowing { get; set; } =
        [new("1", "alice", "Alice", "https://x/a.png"), new("2", "bob", "Bob", "https://x/b.png")];
    public SiteUserInfo NextUserInfo { get; set; } = new("42", "carol", "Carol", "https://x/c.png");
    public Func<DownloadPlan, string, IProgress<EngineEvent>, CancellationToken, Task>? OnDownload { get; set; }
    public List<(DownloadPlan Plan, string CookiesFile, IProgress<EngineEvent> Progress)> Downloads { get; } = [];

    public Task<EngineHello> HelloAsync(CancellationToken ct = default) => Task.FromResult(Hello);

    public Task<AccountInfo> WhoAmIAsync(string cookiesFile, CancellationToken ct = default)
        => WhoAmIError is not null ? Task.FromException<AccountInfo>(WhoAmIError) : Task.FromResult(WhoAmI);

    public Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(string cookiesFile, CancellationToken ct = default)
        => Task.FromResult(NextFollowing);

    public Task<SiteUserInfo> GetUserInfoAsync(string cookiesFile, string input, CancellationToken ct = default)
        => Task.FromResult(NextUserInfo);

    public Task DownloadAsync(DownloadPlan plan, string cookiesFile, IProgress<EngineEvent> progress, CancellationToken ct = default)
    {
        Downloads.Add((plan, cookiesFile, progress));
        return OnDownload?.Invoke(plan, cookiesFile, progress, ct) ?? Task.CompletedTask;
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test src/GalleryGUI.sln --filter "AccountServiceTests|UserServiceTests"`
Expected: FAIL（编译错误）

- [ ] **Step 3: 实现 AccountService**

`src/GalleryGUI.Core/Services/AccountService.cs`：

```csharp
using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalleryGUI.Services;

public sealed record ImportCookiesResult(Account Account, string? Error = null)
{ public bool Ok => Error is null; }

public interface IAccountService
{
    Task<ImportCookiesResult> ImportCookiesAsync(string siteId, string cookiesFilePath, CancellationToken ct = default);
    Task<ImportCookiesResult> VerifyAsync(Account account, CancellationToken ct = default);
    Task DeleteAsync(Account account, CancellationToken ct = default);
}

public sealed class AccountService(
    GalleryDbContext db,
    IDownloadEngine engine,
    IAppPaths paths,
    SiteRegistry sites,
    ILogger<AccountService> log) : IAccountService
{
    public static string AbsoluteCookiePath(IAppPaths paths, Account account) =>
        Path.Combine(paths.AccountsDir, account.CookiePath);

    public async Task<ImportCookiesResult> ImportCookiesAsync(
        string siteId, string cookiesFilePath, CancellationToken ct = default)
    {
        sites.Get(siteId); // 校验站点存在
        var id = Guid.NewGuid().ToString("N");
        var relative = Path.Combine(siteId, id, "cookies.txt");
        var target = Path.Combine(paths.AccountsDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(cookiesFilePath, target, overwrite: true);

        // 每站点仅一个活动账号：先全部停用
        await db.Accounts
            .Where(a => a.SiteId == siteId && a.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false), ct);

        var account = new Account
        {
            SiteId = siteId,
            CookiePath = relative,
            Status = AccountStatus.Unverified,
            IsActive = true,
            AddedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);

        var verify = await VerifyAsync(account, ct);
        return verify.Ok
            ? new ImportCookiesResult(verify.Account)
            : new ImportCookiesResult(verify.Account, verify.Error);
    }

    public async Task<ImportCookiesResult> VerifyAsync(Account account, CancellationToken ct = default)
    {
        try
        {
            var who = await engine.WhoAmIAsync(AbsoluteCookiePath(paths, account), ct);
            account.ScreenName = who.ScreenName;
            account.DisplayName = who.DisplayName;
            account.Status = AccountStatus.Ok;
            account.VerifiedAt = DateTime.UtcNow;
        }
        catch (AuthException e)
        {
            log.LogWarning(e, "账号验证失败（cookie 无效）");
            account.Status = AccountStatus.Invalid;
        }
        catch (EngineException e)
        {
            log.LogWarning(e, "账号验证失败（引擎错误）");
            return new(account, $"引擎错误：{e.Message}");
        }
        await db.SaveChangesAsync(ct);
        return account.Status == AccountStatus.Ok
            ? new(account)
            : new(account, "Cookie 无效或已过期，请重新导出");
    }

    public async Task DeleteAsync(Account account, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(AbsoluteCookiePath(paths, account));
        db.Accounts.Remove(account);
        await db.SaveChangesAsync(ct);
        if (dir is not null && Directory.Exists(dir))
            try { Directory.Delete(dir, true); } catch { /* 尽力清理 */ }
    }
}
```

- [ ] **Step 4: 实现 UserService 与 DI 装配**

`src/GalleryGUI.Core/Services/UserService.cs`：

```csharp
using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalleryGUI.Services;

public interface IUserService
{
    Task<int> ImportFollowingAsync(Account account, CancellationToken ct = default);
    Task<User> AddUserAsync(Account account, string input, CancellationToken ct = default);
    Task RemoveAsync(IReadOnlyList<long> userIds, CancellationToken ct = default);
    Task SetPinnedAsync(long userId, bool pinned, CancellationToken ct = default);
}

public sealed class UserService(
    GalleryDbContext db,
    IDownloadEngine engine,
    IAppPaths paths,
    SiteRegistry sites,
    ILogger<UserService> log) : IUserService
{
    public async Task<int> ImportFollowingAsync(Account account, CancellationToken ct = default)
    {
        var cookies = AccountService.AbsoluteCookiePath(paths, account);
        var followed = await engine.ListFollowingAsync(cookies, ct);
        var provider = sites.Get(account.SiteId);
        foreach (var info in followed)
            await UpsertAsync(account, info, UserSource.Following, provider, ct);
        return followed.Count;
    }

    public async Task<User> AddUserAsync(Account account, string input, CancellationToken ct = default)
    {
        var provider = sites.Get(account.SiteId);
        var parsed = provider.ParseInput(input);
        if (!parsed.Ok || parsed.ScreenName is null)
            throw new ArgumentException(parsed.Error ?? "无法识别输入", nameof(input));
        var info = await engine.GetUserInfoAsync(
            AccountService.AbsoluteCookiePath(paths, account), input, ct);
        return await UpsertAsync(account, info,
            input.Contains("://") ? UserSource.Link : UserSource.Manual,
            provider, ct);
    }

    public async Task RemoveAsync(IReadOnlyList<long> userIds, CancellationToken ct = default)
    {
        await db.Users.Where(u => userIds.Contains(u.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task SetPinnedAsync(long userId, bool pinned, CancellationToken ct = default)
    {
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPinned, pinned), ct);
    }

    private async Task<User> UpsertAsync(
        Account account, SiteUserInfo info, UserSource source,
        ISiteProvider provider, CancellationToken ct)
    {
        var user = await db.Users
            .SingleOrDefaultAsync(u => u.SiteId == account.SiteId && u.RestId == info.RestId, ct);
        if (user is null)
        {
            user = new User
            {
                SiteId = account.SiteId,
                RestId = info.RestId,
                Source = source,
                OwnerAccountId = account.Id,
                AddedAt = DateTime.UtcNow,
            };
            db.Users.Add(user);
        }
        user.ScreenName = info.ScreenName;
        user.DisplayName = info.DisplayName;
        user.AvatarUrl = info.AvatarUrl;
        user.ProfileUrl = provider.BuildProfileUrl(info.ScreenName);
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }
}
```

`src/GalleryGUI.Core/DependencyInjection.cs`：

```csharp
using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GalleryGUI;

public static class CoreServices
{
    public static IServiceCollection AddGalleryCore(this IServiceCollection services, IAppPaths paths)
    {
        paths.EnsureCreated();
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<RunnerEngineOptions>(_ => new RunnerEngineOptions
        {
            PythonExe = paths.PythonExe,
            RunnerScript = paths.RunnerScript,
            GalleryDlPath = Path.Combine(paths.EngineDir, "site-packages"),
        });
        services.AddSingleton<IDownloadEngine, RunnerEngine>();
        services.AddSingleton<SiteRegistry>(_ => new SiteRegistry([new TwitterSiteProvider()]));

        services.AddDbContext<GalleryDbContext>(o => o.UseSqlite($"Data Source={paths.DbFile}"));
        services.AddDbContextFactory<GalleryDbContext>(o => o.UseSqlite($"Data Source={paths.DbFile}"));

        services.AddScoped<ISettingsStore, GallerySettingsStore>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IUserService, UserService>();
        services.AddSingleton<StatsAggregator>();
        services.AddSingleton<IDownloadQueueService, DownloadQueueService>();
        return services;
    }
}
```

（Task 10/11 的 `StatsAggregator`/`DownloadQueueService` 为 singleton，二者只用 `IDbContextFactory<GalleryDbContext>` 访问数据库，不捕获 scoped 上下文。）

- [ ] **Step 5: 运行确认通过**

Run: `dotnet test src/GalleryGUI.sln --filter "AccountServiceTests|UserServiceTests"`
Expected: PASS（8 个测试）

- [ ] **Step 6: Commit**

```bash
git add src/GalleryGUI.Core src/GalleryGUI.Tests
git commit -m "feat(core): AccountService/UserService（cookie 导入验证、关注导入、用户增删）+ DI 装配"
```

---

### Task 10: DownloadQueueService（队列、取消、事件落库、启动恢复）

**Files:**
- Create: `src/GalleryGUI.Core/Services/DownloadQueueService.cs`
- Test: `src/GalleryGUI.Tests/Services/DownloadQueueServiceTests.cs`

**Interfaces:**
- Consumes: `IDownloadEngine.DownloadAsync`（Task 7）、`SiteRegistry`/`BuildDownload`（Task 8）、`AccountService.AbsoluteCookiePath`（Task 9）、`IDbContextFactory<GalleryDbContext>`（Task 9）、`StatsAggregator`（Task 11，virtual 方法，测试用 Fake 覆写）
- Produces（Phase B UI 直接消费）：

```csharp
namespace GalleryGUI.Services;

public sealed record JobSnapshot(
    long JobId, string SiteId, TargetKind Kind, string Title, JobStatus Status,
    long Done, long Skipped, long Failed, long Total, string? CurrentFile, string? Error);

public interface IDownloadQueueService
{
    IReadOnlyList<JobSnapshot> Active { get; }
    event Action<JobSnapshot>? JobChanged;      // 新任务或状态/进度变化（含终态，随后触发 JobRemoved）
    event Action<JobSnapshot>? JobRemoved;      // 任务到达终态并从 Active 移除
    event Action<long, string>? AccountInvalid; // (accountId, reason)
    int Concurrency { get; set; }               // 默认 1
    Task<long> EnqueueUserMediaAsync(Account account, IReadOnlyList<User> users,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions,
        CancellationToken ct = default);        // 每用户一个 DownloadJob 行，返回首个 jobId
    Task<long> EnqueueAccountContentAsync(Account account, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions,
        CancellationToken ct = default);        // likes/bookmarks：单任务，UserId 为空
    Task CancelAsync(long jobId, CancellationToken ct = default);
    Task RecoverOnStartupAsync(CancellationToken ct = default); // 遗留 Running/Pending → Failed("应用异常退出")
}
```

语义要点（实现必须遵守）：
- 入队即写库（Pending）并立即出现在 `Active`；
- 工作循环按 `Concurrency` 取任务：置 Running → `engine.DownloadAsync` → 事件实时更新 `download_jobs` 计数并插入 `download_files` → `job-done` 后置 Completed、调 `StatsAggregator.ApplyJobCompletionAsync` → `JobChanged` + `JobRemoved`；
- `file-done`/`file-skip` 各插入一条 `DownloadFile`（Status=Downloaded/Skipped，SourceItemId 取 `item_id`）；`log` 事件 `level=error` 累加 FailedFiles；
- `job-done` 事件的 `total/skipped/failed` 为最终权威计数；
- 取消：按 jobId 管理 `CancellationTokenSource`；取消后任务置 Canceled，已落库文件照常计入统计（对 Canceled 同样调用统计聚合）；
- `AuthException` → 任务 Failed（错误文案"登录态失效，请重新导入 Cookie"）+ 账号置 Invalid + 触发 `AccountInvalid`；
- 其它 `EngineException` → 任务 Failed（含异常消息）；
- cookie 绝对路径入队时解析：`AccountService.AbsoluteCookiePath(paths, account)`，传给 `BuildDownload`（`DownloadPaths.CookiesFile`）与 `DownloadAsync`（`cookiesFile` 参数）；archive 路径每账号：`paths.ArchiveDir/<accountId>.txt`。

- [ ] **Step 1: 写失败测试**

`src/GalleryGUI.Tests/Services/DownloadQueueServiceTests.cs`：

```csharp
using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalleryGUI.Tests.Services;

public class DownloadQueueServiceTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly AppPaths _paths;
    private readonly FakeEngine _engine = new();
    private readonly FakeStats _stats = new();
    private readonly GalleryDbContext _db;
    private readonly Account _account;
    private readonly User _alice;
    private readonly DownloadQueueService _queue;

    public DownloadQueueServiceTests()
    {
        _t = TestDb.Create();
        _db = _t.Item2;
        _paths = TestPaths.Create();
        _account = new Account
        { SiteId = "twitter", CookiePath = "twitter\\c\\cookies.txt", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _alice = new User
        { SiteId = "twitter", RestId = "1", ScreenName = "alice", Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Accounts.Add(_account);
        _db.Users.Add(_alice);
        _db.SaveChanges();
        _queue = new DownloadQueueService(_db, _engine, _stats, _paths,
            new SiteRegistry([new TwitterSiteProvider()]),
            NullLogger<DownloadQueueService>.Instance);
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private static IReadOnlyDictionary<string, object?> Opts() => new Dictionary<string, object?> { ["videos"] = true };

    [Fact]
    public async Task Enqueue_user_runs_job_and_records_files()
    {
        _engine.OnDownload = (plan, cookies, progress, _) =>
        {
            progress.Report(new EngineEvent("url-start", Url: plan.Urls[0]));
            progress.Report(new EngineEvent("file-start", Path: "D:/x/1_a_1.jpg", ItemId: "1"));
            progress.Report(new EngineEvent("file-done", Path: "D:/x/1_a_1.jpg", Size: 100));
            progress.Report(new EngineEvent("file-skip", Path: "D:/x/2_b_1.jpg"));
            progress.Report(new EngineEvent("job-done", Total: 2, Skipped: 1, Failed: 0));
            return Task.CompletedTask;
        };

        var jobId = await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _queue.Active.Count == 0);

        var job = await _db.Jobs.Include(j => j.Files).SingleAsync();
        Assert.Equal(jobId, job.Id);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.DoneFiles);
        Assert.Equal(1, job.SkippedFiles);
        Assert.Equal(2, job.Files.Count);
        Assert.Contains(job.Files, f => f.Status == FileStatus.Downloaded && f.SourceItemId == "1");
        var (plan, cookies, _) = _engine.Downloads.Single();
        Assert.Equal(["https://x.com/alice/media"], plan.Urls);
        Assert.Equal(@"D:\dl", plan.BaseDirectory);
        Assert.EndsWith("cookies.txt", cookies);
        Assert.Equal([jobId], _stats.AppliedJobIds);
    }

    [Fact]
    public async Task Cancel_moves_job_to_canceled()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (plan, cookies, progress, ct) =>
        {
            progress.Report(new EngineEvent("url-start", Url: plan.Urls[0]));
            progress.Report(new EngineEvent("file-start", Path: "D:/x/1_a_1.jpg"));
            await gate.Task; // 挂起模拟长时间下载
            ct.ThrowIfCancellationRequested();
        };
        var jobId = await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _db.Jobs.AsNoTracking().Single().Status == JobStatus.Running);
        await _queue.CancelAsync(jobId);
        await WaitUntil(() => _queue.Active.Count == 0);

        Assert.Equal(JobStatus.Canceled, (await _db.Jobs.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Auth_failure_fails_job_and_flags_account()
    {
        long? flagged = null;
        _queue.AccountInvalid += (id, _) => flagged = id;
        _engine.OnDownload = (_, _, _, _) => throw new AuthException("cookie 无效");

        await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _queue.Active.Count == 0);

        var job = await _db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("登录态", job.ErrorMessage);
        Assert.Equal(_account.Id, flagged);
        Assert.Equal(AccountStatus.Invalid, (await _db.Accounts.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task RecoverOnStartup_marks_stale_jobs_failed()
    {
        _db.Jobs.Add(new DownloadJob
        { AccountId = _account.Id, TargetKind = TargetKind.UserMedia, UserId = _alice.Id, Status = JobStatus.Running, CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        await _queue.RecoverOnStartupAsync();
        Assert.All(_db.Jobs.AsNoTracking().ToList(), j => Assert.Equal(JobStatus.Failed, j.Status));
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 5000)
    {
        var start = Environment.TickCount;
        while (!cond())
        {
            if (Environment.TickCount - start > timeoutMs) throw new TimeoutException("条件等待超时");
            await Task.Delay(20);
        }
    }
}
```

注意：`DownloadQueueService` 构造参数为 `IDbContextFactory<GalleryDbContext>`（见 Step 3），测试里传 `new SingleDbContextFactory(_db)`（Task 11 引入的测试辅助，本任务先在 Helpers.cs 加入同一实现）：

```csharp
// 加入 Helpers.cs
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Tests;

public sealed class SingleDbContextFactory(GalleryDbContext db) : IDbContextFactory<GalleryDbContext>
{
    public GalleryDbContext CreateDbContext() => db;
    public Task<GalleryDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(db);
}

public sealed class FakeStats : GalleryGUI.Services.StatsAggregator
{
    public List<long> AppliedJobIds { get; } = [];
    public FakeStats() : base(null!) { }
    public override Task ApplyJobCompletionAsync(long jobId, CancellationToken ct = default)
    { AppliedJobIds.Add(jobId); return Task.CompletedTask; }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test src/GalleryGUI.sln --filter DownloadQueueServiceTests`
Expected: FAIL（编译错误）

- [ ] **Step 3: 实现**

`src/GalleryGUI.Core/Services/DownloadQueueService.cs`（完整实现，逻辑要点全给出）：

```csharp
using System.Collections.Concurrent;
using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalleryGUI.Services;

public sealed record JobSnapshot(
    long JobId, string SiteId, TargetKind Kind, string Title, JobStatus Status,
    long Done, long Skipped, long Failed, long Total, string? CurrentFile, string? Error);

public interface IDownloadQueueService
{
    IReadOnlyList<JobSnapshot> Active { get; }
    event Action<JobSnapshot>? JobChanged;
    event Action<JobSnapshot>? JobRemoved;
    event Action<long, string>? AccountInvalid;
    int Concurrency { get; set; }
    Task<long> EnqueueUserMediaAsync(Account account, IReadOnlyList<User> users,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default);
    Task<long> EnqueueAccountContentAsync(Account account, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default);
    Task CancelAsync(long jobId, CancellationToken ct = default);
    Task RecoverOnStartupAsync(CancellationToken ct = default);
}

public sealed class DownloadQueueService(
    IDbContextFactory<GalleryDbContext> factory,
    IDownloadEngine engine,
    StatsAggregator stats,
    IAppPaths paths,
    SiteRegistry sites,
    ILogger<DownloadQueueService> log) : IDownloadQueueService
{
    private sealed record WorkItem(
        long JobId, long AccountId, string SiteId, TargetKind Kind, long? UserId,
        string TargetScreenName, string Title, string BaseDirectory,
        IReadOnlyDictionary<string, object?> SiteOptions, string CookieAbsolutePath,
        string ArchiveFile);

    private readonly object _gate = new();
    private readonly Dictionary<long, JobSnapshot> _active = [];
    private readonly Dictionary<long, CancellationTokenSource> _cancels = [];
    private readonly ConcurrentQueue<WorkItem> _pending = [];
    private int _runningWorkers;
    private int _concurrency = 1;

    public int Concurrency { get => _concurrency; set => _concurrency = Math.Max(1, value); }

    public IReadOnlyList<JobSnapshot> Active { get { lock (_gate) return [.. _active.Values]; } }
    public event Action<JobSnapshot>? JobChanged;
    public event Action<JobSnapshot>? JobRemoved;
    public event Action<long, string>? AccountInvalid;

    public async Task<long> EnqueueUserMediaAsync(Account account, IReadOnlyList<User> users,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
    {
        if (users.Count == 0) throw new ArgumentException("至少选择一个用户", nameof(users));
        long first = 0;
        var cookieAbs = AccountService.AbsoluteCookiePath(paths, account);
        var archive = Path.Combine(paths.ArchiveDir, account.Id + ".txt");
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var user in users)
        {
            var job = new DownloadJob
            {
                AccountId = account.Id, TargetKind = TargetKind.UserMedia, UserId = user.Id,
                Status = JobStatus.Pending, CreatedAt = DateTime.UtcNow,
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            first = job.Id;
            var title = string.IsNullOrEmpty(user.DisplayName) ? user.ScreenName : user.DisplayName!;
            AddActive(job.Id, account.SiteId, TargetKind.UserMedia, title, JobStatus.Pending);
            _pending.Enqueue(new WorkItem(job.Id, account.Id, account.SiteId, TargetKind.UserMedia,
                user.Id, user.ScreenName, title, baseDirectory, siteOptions, cookieAbs, archive));
        }
        EnsureWorkers();
        return first;
    }

    public async Task<long> EnqueueAccountContentAsync(Account account, ContentKind kind,
        string baseDirectory, IReadOnlyDictionary<string, object?> siteOptions, CancellationToken ct = default)
    {
        if (kind is not (ContentKind.AccountLikes or ContentKind.AccountBookmarks))
            throw new ArgumentException("仅接受 AccountLikes/AccountBookmarks", nameof(kind));
        var cookieAbs = AccountService.AbsoluteCookiePath(paths, account);
        var archive = Path.Combine(paths.ArchiveDir, account.Id + ".txt");
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = new DownloadJob
        {
            AccountId = account.Id, TargetKind = kind, UserId = null,
            Status = JobStatus.Pending, CreatedAt = DateTime.UtcNow,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        var title = kind == ContentKind.AccountLikes ? $"喜欢（@{account.ScreenName}）" : $"书签（@{account.ScreenName}）";
        AddActive(job.Id, account.SiteId, kind, title, JobStatus.Pending);
        _pending.Enqueue(new WorkItem(job.Id, account.Id, account.SiteId, kind,
            null, account.ScreenName ?? "me", title, baseDirectory, siteOptions, cookieAbs, archive));
        EnsureWorkers();
        return job.Id;
    }

    public Task CancelAsync(long jobId, CancellationToken ct = default)
    {
        lock (_gate) { if (_cancels.TryGetValue(jobId, out var cts)) cts.Cancel(); }
        return Task.CompletedTask;
    }

    public async Task RecoverOnStartupAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Jobs.Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.ErrorMessage, "应用异常退出，任务中断"), ct);
    }

    private void EnsureWorkers()
    {
        lock (_gate)
        {
            while (_runningWorkers < _concurrency && !_pending.IsEmpty)
            {
                _runningWorkers++;
                _ = Task.Run(RunWorkerLoopAsync);
            }
        }
    }

    private async Task RunWorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                WorkItem item;
                lock (_gate)
                {
                    if (!_pending.TryDequeue(out item!)) break;
                }
                try { await RunJobAsync(item); }
                catch (Exception ex) { log.LogError(ex, "任务 {JobId} 工作循环异常", item.JobId); }
            }
        }
        finally
        {
            lock (_gate) _runningWorkers--;
            if (!_pending.IsEmpty) EnsureWorkers();
        }
    }

    private async Task RunJobAsync(WorkItem item)
    {
        var cts = new CancellationTokenSource();
        lock (_gate) _cancels[item.JobId] = cts;

        await using (var db = await factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == item.JobId);
            job.Status = JobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        UpdateSnapshot(item.JobId, status: JobStatus.Running);

        var provider = sites.Get(item.SiteId);
        var target = item.Kind switch
        {
            TargetKind.UserMedia => new UserTarget(item.UserId, item.TargetScreenName, item.BaseDirectory),
            _ => new UserTarget(null, item.TargetScreenName, item.BaseDirectory),
        };
        var plan = provider.BuildDownload(item.Kind, target, item.SiteOptions,
            new DownloadPaths(item.CookieAbsolutePath, item.ArchiveFile));

        long done = 0, skipped = 0, failed = 0;
        long totalFinal = 0, skippedFinal = 0, failedFinal = 0;
        var hasFinal = false;

        try
        {
            var progress = new DelegateProgress<EngineEvent>(ev =>
            {
                switch (ev.Event)
                {
                    case "file-start":
                        UpdateSnapshot(item.JobId, currentFile: ev.Path);
                        break;
                    case "file-done":
                        done++;
                        _ = InsertFileAsync(item.JobId, item.UserId, ev, FileStatus.Downloaded);
                        UpdateSnapshot(item.JobId, done: done);
                        break;
                    case "file-skip":
                        skipped++;
                        _ = InsertFileAsync(item.JobId, item.UserId, ev, FileStatus.Skipped);
                        UpdateSnapshot(item.JobId, skipped: skipped);
                        break;
                    case "log" when ev.Level == "error":
                        failed++;
                        UpdateSnapshot(item.JobId, failed: failed);
                        break;
                    case "job-done":
                        hasFinal = true;
                        totalFinal = ev.Total ?? done + skipped + failed;
                        skippedFinal = ev.Skipped ?? skipped;
                        failedFinal = ev.Failed ?? failed;
                        break;
                }
            });

            await engine.DownloadAsync(plan, item.CookieAbsolutePath, progress, cts.Token);
            await FinishJobAsync(item, JobStatus.Completed, null, hasFinal,
                totalFinal, skippedFinal, failedFinal, done, skipped, failed);
        }
        catch (AuthException e)
        {
            await FinishJobAsync(item, JobStatus.Failed, "登录态失效，请重新导入 Cookie", hasFinal,
                totalFinal, skippedFinal, failedFinal, done, skipped, failed);
            await using var db = await factory.CreateDbContextAsync();
            await db.Accounts.Where(a => a.Id == item.AccountId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AccountStatus.Invalid));
            AccountInvalid?.Invoke(item.AccountId, e.Message);
        }
        catch (OperationCanceledException)
        {
            await FinishJobAsync(item, JobStatus.Canceled, null, hasFinal,
                totalFinal, skippedFinal, failedFinal, done, skipped, failed);
        }
        catch (EngineException e)
        {
            await FinishJobAsync(item, JobStatus.Failed, e.Message, hasFinal,
                totalFinal, skippedFinal, failedFinal, done, skipped, failed);
        }
        finally
        {
            lock (_gate) _cancels.Remove(item.JobId, out _);
        }
    }

    private async Task InsertFileAsync(long jobId, long? userId, EngineEvent ev, FileStatus status)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            db.Files.Add(new DownloadFile
            {
                JobId = jobId, UserId = userId, SourceItemId = ev.ItemId,
                Url = ev.Url ?? "", FilePath = ev.Path ?? "",
                FileSize = ev.Size, Status = status, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { log.LogWarning(ex, "文件记录落库失败"); }
    }

    private async Task FinishJobAsync(WorkItem item, JobStatus status, string? error,
        bool hasFinal, long totalFinal, long skippedFinal, long failedFinal,
        long doneLive, long skippedLive, long failedLive)
    {
        var done = hasFinal ? Math.Max(0, totalFinal - skippedFinal - failedFinal) : doneLive;
        var skipped = hasFinal ? skippedFinal : skippedLive;
        var failed = hasFinal ? failedFinal : failedLive;

        await using var db = await factory.CreateDbContextAsync();
        var job = await db.Jobs.SingleAsync(j => j.Id == item.JobId);
        job.Status = status;
        job.DoneFiles = done;
        job.SkippedFiles = skipped;
        job.FailedFiles = failed;
        job.TotalFiles = done + skipped + failed;
        job.ErrorMessage = error;
        job.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        if (status is JobStatus.Completed or JobStatus.Canceled)
            await stats.ApplyJobCompletionAsync(item.JobId);

        var snapshot = UpdateSnapshot(item.JobId, status: status, error: error,
            done: done, skipped: skipped, failed: failed, total: job.TotalFiles);
        lock (_gate) _active.Remove(item.JobId);
        JobChanged?.Invoke(snapshot);
        JobRemoved?.Invoke(snapshot);
    }

    private JobSnapshot UpdateSnapshot(long jobId, JobStatus? status = null, string? currentFile = null,
        string? error = null, long? done = null, long? skipped = null, long? failed = null, long? total = null)
    {
        JobSnapshot snapshot;
        lock (_gate)
        {
            var existing = _active[jobId];
            existing = existing with
            {
                Status = status ?? existing.Status,
                CurrentFile = currentFile ?? existing.CurrentFile,
                Error = error ?? existing.Error,
                Done = done ?? existing.Done,
                Skipped = skipped ?? existing.Skipped,
                Failed = failed ?? existing.Failed,
                Total = total ?? existing.Total,
            };
            _active[jobId] = existing;
            snapshot = existing;
        }
        JobChanged?.Invoke(snapshot);
        return snapshot;
    }

    private void AddActive(long jobId, string siteId, TargetKind kind, string title, JobStatus status)
    {
        JobSnapshot snapshot = new(jobId, siteId, kind, title, status, 0, 0, 0, 0, null, null);
        lock (_gate) _active[jobId] = snapshot;
        JobChanged?.Invoke(snapshot);
    }

    private sealed class DelegateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test src/GalleryGUI.sln --filter DownloadQueueServiceTests`
Expected: PASS（4 个测试）

- [ ] **Step 5: Commit**

```bash
git add src/GalleryGUI.Core/Services/DownloadQueueService.cs src/GalleryGUI.Tests
git commit -m "feat(core): 下载队列服务（调度/取消/事件落库/认证失效联动/启动恢复）"
```

---

### Task 11: StatsAggregator（统计聚合）

**Files:**
- Create: `src/GalleryGUI.Core/Services/StatsAggregator.cs`
- Test: `src/GalleryGUI.Tests/Services/StatsAndRecoveryTests.cs`

**Interfaces:**
- Consumes: `IDbContextFactory<GalleryDbContext>`（Task 3/9）
- Produces（Task 10 已调用）：

```csharp
namespace GalleryGUI.Services;

public class StatsAggregator
{
    public StatsAggregator(IDbContextFactory<GalleryDbContext> factory);
    public virtual Task ApplyJobCompletionAsync(long jobId, CancellationToken ct = default);
}
```

口径（规格 §4.2）：`DownloadCount` += 该任务中 `UserId != null && Status == Downloaded` 的文件数；`LastDownloadAt` 更新为完成时间；likes/bookmarks 文件（UserId 为空）不更新任何用户。

- [ ] **Step 1: 写失败测试**

`src/GalleryGUI.Tests/Services/StatsAndRecoveryTests.cs`：

```csharp
using GalleryGUI.Data;
using GalleryGUI.Services;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Tests.Services;

public class StatsAndRecoveryTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly GalleryDbContext _db;
    private readonly User _alice;
    private readonly DownloadJob _job;

    public StatsAndRecoveryTests()
    {
        _t = TestDb.Create();
        _db = _t.Item2;
        var account = new Account
        { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _alice = new User
        { SiteId = "twitter", RestId = "1", ScreenName = "alice", AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Accounts.Add(account);
        _db.Users.Add(_alice);
        _db.SaveChanges();
        _job = new DownloadJob
        { AccountId = account.Id, TargetKind = TargetKind.UserMedia, UserId = _alice.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
        _db.Jobs.Add(_job);
        _db.Files.AddRange(
            F(FileStatus.Downloaded, _alice.Id, "1"), F(FileStatus.Downloaded, _alice.Id, "2"),
            F(FileStatus.Downloaded, _alice.Id, "3"), F(FileStatus.Skipped, _alice.Id, "4"),
            F(FileStatus.Downloaded, null, "L1"));  // likes 文件：UserId 为空，不计入
        _db.SaveChanges();
    }
    public void Dispose() => _t.Item1.Dispose();

    private DownloadFile F(FileStatus status, long? userId, string itemId) => new()
    { JobId = _job.Id, UserId = userId, SourceItemId = itemId, Url = "u", FilePath = "p", Status = status, CreatedAt = DateTime.UtcNow };

    [Fact]
    public async Task Apply_counts_only_downloaded_user_files()
    {
        var stats = new StatsAggregator(new SingleDbContextFactory(_db));

        await stats.ApplyJobCompletionAsync(_job.Id);

        var user = await _db.Users.SingleAsync(u => u.Id == _alice.Id);
        Assert.Equal(3, user.DownloadCount);
        Assert.NotNull(user.LastDownloadAt);
    }

    [Fact]
    public async Task Apply_is_incremental_across_jobs()
    {
        var stats = new StatsAggregator(new SingleDbContextFactory(_db));
        await stats.ApplyJobCompletionAsync(_job.Id);

        var job2 = new DownloadJob
        { AccountId = _job.AccountId, TargetKind = TargetKind.UserMedia, UserId = _alice.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
        _db.Jobs.Add(job2);
        _db.Files.Add(new DownloadFile
        { JobId = job2.Id, UserId = _alice.Id, SourceItemId = "9", Url = "u", FilePath = "p", Status = FileStatus.Downloaded, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        await stats.ApplyJobCompletionAsync(job2.Id);
        Assert.Equal(4, (await _db.Users.SingleAsync(u => u.Id == _alice.Id)).DownloadCount);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test src/GalleryGUI.sln --filter StatsAndRecoveryTests`
Expected: FAIL（编译错误）

- [ ] **Step 3: 实现**

`src/GalleryGUI.Core/Services/StatsAggregator.cs`：

```csharp
using GalleryGUI.Data;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Services;

public class StatsAggregator(IDbContextFactory<GalleryDbContext> factory)
{
    public virtual async Task ApplyJobCompletionAsync(long jobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var counts = await db.Files.AsNoTracking()
            .Where(f => f.JobId == jobId && f.UserId != null && f.Status == FileStatus.Downloaded)
            .GroupBy(f => f.UserId!.Value)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var c in counts)
        {
            await db.Users.Where(u => u.Id == c.UserId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.DownloadCount, u => u.DownloadCount + c.Count)
                    .SetProperty(u => u.LastDownloadAt, now)
                    .SetProperty(u => u.UpdatedAt, now), ct);
        }
        await tx.CommitAsync(ct);
    }
}
```

（Task 10 测试中 `FakeStats : base(null!)` 的空注入在覆写路径下不会触碰 factory，安全；若启用可空引用类型警告，可在 FakeStats 构造上加 `#pragma` 或改为主构造穿透。）

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test src/GalleryGUI.sln`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add src/GalleryGUI.Core/Services/StatsAggregator.cs src/GalleryGUI.Tests
git commit -m "feat(core): StatsAggregator 下载统计事务聚合"
```

---

### Task 12: 端到端冒烟测试（本地 HTTP + gallery-dl generic）

**Files:**
- Test: `src/GalleryGUI.Tests/E2E/EngineSmokeTests.cs`

**Interfaces:**
- Consumes: `RunnerEngine`（Task 7）、真实 gallery-dl（`PYTHONPATH` 指向仓库 `gallery-dl\`）、真实 Python
- Produces: 全链路（C# → runner → gallery-dl → 事件流 → 文件落盘）验证；标记 `Trait("Category", "E2E")`，环境缺失自动跳过

- [ ] **Step 1: 写测试**

`src/GalleryGUI.Tests/E2E/EngineSmokeTests.cs`：

```csharp
using System.Diagnostics;
using System.Net;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalleryGUI.Tests.E2E;

[Trait("Category", "E2E")]
public class EngineSmokeTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private static string? LocatePython()
    {
        foreach (var exe in new[] { "python", "python3", "py" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--version")
                { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
                p.WaitForExit(5000);
                if (p is { HasExited: true, ExitCode: 0 }) return exe;
            }
            catch { }
        }
        return null;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "gallery-dl", "setup.py")))
            dir = dir.Parent!;
        return dir?.FullName ?? "";
    }

    [SkippableFact]
    public async Task Engine_downloads_file_via_generic_extractor()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var repo = RepoRoot();
        Skip.If(repo.Length == 0, "未找到仓库内 gallery-dl 源码");

        // 1x1 像素 JPEG（最小合法负载）
        var jpg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0a" +
            "HBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAA" +
            "AAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AVN//2Q==");
        var port = 18000 + Random.Shared.Next(2000);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    ctx.Response.ContentType = "image/jpeg";
                    await ctx.Response.OutputStream.WriteAsync(jpg);
                    ctx.Response.Close();
                }
                catch { break; }
            }
        });

        try
        {
            var engine = new RunnerEngine(_paths,
                new RunnerEngineOptions
                {
                    PythonExe = python!,
                    RunnerScript = Path.Combine(repo, "engine", "runner.py"),
                    GalleryDlPath = Path.Combine(repo, "gallery-dl"),
                },
                NullLogger<RunnerEngine>.Instance);

            // 直接构造 generic 提取器可处理的计划（不经过 TwitterSiteProvider 的 URL 构造）
            var nested = new Dictionary<string, object?>
            {
                ["extractor"] = new Dictionary<string, object?>
                { ["generic"] = new Dictionary<string, object?> { ["enabled"] = true } },
                ["base-directory"] = _paths.Root,
                ["download-archive"] = Path.Combine(_paths.ArchiveDir, "e2e.txt"),
            };
            var plan = new DownloadPlan("twitter", [$"http://127.0.0.1:{port}/file.jpg"], _paths.Root, nested);

            var events = new List<EngineEvent>();
            await engine.DownloadAsync(plan, "unused", new Progress<EngineEvent>(events.Add));

            Assert.Contains(events, e => e.Event == "file-done");
            Assert.Contains(events, e => e.Event == "job-done" && e.Total >= 1);
            var downloaded = Directory.GetFiles(_paths.Root, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}temp{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}logs{Path.DirectorySeparatorChar}"))
                .ToList();
            Assert.NotEmpty(downloaded);
        }
        finally
        {
            listener.Stop();
        }
    }
}
```

- [ ] **Step 2: 本地运行（环境就绪时）**

Run: `dotnet test src/GalleryGUI.sln --filter "Category=E2E"`
Expected: PASS（环境缺失时 SKIP 并注明原因）。runner 的 `download` 命令要求 `--cookies` 必填——本测试传 `unused`，runner 端不校验其存在性即写入 config；gallery-dl generic 提取器不用 cookie，可正常下载。

- [ ] **Step 3: 全量回归**

Run: `dotnet test src/GalleryGUI.sln`
Expected: 全部 PASS

- [ ] **Step 4: Commit**

```bash
git add src/GalleryGUI.Tests/E2E
git commit -m "test(e2e): 引擎全链路冒烟（generic 提取器 + 本地 HTTP）"
```

---

## 计划自审记录

1. **规格覆盖**：§3.2/§3.3（runner 协议与事件）→ Task 6/7；§3.4（job.json）→ Task 7/8；§3.5（cookie 文件复制与状态管理；UI 明文提示属 Phase B）→ Task 9；§3.6（取消/并发/退出码/进程树）→ Task 6/7/10；§4（四张业务表+settings、UNIQUE 约束、WAL、启动恢复、DownloadCount 口径）→ Task 3/10/11；§5（ISiteProvider/OptionSchema/BuildProfileUrl/不做运行时插件）→ Task 8；§7（下载流程与全部错误路径）→ Task 10；§8（单元测试 + 可选 E2E）→ 各任务 TDD 步骤 + Task 12。§6/§9/§10（UI/发布/GPL）归 Phase B/C 计划。
2. **占位符扫描**：无 TBD/TODO；所有代码块均为定稿实现（Task 6 的模块结构已重构为：共享基座 `sites/__init__.py` + CLI `runner.py` + 站点实现 `sites/twitter.py`，无循环导入，代码与文字说明一致）。
3. **类型一致性**：`DownloadPlan(SiteId, Urls, BaseDirectory, Options)` 在 Task 7/8/12 一致；`UserTarget(UserId, ScreenName, BaseDirectory)` 在 Task 8/10 一致；`StatsAggregator.ApplyJobCompletionAsync` 为 `virtual` 且由 `IDbContextFactory` 构造（Task 10/11 一致）；`EngineEvent` 键集与 runner emit 字段对齐（`item_id/rest_id/screen_name/display_name/avatar_url/kind/total/skipped/failed`）；事件名集合含数据命令的 `account/user/end`（Global Constraints 已声明）。
4. **已知风险**：gallery-dl 内部 API（`_transform_user`、`output.select`、`user_following`）已在仓库源码上验证（计划撰写时核实），若上游升级导致形状变化，以 `gallery-dl/` 实际源码为准调整 `engine/sites/twitter.py` 的映射，JSONL 协议层保持不变。

