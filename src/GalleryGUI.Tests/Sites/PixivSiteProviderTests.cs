using GalleryGUI.Sites;

namespace GalleryGUI.Tests.Sites;

public class PixivSiteProviderTests
{
    private readonly PixivSiteProvider _site = new();

    [Theory]
    [InlineData("12345", "12345")]
    [InlineData("https://www.pixiv.net/users/12345", "12345")]
    [InlineData("https://www.pixiv.net/en/users/12345/artworks", "12345")]
    [InlineData("https://www.pixiv.net/member.php?id=99", "99")]
    [InlineData("https://touch.pixiv.net/users/7", "7")]
    public void ParseInput_accepts(string input, string expectedId)
    {
        var r = _site.ParseInput(input);
        Assert.True(r.Ok);
        Assert.Equal(expectedId, r.ScreenName);
        Assert.Equal(expectedId, r.RestId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://x.com/alice")]
    [InlineData("not-a-user")]
    [InlineData("https://www.pixiv.net/artworks/123")]
    public void ParseInput_rejects(string input)
        => Assert.False(_site.ParseInput(input).Ok);

    [Fact]
    public void Requires_refresh_token_and_kind_labels()
    {
        Assert.True(_site.RequiresRefreshToken);
        Assert.Contains("pixiv.net/users", _site.InputHint);
        Assert.Equal("插画漫画", _site.KindLabel(ContentKind.UserMedia));
        Assert.Equal("小说", _site.KindLabel(ContentKind.UserNovels));
        Assert.Equal("收藏插画", _site.KindLabel(ContentKind.AccountBookmarks));
        Assert.Equal("收藏小说", _site.KindLabel(ContentKind.AccountNovelBookmarks));
        Assert.Equal("https://www.pixiv.net/users/12345", _site.BuildProfileUrl("foo", "12345"));
    }

    [Fact]
    public void BuildDownload_artworks_and_novels_use_numeric_id()
    {
        var target = new UserTarget(7, "foo_bar", @"D:\dl", "12345");
        var paths = new DownloadPaths(@"C:\a\cookies.txt", @"C:\arc\1.txt");
        var art = _site.BuildDownload(ContentKind.UserMedia, target, _site.DefaultOptions, paths);
        Assert.Equal("pixiv", art.SiteId);
        Assert.Equal(["https://www.pixiv.net/users/12345/artworks"], art.Urls);
        var novels = _site.BuildDownload(ContentKind.UserNovels, target, _site.DefaultOptions, paths);
        Assert.Equal(["https://www.pixiv.net/users/12345/novels"], novels.Urls);

        var ext = (IReadOnlyDictionary<string, object?>)art.Options["extractor"]!;
        Assert.Contains("pixiv", ext.Keys);
        Assert.Contains("pixiv-novel", ext.Keys);
        var pixiv = (IReadOnlyDictionary<string, object?>)ext["pixiv"]!;
        var novel = (IReadOnlyDictionary<string, object?>)ext["pixiv-novel"]!;
        Assert.Equal(@"C:\a\cookies.txt", pixiv["cookies"]);
        Assert.Equal(@"C:\arc\1.txt", pixiv["archive"]);
        Assert.True((bool)pixiv["ugoira"]!);
        Assert.Equal(@"C:\a\cookies.txt", novel["cookies"]);
        Assert.False((bool)novel["covers"]!);
        Assert.DoesNotContain("download_artworks", pixiv.Keys);
    }

    [Fact]
    public void BuildDownload_bookmarks_include_private()
    {
        var me = new UserTarget(null, "me", @"D:\dl", "99");
        var paths = new DownloadPaths("c", "a");
        var art = _site.BuildDownload(ContentKind.AccountBookmarks, me, _site.DefaultOptions, paths);
        Assert.Equal(
        [
            "https://www.pixiv.net/users/99/bookmarks/artworks",
            "https://www.pixiv.net/users/99/bookmarks/artworks?rest=hide",
        ], art.Urls);
        var novels = _site.BuildDownload(ContentKind.AccountNovelBookmarks, me, _site.DefaultOptions, paths);
        Assert.Equal(
        [
            "https://www.pixiv.net/users/99/bookmarks/novels",
            "https://www.pixiv.net/users/99/bookmarks/novels?rest=hide",
        ], novels.Urls);
    }

    [Fact]
    public void Schema_includes_enqueue_toggles()
    {
        Assert.Contains(_site.OptionsSchema.Fields, f => f.Key == "download_artworks" && f.Kind == OptionKind.Boolean);
        Assert.Contains(_site.OptionsSchema.Fields, f => f.Key == "download_novels" && f.Kind == OptionKind.Boolean);
        Assert.True((bool)_site.DefaultOptions["download_artworks"]!);
        Assert.True((bool)_site.DefaultOptions["download_novels"]!);
    }
}
