using System.Text.Json;
using GGdown.Data;
using GGdown.Services;
using GGdown.Sites;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Settings;

public interface IAppSettings
{
    // —— 全局库（ggdown.db，仅应用级设置）——
    Task<string> GetDownloadDirectoryAsync(CancellationToken ct = default);
    Task SetDownloadDirectoryAsync(string directory, CancellationToken ct = default);
    Task<int> GetConcurrencyAsync(CancellationToken ct = default);
    Task SetConcurrencyAsync(int concurrency, CancellationToken ct = default);
    Task<ProxyConfig> GetProxyAsync(CancellationToken ct = default);
    Task SetProxyAsync(ProxyConfig proxy, CancellationToken ct = default);
    Task<string> GetCurrentSiteIdAsync(CancellationToken ct = default);
    Task SetCurrentSiteIdAsync(string siteId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetVisibleSitesAsync(CancellationToken ct = default);
    Task SetVisibleSitesAsync(IReadOnlyList<string> siteIds, CancellationToken ct = default);
    Task<string> GetSiteLastPageAsync(string siteId, CancellationToken ct = default);
    Task SetSiteLastPageAsync(string siteId, string pageKey, CancellationToken ct = default);
    // —— 平台库（sites\<id>.db 的 SiteSettings 表）——
    Task<IReadOnlyDictionary<string, object?>> GetSiteOptionsAsync(string siteId, CancellationToken ct = default);
    Task SetSiteOptionsAsync(string siteId, IReadOnlyDictionary<string, object?> options, CancellationToken ct = default);
}

/// <summary>
/// Task 3 起拆双库：应用级设置走全局库；站点选项走平台库 SiteSettings 表（原全局键 site.&lt;id&gt;.options
/// 由存量拆分迁移搬入）。captive dependency 裁定不变：注入的两个 factory 均 singleton 安全
/// （ISiteDbContextFactory 进程内持有建库去重表，无 scoped 依赖）。
/// </summary>
public sealed class AppSettings(
    IDbContextFactory<GGdownGlobalDbContext> globalFactory,
    SiteRegistry sites,
    ISiteDbContextFactory siteFactory) : IAppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string DefaultDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GGdown");

    // —— 全局键：每个方法内建上下文（factory 单例、options 单例，无 captive）——

    public async Task<string> GetDownloadDirectoryAsync(CancellationToken ct = default)
    {
        var v = await GetGlobalAsync<string>("download.directory", null, ct);
        return string.IsNullOrWhiteSpace(v) ? DefaultDownloadDirectory : v;
    }

    public async Task SetDownloadDirectoryAsync(string directory, CancellationToken ct = default) =>
        await SetGlobalAsync("download.directory", directory, ct);

    public async Task<int> GetConcurrencyAsync(CancellationToken ct = default) =>
        Math.Max(1, await GetGlobalAsync<int?>("download.concurrency", 1, ct) ?? 1);

    public async Task SetConcurrencyAsync(int concurrency, CancellationToken ct = default) =>
        await SetGlobalAsync("download.concurrency", Math.Max(1, concurrency), ct);

    public async Task<ProxyConfig> GetProxyAsync(CancellationToken ct = default) =>
        await GetGlobalAsync<ProxyConfig>("network.proxy", null, ct) ?? new ProxyConfig();

    public async Task SetProxyAsync(ProxyConfig proxy, CancellationToken ct = default) =>
        await SetGlobalAsync("network.proxy", proxy, ct);

    public async Task<string> GetCurrentSiteIdAsync(CancellationToken ct = default) =>
        await GetGlobalAsync<string>("ui.currentSite", null, ct) is { Length: > 0 } v ? v : SiteCatalog.TwitterId;

    public Task SetCurrentSiteIdAsync(string siteId, CancellationToken ct = default) =>
        SetGlobalAsync("ui.currentSite", SiteCatalog.Get(siteId).SiteId, ct);

