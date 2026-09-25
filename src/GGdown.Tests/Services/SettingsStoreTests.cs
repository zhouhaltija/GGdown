using GGdown.Data;
using GGdown.Services;
using Microsoft.Data.Sqlite;

namespace GGdown.Tests.Services;

public class SettingsStoreTests : IDisposable
{
    private readonly (SqliteConnection, GGdownDbContext) _t;
    private readonly GGdownSettingsStore<GGdownDbContext> _store;

    public SettingsStoreTests()
    {
        _t = TestDb.Create();
        // 旧库 context 仍映射 SettingEntity（存量拆分迁移读取用），泛型化后语义不变
        _store = new GGdownSettingsStore<GGdownDbContext>(_t.Item2);
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
