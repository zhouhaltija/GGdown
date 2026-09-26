using GGdown.ViewModels;
using GGdown.Sites;

namespace GGdown.Tests.ViewModels;

public class InterfaceSettingsViewModelTests
{
    [Fact]
    public async Task Start_loads_all_catalog_sites_with_availability()
    {
        var settings = new FakeAppSettings();
        var nav = new MainNavViewModel(new FakeCurrentSite(), settings);
        await nav.StartAsync();
        var vm = new InterfaceSettingsViewModel(settings, nav);
        await vm.StartAsync();
        Assert.Equal(SiteCatalog.All.Count, vm.Items.Count);
        Assert.Equal(3, vm.Items.Count(i => i.Available));
        Assert.Contains(vm.Items, i => i.SiteId == "douyin" && i.Available);
        Assert.All(vm.Items.Where(i => !i.Available), i => Assert.False(i.IsVisible)); // 不可用站点勾选无效
    }

    [Fact]
    public async Task Toggling_site_updates_visible_sites_and_rail()
    {
        var settings = new FakeAppSettings();
        var nav = new MainNavViewModel(new FakeCurrentSite(), settings);
        await nav.StartAsync();
        var vm = new InterfaceSettingsViewModel(settings, nav);
        await vm.StartAsync();
        Assert.Equal(2, nav.Platforms.Count);

        vm.Items.Single(i => i.SiteId == "pixiv").IsVisible = false;
        await vm.SaveChangesAsync();

        Assert.DoesNotContain("pixiv", await settings.GetVisibleSitesAsync());
        Assert.DoesNotContain(nav.Platforms, p => p.SiteId == "pixiv");
    }

    [Fact]
    public async Task Save_persists_checked_sites_including_unavailable()
    {
        var settings = new FakeAppSettings();
        var nav = new MainNavViewModel(new FakeCurrentSite(), settings);
        await nav.StartAsync();
        var vm = new InterfaceSettingsViewModel(settings, nav);
        await vm.StartAsync();

        // 不可用站点无法勾选（IsEnabled=false 绑定 Available），保存的集合只含可用勾选项
        vm.Items.Single(i => i.SiteId == "pixiv").IsVisible = false;
        await vm.SaveChangesAsync();
        Assert.Equal(["twitter"], await settings.GetVisibleSitesAsync());
    }
}
