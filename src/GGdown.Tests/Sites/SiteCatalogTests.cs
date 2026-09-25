using GGdown.Settings;
using GGdown.Sites;

namespace GGdown.Tests.Sites;

public class SiteCatalogTests
{
    [Fact]
    public void Twitter_and_pixiv_are_available()
    {
        Assert.Contains(SiteCatalog.All, s => s.SiteId == "twitter" && s.Available);
        Assert.Contains(SiteCatalog.All, s => s.SiteId == "pixiv" && s.Available);
        Assert.Equal("twitter", SiteCatalog.Default.SiteId);
        Assert.True(SiteCatalog.Default.Available);
        Assert.Contains(SiteCatalog.All, s => !s.Available);
        Assert.False(SiteCatalog.Get("fanbox").Available);
        Assert.Equal(SiteCatalog.Default, SiteCatalog.Get("nope"));
    }

    [Fact]
    public void Coming_soon_label_marks_unavailable_sites()
    {
        Assert.Equal("X (Twitter)", SiteCatalog.Get("twitter").Label);
        Assert.Equal("Pixiv", SiteCatalog.Get("pixiv").Label);
        Assert.Contains("即将支持", SiteCatalog.Get("fanbox").Label);
    }
}

public class CurrentSiteTests : IDisposable
{
    private readonly (Microsoft.Data.Sqlite.SqliteConnection, global::GGdown.Data.GGdownDbContext) _t;
    private readonly (Microsoft.Data.Sqlite.SqliteConnection, global::GGdown.Data.GGdownGlobalDbContext) _global = TestDb.CreateGlobal();
    private readonly GGdown.Paths.AppPaths _paths = TestPaths.Create();
    private readonly IAppSettings _settings;
    private readonly CurrentSite _current;

    public CurrentSiteTests()
    {
        _t = TestDb.Create();
        // Task 3：AppSettings 走全局库 + 平台库（此处只触 ui.currentSite 全局键）
        _settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2),
            new SiteRegistry([new TwitterSiteProvider()]), new global::GGdown.Data.SiteDbContextFactory(_paths));
        _current = new CurrentSite(_settings);
    }

    public void Dispose()
    {
        _t.Item1.Dispose();
        _global.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [Fact]
    public async Task Defaults_to_twitter_and_persists_selection()
    {
        await _current.LoadAsync();
        Assert.Equal("twitter", _current.SiteId);
        Assert.True(_current.IsAvailable);

        var saw = false;
        _current.Changed += () => saw = true;
        await _current.SelectAsync("pixiv");
        Assert.True(saw);
        Assert.Equal("pixiv", _current.SiteId);
        Assert.True(_current.IsAvailable);

        var other = new CurrentSite(_settings);
        await other.LoadAsync();
        Assert.Equal("pixiv", other.SiteId);
    }
}
