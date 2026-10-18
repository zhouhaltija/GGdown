using GGdown.Services;
using GGdown.Threading;
using GGdown.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests;

public class DependencyLifetimeTests
{
    [Fact]
    public void Application_services_and_page_scopes_pass_lifetime_validation()
    {
        var paths = TestPaths.Create();
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddGGdownCore(paths);
            services.AddSingleton<IUiDispatcher>(new SyncDispatcher());
            services.AddTransient<UsersViewModel>();
            services.AddTransient<SiteSettingsViewModel>();
            services.AddTransient<ImportViewModel>();
            services.AddTransient<FollowingPickerViewModel>();
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
                { ValidateScopes = true, ValidateOnBuild = true });
            using var scope = provider.CreateScope();
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<UsersViewModel>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<SiteSettingsViewModel>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ImportViewModel>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<FollowingPickerViewModel>());
            Assert.NotNull(provider.GetRequiredService<IDownloadQueueService>());
        }
        finally { Directory.Delete(paths.Root, true); }
    }
}
