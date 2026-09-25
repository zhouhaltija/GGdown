namespace GGdown.Sites;

public enum SitePageKey { Users, Downloads, History, SiteSettings }

/// <summary>全局设置页组（Task 9）：栏底 ⚙ 进入后内容区顶部的五个标签。</summary>
public enum GlobalSettingsPageKey { General, Network, Engine, Interface, About }

/// <summary>各平台的功能页组（spec §2.2）：仅 Available 站点的页组会被 UI 实际渲染。</summary>
public static class SiteUiProfiles
{
    private static readonly IReadOnlyList<SitePageKey> All =
        [SitePageKey.Users, SitePageKey.Downloads, SitePageKey.History, SitePageKey.SiteSettings];
    private static readonly IReadOnlyList<SitePageKey> NoUsers =
        [SitePageKey.Downloads, SitePageKey.History, SitePageKey.SiteSettings];

    // 无账号/关注概念的图库板类站点只有 下载/历史/站点设置 三页（占位声明，接入时以此为准）
    private static readonly Dictionary<string, IReadOnlyList<SitePageKey>> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twitter"] = All,
        ["pixiv"] = All,
        ["danbooru"] = NoUsers,
        ["gelbooru"] = NoUsers,
        ["kemono"] = NoUsers,
        ["coomer"] = NoUsers,
    };

    public static IReadOnlyList<SitePageKey> For(string siteId) =>
        Map.GetValueOrDefault(siteId, All);
}
