using System.Text.Json;
using GalleryGUI.Data;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Services;

public interface ISettingsStore
{
    // 控制器裁定：brief 的 GetAsync 第二参数为回退值（缺失键返回 fallback 而非 default）
    Task<T?> GetAsync<T>(string key, T? fallback = default, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
}

public sealed class GallerySettingsStore(GalleryDbContext db) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string key, T? fallback = default, CancellationToken ct = default)
    {
        var entry = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct);
        if (entry?.Value is null) return fallback;
        if (typeof(T) == typeof(string)) return (T)(object)entry.Value;
        return JsonSerializer.Deserialize<T>(entry.Value, JsonOptions);
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var json = typeof(T) == typeof(string) ? value as string : JsonSerializer.Serialize(value, JsonOptions);
        var entry = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (entry is null) db.Settings.Add(new SettingEntry { Key = key, Value = json });
        else entry.Value = json;
        await db.SaveChangesAsync(ct);
    }
}
