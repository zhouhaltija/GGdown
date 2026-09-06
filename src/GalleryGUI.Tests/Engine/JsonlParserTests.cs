using GalleryGUI.Engine;

namespace GalleryGUI.Tests.Engine;

public class JsonlParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"no_ev\":1}")]
    public void Malformed_lines_return_null(string line)
        => Assert.Null(JsonlParser.Parse(line));

    [Fact]
    public void Parses_hello()
    {
        var ev = JsonlParser.Parse(
            """{"ev":"hello","protocol":1,"runner":"1.0.0","gallery_dl":"1.28.5"}""");
        Assert.NotNull(ev);
        Assert.Equal("hello", ev.Event);
        Assert.Equal(1, ev.Protocol);
        Assert.Equal("1.0.0", ev.RunnerVersion);
        Assert.Equal("1.28.5", ev.GalleryDlVersion);
    }

    [Fact]
    public void Parses_file_events()
    {
        var start = JsonlParser.Parse(
            """{"ev":"file-start","path":"D:/x/1234_user_1.jpg","item_id":"1234"}""");
        Assert.Equal("file-start", start!.Event);
        Assert.Equal("D:/x/1234_user_1.jpg", start.Path);
        Assert.Equal("1234", start.ItemId);

        var done = JsonlParser.Parse(
            """{"ev":"file-done","path":"D:/x/1234_user_1.jpg","size":48213}""");
        Assert.Equal(48213L, done!.Size);

        var skip = JsonlParser.Parse("""{"ev":"file-skip","path":"D:/x/9.jpg"}""");
        Assert.Equal("file-skip", skip!.Event);
    }

    [Fact]
    public void Parses_fatal_with_kind()
    {
        var ev = JsonlParser.Parse("""{"ev":"fatal","msg":"invalid cookie","kind":"auth"}""");
        Assert.Equal("fatal", ev!.Event);
        Assert.Equal("invalid cookie", ev.Message);
        Assert.Equal("auth", ev.Kind);
    }

    [Fact]
    public void Parses_job_done_counters()
    {
        var ev = JsonlParser.Parse(
            """{"ev":"job-done","total":120,"skipped":80,"failed":0}""");
        Assert.Equal(120L, ev!.Total);
        Assert.Equal(80L, ev.Skipped);
        Assert.Equal(0L, ev.Failed);
    }

    [Fact]
    public void Parses_account_event_with_rest_id()
    {
        var ev = JsonlParser.Parse(
            """{"ev":"account","screen_name":"alice","display_name":"Alice","rest_id":"99"}""");
        Assert.Equal("account", ev!.Event);
        Assert.Equal("alice", ev.ScreenName);
        Assert.Equal("Alice", ev.DisplayName);
        Assert.Equal("99", ev.RestId);
    }

    [Fact]
    public void Parses_user_event()
    {
        var ev = JsonlParser.Parse(
            """{"ev":"user","rest_id":"44196397","screen_name":"elonmusk","display_name":"Elon Musk","avatar_url":"https://pbs.twimg.com/a.jpg"}""");
        Assert.Equal("user", ev!.Event);
        Assert.Equal("44196397", ev.RestId);
        Assert.Equal("elonmusk", ev.ScreenName);
        Assert.Equal("Elon Musk", ev.DisplayName);
        Assert.Equal("https://pbs.twimg.com/a.jpg", ev.AvatarUrl);
    }

    [Fact]
    public void Parses_user_profile_fields()
    {
        var ev = JsonlParser.Parse(
            """{"ev":"user","rest_id":"1","screen_name":"a","banner_url":"https://pbs.twimg.com/b.jpg","bio":"hi","followers_count":12,"media_count":3}""");
        Assert.Equal("https://pbs.twimg.com/b.jpg", ev!.BannerUrl);
        Assert.Equal("hi", ev.Bio);
        Assert.Equal(12L, ev.FollowersCount);
        Assert.Equal(3L, ev.MediaCount);
    }

    [Fact]
    public void Unknown_event_name_is_preserved()
    {
        var ev = JsonlParser.Parse("""{"ev":"future-event","x":1}""");
        Assert.Equal("future-event", ev!.Event);
    }
}
