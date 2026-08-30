using System.Text.Json;
using GalleryGUI.Services;
using GalleryGUI.Sites;

namespace GalleryGUI.Settings;

public interface IAppSettings
{
    Task<string> GetDownloadDirectoryAsync(CancellationToken ct = default);
    Task SetDownloadDirectoryAsync(string directory, CancellationToken ct = default);
    Task<int> GetConcurrencyAsync(CancellationToken ct = default);
    Task SetConcurrencyAsync(int concurrency, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object?>> GetSiteOptionsAsync(string siteId, CancellationToken ct = default);
    Task SetSiteOptionsAsync(string siteId, IReadOnlyDictionary<string, object?> options, CancellationToken ct = default);
}

public sealed class AppSettings(ISettingsStore store, SiteRegistry sites) : IAppSettings
{
    public static string DefaultDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GalleryGUI");

    public async Task<string> GetDownloadDirectoryAsync(CancellationToken ct = default)
    {
        var v = await store.GetAsync<string>("download.directory", null, ct);
        return string.IsNullOrWhiteSpace(v) ? DefaultDownloadDirectory : v;
    }

    public Task SetDownloadDirectoryAsync(string directory, CancellationToken ct = default) =>
        store.SetAsync("download.directory", directory, ct);

    public async Task<int> GetConcurrencyAsync(CancellationToken ct = default)
    {
        var v = await store.GetAsync("download.concurrency", 1, ct);
        return Math.Max(1, v);
    }

    public Task SetConcurrencyAsync(int concurrency, CancellationToken ct = default) =>
        store.SetAsync("download.concurrency", Math.Max(1, concurrency), ct);

    public async Task<IReadOnlyDictionary<string, object?>> GetSiteOptionsAsync(string siteId, CancellationToken ct = default)
    {
        var saved = await store.GetAsync<Dictionary<string, object?>>($"site.{siteId}.options", null, ct);
        if (saved is { Count: > 0 }) return UnwrapJsonElements(saved);
        return sites.Get(siteId).DefaultOptions;
    }

    public Task SetSiteOptionsAsync(string siteId, IReadOnlyDictionary<string, object?> options, CancellationToken ct = default) =>
        store.SetAsync($"site.{siteId}.options", options, ct);

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
