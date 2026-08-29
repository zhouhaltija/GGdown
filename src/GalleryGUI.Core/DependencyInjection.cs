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
        // T11/T10 增补：services.AddSingleton<StatsAggregator>();
        //           services.AddSingleton<IDownloadQueueService, DownloadQueueService>();
        // （brief 注明二者为 singleton 且只用 IDbContextFactory 访问数据库；类型在 T11/T10 落地，
        //   progress.md 裁定执行顺序 T9 → T11 → T10，故此处暂缓注册以免编译失败。）
        return services;
    }
}
