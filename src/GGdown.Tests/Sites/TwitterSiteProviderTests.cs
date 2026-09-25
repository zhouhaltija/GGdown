using GGdown.Sites;

namespace GGdown.Tests.Sites;

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
    [InlineData("https://x.com/elonmusk/status/123456", "elonmusk", "123456")]
    [InlineData("https://twitter.com/i/web/status/99", "i", "99")]
    [InlineData("https://x.com/i/status/42", "i", "42")]
    public void ParseInput_accepts_tweet_urls(string input, string name, string id)
    {
        var r = _site.ParseInput(input);
        Assert.True(r.Ok);
        Assert.Equal(PasteKind.Tweet, r.Kind);
        Assert.Equal(name, r.ScreenName);
        Assert.Equal(id, r.RestId);
        Assert.Contains("/status/" + id, r.DirectUrl);
    }

    [Fact]
    public void ParseInput_accepts_list_url()
    {
        var r = _site.ParseInput("https://x.com/i/lists/12345");
        Assert.True(r.Ok);
        Assert.Equal(PasteKind.List, r.Kind);
        Assert.Equal("12345", r.RestId);
        Assert.Equal("https://x.com/i/lists/12345", r.DirectUrl);
    }

    [Fact]
    public void ParseInput_accepts_search_url()
    {
        var r = _site.ParseInput("https://x.com/search?q=from%3Aalice%20filter%3Amedia");
        Assert.True(r.Ok);
        Assert.Equal(PasteKind.Search, r.Kind);
        Assert.Contains("search?q=", r.DirectUrl);
    }

    [Fact]
    public void ParseInput_accepts_raw_search_operators()
    {
        var r = _site.ParseInput("from:alice filter:media min_faves:100");
        Assert.True(r.Ok);
        Assert.Equal(PasteKind.Search, r.Kind);
        Assert.Contains("search?q=", r.DirectUrl);
        Assert.Contains("from", r.DirectUrl);
    }

    [Fact]
    public void ParseInput_accepts_hashtag()
    {
        var r = _site.ParseInput("#cats");
        Assert.True(r.Ok);
        Assert.Equal(PasteKind.Search, r.Kind);
        Assert.Contains("hashtag/cats", r.DirectUrl);
    }

    [Theory]
    [InlineData("")]
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
        Assert.Equal(@"C:\arc\1.txt", ex["archive"]);
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
        Assert.Contains(_site.OptionsSchema.Fields, f => f.Key == "media" && f.Kind == OptionKind.Choice);
        Assert.Contains(_site.OptionsSchema.Fields, f => f.Key == "size" && f.Kind == OptionKind.Choice);
    }

    [Fact]
    public void BuildDownload_highlights_and_permalink_and_search()
    {
        var hi = _site.BuildDownload(ContentKind.UserHighlights,
            new UserTarget(7, "alice", @"D:\dl"), _site.DefaultOptions, new DownloadPaths("c", "a"));
        Assert.Equal(["https://x.com/alice/highlights"], hi.Urls);

        var tweet = _site.BuildDownload(ContentKind.Permalink,
            new UserTarget(null, "alice", @"D:\dl", "123", "https://x.com/alice/status/123"),
            _site.DefaultOptions, new DownloadPaths("c", "a"));
        Assert.Equal(["https://x.com/alice/status/123"], tweet.Urls);

        var search = _site.BuildDownload(ContentKind.Search,
            new UserTarget(null, null, @"D:\dl", DirectUrl: "https://x.com/search?q=cats"),
            _site.DefaultOptions, new DownloadPaths("c", "a"));
        Assert.Equal(["https://x.com/search?q=cats"], search.Urls);
    }

    [Fact]
    public void BuildDownload_media_images_disables_videos()
    {
        var plan = _site.BuildDownload(ContentKind.UserMedia,
            new UserTarget(7, "alice", @"D:\dl"),
            new Dictionary<string, object?> { ["media"] = "images", ["size"] = "large" },
            new DownloadPaths("c", "a"));
        var ext = (IReadOnlyDictionary<string, object?>)plan.Options["extractor"]!;
        var ex = (IReadOnlyDictionary<string, object?>)ext["twitter"]!;
        Assert.False((bool)ex["videos"]!);
        Assert.Equal("large", ex["size"]);
    }

    [Fact]
    public void BuildDownload_media_videos_filters_extensions()
    {
        var plan = _site.BuildDownload(ContentKind.UserMedia,
            new UserTarget(7, "alice", @"D:\dl"),
            new Dictionary<string, object?> { ["media"] = "videos" },
            new DownloadPaths("c", "a"));
        var ext = (IReadOnlyDictionary<string, object?>)plan.Options["extractor"]!;
        var ex = (IReadOnlyDictionary<string, object?>)ext["twitter"]!;
        Assert.True((bool)ex["videos"]!);
        Assert.Contains("mp4", (string)ex["image-filter"]!);
    }
}
