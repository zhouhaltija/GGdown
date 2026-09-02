using GalleryGUI.Settings;

namespace GalleryGUI.Sites;

/// <summary>
/// UI 站点目录。Available=true 的才接引擎；其余仅展示「即将支持」。
/// V1 引擎只实现 twitter。
/// </summary>
public sealed record SiteInfo(string SiteId, string DisplayName, bool Available)
{
    public string Label => Available ? DisplayName : $"{DisplayName}（即将支持）";
}

public static class SiteCatalog
{
    public const string TwitterId = "twitter";

    public static IReadOnlyList<SiteInfo> All { get; } =
    [
        new(TwitterId, "X (Twitter)", true),
        new("pixiv", "Pixiv", false),
        new("fanbox", "pixivFANBOX", false),
        new("danbooru", "Danbooru", false),
        new("gelbooru", "Gelbooru", false),
        new("kemono", "Kemono", false),
        new("coomer", "Coomer", false),
        new("fantia", "Fantia", false),
        new("instagram", "Instagram", false),
        new("reddit", "Reddit", false),
        new("tumblr", "Tumblr", false),
        new("bilibili", "哔哩哔哩", false),
        new("weibo", "微博", false),
    ];

    public static SiteInfo Default => All[0];

    public static SiteInfo Get(string? siteId) =>
        All.FirstOrDefault(s => s.SiteId.Equals(siteId, StringComparison.OrdinalIgnoreCase)) ?? Default;
}

public interface ICurrentSite
{
    SiteInfo Current { get; }
    string SiteId { get; }
    bool IsAvailable { get; }
    event Action? Changed;
    Task LoadAsync(CancellationToken ct = default);
    Task SelectAsync(string siteId, CancellationToken ct = default);
}

public sealed class CurrentSite(IAppSettings settings) : ICurrentSite
{
    private SiteInfo _current = SiteCatalog.Default;

    public SiteInfo Current => _current;
    public string SiteId => _current.SiteId;
    public bool IsAvailable => _current.Available;
    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var id = await settings.GetCurrentSiteIdAsync(ct);
        Apply(SiteCatalog.Get(id));
    }

    public async Task SelectAsync(string siteId, CancellationToken ct = default)
    {
        var info = SiteCatalog.Get(siteId);
        if (info.SiteId == _current.SiteId) return;
        Apply(info);
        await settings.SetCurrentSiteIdAsync(info.SiteId, ct);
    }

    private void Apply(SiteInfo info)
    {
        if (info.SiteId == _current.SiteId) return;
        _current = info;
        Changed?.Invoke();
    }
}
