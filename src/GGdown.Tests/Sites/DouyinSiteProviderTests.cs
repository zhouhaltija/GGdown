using GGdown.Sites;

namespace GGdown.Tests.Sites;

public class DouyinSiteProviderTests
{
    private readonly DouyinSiteProvider _site = new();

    [Theory]
    [InlineData("https://www.douyin.com/user/MS4wLjABtest", "MS4wLjABtest")]
    [InlineData("https://www.douyin.com/user/MS4wLjABtest?from_tab_name=main", "MS4wLjABtest")]
    [InlineData("https://www.iesdouyin.com/share/user/MS4wLjABtest", "MS4wLjABtest")]
    public void Parses_user_home_as_sec_user_id(string input, string expected)
    {
        var result = _site.ParseInput(input);
        Assert.True(result.Ok);
        Assert.Equal(PasteKind.User, result.Kind);
        Assert.Equal(expected, result.RestId);
        Assert.Equal(expected, result.ScreenName);
    }

    [Theory]
    [InlineData("https://www.douyin.com/video/1234567890123456789")]
    [InlineData("https://www.douyin.com/note/1234567890123456789?foo=bar")]
    [InlineData("https://www.douyin.com/slides/1234567890123456789")]
    [InlineData("https://www.iesdouyin.com/share/video/1234567890123456789")]
    [InlineData("https://www.iesdouyin.com/share/note/1234567890123456789")]
    [InlineData("https://www.iesdouyin.com/share/slides/1234567890123456789")]
    public void Parses_work_link_without_tracking_query(string input)
    {
        var result = _site.ParseInput(input);
        Assert.True(result.Ok);
        Assert.Equal("Work", result.Kind.ToString());
        Assert.Equal("1234567890123456789", result.RestId);
        Assert.DoesNotContain('?', result.DirectUrl!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://evil.example/video/1234567890123456789")]
    [InlineData("https://www.douyin.com/collection/1234567890123456789")]
    [InlineData("https://www.douyin.com/user/self")]
    [InlineData("https://www.iesdouyin.com/share/user/self")]
    [InlineData("https://www.iesdouyin.com/share/mix/detail/1234567890123456789")]
    public void Rejects_unrelated_or_ambiguous_input(string input)
        => Assert.False(_site.ParseInput(input).Ok);

    [Fact]
    public void Creates_user_and_work_plans_with_quality_option()
    {
        var paths = new DownloadPaths("cookies.txt", "archive.txt");
        var user = _site.BuildDownload(ContentKind.UserMedia,
            new UserTarget(1, "display", "D:/downloads", "MS4wLjABtest"),
            _site.DefaultOptions, paths);
        Assert.Equal(["https://www.douyin.com/user/MS4wLjABtest"], user.Urls);
        Assert.Equal("douyin", user.SiteId);

        var work = _site.BuildDownload(ContentKind.Permalink,
            new UserTarget(null, null, "D:/downloads", DirectUrl: "https://www.douyin.com/video/1234567890123456789"),
            new Dictionary<string, object?> { ["original_quality"] = true }, paths);
        Assert.Equal(["https://www.douyin.com/video/1234567890123456789"], work.Urls);
        var options = (IReadOnlyDictionary<string, object?>)work.Options["douyin"]!;
        Assert.True((bool)options["original_quality"]!);
        Assert.Equal("archive.txt", options["archive"]);
        Assert.Equal("cookies.txt", options["cookies"]);
        Assert.False((bool)_site.DefaultOptions["original_quality"]!);
        Assert.Equal("https://www.douyin.com/user/MS4wLjABtest", _site.BuildProfileUrl("display", "MS4wLjABtest"));
    }

    [Fact]
    public void Catalog_exposes_douyin_with_user_pages()
    {
        Assert.True(SiteCatalog.Get("douyin").Available);
        Assert.Equal(
            new[] { SitePageKey.Users, SitePageKey.Downloads, SitePageKey.History, SitePageKey.SiteSettings },
            SiteUiProfiles.For("douyin"));
        Assert.Equal([ContentKind.UserMedia, ContentKind.Permalink], _site.SupportedKinds);
    }

    [Fact]
    public void Shared_video_text_creates_work_only_plan()
    {
        var input = "0.71 o@q.eb 02/16 lcn:/ :5pm 天赋不会给你刀刻般的肌肉💪🐱 # 无敌小猫拳 # 哈基米  https://v.douyin.com/uJS3Tm5L5iI/ 复制此链接，打开Dou音搜索，直接观看视频！";
        var parsed = _site.ParseInput(input);
        Assert.True(parsed.Ok);
        Assert.Equal(PasteKind.Work, parsed.Kind);
        Assert.Equal("https://v.douyin.com/uJS3Tm5L5iI/", parsed.DirectUrl);

        var plan = _site.BuildDownload(ContentKind.Permalink,
            new UserTarget(null, null, "D:/downloads", DirectUrl: parsed.DirectUrl),
            new Dictionary<string, object?> { ["earliest_date"] = "2026-10-06" },
            new DownloadPaths("cookies.txt", "archive.txt"));
        var options = (IReadOnlyDictionary<string, object?>)plan.Options["douyin"]!;
        Assert.Equal("work", options["target_kind"]);
        Assert.Equal("", options["earliest_date"]);
    }

    [Fact]
    public void Permalink_plan_rejects_user_home()
    {
        Assert.Throws<ArgumentException>(() => _site.BuildDownload(ContentKind.Permalink,
            new UserTarget(null, null, "D:/downloads", DirectUrl: "https://www.douyin.com/user/MS4wLjABtest"),
            _site.DefaultOptions, new DownloadPaths("cookies.txt", "archive.txt")));
    }

    [Fact]
    public void User_plan_applies_content_selection_and_user_date_over_site_date()
    {
        var options = new Dictionary<string, object?> { ["earliest_date"] = "2024-01-01" };
        var paths = new DownloadPaths("cookies.txt", "archive.txt");
        var user = _site.BuildDownload(ContentKind.UserMedia,
            new UserTarget(1, "creator", "D:/downloads", "MS4wLjABtest",
                ContentSelection: GGdown.Data.UserContentSelection.DouyinGalleries,
                DownloadSince: new DateOnly(2025, 2, 3)), options, paths);
        var siteOnly = _site.BuildDownload(ContentKind.UserMedia,
            new UserTarget(2, "creator", "D:/downloads", "MS4wLjABtest"), options, paths);

        var userOptions = (IReadOnlyDictionary<string, object?>)user.Options["douyin"]!;
        var siteOnlyOptions = (IReadOnlyDictionary<string, object?>)siteOnly.Options["douyin"]!;
        Assert.Equal("galleries", userOptions["media_filter"]);
        Assert.Equal("2025-02-03", userOptions["earliest_date"]);
        Assert.Equal("all", siteOnlyOptions["media_filter"]);
        Assert.Equal("2024-01-01", siteOnlyOptions["earliest_date"]);
    }
}
