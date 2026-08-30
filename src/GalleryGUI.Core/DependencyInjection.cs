using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Settings;
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
        // T10 落地：DownloadQueueService 为 singleton 且只用 IDbContextFactory 访问数据库
        services.AddSingleton<IDownloadQueueService, DownloadQueueService>();
        // Task B2 落地：查询服务与 AppSettings（brief Step 4 四处注册）
        services.AddSingleton<IAppSettings, AppSettings>();
        services.AddSingleton<IUserQueryService, UserQueryService>();
        services.AddSingleton<IAccountQueryService, AccountQueryService>();
        services.AddSingleton<IHistoryQueryService, HistoryQueryService>();
        return services;
    }
}
