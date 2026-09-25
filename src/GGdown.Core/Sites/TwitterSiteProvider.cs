using System.Text.RegularExpressions;

namespace GGdown.Sites;

public sealed partial class TwitterSiteProvider : ISiteProvider
{
    public const string Id = "twitter";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "home", "explore", "notifications", "messages", "settings", "search",
        "intent", "hashtag", "share", "compose", "bookmarks",
    };

    [GeneratedRegex(
        @"^(?:https?://)?(?:www\.|mobile\.)?(?:twitter|x)\.com/@?(?<name>[A-Za-z0-9_]{1,15})(?:[/?#].*)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^@?(?<name>[A-Za-z0-9_]{1,15})$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(
        @"^(?:https?://)?(?:www\.|mobile\.)?(?:twitter|x)\.com/(?:@?(?<name>[A-Za-z0-9_]{1,15})|i/web)/status/(?<id>\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex TweetRegex();

    [GeneratedRegex(
        @"^(?:https?://)?(?:www\.|mobile\.)?(?:twitter|x)\.com/i/lists/(?<id>\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ListRegex();

    [GeneratedRegex(
        @"^(?:https?://)?(?:www\.|mobile\.)?(?:twitter|x)\.com/search/?\?(?:[^#]*?&)?q=(?<q>[^&#]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SearchUrlRegex();

    [GeneratedRegex(
        @"^(?:https?://)?(?:www\.|mobile\.)?(?:twitter|x)\.com/hashtag/(?<tag>[^/?#]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex HashtagUrlRegex();

    [GeneratedRegex(@"^#(?<tag>[\w]+)$")]
    private static partial Regex HashtagRegex();

    public string SiteId => Id;
    public string DisplayName => "X (Twitter)";
    public IReadOnlyList<ContentKind> SupportedKinds { get; } =
    [
        ContentKind.UserMedia, ContentKind.AccountLikes, ContentKind.AccountBookmarks,
        ContentKind.UserHighlights, ContentKind.Permalink, ContentKind.Search,
    ];

    public OptionSchema OptionsSchema { get; } = new(
    [
        new OptionField("media", OptionKind.Choice, "all", "下载内容", ["all", "images", "videos"]),
        new OptionField("size", OptionKind.Choice, "orig", "图片质量", ["orig", "large", "medium", "small"]),
        new OptionField("videos", OptionKind.Boolean, true, "同时下载视频和动图（当下载内容为全部时）"),
        new OptionField("conversations", OptionKind.Boolean, true, "粘贴推文时下载整串对话"),
        new OptionField("retweets", OptionKind.Boolean, false, "包含转推"),
        new OptionField("quoted", OptionKind.Boolean, false, "包含引用推文"),
        new OptionField("replies", OptionKind.Boolean, false, "包含回复"),
        new OptionField("filename", OptionKind.Text, "{tweet_id}_{author[name]}_{num}.{extension}", "文件命名模板"),
        new OptionField("sleep", OptionKind.Text, "1", "请求间隔（秒）"),
    ]);

    public IReadOnlyDictionary<string, object?> DefaultOptions { get; } =
        new Dictionary<string, object?>
        {
            ["media"] = "all", ["size"] = "orig", ["videos"] = true, ["conversations"] = true,
            ["retweets"] = false, ["quoted"] = false, ["replies"] = false,
            ["filename"] = "{tweet_id}_{author[name]}_{num}.{extension}", ["sleep"] = "1",
        };

    public UserInputParseResult ParseInput(string input)
    {
        var v = (input ?? "").Trim();
        if (v.Length == 0) return new(false, Error: "输入为空");

        var tweet = TweetRegex().Match(v);
        if (tweet.Success)
        {
            var name = tweet.Groups["name"].Success ? tweet.Groups["name"].Value : "i";
            var id = tweet.Groups["id"].Value;
            var url = name is "i" or "web"
                ? $"https://x.com/i/web/status/{id}"
                : $"https://x.com/{name}/status/{id}";
            return new(true, name, RestId: id, Kind: PasteKind.Tweet, DirectUrl: url);
        }

        var list = ListRegex().Match(v);
        if (list.Success)
        {
            var id = list.Groups["id"].Value;
            return new(true, RestId: id, Kind: PasteKind.List, DirectUrl: $"https://x.com/i/lists/{id}");
        }

        var searchUrl = SearchUrlRegex().Match(v);
        if (searchUrl.Success)
        {
            var q = Uri.UnescapeDataString(searchUrl.Groups["q"].Value.Replace("+", " "));
            return new(true, Kind: PasteKind.Search, DirectUrl: SearchUrl(q));
        }

        var hashtagUrl = HashtagUrlRegex().Match(v);
        if (hashtagUrl.Success)
        {
            var tag = Uri.UnescapeDataString(hashtagUrl.Groups["tag"].Value);
            return new(true, Kind: PasteKind.Search, DirectUrl: $"https://x.com/hashtag/{tag}");
        }

        var hashtag = HashtagRegex().Match(v);
        if (hashtag.Success)
            return new(true, Kind: PasteKind.Search, DirectUrl: $"https://x.com/hashtag/{hashtag.Groups["tag"].Value}");

        if (LooksLikeSearch(v))
            return new(true, Kind: PasteKind.Search, DirectUrl: SearchUrl(v));

        var m = UrlRegex().Match(v);
        if (m.Success)
        {
            var name = m.Groups["name"].Value;
            return Reserved.Contains(name)
                ? new(false, Error: $"'{name}' 不是用户主页链接")
                : new(true, name);
        }
        m = NameRegex().Match(v);
        if (m.Success) return new(true, m.Groups["name"].Value);
        return new(false, Error: $"无法识别的用户名、链接或搜索：{v}");
    }

    private static string SearchUrl(string query) =>
        "https://x.com/search?q=" + Uri.EscapeDataString(query);

    private static bool LooksLikeSearch(string v) =>
        v.Contains("from:", StringComparison.OrdinalIgnoreCase)
        || v.Contains("filter:", StringComparison.OrdinalIgnoreCase)
        || v.Contains("min_faves:", StringComparison.OrdinalIgnoreCase)
        || v.Contains("since:", StringComparison.OrdinalIgnoreCase)
        || v.Contains("until:", StringComparison.OrdinalIgnoreCase);

    public bool RequiresRefreshToken => false;
    public string InputHint => "用户名、主页、推文、列表或搜索，如 elonmusk / https://x.com/elonmusk/status/123";

    public string KindLabel(ContentKind kind) => kind switch
    {
        ContentKind.AccountLikes => "账号喜欢",
        ContentKind.AccountBookmarks => "账号书签",
        ContentKind.UserHighlights => "高光",
        ContentKind.Permalink => "链接",
        ContentKind.Search => "搜索",
        _ => "用户媒体",
    };

    public string BuildProfileUrl(string screenName, string? restId = null) => $"https://x.com/{screenName}";

    public DownloadPlan BuildDownload(ContentKind kind, UserTarget target,
        IReadOnlyDictionary<string, object?> options, DownloadPaths paths)
    {
        var url = kind switch
        {
            ContentKind.UserMedia => $"https://x.com/{target.ScreenName}/media",
            ContentKind.AccountLikes => $"https://x.com/{target.ScreenName}/likes",
            ContentKind.AccountBookmarks => "https://x.com/i/bookmarks",
            ContentKind.UserHighlights => $"https://x.com/{target.ScreenName}/highlights",
            ContentKind.Permalink or ContentKind.Search => target.DirectUrl
                ?? throw new ArgumentException("Permalink/Search 需要 DirectUrl", nameof(target)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        bool B(string k, bool d) => options.TryGetValue(k, out var v) && v is bool b ? b : d;
        string S(string k, string d) => options.TryGetValue(k, out var v) && v is string s ? s : d;

        var media = S("media", "all");
        var videos = media switch
        {
            "images" => false,
            "videos" => true,
            _ => B("videos", true),
        };
        var extractorOptions = new Dictionary<string, object?>
        {
            ["cookies"] = paths.CookiesFile,
            ["archive"] = paths.ArchiveFile,
            ["videos"] = videos,
            ["size"] = S("size", "orig"),
            ["conversations"] = B("conversations", true),
            ["retweets"] = B("retweets", false),
            ["quoted"] = B("quoted", false),
            ["replies"] = B("replies", false),
            ["filename"] = S("filename", "{tweet_id}_{author[name]}_{num}.{extension}"),
            ["sleep-request"] = S("sleep", "1"),
        };
        if (media == "videos")
            extractorOptions["image-filter"] = "extension in ('mp4','webm','mkv','m4v')";
        var nested = new Dictionary<string, object?>
        {
            ["extractor"] = new Dictionary<string, object?> { [Id] = extractorOptions },
            ["base-directory"] = target.BaseDirectory ?? "",
        };
        return new DownloadPlan(Id, [url], target.BaseDirectory ?? "", nested);
    }
}
