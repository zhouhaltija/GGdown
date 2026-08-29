using System.Text.RegularExpressions;

namespace GalleryGUI.Sites;

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

    public string SiteId => Id;
    public string DisplayName => "X (Twitter)";
    public IReadOnlyList<ContentKind> SupportedKinds { get; } =
        [ContentKind.UserMedia, ContentKind.AccountLikes, ContentKind.AccountBookmarks];

    public OptionSchema OptionsSchema { get; } = new(
    [
        new OptionField("videos", OptionKind.Boolean, true, "同时下载视频和动图"),
        new OptionField("retweets", OptionKind.Boolean, false, "包含转推"),
        new OptionField("quoted", OptionKind.Boolean, false, "包含引用推文"),
        new OptionField("replies", OptionKind.Boolean, false, "包含回复"),
        new OptionField("filename", OptionKind.Text, "{tweet_id}_{author[name]}_{num}.{extension}", "文件命名模板"),
        new OptionField("sleep", OptionKind.Text, "1", "请求间隔（秒）"),
    ]);

    public IReadOnlyDictionary<string, object?> DefaultOptions { get; } =
        new Dictionary<string, object?>
        {
            ["videos"] = true, ["retweets"] = false, ["quoted"] = false, ["replies"] = false,
            ["filename"] = "{tweet_id}_{author[name]}_{num}.{extension}", ["sleep"] = "1",
        };

    public UserInputParseResult ParseInput(string input)
    {
        var v = (input ?? "").Trim();
        if (v.Length == 0) return new(false, Error: "输入为空");
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
        return new(false, Error: $"无法识别的用户名或链接：{v}");
    }

    public string BuildProfileUrl(string screenName) => $"https://x.com/{screenName}";

    public DownloadPlan BuildDownload(ContentKind kind, UserTarget target,
        IReadOnlyDictionary<string, object?> options, DownloadPaths paths)
    {
        var url = kind switch
        {
            ContentKind.UserMedia => $"https://x.com/{target.ScreenName}/media",
            ContentKind.AccountLikes => $"https://x.com/{target.ScreenName}/likes",
            ContentKind.AccountBookmarks => "https://x.com/i/bookmarks",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        bool B(string k, bool d) => options.TryGetValue(k, out var v) && v is bool b ? b : d;
        string S(string k, string d) => options.TryGetValue(k, out var v) && v is string s ? s : d;

        var extractorOptions = new Dictionary<string, object?>
        {
            ["cookies"] = paths.CookiesFile,
            ["videos"] = B("videos", true),
            ["retweets"] = B("retweets", false),
            ["quoted"] = B("quoted", false),
            ["replies"] = B("replies", false),
            ["filename"] = S("filename", "{tweet_id}_{author[name]}_{num}.{extension}"),
            ["sleep-request"] = S("sleep", "1"),
        };
        var nested = new Dictionary<string, object?>
        {
            ["extractor"] = new Dictionary<string, object?> { [Id] = extractorOptions },
            ["download-archive"] = paths.ArchiveFile,
            ["base-directory"] = target.BaseDirectory ?? "",
        };
        return new DownloadPlan(Id, [url], target.BaseDirectory ?? "", nested);
    }
}
