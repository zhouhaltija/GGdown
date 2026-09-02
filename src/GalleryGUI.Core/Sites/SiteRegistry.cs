namespace GalleryGUI.Sites;

public sealed class SiteRegistry
{
    private readonly Dictionary<string, ISiteProvider> _byId;

    public SiteRegistry(IEnumerable<ISiteProvider> providers) =>
        _byId = providers.ToDictionary(p => p.SiteId, StringComparer.OrdinalIgnoreCase);

    public ISiteProvider Get(string siteId) =>
        _byId.TryGetValue(siteId, out var p)
            ? p
            : throw new KeyNotFoundException($"未知站点：{siteId}");

    public bool IsRegistered(string siteId) => _byId.ContainsKey(siteId);

    public IReadOnlyList<ISiteProvider> All => [.. _byId.Values];
}
