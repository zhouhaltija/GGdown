using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Sites;
using GGdown.Threading;
using GGdown.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.ViewModels;

public class FollowingPickerViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GGdownSiteDbContext) _t;
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine = new();
    private readonly GGdownSiteDbContext _db;
    private readonly FollowingPickerViewModel _vm;

    public FollowingPickerViewModelTests()
    {
        _t = TestDb.CreateSite();
        _db = _t.Item2;
        var factory = new SingleDbContextFactory(_db);
        _db.Accounts.Add(new Account
        { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow });
        _db.SaveChanges();
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        var userSvc = new UserService(new SingleSiteDbContextFactory(_db), _engine, _paths, sites, NullLogger<UserService>.Instance);
        _vm = new FollowingPickerViewModel(userSvc, new UserQueryService(new SingleSiteDbContextFactory(_db)),
            new AccountQueryService(new SingleSiteDbContextFactory(_db)), new SyncDispatcher(), new FakeCurrentSite());
        _vm.VisibleItems.CollectionChanged += (_, _) => { };
    }

    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [Fact]
    public async Task Load_without_account_is_blocked()
    {
        await _db.Accounts.ExecuteDeleteAsync();
        Assert.False(await _vm.LoadAsync());
        Assert.Contains("Cookie", _vm.ResultMessage);
        Assert.Empty(_vm.VisibleItems);
    }

    [Fact]
    public async Task Load_lists_following_and_marks_already_added()
    {
        _db.Users.Add(new User
        {
            SiteId = "twitter", RestId = "1", ScreenName = "alice", Source = UserSource.Following,
            AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        Assert.True(await _vm.LoadAsync());
        Assert.Equal(2, _vm.VisibleItems.Count);
        var alice = _vm.VisibleItems.Single(r => r.Info.ScreenName == "alice");
        var bob = _vm.VisibleItems.Single(r => r.Info.ScreenName == "bob");
        Assert.True(alice.AlreadyAdded);
        Assert.False(alice.CanSelect);
        Assert.Contains("已添加", alice.Subtitle);
        Assert.False(bob.AlreadyAdded);
        Assert.True(bob.CanSelect);
        Assert.Equal(0, _vm.SelectedCount);
    }

    [Fact]
    public async Task AddSelected_imports_only_checked_users()
    {
        Assert.True(await _vm.LoadAsync());
        _vm.VisibleItems.Single(r => r.Info.ScreenName == "bob").IsSelected = true;

        Assert.True(await _vm.AddSelectedAsync());
        Assert.Contains("已添加 1 个", _vm.ResultMessage);
        Assert.Equal("bob", _db.Users.Single().ScreenName);
    }

    [Fact]
    public async Task SearchText_filters_visible_rows()
    {
        Assert.True(await _vm.LoadAsync());
        _vm.SearchText = "ali";
        Assert.Single(_vm.VisibleItems);
        Assert.Equal("alice", _vm.VisibleItems[0].Info.ScreenName);
        _vm.SearchText = "";
        Assert.Equal(2, _vm.VisibleItems.Count);
    }

    [Fact]
    public async Task AddSelected_with_none_checked_is_blocked()
    {
        Assert.True(await _vm.LoadAsync());
        Assert.False(await _vm.AddSelectedAsync());
        Assert.Contains("请选择", _vm.ResultMessage);
        Assert.Empty(_db.Users);
    }

    [Fact]
    public async Task Load_saves_following_cache()
    {
        Assert.True(await _vm.LoadAsync());
        var cache = await new UserService(new SingleSiteDbContextFactory(_db), _engine, _paths,
            new SiteRegistry([new TwitterSiteProvider()]),
            NullLogger<UserService>.Instance).GetFollowingCacheAsync(
            _db.Accounts.Single());
        Assert.NotNull(cache);
        Assert.Equal(["alice", "bob"], cache!.Users.Select(u => u.ScreenName));
    }

    [Fact]
    public async Task Second_load_shows_cache_immediately_then_applies_refresh_changes()
    {
        Assert.True(await _vm.LoadAsync());
        Assert.Equal(2, _vm.VisibleItems.Count);

        var gate = new TaskCompletionSource<IReadOnlyList<SiteUserInfo>>();
        _engine.FollowingDelay = gate;
        _engine.NextFollowing =
        [
            new("1", "alice", "Alice", "https://x/a.png"),
            new("2", "bob", "Bob", "https://x/b.png"),
            new("3", "carol", "Carol", "https://x/c.png"),
        ];

        var load = _vm.LoadAsync();
        Assert.Equal(2, _vm.VisibleItems.Count);
        Assert.Contains("刷新", _vm.StatusText);
        Assert.False(_vm.IsBusy);

        gate.SetResult(_engine.NextFollowing);
        Assert.True(await load);
        Assert.Equal(3, _vm.VisibleItems.Count);
        Assert.Contains("carol", _vm.VisibleItems.Select(r => r.Info.ScreenName));
        Assert.Equal(2, _engine.ListFollowingCalls);
    }

    [Fact]
    public async Task Refresh_pins_new_follows_to_top_and_keeps_cache_order()
    {
        Assert.True(await _vm.LoadAsync());

        _engine.NextFollowing =
        [
            new("2", "bob", "Bob", "https://x/b.png"),
            new("3", "carol", "Carol", "https://x/c.png"),
            new("1", "alice", "Alice", "https://x/a.png"),
            new("4", "dan", "Dan", "https://x/d.png"),
        ];

        Assert.True(await _vm.LoadAsync());
        Assert.Equal(["carol", "dan", "alice", "bob"], _vm.VisibleItems.Select(r => r.Info.ScreenName));
        Assert.Contains("新关注 2 人已置顶", _vm.StatusText);
    }

    [Fact]
    public async Task Refresh_drops_unfollowed_and_pins_new_follows()
    {
        Assert.True(await _vm.LoadAsync());

        _engine.NextFollowing =
        [
            new("2", "bob", "Bob", "https://x/b.png"),
            new("3", "carol", "Carol", "https://x/c.png"),
        ];

        Assert.True(await _vm.LoadAsync());
        Assert.Equal(["carol", "bob"], _vm.VisibleItems.Select(r => r.Info.ScreenName));
    }

    [Fact]
    public async Task Next_open_shows_pinned_order_from_cache_before_refresh()
    {
        Assert.True(await _vm.LoadAsync());
        _engine.NextFollowing =
        [
            new("1", "alice", "Alice", "https://x/a.png"),
            new("2", "bob", "Bob", "https://x/b.png"),
            new("3", "carol", "Carol", "https://x/c.png"),
        ];
        Assert.True(await _vm.LoadAsync());

        var gate = new TaskCompletionSource<IReadOnlyList<SiteUserInfo>>();
        _engine.FollowingDelay = gate;
        var load = _vm.LoadAsync();
        Assert.Equal(["carol", "alice", "bob"], _vm.VisibleItems.Select(r => r.Info.ScreenName));

        gate.SetResult(_engine.NextFollowing);
        Assert.True(await load);
        Assert.Equal(["carol", "alice", "bob"], _vm.VisibleItems.Select(r => r.Info.ScreenName));
        Assert.DoesNotContain("新关注", _vm.StatusText);
    }

    [Fact]
    public async Task Second_load_does_not_rebuild_rows_when_following_unchanged()
    {
        Assert.True(await _vm.LoadAsync());
        _vm.VisibleItems.Single(r => r.Info.ScreenName == "bob").IsSelected = true;
        var bob = _vm.VisibleItems.Single(r => r.Info.ScreenName == "bob");

        Assert.True(await _vm.LoadAsync());
        Assert.Same(bob, _vm.VisibleItems.Single(r => r.Info.ScreenName == "bob"));
        Assert.True(bob.IsSelected);
    }

    [Fact]
    public async Task Refresh_failure_keeps_cached_list()
    {
        Assert.True(await _vm.LoadAsync());
        _engine.ListFollowingError = new InvalidOperationException("network");
        Assert.True(await _vm.LoadAsync());
        Assert.Equal(2, _vm.VisibleItems.Count);
        Assert.Contains("刷新失败", _vm.StatusText);
    }
}
