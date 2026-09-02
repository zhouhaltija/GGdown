using GalleryGUI.Settings;
using GalleryGUI.Sites;

namespace GalleryGUI.Tests.Sites;

public class SiteCatalogTests
{
    [Fact]
    public void Twitter_is_the_only_available_site()
    {
        Assert.Contains(SiteCatalog.All, s => s.SiteId == "twitter" && s.Available);
        Assert.Equal("twitter", SiteCatalog.Default.SiteId);
        Assert.True(SiteCatalog.Default.Available);
        Assert.Contains(SiteCatalog.All, s => !s.Available);
        Assert.False(SiteCatalog.Get("pixiv").Available);
        Assert.Equal(SiteCatalog.Default, SiteCatalog.Get("nope"));
    }

    [Fact]
    public void Coming_soon_label_marks_unavailable_sites()
    {
        Assert.Equal("X (Twitter)", SiteCatalog.Get("twitter").Label);
        Assert.Contains("即将支持", SiteCatalog.Get("pixiv").Label);
    }
}

public class CurrentSiteTests : IDisposable
{
    private readonly (Microsoft.Data.Sqlite.SqliteConnection, global::GalleryGUI.Data.GalleryDbContext) _t;
    private readonly IAppSettings _settings;
    private readonly CurrentSite _current;

    public CurrentSiteTests()
    {
        _t = TestDb.Create();
        _settings = new AppSettings(new SingleDbContextFactory(_t.Item2),
            new SiteRegistry([new TwitterSiteProvider()]));
        _current = new CurrentSite(_settings);
    }

    public void Dispose() => _t.Item1.Dispose();

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
        Assert.False(_current.IsAvailable);

        var other = new CurrentSite(_settings);
        await other.LoadAsync();
        Assert.Equal("pixiv", other.SiteId);
    }
}
