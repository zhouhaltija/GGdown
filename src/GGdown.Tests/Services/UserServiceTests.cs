using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Sites;
using Microsoft.Data.Sqlite; // 适配：字段元组类型 SqliteConnection 需要（同 SettingsStoreTests）
using Microsoft.EntityFrameworkCore; // 适配：AsNoTracking（ExecuteUpdate 绕过变更跟踪器，需绕开陈旧跟踪实例读库中真实状态）
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.Services;

public class UserServiceTests : IDisposable
{
    private readonly (SqliteConnection, GGdownSiteDbContext) _t;
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine;
    private readonly UserService _svc;
    private readonly Account _account;

    public UserServiceTests()
    {
        _t = TestDb.CreateSite();
        _engine = new FakeEngine();
        _svc = new UserService(new SingleSiteDbContextFactory(_t.Item2), _engine, _paths,
            new SiteRegistry([new TwitterSiteProvider()]),
            NullLogger<UserService>.Instance);
        _account = new Account
        { SiteId = "twitter", CookiePath = "twitter\\c\\cookies.txt", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _t.Item2.Accounts.Add(_account);
        _t.Item2.SaveChanges();
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [Fact]
    public async Task ListFollowing_returns_engine_following()
    {
        var followed = await _svc.ListFollowingAsync(_account);
        Assert.Equal(["alice", "bob"], followed.Select(u => u.ScreenName));
        Assert.Empty(_t.Item2.Users);
    }

    [Fact]
    public async Task Following_cache_roundtrip_replaces_previous()
    {
        Assert.Null(await _svc.GetFollowingCacheAsync(_account));
        var first = await _svc.ListFollowingAsync(_account);
        await _svc.SaveFollowingCacheAsync(_account, first);
        var cached = await _svc.GetFollowingCacheAsync(_account);
        Assert.Equal(["alice", "bob"], cached!.Users.Select(u => u.ScreenName));

        await _svc.SaveFollowingCacheAsync(_account, [new SiteUserInfo("9", "zoe", "Zoe", null)]);
        var again = await _svc.GetFollowingCacheAsync(_account);
        Assert.Equal(["zoe"], again!.Users.Select(u => u.ScreenName));
    }

    [Fact]
    public async Task Ignored_following_is_persistent_reversible_and_account_scoped()
    {
        var other = new Account
        { SiteId = "twitter", CookiePath = "twitter\\other\\cookies.txt", AddedAt = DateTime.UtcNow };
        _t.Item2.Accounts.Add(other);
        _t.Item2.SaveChanges();

        await _svc.SetFollowingIgnoredAsync(_account, "2", true);
        await _svc.SetFollowingIgnoredAsync(_account, "2", true);
        Assert.Equal(["2"], await _svc.GetIgnoredFollowingIdsAsync(_account));
        Assert.Empty(await _svc.GetIgnoredFollowingIdsAsync(other));
        Assert.Single(_t.Item2.IgnoredFollowing);

        await _svc.SetFollowingIgnoredAsync(_account, "2", false);
        Assert.Empty(await _svc.GetIgnoredFollowingIdsAsync(_account));
    }

    [Fact]
    public async Task AddFollowingUsers_adds_only_selected()
    {
        var followed = await _svc.ListFollowingAsync(_account);
        var added = await _svc.AddFollowingUsersAsync(_account, [followed[0]]);
        Assert.Equal(1, added);
        Assert.Equal("alice", _t.Item2.Users.Single().ScreenName);
    }

    [Fact]
    public async Task AddFollowingUsers_upserts_existing_without_duplicate()
    {
        var alice = new SiteUserInfo("1", "alice", "Alice", "https://x/a.png");
        await _svc.AddFollowingUsersAsync(_account, [alice]);

        var renamed = new SiteUserInfo("1", "alice2", "Alice Renamed", "https://x/a2.png");
        var added = await _svc.AddFollowingUsersAsync(_account, [renamed]);
        Assert.Equal(0, added);
        var users = _t.Item2.Users.ToList();
        Assert.Single(users);
        Assert.Equal("alice2", users[0].ScreenName);
        Assert.Equal(UserSource.Following, users[0].Source);
        Assert.Equal("https://x.com/alice2", users[0].ProfileUrl);
    }

    [Fact]
    public async Task AddUser_accepts_url_and_username()
    {
        var byUrl = await _svc.AddUserAsync(_account, "https://x.com/carol");
        Assert.Equal("carol", byUrl.ScreenName);
        Assert.Equal(UserSource.Link, byUrl.Source);

        var byName = await _svc.AddUserAsync(_account, "@carol");
        Assert.Equal(byUrl.Id, byName.Id);   // 同一 rest_id 幂等
        Assert.Equal(UserSource.Manual, byName.Source);
    }

    [Fact]
    public async Task AddUser_invalid_input_throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _svc.AddUserAsync(_account, "!!bad!!"));
    }

