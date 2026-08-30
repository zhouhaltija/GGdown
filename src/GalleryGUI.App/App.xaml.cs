using System.IO;
using GalleryGUI;
using GalleryGUI.Data;
using GalleryGUI.Paths;
using GalleryGUI.Engine;
using GalleryGUI.Services;
using GalleryGUI.Threading;
using GalleryGUI.App.Infrastructure;
using GalleryGUI.App.Views;
using GalleryGUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;

namespace GalleryGUI.App;

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
        services.AddSingleton<UsersViewModel>();
        services.AddSingleton<DownloadsViewModel>(); // B5：下载页 VM（页面缓存 NavigationCacheMode=Enabled，singleton 保持订阅/退订对称）
        services.AddTransient<ImportViewModel>(); // B4：每次打开对话框取新实例（UserInput/ResultMessage 不串台）
        Services = services.BuildServiceProvider();

        ApplyDevEngineOverrides();

        // 启动恢复：遗留 Running/Pending → Failed（后台执行，不阻塞首帧）
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = Services.CreateScope();
                // B1 补充（见报告偏离 5）：brief 未规定 DbInitializer 调用点，Phase A 的建库/迁移无人执行——
                // 全新机器上首跑会因缺表使恢复与所有页面查询失败，故在首次触库前先建库
                await DbInitializer.InitializeAsync(
                    scope.ServiceProvider.GetRequiredService<GalleryDbContext>());
                await scope.ServiceProvider.GetRequiredService<IDownloadQueueService>()
                    .RecoverOnStartupAsync();
            }
            catch (Exception ex) { Log.Error(ex, "启动恢复失败"); }
            finally { _readyTcs.TrySetResult(); } // 控制器裁定 2：成功失败都放行就绪门
        });

        _window = new MainWindow(Services);
        // B4 控制器裁定：Picker 属主句柄注入（替代 B1 的 GetActiveWindow 启发式），须在窗口创建后
        Services.GetRequiredService<FileDialogService>().SetOwner(_window.WindowHandle);
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
