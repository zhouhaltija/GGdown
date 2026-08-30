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
    private int _selectionNotifications;

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
            userSvc, queue, settings, new SyncDispatcher(), sites);
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
}
