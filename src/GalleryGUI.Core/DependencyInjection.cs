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

        // 适配（对调 brief 中两行的顺序）：AddDbContextFactory 先注册，使 DbContextOptions 为 Singleton——
        // singleton 工厂捕获 scoped options 会在启用 scope 校验的宿主（Development 默认）构建 DI 时失败；
        // 两个注册的连接串相同，调序后功能不变。
        services.AddDbContextFactory<GalleryDbContext>(o => o.UseSqlite($"Data Source={paths.DbFile}"));
        services.AddDbContext<GalleryDbContext>(o => o.UseSqlite($"Data Source={paths.DbFile}"));

        services.AddScoped<ISettingsStore, GallerySettingsStore>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IUserService, UserService>();
        services.AddSingleton<StatsAggregator>(); // T11 落地
        // T10 增补：services.AddSingleton<IDownloadQueueService, DownloadQueueService>();
        // （brief 注明 DownloadQueueService 为 singleton 且只用 IDbContextFactory 访问数据库；类型与注册在 Task 10 落地，
        //   progress.md 裁定执行顺序 T9 → T11 → T10，故此处暂缓注册以免编译失败。）
        return services;
    }
}
