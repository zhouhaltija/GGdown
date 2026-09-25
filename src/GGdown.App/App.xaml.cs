using System.IO;
using GGdown;
using GGdown.Data;
using GGdown.Paths;
using GGdown.Engine;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Threading;
using GGdown.App.Infrastructure;
using GGdown.App.Views;
using GGdown.Sites;
using GGdown.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;

namespace GGdown.App;

public partial class App : Application
{
    // 控制器裁定 1（B1 遗漏补齐）：页面代码用 App.Current.Services 解析服务
    public static App Current => (App)Application.Current;

    // 控制器裁定 2：就绪门——启动任务（建库+恢复）完成后放行，
    // 页面 OnNavigatedTo 中 await App.Readiness 后再触发首刷，避免新机器上首查因缺表失败
    private static readonly TaskCompletionSource _readyTcs = new();
    public static Task Readiness => _readyTcs.Task;

    public IServiceProvider Services { get; private set; } = null!;
    private MainWindow? _window;

    /// <summary>B4：对话框 XamlRoot / Picker 属主句柄的宿主窗口（页面经 App.Current.MainWindow.DialogXamlRoot 取用）。</summary>
    public MainWindow MainWindow => _window!;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var paths = AppPaths.CreateDefault();
        var legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataRootMigration.LegacyFolderName);
        var migration = DataRootMigration.Apply(legacyRoot, paths.Root);
        paths.EnsureCreated();
        ApplyRuntimeSeeding(paths);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();
        // B8：启动即落一条日志——文件 sink 首条事件才建文件，保证"日志文件生成"可手测验证
        Log.Information("GGdown 启动：日志目录 {LogsDir}", paths.LogsDir);
        if (migration.MovedRoot)
            Log.Information("已将用户数据从 {Legacy} 迁到 {Current}", legacyRoot, paths.Root);
        if (migration.RenamedDatabase)
            Log.Information("数据库文件已从 {LegacyDb} 改名为 {CurrentDb}",
                DataRootMigration.LegacyDbFileName, DataRootMigration.CurrentDbFileName);
        if (migration.LegacyLeftInPlace)
            Log.Warning("旧数据目录 {Legacy} 与 {Current} 同时存在，已保留旧目录", legacyRoot, paths.Root);

        // B8：UI 线程未处理异常记日志（不主动标 Handled，保持默认崩溃对话框行为），便于事后从日志定位；
        // 崩溃路径不走 MainWindow.Closed，这里兜底刷盘（若被其他处理器标 Handled 则应用继续运行，不能关日志）
        UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "UI 线程未处理异常：{Message}", e.Message);
            if (!e.Handled) Log.CloseAndFlush();
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true)); // 必须先于 AddGGdownCore
        services.AddGGdownCore(paths);
        services.AddSingleton<IUiDispatcher>(sp =>
            new UiDispatcher(DispatcherQueue.GetForCurrentThread()));
        services.AddSingleton<FileDialogService>();
        services.AddSingleton<LauncherService>();
        // 依赖 IUserService(Scoped)——同 SettingsViewModel 裁定；页面 NavigationCacheMode=Enabled 构造一次，实例与页面同生命周期
        services.AddTransient<UsersViewModel>();
        services.AddSingleton<DownloadsViewModel>(); // B5：下载页 VM（页面缓存 NavigationCacheMode=Enabled，singleton 保持订阅/退订对称）
        services.AddSingleton<HistoryViewModel>(); // B6：历史页 VM（同上，singleton 保持筛选状态跨导航）
        // Task 9 拆分（B7 captive dependency 裁定沿用）：全局设置 VM 无 Scoped 依赖 → singleton（四页共享）；
        // 界面设置 VM 依赖 MainNavViewModel/IAppSettings（均 singleton）→ singleton；
        // 平台设置 VM 依赖 Scoped 的 IAccountService → Transient（页面缓存 + 构造时解析一次，语义等同）。
        services.AddSingleton<GlobalSettingsViewModel>();
        services.AddSingleton<InterfaceSettingsViewModel>();
        services.AddTransient<SiteSettingsViewModel>();
        services.AddTransient<ImportViewModel>(); // B4：每次打开对话框取新实例（UserInput/ResultMessage 不串台）
        services.AddTransient<FollowingPickerViewModel>();
        services.AddSingleton<MainNavViewModel>(); // Task 8：主窗口导航 VM（替代 SiteSwitcherViewModel）
        Services = services.BuildServiceProvider();
        try { Services.GetRequiredService<ICurrentSite>().LoadAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Error(ex, "加载当前站点失败"); }

        ApplyDevEngineOverrides();

        // 启动恢复：遗留 Running/Pending → Failed（后台执行，不阻塞首帧）
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = Services.CreateScope();
                // Task 6：存量单库拆分（幂等：主库含 Accounts 表才动，先于全局库初始化）
                var split = await SiteDbSplitMigration.ApplyAsync(paths);
                if (split.Sites > 0)
                    Log.Information("存量库拆分完成：{Sites} 个平台库，账号 {Accounts}/用户 {Users}/任务 {Jobs}/文件 {Files}，孤儿任务 {Orphans}",
                        split.Sites, split.Accounts, split.Users, split.Jobs, split.Files, split.OrphanJobs);
                // B1 补充（沿用）：首次触库前先建库/迁移（全局库）；平台库由 ISiteDbContextFactory 惰性建
                await DbInitializer.InitializeGlobalAsync(
                    scope.ServiceProvider.GetRequiredService<GGdownGlobalDbContext>());
                await scope.ServiceProvider.GetRequiredService<StatsAggregator>()
                    .RecalculateDownloadCountsAsync();
                // Global Constraint：启动即应用保存的并发数（此前仅设置页应用，未访问设置页不生效）
                var queue = scope.ServiceProvider.GetRequiredService<IDownloadQueueService>();
                queue.Concurrency = await scope.ServiceProvider.GetRequiredService<IAppSettings>()
                    .GetConcurrencyAsync();
                await scope.ServiceProvider.GetRequiredService<IDownloadQueueService>()
                    .RecoverOnStartupAsync();
            }
            catch (Exception ex) { Log.Error(ex, "启动恢复失败"); }
            finally { _readyTcs.TrySetResult(); } // 控制器裁定 2：成功失败都放行就绪门
        });

        _window = new MainWindow(Services);
        // B4 控制器裁定：Picker 属主句柄注入（替代 B1 的 GetActiveWindow 启发式），须在窗口创建后
        Services.GetRequiredService<FileDialogService>().SetOwner(_window.WindowHandle);
        // B8：窗口关闭即应用生命周期终点（单窗口桌面应用）——在 UI 线程确定性刷盘。
        // 实现 selection：相比 AppDomain.ProcessExit（打包外 WinUI 进程退出时机不保证触发），
        // MainWindow.Closed 在消息循环终止前同步触发，简单可靠；崩溃路径由上面 UnhandledException 兜底。
        _window.Closed += (_, _) => Log.CloseAndFlush();
        _window.Activate();
    }

    /// <summary>DEBUG：引擎用仓库源码 + PATH python（与 E2E 相同），免去播种。</summary>
    partial void ApplyDevEngineOverrides();

    /// <summary>
    /// Phase C 首启播种（RELEASE/DEBUG 均生效）：安装版引擎随安装目录分发，
    /// 而 AppPaths 指向 %LOCALAPPDATA%\GGdown\engine。首次启动时把
    /// 安装目录 engine（AppContext.BaseDirectory\engine）一次性复制到
    /// appdata 引擎目录；已存在则跳过（幂等）；失败仅记日志不崩溃。
    /// </summary>
    private void ApplyRuntimeSeeding(AppPaths paths)
    {
        try
        {
            var bundledEngine = Path.Combine(AppContext.BaseDirectory, "engine");
            if (File.Exists(paths.PythonExe) || !Directory.Exists(bundledEngine))
                return;

            Log.Information("首启播种：复制安装目录引擎 {Source} -> {Target}", bundledEngine, paths.EngineDir);
            CopyDirectory(bundledEngine, paths.EngineDir);
            Log.Information("首启播种完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "首启播种失败（不阻断启动）");
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}

#if DEBUG
public partial class App
{
    partial void ApplyDevEngineOverrides()
    {
        var repo = FindRepoRoot(AppContext.BaseDirectory);
        if (repo is null) return;
        var opts = Services.GetRequiredService<RunnerEngineOptions>();
        var paths = Services.GetRequiredService<IAppPaths>();
        opts.PythonExe = "python";
        opts.RunnerScript = Path.Combine(repo, "engine", "runner.py");
        // gallery-dl 源码 + 已播种的 site-packages（含 PySocks）。只指源码时系统 Python 没有 socks，
        // SOCKS5 会报 InvalidSchema: Missing dependencies for SOCKS support。
        var parts = new List<string> { Path.Combine(repo, "gallery-dl") };
        var bundled = Path.Combine(paths.EngineDir, "site-packages");
        if (Directory.Exists(bundled))
            parts.Add(bundled);
        opts.GalleryDlPath = string.Join(Path.PathSeparator, parts);
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
