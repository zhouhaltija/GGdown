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
    private readonly (SqliteConnection, GGdownDbContext) _t;
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine;
    private readonly UserService _svc;
    private readonly Account _account;

    public UserServiceTests()
    {
        _t = TestDb.Create();
        _engine = new FakeEngine();
        _svc = new UserService(_t.Item2, _engine, _paths,
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
    public async Task Remove_deletes_users()
    {
        var u1 = await _svc.AddUserAsync(_account, "carol");
        var u2 = await _svc.AddUserAsync(_account, "dave");
        await _svc.RemoveAsync([u1.Id, u2.Id]);
        Assert.Empty(_t.Item2.Users);
    }

    [Fact]
    public async Task SetPinned_toggles()
    {
        var u = await _svc.AddUserAsync(_account, "carol");
        await _svc.SetPinnedAsync(u.Id, true);
        // 适配：ExecuteUpdate 绕过变更跟踪器，同一 DbContext 的跟踪查询会返回陈旧实例，用 AsNoTracking 读库中真实状态
        Assert.True(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).IsPinned);
    }

    [Fact]
    public async Task SetSkipped_toggles_and_import_preserves_flag()
    {
        var u = await _svc.AddUserAsync(_account, "alice");
        await _svc.SetSkippedAsync([u.Id], true);
        Assert.True(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).IsSkipped);

        await _svc.AddFollowingUsersAsync(_account,
            [new SiteUserInfo(u.RestId, "alice", "Alice", "https://x/a.png")]);
        Assert.True(_t.Item2.Users.AsNoTracking().Single(x => x.RestId == u.RestId).IsSkipped);

        await _svc.SetSkippedAsync([u.Id], false);
        Assert.False(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).IsSkipped);
    }

    [Fact]
    public async Task SetInDownloadList_toggles_and_skip_removes_from_list()
    {
        var u = await _svc.AddUserAsync(_account, "alice");
        await _svc.SetInDownloadListAsync([u.Id], true);
        Assert.True(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).InDownloadList);

        await _svc.SetSkippedAsync([u.Id], true);
        Assert.False(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).InDownloadList);
    }

    [Fact]
    public async Task SetInDownloadList_does_not_add_skipped_user()
    {
        var u = await _svc.AddUserAsync(_account, "alice");
        await _svc.SetSkippedAsync([u.Id], true);
        await _svc.SetInDownloadListAsync([u.Id], true);
        Assert.False(_t.Item2.Users.AsNoTracking().Single(x => x.Id == u.Id).InDownloadList);
    }
}
