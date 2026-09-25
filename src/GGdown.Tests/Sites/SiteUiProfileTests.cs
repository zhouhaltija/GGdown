using GGdown.Sites;

namespace GGdown.Tests.Sites;

public class SiteUiProfileTests
{
    [Theory]
    [InlineData("twitter")]
    [InlineData("pixiv")]
    public void Available_sites_have_all_four_pages(string siteId) =>
        Assert.Equal(new[] { SitePageKey.Users, SitePageKey.Downloads, SitePageKey.History, SitePageKey.SiteSettings },
            SiteUiProfiles.For(siteId));

    [Theory]
    [InlineData("danbooru")]
    [InlineData("gelbooru")]
    [InlineData("kemono")]
    [InlineData("coomer")]
    public void Booru_like_sites_skip_users_page(string siteId) =>
        Assert.Equal(new[] { SitePageKey.Downloads, SitePageKey.History, SitePageKey.SiteSettings },
            SiteUiProfiles.For(siteId));

    [Theory]
    [InlineData("fanbox")]
    [InlineData("weibo")]
    [InlineData("bilibili")]
    [InlineData("instagram")]
    [InlineData("reddit")]
    [InlineData("tumblr")]
    [InlineData("fantia")]
    public void Other_placeholder_sites_have_four_pages(string siteId) =>
        Assert.Equal(4, SiteUiProfiles.For(siteId).Count);

    [Fact]
    public void Unknown_site_falls_back_to_all_pages() =>
        Assert.Equal(4, SiteUiProfiles.For("nope").Count);

    [Fact]
    public void Every_catalog_entry_has_icon_glyph() =>
        Assert.All(SiteCatalog.All, s => Assert.False(string.IsNullOrWhiteSpace(s.IconGlyph)));
}
