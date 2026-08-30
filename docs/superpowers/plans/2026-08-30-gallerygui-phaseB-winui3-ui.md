# GalleryGUI Phase B（WinUI3 界面）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Phase A 核心层之上构建完整可操作的 WinUI3 界面——导航壳、四个页面（用户管理/下载/历史/设置）、对话框与 ViewModel 层，交付一个可以真实使用的应用。

**Architecture:** ViewModel 全部在 `GalleryGUI.Core`（可单测），App 项目只有 XAML 薄壳与 DI 装配；ViewModel 经 `IUiDispatcher` 抽象把集合变更投递到 UI 线程；设置经 `IAppSettings`（包装 ISettingsStore）；查询能力以只读查询服务补入 Core（Phase A 只有不落 UI 的写入服务）。DEBUG 构建用仓库源码引擎 + PATH Python（与 E2E 相同），发布形态由 Phase C 打包。

**Tech Stack:** WinUI 3 / Windows App SDK 1.6.x（非打包、self-contained）、.NET 8、CommunityToolkit.Mvvm 8.3.x、CommunityToolkit SettingsControls 8.x、Windows CommunityToolkit DataGrid 7.1.2、Serilog（文件日志）。

**Spec:** `docs/superpowers/specs/2026-08-29-gallerygui-design.md`（§6 UI 设计、§2 架构约束；执行者需同时阅读）

## Global Constraints

- **依赖方向**：App → Core 单向；Core 不引用任何 UI 框架；ViewModel 全在 Core。
- **Phase A 冻结接口**（本计划消费，不得改动签名）：`IAccountService`（ImportCookiesAsync/VerifyAsync/DeleteAsync、`AbsoluteCookiePath`）、`IUserService`（ImportFollowingAsync/AddUserAsync/RemoveAsync/SetPinnedAsync）、`IDownloadQueueService`（Active、JobChanged/JobRemoved/AccountInvalid、Concurrency、EnqueueUserMediaAsync/EnqueueAccountContentAsync、CancelAsync、RecoverOnStartupAsync）、`IAppPaths`、`SiteRegistry`/`ISiteProvider`（OptionsSchema/DefaultOptions/BuildProfileUrl/ParseInput）、`IEngineHello`（`IDownloadEngine.HelloAsync`）、`JobSnapshot`、`User`/`Account` 实体、`ContentKind`、`TargetKind`。
- **宿主装配顺序**：`services.AddLogging(...)` 必须在 `AddGalleryCore` 之前（Phase A 审查裁定：工厂校验需要 ILogger<> 可解析）。
- **下载目录强制非空**：任何入队调用前，若设置值为空则回退默认 `%USERPROFILE%\Downloads\GalleryGUI`（Phase A 裁定：空串会把文件落进 EngineDir）。
- **cookie 导入 UI 必须显示明文提示**："Cookie 将明文保存在本机，等同于浏览器登录态，请勿分享给他人"（规格 §3.5）。
- **并发默认 1**：App 启动时把 `IDownloadQueueService.Concurrency` 设为设置值（默认 1）。
- **界面语言中文**；**裁定**：V1 文案直写 XAML（中文），resw 本地化迁移挪到 Phase C（公开发布前完成），降低本阶段实现面。若错：Phase C 多一次性迁移。
- **裁定**：任务栏进度（规格 §6.2"任务栏同步进度"）延后到 Phase C（需 ITaskbarList3 COM 互操作，独立小模块）。若错：Phase C 补一个独立 helper，无架构影响。
- **DEBUG 引擎覆盖**：`#if DEBUG` 下把 `RunnerEngineOptions` 改为 `PythonExe="python"`、`RunnerScript=<repo>/engine/runner.py`、`GalleryDlPath=<repo>/gallery-dl`（与 E2E 相同），仓库根从输出目录向上探测 `gallery-dl/setup.py`；RELEASE 不做覆盖（引擎播种属 Phase C）。
- **SiteId 固定 "twitter"**（V1 单站点；UI 按 SiteRegistry.All 渲染，不为单站点硬编码绕过抽象）。
- 提交信息约定式；每任务至少一次提交；ViewModel 行为必须带 xUnit 测试（XAML 页面以"构建通过 + 手测清单"验收）。

## Phase A 补入 Core 的缺口（本计划新增）

Phase A 只有写入型服务，UI 需要的查询能力在 Task B2 补入（均为只读查询，不改动 Phase A 接口）：

```csharp
// GalleryGUI.Core/Services/Queries.cs
public sealed record UserFilter(string? SearchText = null, string SortBy = "last_download", bool SortDesc = true);
public sealed record HistoryFilter(long? UserId = null, DateTime? From = null, DateTime? To = null);
public sealed record HistoryRow(long FileId, long? UserId, string? UserScreenName, string? SourceItemId,
    string FilePath, long? FileSize, FileStatus Status, DateTime CreatedAt);

public interface IUserQueryService
{ Task<IReadOnlyList<User>> ListAsync(string siteId, UserFilter filter, CancellationToken ct = default); }
public interface IAccountQueryService
{ Task<Account?> GetActiveAsync(string siteId, CancellationToken ct = default); }
public interface IHistoryQueryService
{
    Task<IReadOnlyList<HistoryRow>> ListAsync(HistoryFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListUsersAsync(string siteId, CancellationToken ct = default); // 筛选下拉
}

// GalleryGUI.Core/Threading/IUiDispatcher.cs
public interface IUiDispatcher { void Post(Action action); }
```

`UserFilter.SortBy` 取值：`"last_download"`（默认）/ `"download_count"` / `"added_at"`；排序含置顶优先（`IsPinned DESC` 恒为第一排序键）。

## 文件结构总览

```
src/GalleryGUI.App/                     # 唯一含 XAML 的项目
├── GalleryGUI.App.csproj / app.manifest
├── App.xaml / App.xaml.cs              # DI 装配、Serilog、启动恢复、DEBUG 引擎覆盖
├── MainWindow.xaml(.cs)                # NavigationView 壳（Mica、主题、页签）
├── Infrastructure/
│   ├── UiDispatcher.cs                 # DispatcherQueue → IUiDispatcher
│   ├── FileDialogService.cs            # FileOpenPicker（HWND 初始化）：选 cookies.txt / 文件夹
│   └── LauncherService.cs              # 打开 URL / explorer /select 打开文件夹
├── ViewModels/                         # （薄壳：页面级组合 Core ViewModel 的绑定适配）
├── Views/
│   ├── UsersPage.xaml(.cs)             # 用户管理（主页）
│   ├── DownloadsPage.xaml(.cs)         # 下载
│   ├── HistoryPage.xaml(.cs)           # 历史
│   ├── SettingsPage.xaml(.cs)          # 设置
│   └── Dialogs/
│       ├── AddUserDialog.xaml(.cs)     # 添加用户（用户名/链接）
│       └── ImportCookieDialog.xaml(.cs)# cookie 导入（含明文提示）
src/GalleryGUI.Core/
├── Services/Queries.cs                 # B2：查询服务实现（EF 只读）
├── Settings/AppSettings.cs             # B2：IAppSettings（下载目录/并发/站点选项）
├── Threading/IUiDispatcher.cs          # B2：UI 线程投递抽象
└── ViewModels/
    ├── ViewModelBase.cs                # B2：共通依赖封装（可空）
    ├── UsersViewModel.cs               # B3
    ├── ImportViewModel.cs              # B4（添加用户/导入 cookie/导入关注 流程）
    ├── DownloadsViewModel.cs           # B5
    ├── HistoryViewModel.cs             # B6
    └── SettingsViewModel.cs            # B7（含 OptionItemViewModel 渲染模型）
src/GalleryGUI.Tests/
├── ViewModels/UsersViewModelTests.cs / DownloadsViewModelTests.cs /
│   HistoryViewModelTests.cs / SettingsViewModelTests.cs / QueriesTests.cs / AppSettingsTests.cs
```

---

### Task B1: App 项目与导航壳

**Files:**
- Create: `src/GalleryGUI.App/`（csproj、app.manifest、App.xaml、App.xaml.cs、MainWindow.xaml/.cs）
- Create: `src/GalleryGUI.App/Views/UsersPage/DownloadsPage/HistoryPage/SettingsPage`（占位：一个 TextBlock）
- Modify: `src/GalleryGUI.sln`（加入 App 项目）

**Interfaces:**
- Consumes: `CoreServices.AddGalleryCore(IServiceCollection, IAppPaths)`（Phase A）、`IAppPaths.CreateDefault()`、`IDownloadQueueService.RecoverOnStartupAsync`
- Produces: 可启动的应用窗口（Mica、深浅色跟随、4 页签导航、DEBUG 引擎覆盖）；`App.Services`（IServiceProvider）供页面解析 ViewModel；`UiDispatcher`/`FileDialogService`/`LauncherService`（B3-B7 消费）

