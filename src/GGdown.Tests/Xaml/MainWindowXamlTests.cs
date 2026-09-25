namespace GGdown.Tests.Xaml;

/// <summary>
/// Task 8：主窗口壳层静态约束——平台栏/页组标签/栏底入口存在，旧 NavigationView 与站点 Flyout 已移除。
/// </summary>
public class MainWindowXamlTests
{
    private static string Load() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "XamlFixtures", "MainWindow.xaml"));

    [Fact]
    public void MainWindow_has_platform_rail_and_tab_header()
    {
        var xaml = Load();
        Assert.Contains("PlatformList", xaml);
        Assert.Contains("PageTabs", xaml);
        Assert.Contains("GlobalSettingsButton", xaml);
        Assert.Contains("GlobalDownloadsButton", xaml);
    }

    [Fact]
    public void MainWindow_drops_navigation_view_and_site_flyout()
    {
        var xaml = Load();
        Assert.DoesNotContain("NavigationView", xaml);
        Assert.DoesNotContain("SiteFlyout", xaml);
        Assert.DoesNotContain("SiteSwitcher", xaml);
    }

    [Fact]
    public void Notice_bar_with_go_settings_button_is_kept()
    {
        var xaml = Load();
        Assert.Contains("NoticeBar", xaml);
        Assert.Contains("GoSettingsButton", xaml);
    }
}