    [Fact]
    public async Task Content_selection_is_saved_per_user_and_survives_profile_refresh()
    {
        var account = new Account
        { SiteId = "pixiv", CookiePath = "pixiv\\c\\cookies.txt", Status = AccountStatus.Ok,
            IsActive = true, AddedAt = DateTime.UtcNow };
        _t.Item2.Accounts.Add(account);
        await _t.Item2.SaveChangesAsync();
        var service = new UserService(new SingleSiteDbContextFactory(_t.Item2), _engine, _paths,
            new SiteRegistry([new PixivSiteProvider()]), NullLogger<UserService>.Instance);
        _engine.NextUserInfo = new SiteUserInfo("12345", "12345", "First", null);
        var first = await service.AddUserAsync(account, "12345");
        _engine.NextUserInfo = new SiteUserInfo("67890", "67890", "Second", null);
        var second = await service.AddUserAsync(account, "67890");

        await service.SetContentSelectionAsync("pixiv", first.Id, UserContentSelection.PixivNovels);
        _engine.NextUserInfo = new SiteUserInfo("12345", "12345", "First Renamed", null);
        await service.AddUserAsync(account, "12345");

        Assert.Equal(UserContentSelection.PixivNovels,
            _t.Item2.Users.AsNoTracking().Single(u => u.Id == first.Id).ContentSelection);
        Assert.Equal(UserContentSelection.All,
            _t.Item2.Users.AsNoTracking().Single(u => u.Id == second.Id).ContentSelection);
    }

    [Fact]
    public async Task Douyin_user_date_can_override_and_clear_site_default()
    {
        var account = new Account
        { SiteId = "douyin", CookiePath = "douyin\\c\\cookies.txt", Status = AccountStatus.Ok,
            IsActive = true, AddedAt = DateTime.UtcNow };
        _t.Item2.Accounts.Add(account);
        await _t.Item2.SaveChangesAsync();
        _engine.NextUserInfo = new SiteUserInfo("MS4wLjABtest", "creator", "Creator", null);
        var service = new UserService(new SingleSiteDbContextFactory(_t.Item2), _engine, _paths,
            new SiteRegistry([new DouyinSiteProvider()]), NullLogger<UserService>.Instance);
        var user = await service.AddUserAsync(account, "https://www.douyin.com/user/MS4wLjABtest");

        await service.SetDownloadSinceAsync("douyin", user.Id, new DateOnly(2025, 2, 3));
        Assert.Equal(new DateOnly(2025, 2, 3),
            _t.Item2.Users.AsNoTracking().Single(u => u.Id == user.Id).DownloadSince);
        await service.SetDownloadSinceAsync("douyin", user.Id, null);
        Assert.Null(_t.Item2.Users.AsNoTracking().Single(u => u.Id == user.Id).DownloadSince);
    }

    [Fact]
    public async Task AddUser_accepts_douyin_profile_share_short_link()
    {
        var account = new Account
        { SiteId = "douyin", CookiePath = "douyin\\c\\cookies.txt", Status = AccountStatus.Ok,
            IsActive = true, AddedAt = DateTime.UtcNow };
        _t.Item2.Accounts.Add(account);
        await _t.Item2.SaveChangesAsync();
        _engine.NextUserInfo = new SiteUserInfo("MS4wLjABtest", "creator_name", "Creator", null);
        var service = new UserService(new SingleSiteDbContextFactory(_t.Item2), _engine, _paths,
            new SiteRegistry([new DouyinSiteProvider()]), NullLogger<UserService>.Instance);
        var input = "长按复制此条消息，打开抖音搜索，查看TA的更多作品。 https://v.douyin.com/F2tY_NKbYjQ/\u00A0";

        var user = await service.AddUserAsync(account, input);

        Assert.Equal("MS4wLjABtest", user.RestId);
        Assert.Equal("creator_name", user.ScreenName);
        Assert.Equal(UserSource.Link, user.Source);
        Assert.Equal("https://www.douyin.com/user/MS4wLjABtest", user.ProfileUrl);
    }

