using System.Globalization;
using System.Text.RegularExpressions;
using GGdown.Data;

namespace GGdown.Sites;

public sealed partial class DouyinSiteProvider : ISiteProvider
{
    public const string Id = "douyin";

    [GeneratedRegex(@"^[A-Za-z0-9_-]{8,}$")]
    private static partial Regex SecUserIdRegex();

    [GeneratedRegex(@"^\d{19}$")]
    private static partial Regex WorkIdRegex();

    [GeneratedRegex(@"https?://[^\s，。；！？、【】《》]+", RegexOptions.IgnoreCase)]
    private static partial Regex SharedUrlRegex();

    public string SiteId => Id;
    public string DisplayName => "抖音";
    public IReadOnlyList<ContentKind> SupportedKinds { get; } =
        [ContentKind.UserMedia, ContentKind.Permalink];
    public bool RequiresRefreshToken => false;
    public string InputHint => "用户主页、sec_user_id 或作品链接，如 https://www.douyin.com/user/MS4w...";

    public OptionSchema OptionsSchema { get; } = new(
    [
        new OptionField("original_quality", OptionKind.Boolean, false, "最高质量视频"),
        new OptionField("earliest_date", OptionKind.Text, "", "最早发布日期（yyyy-MM-dd）"),
    ]);

    public IReadOnlyDictionary<string, object?> DefaultOptions { get; } =
        new Dictionary<string, object?> { ["original_quality"] = false, ["earliest_date"] = "" };

    public UserInputParseResult ParseInput(string input)
    {
        var value = (input ?? "").Trim();
        if (value.Length == 0) return new(false, Error: "输入为空");

        if (SecUserIdRegex().IsMatch(value) && !WorkIdRegex().IsMatch(value)
            && !value.Equals("self", StringComparison.OrdinalIgnoreCase))
            return new(true, value, RestId: value);

        var urlText = SharedUrlRegex().Match(value).Value.TrimEnd('"', '\'', ')', ']', '}', '.', ',');
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https"))
            return new(false, Error: $"无法识别的抖音用户或作品链接：{value}");

        var host = url.Host.ToLowerInvariant();
        if (host == "v.douyin.com" && url.AbsolutePath.Trim('/').Length > 0)
            return new(true, Kind: PasteKind.Work,
                DirectUrl: $"https://v.douyin.com{url.AbsolutePath}");
        if (host == "www.iesdouyin.com")
        {
            var shareSegments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (shareSegments.Length >= 2 && shareSegments[0] == "share")
            {
                if (shareSegments[1] == "user" && shareSegments.Length >= 3 && SecUserIdRegex().IsMatch(shareSegments[2]))
                    return new(true, shareSegments[2], RestId: shareSegments[2]);
                if ((shareSegments[1] is "video" or "note" or "slides") && shareSegments.Length >= 3 && WorkIdRegex().IsMatch(shareSegments[2]))
                    return new(true, RestId: shareSegments[2], Kind: PasteKind.Work, DirectUrl: $"https://www.iesdouyin.com/share/{shareSegments[1]}/{shareSegments[2]}");
            }
            return new(false, Error: "请输入抖音用户主页或单条作品链接");
        }
        if (host is not ("www.douyin.com" or "douyin.com"))
            return new(false, Error: "请输入抖音网站的用户或作品链接");

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
            return new(false, Error: "请输入抖音用户主页或单条作品链接");

        var id = segments[1];
        if (segments[0] == "user" && SecUserIdRegex().IsMatch(id) && id != "self")
            return new(true, id, RestId: id);
        if ((segments[0] is "video" or "note" or "slides") && WorkIdRegex().IsMatch(id))
            return new(true, RestId: id, Kind: PasteKind.Work,
                DirectUrl: $"https://www.douyin.com/{segments[0]}/{id}");
        return new(false, Error: "请输入抖音用户主页或单条作品链接");
    }

    public string BuildProfileUrl(string screenName, string? restId = null)
        => $"https://www.douyin.com/user/{restId ?? screenName}";

    public string KindLabel(ContentKind kind) => kind switch
    {
        ContentKind.UserMedia => "发布作品",
        ContentKind.Permalink => "单条作品",
        _ => kind.ToString(),
    };

    public DownloadPlan BuildDownload(ContentKind kind, UserTarget target,
        IReadOnlyDictionary<string, object?> options, DownloadPaths paths)
    {
        string url = kind switch
        {
            ContentKind.UserMedia => BuildProfileUrl(target.ScreenName ?? "", target.RestId
                ?? throw new ArgumentException("抖音用户下载需要 sec_user_id", nameof(target))),
            ContentKind.Permalink => target.DirectUrl
                ?? throw new ArgumentException("单条作品下载需要链接", nameof(target)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        if (kind == ContentKind.Permalink)
        {
            var parsed = ParseInput(url);
            if (!parsed.Ok || parsed.Kind != PasteKind.Work || parsed.DirectUrl is null)
                throw new ArgumentException("无效的抖音作品链接", nameof(target));
            url = parsed.DirectUrl;
        }

        var quality = options.TryGetValue("original_quality", out var selected)
            && selected is bool enabled && enabled;
        DateOnly? siteSince = null;
        if (kind == ContentKind.UserMedia && options.TryGetValue("earliest_date", out var dateValue)
            && dateValue is string dateText && !string.IsNullOrWhiteSpace(dateText))
        {
            if (!DateOnly.TryParseExact(dateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsedDate))
                throw new ArgumentException("最早发布日期须为 yyyy-MM-dd", nameof(options));
            siteSince = parsedDate;
        }
        var selection = kind == ContentKind.UserMedia ? target.ContentSelection : UserContentSelection.All;
        var mediaFilter = selection switch
        {
            UserContentSelection.DouyinVideos => "videos",
            UserContentSelection.DouyinGalleries => "galleries",
            _ => "all",
        };
        var douyinOptions = new Dictionary<string, object?>
        {
            ["cookies"] = paths.CookiesFile,
            ["archive"] = paths.ArchiveFile,
            ["original_quality"] = quality,
            ["media_filter"] = mediaFilter,
            ["earliest_date"] = kind == ContentKind.UserMedia
                ? (target.DownloadSince ?? siteSince)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""
                : "",
        };
        var nested = new Dictionary<string, object?>
        {
            [Id] = douyinOptions,
            ["base-directory"] = target.BaseDirectory ?? "",
        };
        // 短链展开后仍需校验类型，单条下载不能意外拉取整位作者的作品。
        if (kind == ContentKind.Permalink)
            douyinOptions["target_kind"] = "work";
        return new DownloadPlan(Id, [url], target.BaseDirectory ?? "", nested);
    }
}
