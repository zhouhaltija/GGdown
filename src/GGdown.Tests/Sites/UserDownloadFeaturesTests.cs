using GGdown.Data;
using GGdown.Sites;

namespace GGdown.Tests.Sites;

public class UserDownloadFeaturesTests
{
    [Fact]
    public void Pixiv_author_choice_expands_to_the_selected_content_kinds()
    {
        ISiteProvider site = new PixivSiteProvider();
        Assert.Equal(new[] { ContentKind.UserMedia, ContentKind.UserNovels }, site.UserFeatures.KindsFor(UserContentSelection.All));
        Assert.Equal(new[] { ContentKind.UserNovels }, site.UserFeatures.KindsFor(UserContentSelection.PixivNovels));
        Assert.Throws<ArgumentException>(() => site.UserFeatures.KindsFor(UserContentSelection.DouyinVideos));
    }

    [Fact]
    public void Douyin_video_choice_uses_media_download_with_date_filter_and_no_following_import()
    {
        ISiteProvider site = new DouyinSiteProvider();
        Assert.True(site.UserFeatures.DateFilter);
        Assert.True(site.UserFeatures.Incremental);
        Assert.False(site.UserFeatures.Following);
        Assert.Equal(new[] { ContentKind.UserMedia }, site.UserFeatures.KindsFor(UserContentSelection.DouyinVideos));
    }
}
