using System.Text.Json;
using GGdown.Data;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Services;

public interface ISettingsStore
{
    // 控制器裁定：brief 的 GetAsync 第二参数为回退值（缺失键返回 fallback 而非 default）
    Task<T?> GetAsync<T>(string key, T? fallback = default, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
}

/// <summary>
/// 设置存取（Task 3 起泛型化）：全局库（GGdownGlobalDbContext）与旧库（GGdownDbContext，供存量拆分迁移读）
/// 都映射 SettingEntry，经 Set&lt;SettingEntry&gt;() 访问，语义与原单库版一致。
/// </summary>
public sealed class GGdownSettingsStore<TCtx>(TCtx db) : ISettingsStore where TCtx : DbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string key, T? fallback = default, CancellationToken ct = default)
    {
        var entry = await db.Set<SettingEntry>().AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct);
        if (entry?.Value is null) return fallback;
        if (typeof(T) == typeof(string)) return (T)(object)entry.Value;
        return JsonSerializer.Deserialize<T>(entry.Value, JsonOptions);
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var json = typeof(T) == typeof(string) ? value as string : JsonSerializer.Serialize(value, JsonOptions);
        var entry = await db.Set<SettingEntry>().SingleOrDefaultAsync(s => s.Key == key, ct);
        if (entry is null) db.Set<SettingEntry>().Add(new SettingEntry { Key = key, Value = json });
        else entry.Value = json;
        await db.SaveChangesAsync(ct);
    }
}