    [Fact]
    public async Task AddUser_rejects_douyin_work_link()
    {
        var account = new Account
        { SiteId = "douyin", CookiePath = "douyin\\c\\cookies.txt", Status = AccountStatus.Ok,
            IsActive = true, AddedAt = DateTime.UtcNow };
        var service = new UserService(new SingleSiteDbContextFactory(_t.Item2), _engine, _paths,
            new SiteRegistry([new DouyinSiteProvider()]), NullLogger<UserService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => service.AddUserAsync(
            account, "https://www.douyin.com/video/1234567890123456789"));
    }

    [Fact]
    public async Task Douyin_refresh_uses_rest_id_profile_url()
    {
        var account = new Account
        { SiteId = "douyin", CookiePath = "douyin\\c\\cookies.txt", Status = AccountStatus.Ok,
            IsActive = true, AddedAt = DateTime.UtcNow };
        _t.Item2.Accounts.Add(account);
        _t.Item2.Users.Add(new User
        { SiteId = "douyin", RestId = "MS4wLjABtest", ScreenName = "creator_name",
            Source = UserSource.Manual, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _t.Item2.SaveChangesAsync();
        _engine.NextUserInfo = new SiteUserInfo("MS4wLjABtest", "creator_name", "Creator", null);
        var service = new UserService(new SingleSiteDbContextFactory(_t.Item2), _engine, _paths,
            new SiteRegistry([new TwitterSiteProvider(), new DouyinSiteProvider()]),
            NullLogger<UserService>.Instance);

        var updated = await service.RefreshProfilesAsync(account);

        Assert.Equal(1, updated);
        Assert.Equal("https://www.douyin.com/user/MS4wLjABtest", _engine.LastUserInfoInput);
    }

    [Fact]
    public async Task Remove_deletes_users()
    {
        var u1 = await _svc.AddUserAsync(_account, "carol");
        var u2 = await _svc.AddUserAsync(_account, "dave");
        await _svc.RemoveAsync("twitter", [u1.Id, u2.Id]);
        Assert.Empty(_t.Item2.Users);
    }

    [Fact]
    public async Task SetPinned_toggles()
    {
        var u = await _svc.AddUserAsync(_account, "carol");
        await _svc.SetPinnedAsync("twitter", u.Id, true);
        // 适配：ExecuteUpdate 绕过变更跟踪器，同一 DbContext 的跟踪查询会返回陈旧实例，用 AsNoTracking 读库中真实状态
        Assert.True(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).IsPinned);
    }

    [Fact]
    public async Task SetSkipped_toggles_and_import_preserves_flag()
    {
        var u = await _svc.AddUserAsync(_account, "alice");
        await _svc.SetSkippedAsync("twitter", [u.Id], true);
        Assert.True(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).IsSkipped);

        await _svc.AddFollowingUsersAsync(_account,
            [new SiteUserInfo(u.RestId, "alice", "Alice", "https://x/a.png")]);
        Assert.True(_t.Item2.Users.AsNoTracking().Single(x => x.RestId == u.RestId).IsSkipped);

        await _svc.SetSkippedAsync("twitter", [u.Id], false);
        Assert.False(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).IsSkipped);
    }

    [Fact]
    public async Task SetInDownloadList_toggles_and_skip_removes_from_list()
    {
        var u = await _svc.AddUserAsync(_account, "alice");
        await _svc.SetInDownloadListAsync("twitter", [u.Id], true);
        Assert.True(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).InDownloadList);

        await _svc.SetSkippedAsync("twitter", [u.Id], true);
        Assert.False(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).InDownloadList);
    }

    [Fact]
    public async Task SetInDownloadList_does_not_add_skipped_user()
    {
        var u = await _svc.AddUserAsync(_account, "alice");
        await _svc.SetSkippedAsync("twitter", [u.Id], true);
        await _svc.SetInDownloadListAsync("twitter", [u.Id], true);
        Assert.False(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).InDownloadList);
    }
}
