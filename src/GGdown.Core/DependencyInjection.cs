using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GGdown;

public static class CoreServices
{
    public static IServiceCollection AddGGdownCore(this IServiceCollection services, IAppPaths paths)
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
        services.AddSingleton<SiteRegistry>(_ => new SiteRegistry([new TwitterSiteProvider(), new PixivSiteProvider()]));

        // 适配（对调 brief 中两行的顺序）：AddDbContextFactory 先注册，使 DbContextOptions 为 Singleton——
        // singleton 工厂捕获 scoped options 会在启用 scope 校验的宿主（Development 默认）构建 DI 时失败；
        // 两个注册的连接串相同，调序后功能不变。
        services.AddDbContextFactory<GGdownDbContext>(o => o.UseSqlite($"Data Source={paths.DbFile}"));
        services.AddDbContext<GGdownDbContext>(o => o.UseSqlite($"Data Source={paths.DbFile}"));

        // Task 3 起双库：全局库走 DI（AppSettings/启动初始化用）；平台库不进 AddDbContextFactory
        // ——由 ISiteDbContextFactory 按站点建 options（无 scoped 需求，Pooling=False 见其实现注释）。
        services.AddDbContextFactory<GGdownGlobalDbContext>(o => o.UseSqlite($"Data Source={paths.DbFile}"));
        services.AddDbContext<GGdownGlobalDbContext>(o => o.UseSqlite($"Data Source={paths.DbFile}"));
        services.AddSingleton<ISiteDbContextFactory>(sp => new SiteDbContextFactory(sp.GetRequiredService<IAppPaths>()));

        services.AddScoped<ISettingsStore, GGdownSettingsStore<GGdownGlobalDbContext>>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IUserService, UserService>();
        services.AddSingleton<StatsAggregator>(); // T11 落地
        // T10 落地：DownloadQueueService 为 singleton 且只用 IDbContextFactory 访问数据库
        services.AddSingleton<IDownloadQueueService, DownloadQueueService>();
        // Task B2 落地：查询服务与 AppSettings（brief Step 4 四处注册）。
        // AppSettings 注入 IDbContextFactory（控制器裁定修复 captive dependency），singleton 安全。
        services.AddSingleton<IAppSettings, AppSettings>();
        services.AddSingleton<ICurrentSite, CurrentSite>();
        services.AddSingleton<IUserQueryService, UserQueryService>();
        services.AddSingleton<IAccountQueryService, AccountQueryService>();
        services.AddSingleton<IHistoryQueryService, HistoryQueryService>();
        return services;
    }
}
