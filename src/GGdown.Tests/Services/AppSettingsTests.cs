using GGdown.Data;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Sites;
using Microsoft.Data.Sqlite; // 补 using：SqliteConnection 在 tuple 类型中未限定（brief 已知偏离模式）
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Services;

public class AppSettingsTests : IDisposable
{
    // 命名空间限定偏离：GGdown.Tests.Data（GGdownDbContextTests.cs）遮蔽 GGdown.Data，改用 global:: 全限定。
    private readonly (SqliteConnection, GGdownGlobalDbContext) _global;
    private readonly IAppSettings _settings;
    private readonly GGdown.Paths.AppPaths _paths;

    public AppSettingsTests()
    {
        _global = TestDb.CreateGlobal();
        _paths = TestPaths.Create();
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        // Task 3：AppSettings 走全局库 + 平台库双 factory（captive dependency 裁定不变：
        // 两个 factory 均为 singleton 安全，await using 释放的不是测试持有的连接）
        _settings = new AppSettings(
            new SingleGlobalDbContextFactory(_global.Item2),
            sites,
            new SiteDbContextFactory(_paths));
    }

    public void Dispose()
    {
        _global.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [Fact]
    public async Task Download_directory_falls_back_to_default_when_missing_or_blank()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GGdown");
        Assert.Equal(expected, await _settings.GetDownloadDirectoryAsync());
        await _settings.SetDownloadDirectoryAsync("   ");
        Assert.Equal(expected, await _settings.GetDownloadDirectoryAsync());
        await _settings.SetDownloadDirectoryAsync(@"D:\Media");
        Assert.Equal(@"D:\Media", await _settings.GetDownloadDirectoryAsync());
    }

    [Fact]
    public async Task Concurrency_floors_at_one()
    {
        Assert.Equal(1, await _settings.GetConcurrencyAsync());
        await _settings.SetConcurrencyAsync(3);
        Assert.Equal(3, await _settings.GetConcurrencyAsync());
        await _settings.SetConcurrencyAsync(0);
        Assert.Equal(1, await _settings.GetConcurrencyAsync());
    }

    [Fact]
    public async Task Site_options_fall_back_to_provider_defaults()
    {
        var d = await _settings.GetSiteOptionsAsync("twitter");
        Assert.True((bool)d["videos"]!);
        Assert.Equal("{tweet_id}_{author[name]}_{num}.{extension}", d["filename"]);
        await _settings.SetSiteOptionsAsync("twitter", new Dictionary<string, object?> { ["videos"] = false });
        Assert.False((bool)(await _settings.GetSiteOptionsAsync("twitter"))["videos"]!);
    }

    [Fact]
    public async Task Site_options_stored_in_site_db_not_global()
    {
        await _settings.SetSiteOptionsAsync("twitter", new Dictionary<string, object?> { ["videos"] = true });
        // 平台库有行
        await using var siteDb = await new SiteDbContextFactory(_paths).CreateAsync("twitter");
        Assert.True(await siteDb.SiteSettings.AnyAsync(s => s.SiteId == "twitter" && s.Key == "videos"));
        // 全局库没有 site.<id>.options 键
        Assert.Null(await new GGdownSettingsStore<GGdownGlobalDbContext>(_global.Item2)
            .GetAsync<string>("site.twitter.options", null));
    }

    [Fact]
    public async Task Proxy_missing_is_disabled_and_roundtrips()
    {
        var missing = await _settings.GetProxyAsync();
        Assert.False(missing.IsEnabled);
        Assert.Null(missing.ToUrl());

        var cfg = new ProxyConfig("socks5h", "127.0.0.1", 1080, "u", "p@ss");
        await _settings.SetProxyAsync(cfg);
        var loaded = await _settings.GetProxyAsync();
        Assert.Equal("socks5h", loaded.Scheme);
        Assert.Equal("127.0.0.1", loaded.Host);
        Assert.Equal(1080, loaded.Port);
        Assert.Equal("u", loaded.Username);
        Assert.Equal("p@ss", loaded.Password);
        Assert.Equal("socks5h://u:p%40ss@127.0.0.1:1080", loaded.ToUrl());

        await _settings.SetProxyAsync(new ProxyConfig("http", "", 0));
        Assert.False((await _settings.GetProxyAsync()).IsEnabled);
    }

    [Fact]
    public async Task Visible_sites_default_to_available_and_survive_roundtrip()
    {
        // 默认 = 目录中全部 Available 站点（与 SiteRegistry 注册了几个 provider 无关）
        Assert.Equal(DefaultAvailable, await _settings.GetVisibleSitesAsync());
        await _settings.SetVisibleSitesAsync(["twitter", "pixiv", "danbooru"]);
        Assert.Equal(["twitter", "pixiv", "danbooru"], await _settings.GetVisibleSitesAsync());
    }

    [Fact]
    public async Task Visible_sites_empty_or_corrupt_json_falls_back_to_available()
    {
        var store = new GGdownSettingsStore<GGdownGlobalDbContext>(_global.Item2);
        await store.SetAsync<string>("ui.visibleSites", "");
        Assert.Equal(DefaultAvailable, await _settings.GetVisibleSitesAsync());
        await store.SetAsync<string>("ui.visibleSites", "not json");
        Assert.Equal(DefaultAvailable, await _settings.GetVisibleSitesAsync());
        await _settings.SetVisibleSitesAsync(Array.Empty<string>());
        Assert.Equal(DefaultAvailable, await _settings.GetVisibleSitesAsync());
    }

    [Fact]
    public async Task Site_last_page_roundtrip_and_default()
    {
        Assert.Equal("users", await _settings.GetSiteLastPageAsync("twitter"));
        await _settings.SetSiteLastPageAsync("pixiv", "history");
        Assert.Equal("history", await _settings.GetSiteLastPageAsync("pixiv"));
        Assert.Equal("users", await _settings.GetSiteLastPageAsync("twitter"));
    }

    private static IReadOnlyList<string> DefaultAvailable =>
        [.. SiteCatalog.All.Where(s => s.Available).Select(s => s.SiteId)];
}
