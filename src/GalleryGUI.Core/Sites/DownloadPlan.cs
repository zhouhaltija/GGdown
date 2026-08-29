namespace GalleryGUI.Sites;

public sealed record DownloadPlan(
    string SiteId, IReadOnlyList<string> Urls, string BaseDirectory,
    IReadOnlyDictionary<string, object?> Options);