    public async Task<IReadOnlyList<string>> GetVisibleSitesAsync(CancellationToken ct = default)
    {
        var raw = await GetGlobalAsync<string>("ui.visibleSites", null, ct);
        if (string.IsNullOrWhiteSpace(raw)) return DefaultVisible;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(raw, JsonOptions) ?? [];
            // 空数组视为未配置（回退默认），避免「栏里一个平台都没有」的空白态
            return parsed.Count == 0 ? DefaultVisible : [.. parsed];
        }
        catch (JsonException) { return DefaultVisible; }
    }

    public Task SetVisibleSitesAsync(IReadOnlyList<string> siteIds, CancellationToken ct = default) =>
        SetGlobalAsync("ui.visibleSites", JsonSerializer.Serialize(siteIds, JsonOptions), ct);

    public async Task<string> GetSiteLastPageAsync(string siteId, CancellationToken ct = default) =>
        await GetGlobalAsync<string>($"ui.site.{SiteCatalog.Get(siteId).SiteId}.lastPage", "users", ct) ?? "users";

    public Task SetSiteLastPageAsync(string siteId, string pageKey, CancellationToken ct = default) =>
        SetGlobalAsync($"ui.site.{SiteCatalog.Get(siteId).SiteId}.lastPage", pageKey, ct);

    private static IReadOnlyList<string> DefaultVisible =>
        [.. SiteCatalog.All.Where(s => s.Available).Select(s => s.SiteId)];

    // —— 站点选项：平台库 SiteSettings 行集（键=字段 Key）——

    public async Task<IReadOnlyDictionary<string, object?>> GetSiteOptionsAsync(string siteId, CancellationToken ct = default)
    {
        var id = SiteCatalog.Get(siteId).SiteId;
        await using var db = await siteFactory.CreateAsync(id, ct);
        var rows = await db.SiteSettings.AsNoTracking().Where(s => s.SiteId == id).ToListAsync(ct);
        if (rows.Count == 0) return sites.Get(id).DefaultOptions;
        var dict = new Dictionary<string, object?>();
        foreach (var row in rows)
            dict[row.Key] = UnwrapJson(row.Value);
        return dict;
    }

    public async Task SetSiteOptionsAsync(string siteId, IReadOnlyDictionary<string, object?> options, CancellationToken ct = default)
    {
        var id = SiteCatalog.Get(siteId).SiteId;
        await using var db = await siteFactory.CreateAsync(id, ct);
        // 语义对齐旧整键存储：保存的选项集整体替换（缺省键回退 DefaultOptions）
        var existing = await db.SiteSettings.Where(s => s.SiteId == id).ToListAsync(ct);
        db.SiteSettings.RemoveRange(existing);
        foreach (var (key, value) in options)
            db.SiteSettings.Add(new SiteSettingEntry
            {
                SiteId = id,
                Key = key,
                // 值一律 JSON 序列化（字符串带引号）——裸存会让 "3" 回读成数字、丢失 string 语义
                Value = JsonSerializer.Serialize(value, JsonOptions),
            });
        await db.SaveChangesAsync(ct);
    }

    // 偏离（最小修正，沿用单库版注释）：System.Text.Json 把 object? 值反序列化为 JsonElement，
    // round-trip 后 (bool)options["videos"] 会抛 InvalidCastException，故把 JSON 标量还原为 CLR 原生类型。
    private static object? UnwrapJson(string? value)
    {
        if (value is null) return null;
        try
        {
            var e = JsonSerializer.Deserialize<JsonElement>(value, JsonOptions);
            return e.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
                JsonValueKind.Null => null,
                _ => e,
            };
        }
        catch (JsonException) { return value; } // 裸字符串（非合法 JSON）按字符串处理
    }

    private async Task<T?> GetGlobalAsync<T>(string key, T? fallback, CancellationToken ct = default)
    {
        await using var db = await globalFactory.CreateDbContextAsync(ct);
        return await new GGdownSettingsStore<GGdownGlobalDbContext>(db).GetAsync(key, fallback, ct);
    }

    private async Task SetGlobalAsync<T>(string key, T value, CancellationToken ct = default)
    {
        await using var db = await globalFactory.CreateDbContextAsync(ct);
        await new GGdownSettingsStore<GGdownGlobalDbContext>(db).SetAsync(key, value, ct);
    }
}
