using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Settings;
using GalleryGUI.Sites;
using GalleryGUI.Threading;
using GalleryGUI.ViewModels;
using Microsoft.Data.Sqlite; // 补 using：SqliteConnection 在 tuple 类型中未限定（brief 已知偏离模式，同 QueriesTests）
using Microsoft.EntityFrameworkCore; // 补 using：测试体用了 ExecuteDeleteAsync/AsNoTracking（brief 遗漏）
using Microsoft.Extensions.Logging.Abstractions;

namespace GalleryGUI.Tests.ViewModels;

public class UsersViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine = new();
    private readonly GalleryDbContext _db;
    private readonly UsersViewModel _vm;
    private readonly Account _account;

    public UsersViewModelTests()
    {
        _t = TestDb.Create();
        _db = _t.Item2;
        var factory = new SingleDbContextFactory(_db);
        _account = new Account { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _db.Accounts.Add(_account);
        _db.Users.AddRange(
            NewUser("1", "alice", count: 3, last: DateTime.UtcNow.AddDays(-2)),
            NewUser("2", "bob"));
        _db.SaveChanges();
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        // 适配（B2 控制器裁定后 brief 原行过时）：AppSettings 注入 IDbContextFactory 而非 ISettingsStore，
        // 原文 new AppSettings(new GallerySettingsStore(_db), sites) 已无法编译
        var settings = new AppSettings(factory, sites);
        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, sites, NullLogger<DownloadQueueService>.Instance);
        var accountSvc = new AccountService(_db, _engine, _paths, sites, NullLogger<AccountService>.Instance);
        var userSvc = new UserService(_db, _engine, _paths, sites, NullLogger<UserService>.Instance);
        _vm = new UsersViewModel(new UserQueryService(factory), new AccountQueryService(factory),
            userSvc, queue, settings, new SyncDispatcher(), sites, new FakeCurrentSite());
        _vm.Users.CollectionChanged += (_, _) => { };
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private static User NewUser(string restId, string name, long count = 0, DateTime? last = null) => new()
    { SiteId = "twitter", RestId = restId, ScreenName = name, Source = UserSource.Following, DownloadCount = count, LastDownloadAt = last, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

    [Fact]
    public async Task Refresh_loads_users_with_pinned_first_and_binds_fields()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, _vm.Users.Count);
        Assert.True(_vm.HasUsers);
        Assert.True(_vm.HasAccount);
        var row = _vm.Users.First();
        Assert.Equal("alice", row.Model.ScreenName);
        Assert.Equal("@alice · 关注导入", row.Subtitle);
        Assert.Equal("3", row.DownloadCountText);
        Assert.Equal("—", _vm.Users.Last().LastDownloadText); // bob 无记录
    }

    [Fact]
    public async Task SearchText_filters_after_debounce_window()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.SearchText = "ali";
        await Task.Delay(450); // 300ms 防抖 + 余量
        Assert.Single(_vm.Users);
        _vm.SearchText = "";
        await Task.Delay(450);
        Assert.Equal(2, _vm.Users.Count);
    }

    [Fact]
    public async Task DownloadSelected_enqueues_with_settings_directory()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.Users[0].IsSelected = true;
        Assert.Equal(1, _vm.SelectedCount);
        await _vm.DownloadSelectedCommand.ExecuteAsync(null);
        Assert.Contains("已加入下载队列", _vm.StatusMessage);
    }

    [Fact]
    public async Task DownloadSelected_without_account_blocks_with_message()
    {
        await _db.Accounts.ExecuteDeleteAsync();
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.Users[0].IsSelected = true;
        await _vm.DownloadSelectedCommand.ExecuteAsync(null);
        Assert.Contains("请先在设置中导入 Cookie", _vm.StatusMessage);
    }

    [Fact]
    public async Task DeleteSelected_removes_and_refreshes()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.Users[0].IsSelected = true;
        await _vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Single(_vm.Users);
        Assert.False(_db.Users.AsNoTracking().Any(u => u.ScreenName == "alice"));
    }

    // 修复波 F3 回归：ListAsync 抛异常（坏库等）→ StatusMessage 兜底"刷新失败"，不抛出崩调用线程
    [Fact]
    public async Task Refresh_failure_sets_status_message_instead_of_throwing()
    {
        var factory = new SingleDbContextFactory(_db);
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        var settings = new AppSettings(factory, sites);
        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, sites, NullLogger<DownloadQueueService>.Instance);
        var vm = new UsersViewModel(new ThrowingUserQuery(), new AccountQueryService(factory),
            new UserService(_db, _engine, _paths, sites, NullLogger<UserService>.Instance),
            queue, settings, new SyncDispatcher(), sites, new FakeCurrentSite());
        vm.Users.CollectionChanged += (_, _) => { };

        var ex = await Record.ExceptionAsync(() => vm.RefreshCommand.ExecuteAsync(null));
        Assert.Null(ex);
        Assert.Contains("刷新失败", vm.StatusMessage);
    }

    // 修复波 F4：SortBy 变更触发与 SearchText 同机制的防抖刷新，UserFilter(SortBy) 传入查询
    [Fact]
    public async Task SortBy_change_refreshes_ordered_by_download_count()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _db.Users.Single(u => u.ScreenName == "alice").IsPinned = true; // alice 置顶（QueriesTests 播种模式）
        _db.Users.Single(u => u.ScreenName == "bob").DownloadCount = 1;
        _db.Users.Add(NewUser("3", "carol", count: 9));
        _db.SaveChanges();

        _vm.SortBy = "download_count";
        await Task.Delay(450); // 300ms 防抖 + 余量（防抖路径本身即被测行为）

        Assert.Equal(["alice", "carol", "bob"], _vm.Users.Select(r => r.Model.ScreenName).ToArray()); // 置顶优先，其余按计数降序
    }

    [Fact]
    public async Task Unavailable_site_shows_coming_soon()
    {
        var site = new FakeCurrentSite();
        await site.SelectAsync("pixiv");
        var factory = new SingleDbContextFactory(_db);
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        var settings = new AppSettings(factory, sites);
        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, sites, NullLogger<DownloadQueueService>.Instance);
        var vm = new UsersViewModel(new UserQueryService(factory), new AccountQueryService(factory),
            new UserService(_db, _engine, _paths, sites, NullLogger<UserService>.Instance),
            queue, settings, new SyncDispatcher(), sites, site);
        vm.Users.CollectionChanged += (_, _) => { };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.ShowComingSoon);
        Assert.False(vm.ShowUserList);
        Assert.Contains("Pixiv", vm.ComingSoonMessage);
        Assert.Contains("即将支持", vm.ComingSoonMessage);
    }

    [Fact]
    public async Task DownloadSelected_omits_skipped_users()
    {
        _db.Users.Single(u => u.ScreenName == "alice").IsSkipped = true;
        _db.SaveChanges();
        await _vm.RefreshCommand.ExecuteAsync(null);
        var alice = _vm.Users.Single(r => r.Model.ScreenName == "alice");
        Assert.True(alice.IsSkipped);
        Assert.False(alice.CanDownload);
        Assert.Contains("已跳过", alice.Subtitle);

        _vm.SelectAll(true);
        await _vm.DownloadSelectedCommand.ExecuteAsync(null);
        Assert.Contains("跳过 1 个", _vm.StatusMessage);
        Assert.Single(_db.Jobs);
        Assert.Equal(_db.Users.Single(u => u.ScreenName == "bob").Id, _db.Jobs.Single().UserId);
    }

    [Fact]
    public async Task DownloadOne_blocked_when_user_is_skipped()
    {
        _db.Users.Single(u => u.ScreenName == "alice").IsSkipped = true;
        _db.SaveChanges();
        await _vm.RefreshCommand.ExecuteAsync(null);
        await _vm.Users.Single(r => r.Model.ScreenName == "alice").DownloadCommand.ExecuteAsync(null);
        Assert.Contains("已跳过下载", _vm.StatusMessage);
        Assert.Empty(_db.Jobs);
    }

    [Fact]
    public async Task SkipSelected_marks_users_and_unskip_clears()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        var id = _vm.Users[0].Model.Id;
        _vm.Users[0].IsSelected = true;
        await _vm.SkipSelectedCommand.ExecuteAsync(null);
        Assert.True(_db.Users.AsNoTracking().Single(u => u.Id == id).IsSkipped);
        Assert.Contains("已跳过", _vm.StatusMessage);

        _vm.Users.Single(r => r.Model.Id == id).IsSelected = true;
        await _vm.UnskipSelectedCommand.ExecuteAsync(null);
        Assert.False(_db.Users.AsNoTracking().Single(u => u.Id == id).IsSkipped);
    }

    [Fact]
    public async Task AddSelectedToList_and_remove_selected_toggle_flag()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        var id = _vm.Users[0].Model.Id;
        Assert.Equal("加入清单", _vm.Users[0].ListButtonText);
        _vm.Users[0].IsSelected = true;
        await _vm.AddSelectedToListCommand.ExecuteAsync(null);
        Assert.True(_db.Users.AsNoTracking().Single(u => u.Id == id).InDownloadList);
        Assert.Contains("已加入下载清单", _vm.StatusMessage);
        Assert.Equal("移出清单", _vm.Users.Single(r => r.Model.Id == id).ListButtonText);

        _vm.Users.Single(r => r.Model.Id == id).IsSelected = true;
        await _vm.RemoveSelectedFromListCommand.ExecuteAsync(null);
        Assert.False(_db.Users.AsNoTracking().Single(u => u.Id == id).InDownloadList);
        Assert.Contains("已移出下载清单", _vm.StatusMessage);
        Assert.Equal("加入清单", _vm.Users.Single(r => r.Model.Id == id).ListButtonText);
    }

    [Fact]
    public async Task AddSelectedToList_omits_skipped_users()
    {
        _db.Users.Single(u => u.ScreenName == "alice").IsSkipped = true;
        _db.SaveChanges();
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.SelectAll(true);
        await _vm.AddSelectedToListCommand.ExecuteAsync(null);
        Assert.False(_db.Users.AsNoTracking().Single(u => u.ScreenName == "alice").InDownloadList);
        Assert.True(_db.Users.AsNoTracking().Single(u => u.ScreenName == "bob").InDownloadList);
    }

    [Fact]
    public async Task SkipSelected_removes_from_download_list()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        var id = _vm.Users[0].Model.Id;
        _vm.Users[0].IsSelected = true;
        await _vm.AddSelectedToListCommand.ExecuteAsync(null);
        _vm.Users.Single(r => r.Model.Id == id).IsSelected = true;
        await _vm.SkipSelectedCommand.ExecuteAsync(null);
        var user = _db.Users.AsNoTracking().Single(u => u.Id == id);
        Assert.True(user.IsSkipped);
        Assert.False(user.InDownloadList);
    }

    // 修复波 F4：两态全选 → 逐行 IsSelected 且 SelectedCount 同步更新
    [Fact]
    public async Task SelectAll_sets_every_row_and_updates_SelectedCount()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.SelectAll(true);
        Assert.Equal(2, _vm.SelectedCount);
        Assert.All(_vm.Users, r => Assert.True(r.IsSelected));
        _vm.SelectAll(false);
        Assert.Equal(0, _vm.SelectedCount);
    }

    private sealed class ThrowingUserQuery : IUserQueryService
    {
        public Task<IReadOnlyList<User>> ListAsync(string siteId, UserFilter filter, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<User>> ListDownloadListAsync(string siteId, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}
