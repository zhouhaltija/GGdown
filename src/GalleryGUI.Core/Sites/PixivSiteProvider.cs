using System.Text.RegularExpressions;

namespace GalleryGUI.Sites;

public sealed partial class PixivSiteProvider : ISiteProvider
{
    public const string Id = "pixiv";

    [GeneratedRegex(
        @"^(?:https?://)?(?:www\.|touch\.)?ph?ixiv\.net/(?:en/)?users/(?<id>\d+)(?:[/?#].*)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(
        @"^(?:https?://)?(?:www\.|touch\.)?ph?ixiv\.net/member\.php\?id=(?<id>\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex MemberRegex();

    [GeneratedRegex(@"^(?<id>\d+)$")]
    private static partial Regex IdRegex();

    public string SiteId => Id;
    public string DisplayName => "Pixiv";
    public bool RequiresRefreshToken => true;
    public string InputHint => "用户 ID 或主页链接，如 12345 或 https://www.pixiv.net/users/12345";

    public IReadOnlyList<ContentKind> SupportedKinds { get; } =
        [ContentKind.UserMedia, ContentKind.UserNovels, ContentKind.AccountBookmarks, ContentKind.AccountNovelBookmarks];

    public OptionSchema OptionsSchema { get; } = new(
    [
        new OptionField("download_artworks", OptionKind.Boolean, true, "下载插画和漫画"),
        new OptionField("download_novels", OptionKind.Boolean, true, "下载小说"),
        new OptionField("ugoira", OptionKind.Boolean, true, "下载动图（ugoira）"),
        new OptionField("novel_covers", OptionKind.Boolean, false, "同时下载小说封面"),
        new OptionField("novel_embeds", OptionKind.Boolean, false, "同时下载小说内嵌图"),
        new OptionField("filename", OptionKind.Text, "{id}_p{num}.{extension}", "文件命名模板"),
        new OptionField("sleep", OptionKind.Text, "1", "请求间隔（秒）"),
    ]);

    public IReadOnlyDictionary<string, object?> DefaultOptions { get; } =
        new Dictionary<string, object?>
        {
            ["download_artworks"] = true,
            ["download_novels"] = true,
            ["ugoira"] = true,
            ["novel_covers"] = false,
            ["novel_embeds"] = false,
            ["filename"] = "{id}_p{num}.{extension}",
            ["sleep"] = "1",
        };

    public UserInputParseResult ParseInput(string input)
    {
        var v = (input ?? "").Trim();
        if (v.Length == 0) return new(false, Error: "输入为空");
        foreach (var re in new[] { UrlRegex(), MemberRegex(), IdRegex() })
        {
            var m = re.Match(v);
            if (!m.Success) continue;
            var id = m.Groups["id"].Value;
            return new(true, id, RestId: id);
        }
        return new(false, Error: $"无法识别的 Pixiv 用户 ID 或链接：{v}");
    }

    public string KindLabel(ContentKind kind) => kind switch
    {
        ContentKind.UserMedia => "插画漫画",
        ContentKind.UserNovels => "小说",
        ContentKind.AccountBookmarks => "收藏插画",
        ContentKind.AccountNovelBookmarks => "收藏小说",
        _ => kind.ToString(),
    };

    public string BuildProfileUrl(string screenName, string? restId = null)
        => $"https://www.pixiv.net/users/{restId ?? screenName}";

    public DownloadPlan BuildDownload(ContentKind kind, UserTarget target,
        IReadOnlyDictionary<string, object?> options, DownloadPaths paths)
    {
        var id = target.RestId ?? target.ScreenName
            ?? throw new ArgumentException("Pixiv 下载需要数字用户 ID", nameof(target));
        var urls = kind switch
        {
            ContentKind.UserMedia => (IReadOnlyList<string>)[$"https://www.pixiv.net/users/{id}/artworks"],
            ContentKind.UserNovels => [$"https://www.pixiv.net/users/{id}/novels"],
            ContentKind.AccountBookmarks =>
            [
                $"https://www.pixiv.net/users/{id}/bookmarks/artworks",
                $"https://www.pixiv.net/users/{id}/bookmarks/artworks?rest=hide",
            ],
            ContentKind.AccountNovelBookmarks =>
            [
                $"https://www.pixiv.net/users/{id}/bookmarks/novels",
                $"https://www.pixiv.net/users/{id}/bookmarks/novels?rest=hide",
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        bool B(string k, bool d) => options.TryGetValue(k, out var v) && v is bool b ? b : d;
        string S(string k, string d) => options.TryGetValue(k, out var v) && v is string s ? s : d;
        var token = ReadRefreshToken(paths.CookiesFile);
        var subdir = kind is ContentKind.UserNovels or ContentKind.AccountNovelBookmarks ? "novels" : "artworks";
        var directory = new[] { "{user[id]} {user[account]}", subdir };

        var pixiv = new Dictionary<string, object?>
        {
            ["cookies"] = paths.CookiesFile,
            ["archive"] = paths.ArchiveFile,
            ["ugoira"] = B("ugoira", true),
            ["filename"] = S("filename", "{id}_p{num}.{extension}"),
            ["sleep-request"] = S("sleep", "1"),
            ["directory"] = directory,
        };
        var novel = new Dictionary<string, object?>
        {
            ["cookies"] = paths.CookiesFile,
            ["archive"] = paths.ArchiveFile,
            ["covers"] = B("novel_covers", false),
            ["embeds"] = B("novel_embeds", false),
            ["filename"] = S("filename", "{id}_p{num}.{extension}"),
            ["sleep-request"] = S("sleep", "1"),
            ["directory"] = directory,
        };
        if (!string.IsNullOrEmpty(token))
        {
            pixiv["refresh-token"] = token;
            novel["refresh-token"] = token;
        }

        var nested = new Dictionary<string, object?>
        {
            ["extractor"] = new Dictionary<string, object?>
            {
                [Id] = pixiv,
                ["pixiv-novel"] = novel,
            },
            ["base-directory"] = target.BaseDirectory ?? "",
        };
        return new DownloadPlan(Id, urls, target.BaseDirectory ?? "", nested);
    }

    private static string? ReadRefreshToken(string cookiesFile)
    {
        var dir = Path.GetDirectoryName(cookiesFile);
        if (string.IsNullOrEmpty(dir)) return null;
        var path = Path.Combine(dir, "refresh-token.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
}