- [ ] **Step 1: 创建 App 项目**

`src/GalleryGUI.App/GalleryGUI.App.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RootNamespace>GalleryGUI.App</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Platforms>x64</Platforms>
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
    <UseWinUI>true</UseWinUI>
    <EnableMsixTooling>true</EnableMsixTooling>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <DefaultLanguage>zh-CN</DefaultLanguage>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.6.240923002" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.1" />
    <PackageReference Include="CommunityToolkit.WinUI.Controls.SettingsControls" Version="8.1.240916" />
    <PackageReference Include="CommunityToolkit.WinUI.UI.Controls.DataGrid" Version="7.1.2" />
    <PackageReference Include="Serilog" Version="4.1.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
    <ProjectReference Include="..\GalleryGUI.Core\GalleryGUI.Core.csproj" />
  </ItemGroup>
</Project>
```

注：包版本若还原失败，改用同主版本最新补丁并在报告记录（如 WindowsAppSDK 1.6.x 更新、SettingsControls 8.x）。

`src/GalleryGUI.App/app.manifest`：

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="GalleryGUI.App"/>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10 / 11 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

命令：`dotnet sln src/GalleryGUI.sln add src/GalleryGUI.App/GalleryGUI.App.csproj`；`dotnet new classlib` 不可用于 WinUI，文件全部手写。

- [ ] **Step 2: App.xaml 与 DI 装配**

`src/GalleryGUI.App/App.xaml`：

```xml
<Application
    x:Class="GalleryGUI.App.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

`src/GalleryGUI.App/App.xaml.cs`：

```csharp
using System.IO;
using GalleryGUI;
using GalleryGUI.Paths;
using GalleryGUI.Engine;
using GalleryGUI.Services;
using GalleryGUI.App.Infrastructure;
using GalleryGUI.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;

namespace GalleryGUI.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true)); // 必须先于 AddGalleryCore
        services.AddGalleryCore(paths);
        services.AddSingleton<IUiDispatcher>(sp =>
            new UiDispatcher(DispatcherQueue.GetForCurrentThread()));
        services.AddSingleton<FileDialogService>();
        services.AddSingleton<LauncherService>();
        Services = services.BuildServiceProvider();

        ApplyDevEngineOverrides();

        // 启动恢复：遗留 Running/Pending → Failed（后台执行，不阻塞首帧）
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IDownloadQueueService>()
                    .RecoverOnStartupAsync();
            }
            catch (Exception ex) { Log.Error(ex, "启动恢复失败"); }
        });

        _window = new MainWindow(Services);
        _window.Activate();
    }

    /// <summary>DEBUG：引擎用仓库源码 + PATH python（与 E2E 相同），免去播种。</summary>
    partial void ApplyDevEngineOverrides();
}

#if DEBUG
public partial class App
{
    partial void ApplyDevEngineOverrides()
    {
        var repo = FindRepoRoot(AppContext.BaseDirectory);
        if (repo is null) return;
        var opts = Services.GetRequiredService<RunnerEngineOptions>();
        opts.PythonExe = "python";
        opts.RunnerScript = Path.Combine(repo, "engine", "runner.py");
        opts.GalleryDlPath = Path.Combine(repo, "gallery-dl");
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "gallery-dl", "setup.py")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
#endif
```

注意：`OnLaunched` 运行在 UI 线程，`DispatcherQueue.GetForCurrentThread()` 此时有效；`RunnerEngineOptions` 是可变单例，引擎在每次调用时读取属性，DEBUG 覆盖立即生效。

- [ ] **Step 3: MainWindow 导航壳**

`src/GalleryGUI.App/MainWindow.xaml`：

```xml
<Window
    x:Class="GalleryGUI.App.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="GalleryGUI">
    <NavigationView x:Name="Nav"
                    IsBackButtonVisible="Collapsed"
                    IsSettingsVisible="False"
                    PaneDisplayMode="Left"
                    OpenPaneLength="200"
                    SelectionChanged="Nav_SelectionChanged">
        <NavigationView.MenuItems>
            <NavigationViewItem Content="用户管理" Tag="users" Icon="&#xE716;"/>
            <NavigationViewItem Content="下载" Tag="downloads" Icon="&#xE896;"/>
            <NavigationViewItem Content="历史" Tag="history" Icon="&#xE81C;"/>
            <NavigationViewItem Content="设置" Tag="settings" Icon="&#xE713;"/>
        </NavigationView.MenuItems>
        <Frame x:Name="ContentFrame"/>
    </NavigationView>
</Window>
```

`src/GalleryGUI.App/MainWindow.xaml.cs`：

```csharp
using GalleryGUI.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GalleryGUI.App;

public sealed partial class MainWindow : Window
{
    private readonly IServiceProvider _services;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ContentFrame.Navigate(typeof(UsersPage), services);
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (string)((NavigationViewItem)args.SelectedItem).Tag;
        var pageType = tag switch
        {
            "users" => typeof(UsersPage),
            "downloads" => typeof(DownloadsPage),
            "history" => typeof(HistoryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(UsersPage),
        };
        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, _services);
    }
}
```

四个占位页面统一模式（以 UsersPage 为例，其余三个替换类名与文案）：

```xml
<Page x:Class="GalleryGUI.App.Views.UsersPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      NavigationCacheMode="Enabled">
    <TextBlock Text="用户管理（建设中）" FontSize="24" Margin="24"/>
</Page>
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace GalleryGUI.App.Views;

public sealed partial class UsersPage : Page
{
    public UsersPage() => InitializeComponent();
}
```

- [ ] **Step 4: 构建并启动验证**

Run: `dotnet build src/GalleryGUI.sln` → 0 错误
Run: `dotnet run --project src/GalleryGUI.App` → 窗口出现：Mica 背景、左侧 4 页签、点各页签内容切换、关闭不崩溃；`%LOCALAPPDATA%\GalleryGUI\logs\` 出现当日日志文件。
（无显示器/CI 环境下以构建通过为准，启动项留给手测清单。）

- [ ] **Step 5: Commit**

```bash
git add src/GalleryGUI.App src/GalleryGUI.sln
git commit -m "feat(app): WinUI3 壳——DI 装配/Serilog/启动恢复/DEBUG 引擎覆盖/导航（Mica）"
```

---

### Task B2: 查询服务、AppSettings 与 UI 线程抽象

**Files:**
- Create: `src/GalleryGUI.Core/Services/Queries.cs`、`src/GalleryGUI.Core/Settings/AppSettings.cs`、`src/GalleryGUI.Core/Threading/IUiDispatcher.cs`
- Modify: `src/GalleryGUI.Core/DependencyInjection.cs`（注册新服务）
- Test: `src/GalleryGUI.Tests/Services/QueriesTests.cs`、`src/GalleryGUI.Tests/Services/AppSettingsTests.cs`

**Interfaces:**
- Consumes: `GalleryDbContext`（Phase A）、`ISettingsStore.GetAsync(key, fallback, ct)`（Phase A 裁定签名）、`ISiteProvider.DefaultOptions`
- Produces（B3-B7 全部消费）：本计划"Phase A 补入 Core 的缺口"节的全部接口，外加：

```csharp
namespace GalleryGUI.Settings;

public interface IAppSettings
{
    Task<string> GetDownloadDirectoryAsync(CancellationToken ct = default);
    Task SetDownloadDirectoryAsync(string directory, CancellationToken ct = default);
    Task<int> GetConcurrencyAsync(CancellationToken ct = default);      // 默认 1
    Task SetConcurrencyAsync(int concurrency, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object?>> GetSiteOptionsAsync(string siteId, CancellationToken ct = default);
    Task SetSiteOptionsAsync(string siteId, IReadOnlyDictionary<string, object?> options, CancellationToken ct = default);
}
```

语义：下载目录设置键 `"download.directory"`，缺失/空白回退 `%USERPROFILE%\Downloads\GalleryGUI`；并发键 `"download.concurrency"`，回退 1（值 <1 视为 1）；站点选项键 `"site.<siteId>.options"`，缺失回退 `SiteRegistry.Get(siteId).DefaultOptions`。

`IUiDispatcher`：`void Post(Action action);`（测试用同步实现 `SyncDispatcher` 加入 Tests/Helpers.cs）。

- [ ] **Step 1: 写失败测试**

`src/GalleryGUI.Tests/Services/QueriesTests.cs`：

```csharp
using GalleryGUI.Data;
using GalleryGUI.Services;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Tests.Services;

public class QueriesTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly GalleryDbContext _db;
    private readonly UserQueryService _users;
    private readonly AccountQueryService _accounts;
    private readonly HistoryQueryService _history;

