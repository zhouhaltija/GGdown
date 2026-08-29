using GalleryGUI.Data;
using GalleryGUI.Services;
using Microsoft.Data.Sqlite;

namespace GalleryGUI.Tests.Services;

public class SettingsStoreTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly GallerySettingsStore _store;

    public SettingsStoreTests()
    {
        _t = TestDb.Create();
        _store = new GallerySettingsStore(_t.Item2);
    }
    public void Dispose() => _t.Item1.Dispose();

    [Fact]
    public async Task Missing_key_returns_fallback()
    {
        Assert.Equal(7, await _store.GetAsync("nope", 7));
        Assert.Null(await _store.GetAsync<string>("nope"));
    }

    [Fact]
    public async Task Roundtrip_and_overwrite()
    {
        await _store.SetAsync("download.dir", @"D:\Downloads");
        Assert.Equal(@"D:\Downloads", await _store.GetAsync<string>("download.dir"));
        await _store.SetAsync("download.dir", @"E:\Media");
        Assert.Equal(@"E:\Media", await _store.GetAsync<string>("download.dir"));
    }

    [Fact]
    public async Task Complex_value_survives_json()
    {
        await _store.SetAsync("x.options", new { videos = true, sleep = "2" });
        var json = await _store.GetAsync<string>("x.options");
        Assert.Contains("\"videos\":true", json);
    }
}
