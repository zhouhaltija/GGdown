using System.Text.Json;
using GalleryGUI.Data;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Settings;

public interface IAppSettings
{
    Task<string> GetDownloadDirectoryAsync(CancellationToken ct = default);
    Task SetDownloadDirectoryAsync(string directory, CancellationToken ct = default);
    Task<int> GetConcurrencyAsync(CancellationToken ct = default);
    Task SetConcurrencyAsync(int concurrency, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object?>> GetSiteOptionsAsync(string siteId, CancellationToken ct = default);
    Task SetSiteOptionsAsync(string siteId, IReadOnlyDictionary<string, object?> options, CancellationToken ct = default);
    Task<ProxyConfig> GetProxyAsync(CancellationToken ct = default);
    Task SetProxyAsync(ProxyConfig proxy, CancellationToken ct = default);
    Task<string> GetCurrentSiteIdAsync(CancellationToken ct = default);
    Task SetCurrentSiteIdAsync(string siteId, CancellationToken ct = default);
}

// 控制器裁定（captive dependency 修复）：不注入 Scoped 的 ISettingsStore（内部持有 scoped DbContext，
// Singleton 捕获后在 scope 校验宿主解析必炸），改为注入 Singleton 的 IDbContextFactory<GalleryDbContext>，
// 每个方法内 `await using` 建上下文后构造 GallerySettingsStore 转发——接口与行为语义不变。
public sealed class AppSettings(IDbContextFactory<GalleryDbContext> factory, SiteRegistry sites) : IAppSettings
{
    public static string DefaultDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GalleryGUI");

    public async Task<string> GetDownloadDirectoryAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var store = new GallerySettingsStore(db);
        var v = await store.GetAsync<string>("download.directory", null, ct);
        return string.IsNullOrWhiteSpace(v) ? DefaultDownloadDirectory : v;
    }

    public async Task SetDownloadDirectoryAsync(string directory, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await new GallerySettingsStore(db).SetAsync("download.directory", directory, ct);
    }

    public async Task<int> GetConcurrencyAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var store = new GallerySettingsStore(db);
        var v = await store.GetAsync("download.concurrency", 1, ct);
        return Math.Max(1, v);
    }

    public async Task SetConcurrencyAsync(int concurrency, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await new GallerySettingsStore(db).SetAsync("download.concurrency", Math.Max(1, concurrency), ct);
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetSiteOptionsAsync(string siteId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var store = new GallerySettingsStore(db);
        var saved = await store.GetAsync<Dictionary<string, object?>>($"site.{siteId}.options", null, ct);
        if (saved is { Count: > 0 }) return UnwrapJsonElements(saved);
        return sites.Get(siteId).DefaultOptions;
    }

    public async Task SetSiteOptionsAsync(string siteId, IReadOnlyDictionary<string, object?> options, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await new GallerySettingsStore(db).SetAsync($"site.{siteId}.options", options, ct);
    }

    public async Task<ProxyConfig> GetProxyAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stored = await new GallerySettingsStore(db).GetAsync<ProxyConfig>("network.proxy", null, ct);
        return stored ?? new ProxyConfig();
    }

    public async Task SetProxyAsync(ProxyConfig proxy, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await new GallerySettingsStore(db).SetAsync("network.proxy", proxy, ct);
    }

    public async Task<string> GetCurrentSiteIdAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var v = await new GallerySettingsStore(db).GetAsync<string>("ui.currentSite", null, ct);
        return string.IsNullOrWhiteSpace(v) ? SiteCatalog.TwitterId : v;
    }

    public async Task SetCurrentSiteIdAsync(string siteId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await new GallerySettingsStore(db).SetAsync("ui.currentSite", SiteCatalog.Get(siteId).SiteId, ct);
    }

    // 偏离（最小修正，已记录）：System.Text.Json 把 object? 值反序列化为 JsonElement，
    // brief 逐字实现会让 round-trip 后 (bool)options["videos"] 抛 InvalidCastException
    // （B3-B7 与 BuildDownload 同样按 bool/string 读取），故把 JSON 标量还原为 CLR 原生类型；
    // 其余路径与 brief 逐字一致。
    private static Dictionary<string, object?> UnwrapJsonElements(Dictionary<string, object?> saved)
    {
        foreach (var key in saved.Keys.ToArray())
        {
            if (saved[key] is JsonElement e)
                saved[key] = e.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => e.GetString(),
                    JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
                    JsonValueKind.Null => null,
                    _ => e,
                };
        }
        return saved;
    }
}
