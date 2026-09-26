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
        services.AddSingleton<SiteRegistry>(_ => new SiteRegistry([new TwitterSiteProvider(), new PixivSiteProvider(), new DouyinSiteProvider()]));

        // Task 3/5/6 双库：全局库走 DI（AppSettings/启动初始化用），AddDbContextFactory 先注册使
        // options 为 Singleton（沿用控制器裁定：singleton 工厂捕获 scoped options 在 scope 校验宿主会炸）；
        // 平台库不进 AddDbContextFactory——由 ISiteDbContextFactory 按站点建 options
        // （无 scoped 需求，Pooling=False 见其实现注释）。旧 GGdownDbContext 已无运行时消费方，
        // 仅保留给存量拆分迁移（自建 options）与设计时工厂。
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
