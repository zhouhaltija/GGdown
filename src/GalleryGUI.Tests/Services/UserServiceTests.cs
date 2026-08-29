using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using Microsoft.Data.Sqlite; // 适配：字段元组类型 SqliteConnection 需要（同 SettingsStoreTests）
using Microsoft.EntityFrameworkCore; // 适配：AsNoTracking（ExecuteUpdate 绕过变更跟踪器，需绕开陈旧跟踪实例读库中真实状态）
using Microsoft.Extensions.Logging.Abstractions;

namespace GalleryGUI.Tests.Services;

public class UserServiceTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
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
    public async Task ImportFollowing_upserts_users()
    {
        var count = await _svc.ImportFollowingAsync(_account);
        Assert.Equal(2, count);

        // 二次导入不产生重复，且资料被刷新
        _engine.NextFollowing = [new SiteUserInfo("1", "alice2", "Alice Renamed", "https://x/a2.png")];
        var count2 = await _svc.ImportFollowingAsync(_account);
        Assert.Equal(1, count2);
        var users = _t.Item2.Users.ToList();
        Assert.Equal(2, users.Count);
        var alice = users.Single(u => u.RestId == "1");
        Assert.Equal("alice2", alice.ScreenName);
        Assert.Equal(UserSource.Following, alice.Source);
        Assert.Equal("https://x.com/alice2", alice.ProfileUrl);
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
}
