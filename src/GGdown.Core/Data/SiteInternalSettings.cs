namespace GGdown.Data;

/// <summary>
/// 平台库 <c>SiteSettings</c> 中由应用内部维护的键，不属于用户可编辑的站点选项。
/// 站点选项保存会整体替换该表的用户键，因此内部键必须能被识别并跳过，
/// 否则统计校正标记会被连带删除，导致下次启动重新做全量校正。
/// </summary>
public static class SiteInternalSettings
{
    /// <summary>内部键统一前缀；站点选项键不使用该前缀。</summary>
    public const string Prefix = "ggdown.";

    /// <summary>统计投影版本；值为 <c>1</c> 表示该平台库已完成一次全量校正。</summary>
    public const string StatsProjectionVersionKey = Prefix + "stats-projection.version";

    public static bool IsInternal(string key) =>
        key.StartsWith(Prefix, StringComparison.Ordinal);
}
