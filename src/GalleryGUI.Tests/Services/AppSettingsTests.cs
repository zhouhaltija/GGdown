using GalleryGUI.Services;
using GalleryGUI.Settings;
using GalleryGUI.Sites;
using Microsoft.Data.Sqlite; // 补 using：SqliteConnection 在 tuple 类型中未限定（brief 已知偏离模式）

namespace GalleryGUI.Tests.Services;

public class AppSettingsTests : IDisposable
{
    // 命名空间限定偏离：GalleryGUI.Tests.Data（GalleryDbContextTests.cs）遮蔽 GalleryGUI.Data，
    // brief 的 `Data.GalleryDbContext` 解析失败，改用 global:: 全限定（最小修正，已记录）。
    private readonly (SqliteConnection, global::GalleryGUI.Data.GalleryDbContext) _t;
    private readonly IAppSettings _settings;

    public AppSettingsTests()
    {
        _t = TestDb.Create();
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        _settings = new AppSettings(new GallerySettingsStore(_t.Item2), sites);
    }
    public void Dispose() => _t.Item1.Dispose();

    [Fact]
    public async Task Download_directory_falls_back_to_default_when_missing_or_blank()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GalleryGUI");
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
}
