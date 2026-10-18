using GGdown.Data;

namespace GGdown.Sites;

public static class SiteSourceLinks
{
    public static string? Build(string siteId, TargetKind kind, string itemId)
    {
        var id = Uri.EscapeDataString(itemId);
        return siteId switch
        {
            TwitterSiteProvider.Id => $"https://x.com/i/status/{id}",
            PixivSiteProvider.Id when kind is TargetKind.UserNovels or TargetKind.AccountNovelBookmarks
                => $"https://www.pixiv.net/novel/show.php?id={id}",
            PixivSiteProvider.Id => $"https://www.pixiv.net/artworks/{id}",
            DouyinSiteProvider.Id => $"https://www.douyin.com/video/{id}",
            _ => null,
        };
    }
}
