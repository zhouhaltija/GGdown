using System.IO;
using GalleryGUI;
using GalleryGUI.Data;
using GalleryGUI.Paths;
using GalleryGUI.Engine;
using GalleryGUI.Services;
using GalleryGUI.Threading;
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
                // B1 补充（见报告偏离 5）：brief 未规定 DbInitializer 调用点，Phase A 的建库/迁移无人执行——
                // 全新机器上首跑会因缺表使恢复与所有页面查询失败，故在首次触库前先建库
                await DbInitializer.InitializeAsync(
                    scope.ServiceProvider.GetRequiredService<GalleryDbContext>());
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
