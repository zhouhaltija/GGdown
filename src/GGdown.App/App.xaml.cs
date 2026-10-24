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
    public new static App Current => (App)Application.Current;

    // 控制器裁定 2：就绪门——启动任务（建库+恢复）完成后放行，
    // 页面 OnNavigatedTo 中 await App.Readiness 后再触发首刷，避免新机器上首查因缺表失败
    private static readonly TaskCompletionSource _readyTcs = new();
    public static Task Readiness => _readyTcs.Task;

    // 全局库就绪门：只等全局库迁移 + 当前站点载入，主窗口据此先显示侧栏；
    // 统计重算/任务恢复（需逐个打开平台库）不阻塞侧栏，页面仍等 Readiness
    private static readonly TaskCompletionSource _globalReadyTcs = new();
    public static Task GlobalReadiness => _globalReadyTcs.Task;

    public IServiceProvider Services { get; private set; } = null!;
    private MainWindow? _window;
    private AppInstanceLock? _instanceLock;

    /// <summary>B4：对话框 XamlRoot / Picker 属主句柄的宿主窗口（页面经 App.Current.MainWindow.DialogXamlRoot 取用）。</summary>
    public MainWindow MainWindow => _window!;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var paths = AppPaths.CreateDefault();
        _instanceLock = AppInstanceLock.TryAcquire(paths.Root);
        if (_instanceLock is null)
        {
            Exit();
            return;
        }
        var legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataRootMigration.LegacyFolderName);
        var migration = DataRootMigration.Apply(legacyRoot, paths.Root);
        paths.EnsureCreated();
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
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "后台线程未处理异常，应用即将退出");
            Log.CloseAndFlush();
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
        Services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        // 引擎与数据库独立准备；浏览已有数据无需等待引擎校验或复制。
        var startup = StartupCoordinator.Start(
            async () =>
            {
                await ApplyRuntimeSeedingAsync(paths);
                ApplyDevEngineOverrides();
            },
            async () =>
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
                // 原先在 UI 线程同步等待（EF 冷启动推迟窗口出现），移到后台、先于全局就绪门
                try { await Services.GetRequiredService<ICurrentSite>().LoadAsync(); }
                catch (Exception ex) { Log.Error(ex, "加载当前站点失败"); }
            },
            async () =>
            {
                using var scope = Services.CreateScope();
                // Global Constraint：启动即应用保存的并发数（此前仅设置页应用，未访问设置页不生效）
                var queue = scope.ServiceProvider.GetRequiredService<IDownloadQueueService>();
                queue.Concurrency = await scope.ServiceProvider.GetRequiredService<IAppSettings>()
                    .GetConcurrencyAsync();
                await scope.ServiceProvider.GetRequiredService<IDownloadQueueService>()
                    .RecoverOnStartupAsync();
                // 先恢复中断任务，再修复尚未提交完的统计投影。
                await scope.ServiceProvider.GetRequiredService<StatsAggregator>()
                    .RepairOnStartupAsync();
            });
        Services.GetRequiredService<RunnerEngineOptions>().Initialization = startup.EngineReady;
        _ = PublishDataReadinessAsync(startup);
        _ = ObserveEngineReadinessAsync(startup.EngineReady);

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

    private static async Task PublishDataReadinessAsync(StartupTasks startup)
    {
        try { await startup.GlobalReady; }
        catch { /* 完整数据就绪任务会传播并记录同一错误，侧栏仍可打开。 */ }
        finally { _globalReadyTcs.TrySetResult(); }
        try
        {
            await startup.DataReady;
            _readyTcs.TrySetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动恢复失败");
            _readyTcs.TrySetException(ex);
        }
    }

    private static async Task ObserveEngineReadinessAsync(Task readiness)
    {
        try { await readiness; }
        catch (Exception ex) { Log.Error(ex, "引擎初始化失败，仍可浏览已有数据"); }
    }

    /// <summary>
    /// 后台安装独立引擎版本，验证后切换指针；保留旧版本供运行中的进程继续使用。
    /// 更新失败时使用旧版本，无可用引擎时仍允许浏览和管理已有数据。
    /// </summary>
    private async Task ApplyRuntimeSeedingAsync(AppPaths paths)
    {
        try
        {
#if DEBUG
            // 开发环境始终使用仓库源码，不安装或探测发行引擎。
            if (DevEngineLocator.FindRepoRoot(AppContext.BaseDirectory) is not null) return;
#endif
            var installation = await new EngineInstaller().InstallAsync(
                Path.Combine(AppContext.BaseDirectory, "engine"), paths.EngineDir);
            var options = Services.GetRequiredService<RunnerEngineOptions>();
            options.PythonExe = Path.Combine(installation.Directory, "python", "python.exe");
            options.RunnerScript = Path.Combine(installation.Directory, "runner.py");
            options.GalleryDlPath = Path.Combine(installation.Directory, "site-packages");
            if (installation.UpdateError is not null)
                Log.Warning(installation.UpdateError, "引擎更新失败，继续使用 {EngineDirectory}", installation.Directory);
            else
                Log.Information("引擎已就绪：{EngineDirectory}，本次安装 {Updated}", installation.Directory, installation.Updated);
            if (installation.UpdateError is null)
            {
                // 维护失败只记诊断：清理历史版本绝不能影响引擎就绪。
                try
                {
                    var removed = EngineVersionMaintenance.Cleanup(paths.EngineDir, installation.Directory, DateTime.UtcNow,
                        ex => Log.Warning(ex, "跳过无法清理的引擎历史版本"));
                    if (removed > 0) Log.Information("已清理 {Count} 个闲置引擎版本", removed);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "引擎历史版本维护失败，不影响本次启动");
                }
            }
        }
        catch (Exception ex) when (ex is not EngineException)
        {
            // EngineException 不携带内部异常，先完整记录再包装，避免诊断信息只留一行 Message。
            Log.Error(ex, "引擎初始化失败");
            throw new EngineException($"引擎初始化失败：{ex.Message}");
        }
    }
}

#if DEBUG
public partial class App
{
    partial void ApplyDevEngineOverrides()
    {
        var repo = DevEngineLocator.FindRepoRoot(AppContext.BaseDirectory);
        if (repo is null) return;
        var opts = Services.GetRequiredService<RunnerEngineOptions>();
        opts.RunnerScript = Path.Combine(repo, "engine", "runner.py");
        // 开发态由 uv 管理固定 Python 和依赖；第三方源码检出不作为 pip 包安装。
        opts.PythonExe = Path.Combine(repo, "engine", ".venv", "Scripts", "python.exe");
        opts.GalleryDlPath = Path.Combine(repo, "third_party", "TikTokDownloader");
    }

}
#endif