    public QueriesTests()
    {
        _t = TestDb.Create();
        _db = _t.Item2;
        var account = new Account { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        var pinned = NewUser("p1", "alice", isPinned: true, lastDownload: DateTime.UtcNow.AddDays(-1), count: 5);
        var recent = NewUser("u2", "bob", lastDownload: DateTime.UtcNow, count: 1);
        var old = NewUser("u3", "carol", lastDownload: DateTime.UtcNow.AddDays(-30), count: 9);
        _db.Accounts.Add(account);
        _db.Users.AddRange(pinned, recent, old);
        _db.SaveChanges();
        var job = new DownloadJob { AccountId = account.Id, TargetKind = TargetKind.UserMedia, UserId = recent.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
        _db.Jobs.Add(job);
        _db.Files.AddRange(
            new DownloadFile { JobId = job.Id, UserId = recent.Id, SourceItemId = "11", Url = "u", FilePath = @"D:\dl\bob\11_1.jpg", FileSize = 100, Status = FileStatus.Downloaded, CreatedAt = DateTime.UtcNow },
            new DownloadFile { JobId = job.Id, UserId = null, SourceItemId = null, Url = "u", FilePath = @"D:\dl\_likes\1.jpg", FileSize = 200, Status = FileStatus.Downloaded, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        _users = new UserQueryService(new SingleDbContextFactory(_db));
        _accounts = new AccountQueryService(new SingleDbContextFactory(_db));
        _history = new HistoryQueryService(new SingleDbContextFactory(_db));
    }
    public void Dispose() => _t.Item1.Dispose();

    private static User NewUser(string restId, string name, bool isPinned = false, DateTime? lastDownload = null, long count = 0) => new()
    { SiteId = "twitter", RestId = restId, ScreenName = name, Source = UserSource.Following, IsPinned = isPinned, LastDownloadAt = lastDownload, DownloadCount = count, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

    [Fact]
    public async Task List_sorts_pinned_first_then_by_key()
    {
        var byCount = await _users.ListAsync("twitter", new UserFilter(SortBy: "download_count"));
        Assert.Equal(["alice", "carol", "bob"], byCount.Select(u => u.ScreenName)); // 置顶优先，其余按计数降序
        var byLast = await _users.ListAsync("twitter", new UserFilter());
        Assert.Equal(["alice", "bob", "carol"], byLast.Select(u => u.ScreenName));
    }

    [Fact]
    public async Task List_filters_by_search_text()
    {
        var r = await _users.ListAsync("twitter", new UserFilter(SearchText: "ali"));
        Assert.Equal(["alice"], r.Select(u => u.ScreenName)); // 匹配 ScreenName 或 DisplayName
    }

    [Fact]
    public async Task GetActive_returns_active_account_only()
    {
        var acc = await _accounts.GetActiveAsync("twitter");
        Assert.NotNull(acc);
        Assert.Equal(AccountStatus.Ok, acc!.Status);
        Assert.Null(await _accounts.GetActiveAsync("nope"));
    }

    [Fact]
    public async Task History_joins_user_and_filters()
    {
        var all = await _history.ListAsync(new HistoryFilter());
        Assert.Equal(2, all.Count);
        Assert.Equal("bob", all.Single(r => r.SourceItemId == "11").UserScreenName);
        Assert.Null(all.Single(r => r.SourceItemId is null).UserScreenName); // likes 文件无用户

        var byUser = await _history.ListAsync(new HistoryFilter(UserId: (await _db.Users.SingleAsync(u => u.ScreenName == "bob")).Id));
        Assert.Single(byUser);
        Assert.All(byUser, r => Assert.Equal(100, r.FileSize));
    }
}
```

`src/GalleryGUI.Tests/Services/AppSettingsTests.cs`：

```csharp
using GalleryGUI.Services;
using GalleryGUI.Settings;
using GalleryGUI.Sites;

namespace GalleryGUI.Tests.Services;

public class AppSettingsTests : IDisposable
{
    private readonly (SqliteConnection, Data.GalleryDbContext) _t;
    private readonly IAppSettings _settings;

    public AppSettingsTests()
    {
        _t = TestDb.Create();
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        _settings = new AppSettings(new GallerySettingsStore(_t.Item2), sites);
    }
    public void Dispose() => _t.Item1.Dispose();

    [Fact]
    public async Task Download_directory_falls_back_to_default_when_missing_or_blank()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GalleryGUI");
        Assert.Equal(expected, await _settings.GetDownloadDirectoryAsync());
        await _settings.SetDownloadDirectoryAsync("   ");
        Assert.Equal(expected, await _settings.GetDownloadDirectoryAsync());
        await _settings.SetDownloadDirectoryAsync(@"D:\Media");
        Assert.Equal(@"D:\Media", await _settings.GetDownloadDirectoryAsync());
    }

    [Fact]
    public async Task Concurrency_floors_at_one()
    {
        Assert.Equal(1, await _settings.GetConcurrencyAsync());
        await _settings.SetConcurrencyAsync(3);
        Assert.Equal(3, await _settings.GetConcurrencyAsync());
        await _settings.SetConcurrencyAsync(0);
        Assert.Equal(1, await _settings.GetConcurrencyAsync());
    }

    [Fact]
    public async Task Site_options_fall_back_to_provider_defaults()
    {
        var d = await _settings.GetSiteOptionsAsync("twitter");
        Assert.True((bool)d["videos"]!);
        Assert.Equal("{tweet_id}_{author[name]}_{num}.{extension}", d["filename"]);
        await _settings.SetSiteOptionsAsync("twitter", new Dictionary<string, object?> { ["videos"] = false });
        Assert.False((bool)(await _settings.GetSiteOptionsAsync("twitter"))["videos"]!);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test src/GalleryGUI.sln --filter "QueriesTests|AppSettingsTests"`
Expected: FAIL（编译错误，类型不存在）

- [ ] **Step 3: 实现**

`src/GalleryGUI.Core/Threading/IUiDispatcher.cs`：

```csharp
namespace GalleryGUI.Threading;

/// <summary>把委托投递到 UI 线程；桌面宿主用 DispatcherQueue 实现，测试用同步实现。</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
```

`src/GalleryGUI.Core/Services/Queries.cs`：

```csharp
using GalleryGUI.Data;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Services;

public sealed record UserFilter(string? SearchText = null, string SortBy = "last_download", bool SortDesc = true);
public sealed record HistoryFilter(long? UserId = null, DateTime? From = null, DateTime? To = null);
public sealed record HistoryRow(long FileId, long? UserId, string? UserScreenName, string? SourceItemId,
    string FilePath, long? FileSize, FileStatus Status, DateTime CreatedAt);

public interface IUserQueryService
{
    Task<IReadOnlyList<User>> ListAsync(string siteId, UserFilter filter, CancellationToken ct = default);
}

public sealed class UserQueryService(IDbContextFactory<GalleryDbContext> factory) : IUserQueryService
{
    public async Task<IReadOnlyList<User>> ListAsync(string siteId, UserFilter filter, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Users.AsNoTracking().Where(u => u.SiteId == siteId);
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
            query = query.Where(u => u.ScreenName.Contains(filter.SearchText) ||
                                     (u.DisplayName != null && u.DisplayName.Contains(filter.SearchText)));
        query = filter.SortBy switch
        {
            "download_count" => filter.SortDesc
                ? query.OrderByDescending(u => u.DownloadCount)
                : query.OrderBy(u => u.DownloadCount),
            "added_at" => filter.SortDesc
                ? query.OrderByDescending(u => u.AddedAt)
                : query.OrderBy(u => u.AddedAt),
            _ => filter.SortDesc
                ? query.OrderByDescending(u => u.LastDownloadAt)
                : query.OrderBy(u => u.LastDownloadAt),
        };
        // 置顶恒为第一排序键（ThenBy Before/After 语义：先主键排序再叠加）
        return await query
            .OrderByDescending(u => u.IsPinned)
            .OrderBy(u => 0) // 占位：EF Core 链式 OrderBy 会重置——见下方修正
            .ToListAsync(ct);
    }
}
```

⚠ 修正：EF Core 中后一个 `OrderBy` 会**替换**前面的排序。定稿写法——把置顶放进主排序：

```csharp
        var q = query;
        IOrderedQueryable<User> ordered = filter.SortBy switch
        {
            "download_count" => q.OrderByDescending(u => u.IsPinned).ThenByDescending(u => u.DownloadCount),
            "added_at" => q.OrderByDescending(u => u.IsPinned).ThenByDescending(u => u.AddedAt),
            _ => q.OrderByDescending(u => u.IsPinned).ThenByDescending(u => u.LastDownloadAt),
        };
        if (!filter.SortDesc) // SortDesc=false 时次键升序（置顶仍第一）
        {
            ordered = filter.SortBy switch
            {
                "download_count" => q.OrderByDescending(u => u.IsPinned).ThenBy(u => u.DownloadCount),
                "added_at" => q.OrderByDescending(u => u.IsPinned).ThenBy(u => u.AddedAt),
                _ => q.OrderByDescending(u => u.IsPinned).ThenBy(u => u.LastDownloadAt),
            };
        }
        return await ordered.ToListAsync(ct);
```

（文件只保留修正后的版本。`SortDesc` 默认 true。）

```csharp
public interface IAccountQueryService
{
    Task<Account?> GetActiveAsync(string siteId, CancellationToken ct = default);
}

public sealed class AccountQueryService(IDbContextFactory<GalleryDbContext> factory) : IAccountQueryService
{
    public async Task<Account?> GetActiveAsync(string siteId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.SiteId == siteId && a.IsActive, ct);
    }
}

public interface IHistoryQueryService
{
    Task<IReadOnlyList<HistoryRow>> ListAsync(HistoryFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListUsersAsync(string siteId, CancellationToken ct = default);
}

public sealed class HistoryQueryService(IDbContextFactory<GalleryDbContext> factory) : IHistoryQueryService
{
    public async Task<IReadOnlyList<HistoryRow>> ListAsync(HistoryFilter filter, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = from f in db.Files.AsNoTracking()
                    join j in db.Jobs.AsNoTracking() on f.JobId equals j.Id
                    join u in db.Users.AsNoTracking() on f.UserId equals u.Id into gj
                    from user in gj.DefaultIfEmpty()
                    orderby f.CreatedAt descending
                    select new HistoryRow(
                        f.Id, f.UserId, user != null ? user.ScreenName : null, f.SourceItemId,
                        f.FilePath, f.FileSize, f.Status, f.CreatedAt);
        if (filter.UserId is { } uid) query = query.Where(r => r.UserId == uid);
        if (filter.From is { } from) query = query.Where(r => r.CreatedAt >= from);
        if (filter.To is { } to) query = query.Where(r => r.CreatedAt < to);
        return await query.Take(2000).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<User>> ListUsersAsync(string siteId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.SiteId == siteId)
            .OrderBy(u => u.ScreenName).ToListAsync(ct);
    }
}
```

（`HistoryRow` 过滤在投影后用 `IQueryable<HistoryRow>` 继续组合——EF Core 支持。若编译报转换错误，把过滤条件改为投影前对 `f`/`j` 施加（UserId 过滤作用于 `f.UserId`，日期作用于 `f.CreatedAt`），语义等价，以编译通过为准。）

`src/GalleryGUI.Core/Settings/AppSettings.cs`：

```csharp
using GalleryGUI.Services;
using GalleryGUI.Sites;

namespace GalleryGUI.Settings;

public interface IAppSettings
{
    Task<string> GetDownloadDirectoryAsync(CancellationToken ct = default);
    Task SetDownloadDirectoryAsync(string directory, CancellationToken ct = default);
    Task<int> GetConcurrencyAsync(CancellationToken ct = default);
    Task SetConcurrencyAsync(int concurrency, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object?>> GetSiteOptionsAsync(string siteId, CancellationToken ct = default);
    Task SetSiteOptionsAsync(string siteId, IReadOnlyDictionary<string, object?> options, CancellationToken ct = default);
}

public sealed class AppSettings(ISettingsStore store, SiteRegistry sites) : IAppSettings
{
    public static string DefaultDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GalleryGUI");

    public async Task<string> GetDownloadDirectoryAsync(CancellationToken ct = default)
    {
        var v = await store.GetAsync<string>("download.directory", null, ct);
        return string.IsNullOrWhiteSpace(v) ? DefaultDownloadDirectory : v;
    }

    public Task SetDownloadDirectoryAsync(string directory, CancellationToken ct = default) =>
        store.SetAsync("download.directory", directory, ct);

    public async Task<int> GetConcurrencyAsync(CancellationToken ct = default)
    {
        var v = await store.GetAsync("download.concurrency", 1, ct);
        return Math.Max(1, v);
    }

    public Task SetConcurrencyAsync(int concurrency, CancellationToken ct = default) =>
        store.SetAsync("download.concurrency", Math.Max(1, concurrency), ct);

    public async Task<IReadOnlyDictionary<string, object?>> GetSiteOptionsAsync(string siteId, CancellationToken ct = default)
    {
        var saved = await store.GetAsync<Dictionary<string, object?>>($"site.{siteId}.options", null, ct);
        if (saved is { Count: > 0 }) return saved;
        return sites.Get(siteId).DefaultOptions;
    }

    public Task SetSiteOptionsAsync(string siteId, IReadOnlyDictionary<string, object?> options, CancellationToken ct = default) =>
        store.SetAsync($"site.{siteId}.options", options, ct);
}
```

- [ ] **Step 4: 注册 DI 并确认通过**

`DependencyInjection.cs` 在 `AddGalleryCore` 内追加：

```csharp
        services.AddSingleton<IAppSettings, AppSettings>();
        services.AddSingleton<IUserQueryService, UserQueryService>();
        services.AddSingleton<IAccountQueryService, AccountQueryService>();
        services.AddSingleton<IHistoryQueryService, HistoryQueryService>();
```

（查询服务与 AppSettings 均只用 `IDbContextFactory`，singleton 安全。）

Run: `dotnet test src/GalleryGUI.sln`
Expected: 全部 PASS（61 + 新增 7）

- [ ] **Step 5: Commit**

```bash
git add src/GalleryGUI.Core src/GalleryGUI.Tests
git commit -m "feat(core): 查询服务/UserFilter/IAppSettings/IUiDispatcher（UI 层数据源）"
```

---

### Task B3: 用户管理页（列表/多选/排序/操作条）

**Files:**
- Create: `src/GalleryGUI.Core/ViewModels/UsersViewModel.cs`
- Modify: `src/GalleryGUI.App/Views/UsersPage.xaml(.cs)`（占位 → 完整页面）
- Test: `src/GalleryGUI.Tests/ViewModels/UsersViewModelTests.cs`（新增 Tests/Helpers.cs 的 `SyncDispatcher`）

**Interfaces:**
- Consumes: B2 的 `IUserQueryService/IAccountQueryService/IAppSettings/IUiDispatcher`、Phase A `IUserService/IDownloadQueueService/SiteRegistry/ContentKind`
- Produces（B4 复用）：

```csharp
namespace GalleryGUI.ViewModels;

public sealed partial class UserRowViewModel : ObservableObject
{
    public User Model { get; }
    public bool IsSelected { get => _isSelected; set { if (SetProperty(ref _isSelected, value)) SelectedChanged?.Invoke(); } }
    public event Action? SelectedChanged;
    public string Title { get; }          // DisplayName 优先，回退 ScreenName
    public string Subtitle { get; }       // @screenName · 来源徽标文字（关注导入/手动添加/链接导入）
    public string DownloadCountText { get; }
    public string LastDownloadText { get; } // 无记录显示 "—"
    public bool IsPinned { get; }
}

public partial class UsersViewModel : ObservableObject
{
    public UsersViewModel(IUserQueryService userQuery, IAccountQueryService accountQuery,
        IUserService users, IDownloadQueueService queue, IAppSettings settings,
        IUiDispatcher dispatcher, SiteRegistry sites);
    public ObservableCollection<UserRowViewModel> Users { get; }
    public int SelectedCount { get; }
    public bool HasUsers { get; }
    public bool HasAccount { get; }       // 驱动空状态引导
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private string? _statusMessage;   // 操作结果反馈（成功/失败一行话）
    public event Action? RequestReload;                     // B4 导入流程完成后通知刷新
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand DownloadSelectedCommand { get; }
    public IAsyncRelayCommand DeleteSelectedCommand { get; }
    public IRelayCommand<UserRowViewModel> TogglePinCommand { get; }
    public IRelayCommand<UserRowViewModel> DownloadOneCommand { get; }
    public IRelayCommand<UserRowViewModel> OpenProfileCommand { get; }
    public void SelectAll(bool selected);
}
```

行为：`SearchText` 变更（300ms 防抖）与排序键变更触发 `RefreshCommand`；`DownloadSelected/DownloadOne` 先 `settings.GetDownloadDirectoryAsync()`，再 `queue.EnqueueUserMediaAsync(account, users, dir, siteOptions)`（**无活动账号时 StatusMessage 提示"请先在设置中导入 Cookie"且不执行**）；siteOptions 取 `settings.GetSiteOptionsAsync("twitter")`；`AccountInvalid` 订阅 → StatusMessage 提示并刷新 HasAccount。所有集合变更经 `IUiDispatcher.Post`。

- [ ] **Step 1: 写失败测试**（含 `SyncDispatcher`——加入 Tests/Helpers.cs：`public sealed class SyncDispatcher : IUiDispatcher { public void Post(Action a) => a(); }`；FakeServices 复用 Phase A 的 FakeEngine，新增薄包装：测试里用真服务 + TestDb/SingleDbContextFactory 组装，FakeEngine 注入）

`src/GalleryGUI.Tests/ViewModels/UsersViewModelTests.cs`：

```csharp
using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Settings;
using GalleryGUI.Sites;
using GalleryGUI.Threading;
using GalleryGUI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalleryGUI.Tests.ViewModels;

public class UsersViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine = new();
    private readonly GalleryDbContext _db;
    private readonly UsersViewModel _vm;
    private readonly Account _account;
    private int _selectionNotifications;

    public UsersViewModelTests()
    {
        _t = TestDb.Create();
        _db = _t.Item2;
        var factory = new SingleDbContextFactory(_db);
        _account = new Account { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _db.Accounts.Add(_account);
        _db.Users.AddRange(
            NewUser("1", "alice", count: 3, last: DateTime.UtcNow.AddDays(-2)),
            NewUser("2", "bob"));
        _db.SaveChanges();
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        var settings = new AppSettings(new GallerySettingsStore(_db), sites);
        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, sites, NullLogger<DownloadQueueService>.Instance);
        var accountSvc = new AccountService(_db, _engine, _paths, sites, NullLogger<AccountService>.Instance);
        var userSvc = new UserService(_db, _engine, _paths, sites, NullLogger<UserService>.Instance);
        _vm = new UsersViewModel(new UserQueryService(factory), new AccountQueryService(factory),
            userSvc, queue, settings, new SyncDispatcher(), sites);
        _vm.Users.CollectionChanged += (_, _) => { };
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private static User NewUser(string restId, string name, long count = 0, DateTime? last = null) => new()
    { SiteId = "twitter", RestId = restId, ScreenName = name, Source = UserSource.Following, DownloadCount = count, LastDownloadAt = last, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

    [Fact]
    public async Task Refresh_loads_users_with_pinned_first_and_binds_fields()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, _vm.Users.Count);
        Assert.True(_vm.HasUsers);
        Assert.True(_vm.HasAccount);
        var row = _vm.Users.First();
        Assert.Equal("alice", row.Model.ScreenName);
        Assert.Equal("@alice · 关注导入", row.Subtitle);
        Assert.Equal("3", row.DownloadCountText);
        Assert.Equal("—", _vm.Users.Last().LastDownloadText); // bob 无记录
    }

    [Fact]
    public async Task SearchText_filters_after_debounce_window()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.SearchText = "ali";
        await Task.Delay(450); // 300ms 防抖 + 余量
        Assert.Single(_vm.Users);
        _vm.SearchText = "";
        await Task.Delay(450);
        Assert.Equal(2, _vm.Users.Count);
    }

    [Fact]
    public async Task DownloadSelected_enqueues_with_settings_directory()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.Users[0].IsSelected = true;
        Assert.Equal(1, _vm.SelectedCount);
        await _vm.DownloadSelectedCommand.ExecuteAsync(null);
        Assert.Contains("已加入下载队列", _vm.StatusMessage);
    }

    [Fact]
    public async Task DownloadSelected_without_account_blocks_with_message()
    {
        await _db.Accounts.ExecuteDeleteAsync();
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.Users[0].IsSelected = true;
        await _vm.DownloadSelectedCommand.ExecuteAsync(null);
        Assert.Contains("请先在设置中导入 Cookie", _vm.StatusMessage);
    }

    [Fact]
    public async Task DeleteSelected_removes_and_refreshes()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.Users[0].IsSelected = true;
        await _vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Single(_vm.Users);
        Assert.False(_db.Users.AsNoTracking().Any(u => u.ScreenName == "alice"));
    }
}
```

- [ ] **Step 2: 运行确认失败** → `dotnet test --filter UsersViewModelTests`，FAIL（类型不存在）

- [ ] **Step 3: 实现 UsersViewModel**

按 Produces 契约实现。要点：
- 防抖：`SearchText` 的 `OnPropertyChanged` 中 `Task.Delay(300)` 后比对当前值未变再刷新（CancellationTokenSource 竞争取消旧等待）。
- `RefreshAsync`：`_users.ListAsync("twitter", filter)` → 重建 Users 集合（保留同 Id 行的选中态）；`HasUsers/HasAccount` 更新。
- `SelectedCount`：行 `SelectedChanged` 事件聚合；`OnPropertyChanged(nameof(SelectedCount))` 经 dispatcher。
- `DownloadSelectedAsync`：选中行 → `Model` 列表 → 账号/目录/siteOptions 解析 → `EnqueueUserMediaAsync` → StatusMessage "已加入下载队列（N 个用户）"；异常（EngineException 等）→ StatusMessage = 错误消息。
- `DeleteSelectedAsync`：先 `ContentDialog` 由页面层确认——Core VM 不持对话框：VM 提供 `DeleteSelectedCommand`，页面层订阅 `ConfirmDeleteRequested` 事件弹确认框后再调 VM 的内部方法。**定稿**：VM 声明 `public event Func<long[], Task>? ConfirmDeleteRequested;`（参数为待删 Id 数组），命令处理器先置 `List<long> pendingDeleteIds` 再触发事件，页面处理器调用 `vm.ConfirmDeleteAsync(true/false)` 完成。简化替代（允许）：V1 删除不做二次确认，直接删 + StatusMessage "已删除 N 个用户"。**执行者按简化替代实现**（删除的是库记录而非文件，风险低），在报告中注明。
- `TogglePin`/`DownloadOne`/`OpenProfile`：分别调 `IUserService.SetPinnedAsync`、复用下载路径、`System.Diagnostics.Process.Start(new ProcessStartInfo(ProfileUrl){UseShellExecute=true})`——**Core 不能用 Process？** net8.0 可以（System.Diagnostics.Process 跨平台可用）。`OpenProfile` 若 ProfileUrl 空则用 `sites.Get("twitter").BuildProfileUrl(ScreenName)`。

- [ ] **Step 4: 实现页面 XAML**

`src/GalleryGUI.App/Views/UsersPage.xaml`（关键结构，绑定到 VM 属性名）：

```xml
<Page x:Class="GalleryGUI.App.Views.UsersPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:controls="using:CommunityToolkit.WinUI.UI.Controls"
      xmlns:vm="using:GalleryGUI.ViewModels"
      NavigationCacheMode="Enabled">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>   <!-- 工具栏 -->
            <RowDefinition Height="*"/>      <!-- 列表或空状态 -->
            <RowDefinition Height="Auto"/>   <!-- 底部操作条 -->
        </Grid.RowDefinitions>
        <StackPanel Orientation="Horizontal" Spacing="8" Padding="12,8">
            <Button Content="添加用户" Command="{x:Bind Vm.ShowAddUserCommand}" Style="{StaticResource AccentButtonStyle}"/>
            <Button Content="导入关注列表" Command="{x:Bind Vm.ShowImportCookieCommand}" Visibility="{x:Bind Vm.ImportButtonVisibility, Mode=OneWay}"/>
            <AutoSuggestBox PlaceholderText="搜索用户名/昵称" QueryIcon="Find"
                            Text="{x:Bind Vm.SearchText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                            Width="240"/>
        </StackPanel>
        <!-- 列表：DataGrid -->
        <controls:DataGrid Grid.Row="1" ItemsSource="{x:Bind Vm.Users, Mode=OneWay}"
                           AutoGenerateColumns="False" CanUserSortColumns="False"
                           IsReadOnly="True" SelectionMode="Single" HeadersVisibility="All" Margin="12,0">
            <controls:DataGrid.Columns>
                <controls:DataGridTemplateColumn Header="" Width="40">
                    <controls:DataGridTemplateColumn.CellTemplate>
                        <DataTemplate><CheckBox IsChecked="{Binding IsSelected, Mode=TwoWay}" HorizontalAlignment="Center"/></DataTemplate>
                    </controls:DataGridTemplateColumn.CellTemplate>
                </controls:DataGridTemplateColumn>
                <controls:DataGridTemplateColumn Header="用户" Width="280">
                    <controls:DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <StackPanel Orientation="Horizontal" Spacing="8">
                                <PersonPicture ProfilePicture="{Binding AvatarSource}" Width="32" Height="32"/>
                                <StackPanel>
                                    <TextBlock Text="{Binding Title}"/>
                                    <TextBlock Text="{Binding Subtitle}" FontSize="12" Opacity="0.7"/>
                                </StackPanel>
                            </StackPanel>
                        </DataTemplate>
                    </controls:DataGridTemplateColumn.CellTemplate>
                </controls:DataGridTemplateColumn>
                <controls:DataGridTextColumn Header="下载次数" Binding="{Binding DownloadCountText}" Width="90"/>
                <controls:DataGridTextColumn Header="末次下载" Binding="{Binding LastDownloadText}" Width="160"/>
                <controls:DataGridTemplateColumn Header="操作" Width="220">
                    <controls:DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <StackPanel Orientation="Horizontal" Spacing="4">
                                <Button Content="下载" Command="{Binding DownloadCommand}" Padding="8,2"/>
                                <Button Content="主页" Command="{Binding OpenProfileCommand}" Padding="8,2"/>
                                <Button Content="{Binding PinButtonText}" Command="{Binding TogglePinCommand}" Padding="8,2"/>
                                <Button Content="删除" Command="{Binding DeleteCommand}" Padding="8,2"/>
                            </StackPanel>
                        </DataTemplate>
                    </controls:DataGridTemplateColumn.CellTemplate>
                </controls:DataGridTemplateColumn>
            </controls:DataGrid.Columns>
        </controls:DataGrid>
        <!-- 空状态（HasUsers=false 时显示，用户行级删除按钮走行命令，页面层弹确认） -->
        <!-- 底部操作条 -->
        <Grid Grid.Row="2" Padding="12,8" Visibility="{x:Bind Vm.BottomBarVisibility, Mode=OneWay}">
            <StackPanel Orientation="Horizontal" Spacing="12">
                <TextBlock VerticalAlignment="Center" Text="{x:Bind Vm.SelectedCountText, Mode=OneWay}"/>
                <Button Content="下载选中" Style="{StaticResource AccentButtonStyle}" Command="{x:Bind Vm.DownloadSelectedCommand}"/>
                <Button Content="删除选中" Command="{x:Bind Vm.DeleteSelectedCommand}"/>
                <TextBlock VerticalAlignment="Center" Opacity="0.8" Text="{x:Bind Vm.StatusMessage, Mode=OneWay}"/>
            </StackPanel>
        </Grid>
    </Grid>
</Page>
```

行级命令（Download/OpenProfile/TogglePin/Delete）落在 `UserRowViewModel` 上（构造时由父 VM 传入回调委托），避免 DataGrid 模板里找不到页面级 DataContext；`AvatarSource` 为 `ImageSource?`（`BitmapImage(AvatarUrl)`，URL 空则 null）。页面的 code-behind 仅构造 VM（`App.Services` 解析）并赋 `Vm` 属性 + 处理 `ShowAddUser/ShowImportCookie` 两个事件转对话框（B4 实现对话框前先留空处理或直接调 VM 内部占位命令——B4 接线）。**绑定契约**：x:Bind 到的每个 VM 属性/命令名必须存在（含 `ImportButtonVisibility`（Visibility）、`BottomBarVisibility`、`SelectedCountText`（"已选 N 个"））。

- [ ] **Step 5: 运行确认通过** → `dotnet test src/GalleryGUI.sln` 全绿；`dotnet build src/GalleryGUI.sln` 0 错误
- [ ] **Step 6: Commit** — `git commit -m "feat(ui): 用户管理页（列表/多选/搜索防抖/操作条/行内命令）"`

---

### Task B4: 导入流程（添加用户、cookie 导入、关注导入、空状态引导）

**Files:**
- Create: `src/GalleryGUI.Core/ViewModels/ImportViewModel.cs`
- Create: `src/GalleryGUI.App/Views/Dialogs/AddUserDialog.xaml(.cs)`、`ImportCookieDialog.xaml(.cs)`
- Modify: `src/GalleryGUI.App/Views/UsersPage.xaml(.cs)`（接线对话框 + 空状态）
- Test: `src/GalleryGUI.Tests/ViewModels/ImportViewModelTests.cs`

**Interfaces:**
- Consumes: B3 的 `UsersViewModel`（事件 `ShowAddUser`/`ShowImportCookie` 由页面转 ContentDialog）、Phase A `IAccountService/IUserService/IUiDispatcher`
- Produces:

```csharp
namespace GalleryGUI.ViewModels;

public partial class ImportViewModel : ObservableObject
{
    public ImportViewModel(IAccountService accounts, IUserService users,
        IAccountQueryService accountQuery, IUiDispatcher dispatcher);
    [ObservableProperty] private string? _userInput;          // 添加用户对话框的输入
    [ObservableProperty] private string? _userInputError;      // 解析错误（ParseInput 结果）
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _resultMessage;
    public event Action? ImportCompleted;                      // 任一导入成功 → 页面通知 UsersViewModel.Refresh

    public void ValidateUserInput();                            // 输入即时校验（ParseInput）
    public Task<bool> AddUserAsync(CancellationToken ct = default);          // 成功返回 true
    public Task<bool> ImportCookiesAsync(string cookiesFilePath, CancellationToken ct = default);
    public Task<bool> ImportFollowingAsync(CancellationToken ct = default);  // 需活动账号；数量进 ResultMessage
}
```

**对话框规格：**
- `AddUserDialog`：Title"添加用户"；Content = TextBox（PlaceholderText"用户名或主页链接，如 elonmusk 或 https://x.com/elonmusk"）+ 错误 TextBlock（红，绑定 `UserInputError`）；主按钮"添加"（`IsPrimaryButtonEnabled` 随校验结果）；每次 `TextChanged` 调 `ValidateUserInput()`。`DialogResult` 后调 `AddUserAsync`。
- `ImportCookieDialog`：Title"导入 Cookie"；Content = 说明 + **明文警告（必须逐字）**："Cookie 将明文保存在本机，等同于浏览器登录态，请勿分享给他人" + "选择 cookies.txt 文件"按钮（经 `FileDialogService`，Filter `.txt`）+ 已选文件路径显示；主按钮"导入"→ `ImportCookiesAsync(path)`；成功后 ResultMessage 显示账号昵称与"导入成功"，并提示可到用户管理页导入关注列表。
- `UsersPage` 空状态（`HasUsers=false` 时覆盖列表显示）：三步引导面板——①"导入 Cookie"（→ ImportCookieDialog）②等待验证 ③"导入关注列表"按钮（→ `ImportFollowingAsync`；`HasAccount=false` 时按钮禁用并显示"请先完成第 ① 步"）。

- [ ] **Step 1: 写失败测试**

```csharp
public class ImportViewModelTests : IDisposable
{
    // 组装与 UsersViewModelTests 相同（TestDb/SingleDbContextFactory/FakeEngine/AppPaths/SiteRegistry + 真服务）
    [Fact]
    public async Task AddUser_rejects_invalid_input_without_calling_service()
    {
        _vm.UserInput = "!!bad!!";
        _vm.ValidateUserInput();
        Assert.False(await _vm.AddUserAsync());
        Assert.NotNull(_vm.UserInputError);
        Assert.Empty(_db.Users); // 服务未被调用
    }

    [Fact]
    public async Task AddUser_valid_input_creates_user_and_fires_import_completed()
    {
        var fired = false;
        _vm.ImportCompleted += () => fired = true;
        _vm.UserInput = "carol";
        _vm.ValidateUserInput();
        Assert.Null(_vm.UserInputError);
        Assert.True(await _vm.AddUserAsync());
        Assert.True(fired);
        Assert.Single(_db.Users);
    }

    [Fact]
    public async Task ImportCookies_copies_file_sets_account_and_reports()
    {
        var file = Path.Combine(_paths.TempDir, "cookies.txt");
        await File.WriteAllTextAsync(file, "# Netscape HTTP Cookie File");
        Assert.True(await _vm.ImportCookiesAsync(file));
        Assert.Contains("导入成功", _vm.ResultMessage);
        Assert.Single(_db.Accounts.Where(a => a.Status == AccountStatus.Ok));
    }

    [Fact]
    public async Task ImportCookies_invalid_cookie_reports_error_and_marks_invalid()
    {
        _engine.WhoAmIError = new AuthException("cookie 无效");
        var file = Path.Combine(_paths.TempDir, "bad.txt");
        await File.WriteAllTextAsync(file, "junk");
        Assert.False(await _vm.ImportCookiesAsync(file));
        Assert.Contains("Cookie", _vm.ResultMessage);
        Assert.Single(_db.Accounts.Where(a => a.Status == AccountStatus.Invalid));
    }

    [Fact]
    public async Task ImportFollowing_without_account_is_blocked()
    {
        Assert.False(await _vm.ImportFollowingAsync());
        Assert.Contains("Cookie", _vm.ResultMessage);
    }
}
```

- [ ] **Step 2: 运行确认失败** → FAIL
- [ ] **Step 3: 实现 ImportViewModel**（按 Produces；`ImportCookiesAsync` 复制前的明文提示在对话框层；VM 只管调用与结果消息；`ImportFollowingAsync` 无活动账号 → ResultMessage"请先导入 Cookie"返回 false）
- [ ] **Step 4: 实现两个对话框 XAML + UsersPage 接线**（对话框文件模式：`ContentDialog` + `XamlRoot = App.MainWindowXamlRoot`——MainWindow 暴露 `public XamlRoot XamlRoot => Content.XamlRoot;`；页面 code-behind 订阅 UsersViewModel 的 `ShowAddUser`/`ShowImportCookie` 事件创建对话框实例并 `ShowAsync()`；ImportCompleted → `usersVm.RefreshCommand.ExecuteAsync(null)`）
- [ ] **Step 5: 运行确认通过** → 全量绿 + build 0 错误
- [ ] **Step 6: Commit** — `git commit -m "feat(ui): 添加用户/cookie 导入（明文提示）/关注导入流程与空状态引导"`

---

### Task B5: 下载页（任务卡片/取消/账号内容）

**Files:**
- Create: `src/GalleryGUI.Core/ViewModels/DownloadsViewModel.cs`
- Modify: `src/GalleryGUI.App/Views/DownloadsPage.xaml(.cs)`
- Test: `src/GalleryGUI.Tests/ViewModels/DownloadsViewModelTests.cs`

**Interfaces:**
- Consumes: `IDownloadQueueService`（Active/JobChanged/JobRemoved/CancelAsync/EnqueueAccountContentAsync）、B2 `IAccountQueryService/IAppSettings/IUiDispatcher`
- Produces:

```csharp
public sealed partial class JobCardViewModel : ObservableObject
{
    public long JobId { get; }
    public string Title { get; }
    public string KindBadge { get; }              // 用户媒体 / 账号喜欢 / 账号书签
    public double ProgressPercent { get; }        // Total==0 → indeterminate（UI 层用 IsIndeterminate）
    public bool IsIndeterminate { get; }
    public string ProgressText { get; }           // "12/120 · 跳过 80"
    public string? CurrentFile { get; }
    public string StatusText { get; }             // 排队中/下载中/已完成/已取消/失败
    public bool IsFinished { get; }
    public IRelayCommand CancelCommand { get; }
    public void Update(JobSnapshot snapshot);      // 全字段刷新（同线程调用约定：宿主负责投递）
}

public partial class DownloadsViewModel : ObservableObject
{
    public ObservableCollection<JobCardViewModel> Jobs { get; }
    public bool HasActive { get; }
    public bool HasAccount { get; }
    [ObservableProperty] private string? _statusMessage;
    public event Action? JobsChanged;              // 页面无需订阅；供测试
    public IAsyncRelayCommand DownloadLikesCommand { get; }
    public IAsyncRelayCommand DownloadBookmarksCommand { get; }
    public void Start();                            // 订阅 queue 事件 + 种子 Active（页面 OnNavigatedTo 调用）
    public void Stop();                             // 取消订阅（OnNavigatedFrom）
}
```

行为：`JobChanged` → 找到同 JobId 卡片 `Update(snapshot)`（无则新增，经 dispatcher）；`JobRemoved` → 移除卡片；`CancelCommand` → `queue.CancelAsync(JobId)`；`DownloadLikes/Bookmarks` → 目录 + siteOptions 解析 → `EnqueueAccountContentAsync(account, kind, dir, siteOptions)`（无账号 → StatusMessage 提示）。

- [ ] **Step 1: 写失败测试**（队列用真 `DownloadQueueService` + FakeEngine，测事件驱动的卡片生命周期：入队后卡片出现、file-done 后 ProgressText 变化、终态后卡片移除、无账号时账号内容命令被拦截）
- [ ] **Step 2: 确认失败** → FAIL
- [ ] **Step 3: 实现 VM**（按 Produces；事件处理器先经 dispatcher 投递再操作集合）
- [ ] **Step 4: 页面 XAML**——顶部按钮行（"下载账号内容：喜欢 / 书签"两按钮 + StatusMessage）+ `ItemsControl`（ItemsPanel=`StackPanel`）卡片模板：Title + KindBadge + `ProgressRing`(IsIndeterminate)/`ProgressBar`(Value=ProgressPercent Maximum=100) + ProgressText + CurrentFile + StatusText + 取消按钮（IsFinished 时 Collapsed）。code-behind：OnNavigatedTo → `Vm.Start()`；OnNavigatedFrom → `Vm.Stop()`。
- [ ] **Step 5: 确认通过** → 全量绿
- [ ] **Step 6: Commit** — `git commit -m "feat(ui): 下载页（任务卡片/进度/取消/账号喜欢书签入口）"`

---

### Task B6: 历史页（筛选/列表/溯源）

**Files:**
- Create: `src/GalleryGUI.Core/ViewModels/HistoryViewModel.cs`
- Modify: `src/GalleryGUI.App/Views/HistoryPage.xaml(.cs)`
- Test: `src/GalleryGUI.Tests/ViewModels/HistoryViewModelTests.cs`

**Interfaces:**
- Consumes: B2 `IHistoryQueryService/IUiDispatcher`
- Produces:

```csharp
public sealed partial class HistoryRowViewModel : ObservableObject
{
    public HistoryRow Model { get; }
    public string FileName { get; }        // Path.GetFileName(FilePath)
    public string UserText { get; }        // UserScreenName ?? "账号内容"
    public string? SourceUrl { get; }      // SourceItemId → $"https://x.com/i/status/{SourceItemId}"，空则 null
    public string SizeText { get; }        // 人性化（B/KB/MB），无值 "—"
    public string StatusText { get; }      // 已下载/已跳过/失败
    public string CreatedText { get; }     // 本地时间 "yyyy-MM-dd HH:mm"
}

public partial class HistoryViewModel : ObservableObject
{
    public HistoryViewModel(IHistoryQueryService history, IUiDispatcher dispatcher,
        IAccountQueryService accountQuery, SiteRegistry sites);
    public ObservableCollection<HistoryRowViewModel> Rows { get; }
    public IReadOnlyList<User> FilterUsers { get; }      // 筛选下拉数据
    [ObservableProperty] private User? _selectedUserFilter;   // null=全部
    [ObservableProperty] private DateTimeOffset? _fromDate;    // 仅日期部分
    [ObservableProperty] private DateTimeOffset? _toDate;
    [ObservableProperty] private string? _statusMessage;
    public IAsyncRelayCommand OpenContainingFolderCommand { get; }  // 参数：HistoryRowViewModel（行内按钮）
    public IRelayCommand<HistoryRowViewModel> OpenSourceCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }               // 筛选变更自动触发
}
```

行为：筛选三元组任一变更 → Refresh（`HistoryFilter(UserId, From?.Date, To?.Date.AddDays(1))`）；`OpenContainingFolder(path)`：`Process.Start("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"")`（文件不存在 → StatusMessage "文件已被移动或删除"）；`OpenSource`：SourceUrl 非空才启浏览器，否则 StatusMessage "该记录无来源链接"。

- [ ] **Step 1: 写失败测试**（播种两个用户 + 各自文件 + likes 文件；断言行映射/筛选/SourceUrl 构造/SizeText）
- [ ] **Step 2: 确认失败** → FAIL
- [ ] **Step 3: 实现 VM**；`SizeText` 人性化函数 `FormatSize(long?)` 放 VM 内部 static（B、KB、MB、GB，1 位小数）
- [ ] **Step 4: 页面 XAML**——顶部筛选行（用户 ComboBox + 两个 DatePicker + 清除筛选按钮）+ `ListView`（或 DataGrid）行模板：文件名/用户/内容ID（HyperlinkButton"查看原文"）/大小/时间/状态徽标 + 行内"打开所在文件夹"按钮。code-behind OnNavigatedTo 调 Refresh。
- [ ] **Step 5: 确认通过** → 全量绿
- [ ] **Step 6: Commit** — `git commit -m "feat(ui): 历史页（筛选/来源链接/打开所在文件夹）"`

---

### Task B7: 设置页（通用/账号/站点选项/引擎/关于）

**Files:**
- Create: `src/GalleryGUI.Core/ViewModels/SettingsViewModel.cs`（含 `OptionItemViewModel`）
- Modify: `src/GalleryGUI.App/Views/SettingsPage.xaml(.cs)`
- Test: `src/GalleryGUI.Tests/ViewModels/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: B2 `IAppSettings`、Phase A `IAccountService/IAccountQueryService/IDownloadEngine/IDownloadQueueService/IAppPaths/SiteRegistry`、`LauncherService`（App 层，经事件解耦——VM 声明 `event Action<string>? OpenFolderRequested;` 页面调 LauncherService）
- Produces:

```csharp
public sealed partial class OptionItemViewModel : ObservableObject
{
    public string Key { get; }
    public string DisplayName { get; }
    public OptionKind Kind { get; }
    public bool BoolValue { get => _boolValue; set => SetProperty(ref _boolValue, value); }
    public string TextValue { get => _textValue; set => SetProperty(ref _textValue, value); }
    public OptionItemViewModel(OptionField field, object? value);
    public object? ToValue();   // 按 Kind 回读
}

public partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(IAppSettings settings, IAccountService accounts, IAccountQueryService accountQuery,
        IDownloadEngine engine, IDownloadQueueService queue, IAppPaths paths, SiteRegistry sites, IUiDispatcher dispatcher);
    // 通用
    public string DownloadDirectory { get; set via command; }   // 浏览按钮事件 OpenFolderPickerRequested → 页面调 FileDialogService 后调 vm.SetDownloadDirectoryAsync(path)
    public event Action? OpenFolderPickerRequested;
    [ObservableProperty] private double _concurrency;           // NumberBox 绑定，保存时取整
    [ObservableProperty] private string? _statusMessage;
    // 账号
    public Account? ActiveAccount { get; }                       // null → 显示导入按钮
    public string AccountStatusText { get; }                     // 未验证/有效/失效 徽标文字
    [ObservableProperty] private string? _pendingCookieFile;     // 对话框选择结果
    public event Action? AccountChanged;                          // 导入/验证成功后通知（UsersPage 无需，设置页自刷新）
    // 站点选项
    public ObservableCollection<OptionItemViewModel> TwitterOptions { get; }  // V1 取 SiteRegistry.All.First()
    public IAsyncRelayCommand SaveOptionsCommand { get; }
    public IAsyncRelayCommand SaveGeneralCommand { get; }
    // 引擎/关于
    public string EngineVersion { get; }                          // HelloAsync；失败显示 "引擎未就绪"
    public string AppVersion { get; }                             // Assembly 版本
    public IAsyncRelayCommand VerifyAccountCommand { get; }
    public IRelayCommand OpenLogsCommand { get; }                 // 触发 OpenFolderRequested(paths.LogsDir)
    public void Start();                                          // 异步加载：账号/设置/引擎版本
}
```

- [ ] **Step 1: 写失败测试**（OptionItemViewModel 三种 Kind 的 ToValue 往返；Concurrency 保存后 GetConcurrencyAsync 一致；下载目录保存/读取；VerifyAccount 用 FakeEngine.WhoAmIError 触发 Invalid 徽标文案）
- [ ] **Step 2: 确认失败** → FAIL
- [ ] **Step 3: 实现 VM**
- [ ] **Step 4: 页面 XAML**——`ScrollViewer` + `StackPanel`（Spacing 8，`controls:SettingsCard` 组）：通用卡（下载目录行：路径 TextBlock + "浏览…"按钮 + "保存"按钮；并发 NumberBox Minimum=1 Value 绑定）；账号卡（无账号 → "导入 Cookie"按钮 → ImportCookieDialog 实例复用（B4）；有账号 → 昵称/@handle/状态徽标 + "重新验证" + "更换 Cookie"）；站点-X 卡（`ItemsControl` 逐 OptionItem：`Kind==Boolean`→ToggleSwitch、`Text`→TextBox；"保存站点选项"按钮）；引擎卡（版本 TextBlock + "打开日志文件夹"按钮）；关于卡（AppVersion + 开源地址 HyperlinkButton `https://github.com/TODO-replace` 占位——**定稿**：显示文本"GitHub 仓库（Phase C 配置）"纯 TextBlock，不放死链）。页面 code-behind：订阅 OpenFolderPickerRequested/OpenFolderPicker → FileDialogService.PickFolderAsync → vm.SetDownloadDirectoryAsync。
- [ ] **Step 5: 确认通过** → 全量绿
- [ ] **Step 6: Commit** — `git commit -m "feat(ui): 设置页（通用/账号/站点选项 Schema 渲染/引擎/关于）"`

---

### Task B8: 全局错误呈现、手测清单与收尾

**Files:**
- Modify: `src/GalleryGUI.App/MainWindow.xaml(.cs)`（全局 InfoBar 承载 AccountInvalid 事件）、四个页面（微调）
- Create: `docs/手测清单-PhaseB.md`
- Modify: 视需要清理前序警告（RunnerEngine CS9113：`log` 参数加一行 Debug 级调用；AccountServiceTests xUnit2031：断言改 await）

**Interfaces:**
- Consumes: `IDownloadQueueService.AccountInvalid(accountId, reason)`（Phase A）

**Step 1: 全局通知**——MainWindow 构造时解析 `IDownloadQueueService`，订阅 `AccountInvalid` → 显示 `InfoBar`（IsOpen、Severity=Error、Message="登录态失效，请重新导入 Cookie：<reason>" + "去设置"按钮导航到 SettingsPage）；`JobChanged` 终态 Completed → InfoBar(Severity=Success, "任务完成：<Title>") 5 秒自动关闭（DispatcherQueueTimer）。

**Step 2: 手测清单**（写入 `docs/手测清单-PhaseB.md`，`dotnet run --project src/GalleryGUI.App` 逐项勾选）：

1. 启动：窗口出现、Mica、四页签切换、日志文件生成
2. 设置→导入 Cookie（含明文提示出现）→ 状态徽标"有效"（需真实 cookie；无则验证假 cookie 显示"失效"）
3. 用户管理→导入关注列表（真实 cookie 下数量正确；无网络环境记 SKIP）
4. 添加用户（用户名/链接/非法输入三态）
5. 多选下载 → 下载页卡片出现、进度推进、完成后历史页可查
6. 取消运行中任务 → 卡片移除、状态"已取消"
7. 历史筛选（按用户/日期）、打开所在文件夹、查看原文
8. 设置并发改 3 → 入队 3 用户并发执行
9. 关闭应用重开：用户列表/历史/设置均在（持久化生效）
10. 无 cookie 全新状态：空状态引导三步显示正确

**Step 3: 全量回归** → `dotnet test src/GalleryGUI.sln` + `pytest` 全绿；`dotnet build` 0 错误（警告清单与 Phase A 持平不新增）

**Step 4: Commit** — `git commit -m "feat(ui): 全局通知/手测清单/警告清理"`

---

## 计划自审记录

1. **规格覆盖**：§6.1 用户管理 → B3/B4（含空状态三步引导）；§6.2 下载页 → B5（任务栏进度延后为裁定）；§6.3 历史页 → B6；§6.4 设置页 → B7（四组卡片齐备，开源地址占位为定稿）；§2.2 MVVM/依赖方向 → 全局约束；§3.5 cookie 明文提示 → B4 对话框逐字文案；§7 启动恢复 → B1 App.xaml.cs。Phase A 延迟项承接：AddLogging 顺序（B1）、下载目录非空（B3 消费 IAppSettings 回退 + 空状态）、来源保留词（未承接——评估为低价值 UX 收紧，转 Phase C 一并处理并记入 Phase C 计划输入）、E2E archive 回归防护（转 Phase C，引擎热更新落地前必须补）、HandshakeTimeout 接线（转 Phase C，与引擎播种一并验证）。
2. **占位符扫描**：B1 页面占位是任务自身交付物（B3-B7 替换）；B3 的"简化替代（删除不做二次确认）"为明确定稿而非 TBD；B7 开源地址"Phase C 配置"纯文本为定稿。无 TBD/TODO。
3. **类型一致性**：B2 接口签名与 B3-B7 消费一致（`GetAsync(key, fallback, ct)` 三参、`IAppSettings` 方法集、`HistoryRow` 七字段）；B3 `UserRowViewModel` 行级命令与 XAML 绑定名对应（AvatarSource/DownloadCommand/PinButtonText 等在 Step 4 绑定契约节声明）；B5 `JobCardViewModel.Update(JobSnapshot)` 与 Phase A `JobSnapshot` 九字段对应；`x:Bind` 需要的 VM 成员（ImportButtonVisibility/BottomBarVisibility/SelectedCountText/StatusMessage）已在 B3 契约列出。
4. **已知风险**：DataGrid 7.1.2 与 WinAppSDK 1.6 的兼容性（若模板列绑定异常，回退方案为 ListView+GridView，计划内允许该替换并记录）；FileOpenPicker 非打包 HWND 初始化（`WinRT.Interop.InitializeWithWindow.Initialize`，MainWindow 暴露 HWND：`WindowNative.GetWindowHandle(this)`）；包版本还原失败按 Global Constraints 注记处理。

