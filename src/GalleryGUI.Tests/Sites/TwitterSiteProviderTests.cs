using GalleryGUI.Sites;

namespace GalleryGUI.Tests.Sites;

public class TwitterSiteProviderTests
{
    private readonly TwitterSiteProvider _site = new();

    [Theory]
    [InlineData("elonmusk", "elonmusk")]
    [InlineData("@elonmusk", "elonmusk")]
    [InlineData("https://x.com/elonmusk", "elonmusk")]
    [InlineData("https://twitter.com/elonmusk/media", "elonmusk")]
    [InlineData("https://mobile.x.com/elonmusk?foo=1", "elonmusk")]
    public void ParseInput_accepts(string input, string expected)
    {
        var r = _site.ParseInput(input);
        Assert.True(r.Ok);
        Assert.Equal(expected, r.ScreenName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://x.com/i/status/123")]     // 保留路径非用户名
    [InlineData("https://x.com/i/bookmarks")]
    [InlineData("https://x.com/home")]
    [InlineData("https://google.com/foo")]
    [InlineData("!!bad!!")]
    public void ParseInput_rejects(string input)
        => Assert.False(_site.ParseInput(input).Ok);

    [Fact]
    public void BuildDownload_user_media_maps_options()
    {
        var plan = _site.BuildDownload(ContentKind.UserMedia,
            new UserTarget(7, "alice", @"D:\dl"),
            new Dictionary<string, object?> { ["videos"] = true, ["retweets"] = false, ["sleep"] = "2" },
            new DownloadPaths(@"C:\a\cookies.txt", @"C:\arc\1.txt"));
        Assert.Equal("twitter", plan.SiteId);
        Assert.Equal(["https://x.com/alice/media"], plan.Urls);
        Assert.Equal(@"D:\dl", plan.BaseDirectory);
        var ext = (IReadOnlyDictionary<string, object?>)plan.Options["extractor"]!;
        var ex = (IReadOnlyDictionary<string, object?>)ext["twitter"]!;
        Assert.True((bool)ex["videos"]!);
        Assert.False((bool)ex["retweets"]!);
        Assert.Equal(@"C:\a\cookies.txt", ex["cookies"]);
        Assert.Equal("2", ex["sleep-request"]);
        Assert.Equal(@"C:\arc\1.txt", plan.Options["download-archive"]);
        Assert.Equal(@"D:\dl", plan.Options["base-directory"]);
    }

    [Fact]
    public void BuildDownload_likes_and_bookmarks()
    {
        var likes = _site.BuildDownload(ContentKind.AccountLikes,
            new UserTarget(null, "me", @"D:\dl"), _site.DefaultOptions,
            new DownloadPaths("c", "a"));
        Assert.Equal(["https://x.com/me/likes"], likes.Urls);

        var bookmarks = _site.BuildDownload(ContentKind.AccountBookmarks,
            new UserTarget(null, "me", @"D:\dl"), _site.DefaultOptions, new DownloadPaths("c", "a"));
        Assert.Equal(["https://x.com/i/bookmarks"], bookmarks.Urls);
    }

    [Fact]
    public void Schema_defaults_and_profile_url()
    {
        Assert.True((bool)_site.DefaultOptions["videos"]!);
        Assert.False((bool)_site.DefaultOptions["retweets"]!);
        Assert.Equal("https://x.com/elonmusk", _site.BuildProfileUrl("elonmusk"));
        Assert.Contains(_site.OptionsSchema.Fields, f => f.Key == "filename" && f.Kind == OptionKind.Text);
    }
}
