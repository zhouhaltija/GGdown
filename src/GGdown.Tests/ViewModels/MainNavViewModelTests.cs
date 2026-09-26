using GGdown.Sites;
using GGdown.ViewModels;

namespace GGdown.Tests.ViewModels;

public class MainNavViewModelTests
{
    private static MainNavViewModel Create(FakeCurrentSite site, FakeAppSettings? settings = null)
    {
        settings ??= new FakeAppSettings();
        return new MainNavViewModel(site, settings);
    }

    [Fact]
    public async Task Platforms_default_to_available_visible_sites()
    {
        var vm = Create(new FakeCurrentSite());
        await vm.StartAsync();
        Assert.Equal(["twitter", "pixiv"], vm.Platforms.Select(p => p.SiteId).ToArray());
    }

    [Fact]
    public async Task Selecting_platform_loads_its_tabs_and_persists()
    {
        var site = new FakeCurrentSite();
        var settings = new FakeAppSettings();
        var vm = Create(site, settings);
        await vm.StartAsync();
        await vm.SelectPlatformAsync("pixiv");
        Assert.Equal("pixiv", site.SiteId);
        Assert.Equal("pixiv", settings.SavedCurrentSiteId);
        Assert.Equal(4, vm.CurrentTabs.Count);
        Assert.Equal(SitePageKey.Users, vm.CurrentTabs[0].Key);
    }

    [Fact]
    public async Task Selecting_tab_persists_last_page_and_restores_on_platform_switch()
    {
        var settings = new FakeAppSettings();
        var vm = Create(new FakeCurrentSite(), settings);
        await vm.StartAsync();
        await vm.SelectPlatformAsync("twitter");
        await vm.SelectTabAsync(SitePageKey.History);
        await vm.SelectPlatformAsync("pixiv"); // pixiv 无记忆 → Users
        Assert.Equal(SitePageKey.Users, vm.CurrentTabs.Single(t => t.IsSelected).Key);
        await vm.SelectTabAsync(SitePageKey.SiteSettings);
        await vm.SelectPlatformAsync("twitter"); // 恢复 History
        Assert.Equal(SitePageKey.History, vm.CurrentTabs.Single(t => t.IsSelected).Key);
        Assert.Equal("history", settings.LastPages["twitter"]);
    }

    [Fact]
    public async Task Selecting_tab_raises_navigation_intent()
    {
        var vm = Create(new FakeCurrentSite());
        await vm.StartAsync();
        SitePageKey? requested = null;
        vm.TabNavigationRequested += k => requested = k;
        await vm.SelectTabAsync(SitePageKey.Downloads);
        Assert.Equal(SitePageKey.Downloads, requested);
    }

    [Fact]
    public async Task Global_settings_mode_swaps_tabs_and_platform_reselect_exits()
    {
        var vm = Create(new FakeCurrentSite());
        await vm.StartAsync();
        var requested = false;
        vm.GlobalSettingsNavigationRequested += () => requested = true;
        await vm.EnterGlobalSettingsAsync();
        Assert.True(vm.IsGlobalSettings);
        Assert.True(requested);

        await vm.SelectPlatformAsync("twitter");
        Assert.False(vm.IsGlobalSettings);
        Assert.Equal(4, vm.CurrentTabs.Count);
    }

    [Fact]
    public async Task ReloadVisible_updates_platforms()
    {
        var settings = new FakeAppSettings();
        var vm = Create(new FakeCurrentSite(), settings);
        await vm.StartAsync();
        Assert.Equal(2, vm.Platforms.Count);
        await settings.SetVisibleSitesAsync(["twitter"]);
        await vm.ReloadVisibleAsync();
        Assert.Equal(["twitter"], vm.Platforms.Select(p => p.SiteId).ToArray());
    }

    [Fact]
    public async Task Collapsed_sidebar_keeps_platform_labels_hidden_after_reload()
    {
        var settings = new FakeAppSettings();
        var vm = Create(new FakeCurrentSite(), settings);
        await vm.StartAsync();
        Assert.All(vm.Platforms, p => Assert.True(p.IsSidebarExpanded));

        vm.IsSidebarExpanded = false;
        Assert.All(vm.Platforms, p => Assert.False(p.IsSidebarExpanded));

        await settings.SetVisibleSitesAsync(["pixiv"]);
        await vm.ReloadVisibleAsync();
        Assert.Single(vm.Platforms);
        Assert.False(vm.Platforms[0].IsSidebarExpanded);

        vm.IsSidebarExpanded = true;
        Assert.True(vm.Platforms[0].IsSidebarExpanded);
    }

    [Fact]
    public async Task Current_platform_not_visible_falls_back_to_first()
    {
        var settings = new FakeAppSettings();
        var vm = Create(new FakeCurrentSite(), settings);
        await vm.StartAsync();
        await vm.SelectPlatformAsync("twitter");
        await settings.SetVisibleSitesAsync(["pixiv"]);
        await vm.ReloadVisibleAsync();
        Assert.Equal("pixiv", vm.Platforms.Single().SiteId);
        Assert.True(vm.Platforms.Single().IsSelected);
    }

    [Fact]
    public async Task Start_only_loads_rail_without_selecting_platform()
    {
        var settings = new FakeAppSettings();
        var vm = Create(new FakeCurrentSite(), settings);
        var navigated = false;
        vm.TabNavigationRequested += _ => navigated = true;
        await vm.StartAsync();
        Assert.Equal(2, vm.Platforms.Count);
        Assert.All(vm.Platforms, p => Assert.False(p.IsSelected));
        Assert.Empty(vm.CurrentTabs);
        Assert.False(navigated);
        Assert.Null(settings.SavedCurrentSiteId);
    }

    [Fact]
    public async Task Reload_without_selection_does_not_auto_select()
    {
        var settings = new FakeAppSettings();
        var vm = Create(new FakeCurrentSite(), settings);
        await vm.StartAsync();
        await vm.EnterGlobalSettingsAsync();
        await settings.SetVisibleSitesAsync(["pixiv"]);
        await vm.ReloadVisibleAsync();
        Assert.False(vm.Platforms.Single().IsSelected);
        Assert.Equal(5, vm.CurrentTabs.Count); // 全局设置标签保持
    }
}
