using GGdown.Data;

namespace GGdown.Sites;

public sealed record UserContentChoice(UserContentSelection Selection, string Label, string Summary,
    IReadOnlyList<ContentKind> DownloadKinds);

public sealed record UserDownloadFeatures(bool Following, bool Incremental, bool DateFilter, bool ManualAddGuide,
    IReadOnlyList<UserContentChoice> Choices)
{
    public static UserDownloadFeatures Basic { get; } = new(false, false, false, false,
        [new(UserContentSelection.All, "全部媒体", "全部媒体", [ContentKind.UserMedia])]);

    public IReadOnlyList<ContentKind> KindsFor(UserContentSelection selection) =>
        Choices.FirstOrDefault(c => c.Selection == selection)?.DownloadKinds
            ?? throw new ArgumentException("此平台不支持该下载内容选择", nameof(selection));
}

public static class UserDownloadProfiles
{
    public static UserDownloadFeatures Twitter { get; } = UserDownloadFeatures.Basic with { Following = true, Incremental = true };
    public static UserDownloadFeatures Pixiv { get; } = new(false, false, false, false,
    [
        new(UserContentSelection.All, "插画漫画和小说", "插画漫画+小说", [ContentKind.UserMedia, ContentKind.UserNovels]),
        new(UserContentSelection.PixivArtworks, "仅插画漫画", "仅插画漫画", [ContentKind.UserMedia]),
        new(UserContentSelection.PixivNovels, "仅小说", "仅小说", [ContentKind.UserNovels]),
    ]);
    public static UserDownloadFeatures Douyin { get; } = new(false, true, true, true,
    [
        new(UserContentSelection.All, "视频和图集", "视频+图集", [ContentKind.UserMedia]),
        new(UserContentSelection.DouyinVideos, "仅视频", "仅视频", [ContentKind.UserMedia]),
        new(UserContentSelection.DouyinGalleries, "仅图集", "仅图集", [ContentKind.UserMedia]),
    ]);

    // 旧行 VM 构造入口的兼容默认值；正常页面由 provider 显式注入能力。
    public static UserDownloadFeatures For(string siteId) => siteId switch
    {
        TwitterSiteProvider.Id => Twitter, PixivSiteProvider.Id => Pixiv, DouyinSiteProvider.Id => Douyin,
        _ => UserDownloadFeatures.Basic,
    };
}
