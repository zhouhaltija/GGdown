using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Sites;
using GGdown.Threading;
using GGdown.ViewModels;
using Microsoft.Data.Sqlite; // 补 using：SqliteConnection 在 tuple 类型中未限定（brief 已知偏离模式，同 QueriesTests）
using Microsoft.EntityFrameworkCore; // 补 using：测试体用了 ExecuteDeleteAsync/AsNoTracking（brief 遗漏）
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.ViewModels;

public class UsersViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GGdownDbContext) _t;
    private readonly (SqliteConnection, GGdownGlobalDbContext) _global = TestDb.CreateGlobal();
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine = new();
    private readonly GGdownDbContext _db;
    private readonly UsersViewModel _vm;
    private readonly Account _account;
    private readonly AppSettings _settings;

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
        // 原文 new AppSettings(new GGdownSettingsStore(_db), sites) 已无法编译
        _settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), sites, new SiteDbContextFactory(_paths));
        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, sites, NullLogger<DownloadQueueService>.Instance);
        var accountSvc = new AccountService(_db, _engine, _paths, sites, NullLogger<AccountService>.Instance);
        var userSvc = new UserService(_db, _engine, _paths, sites, NullLogger<UserService>.Instance);
        _vm = new UsersViewModel(new UserQueryService(factory), new AccountQueryService(factory),
            userSvc, queue, _settings, new SyncDispatcher(), sites, new FakeCurrentSite(),
            new HistoryQueryService(factory));
        _vm.Users.CollectionChanged += (_, _) => { };
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        _global.Item1.Dispose();
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
    public async Task DownloadSelected_clears_selection_after_enqueue()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.Users[0].IsSelected = true;
        _vm.Users[1].IsSelected = true;
        await _vm.DownloadSelectedCommand.ExecuteAsync(null);
        Assert.Contains("已加入下载队列", _vm.StatusMessage);
        Assert.Equal(0, _vm.SelectedCount);
        Assert.All(_vm.Users, r => Assert.False(r.IsSelected));
    }

    [Fact]
    public async Task DownloadSelected_without_account_blocks_with_message()
    {
        await _db.Accounts.ExecuteDeleteAsync();
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.Users[0].IsSelected = true;
        await _vm.DownloadSelectedCommand.ExecuteAsync(null);
        Assert.Contains("请先在设置中导入 Cookie", _vm.StatusMessage);
        Assert.Equal(1, _vm.SelectedCount);
        Assert.True(_vm.Users[0].IsSelected);
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
        var settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), sites, new SiteDbContextFactory(_paths));
        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, sites, NullLogger<DownloadQueueService>.Instance);
        var vm = new UsersViewModel(new ThrowingUserQuery(), new AccountQueryService(factory),
            new UserService(_db, _engine, _paths, sites, NullLogger<UserService>.Instance),
            queue, settings, new SyncDispatcher(), sites, new FakeCurrentSite(),
            new HistoryQueryService(factory));
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
        await site.SelectAsync("fanbox");
        var factory = new SingleDbContextFactory(_db);
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        var settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), sites, new SiteDbContextFactory(_paths));
        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, sites, NullLogger<DownloadQueueService>.Instance);
        var vm = new UsersViewModel(new UserQueryService(factory), new AccountQueryService(factory),
            new UserService(_db, _engine, _paths, sites, NullLogger<UserService>.Instance),
            queue, settings, new SyncDispatcher(), sites, site,
            new HistoryQueryService(factory));
        vm.Users.CollectionChanged += (_, _) => { };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.ShowComingSoon);
        Assert.False(vm.ShowUserList);
        Assert.Contains("pixivFANBOX", vm.ComingSoonMessage);
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
        Assert.Contains("已暂停", alice.Subtitle);

        _vm.SelectAll(true);
        await _vm.DownloadSelectedCommand.ExecuteAsync(null);
        Assert.Contains("暂停 1 个", _vm.StatusMessage);
        Assert.Single(_db.Jobs);
        Assert.Equal(_db.Users.Single(u => u.ScreenName == "bob").Id, _db.Jobs.Single().UserId);
    }

    [Fact]
    public async Task Download_marks_row_as_downloading_until_job_finishes()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, ct) => await gate.Task.WaitAsync(ct);
        await _vm.RefreshCommand.ExecuteAsync(null);
        var alice = _vm.Users.Single(r => r.Model.ScreenName == "alice");
        await alice.DownloadCommand.ExecuteAsync(null);

        Assert.True(alice.IsDownloading);
        Assert.False(alice.CanDownload);
        Assert.Equal("下载中", alice.DownloadButtonText);
        Assert.Contains("下载中", alice.Subtitle);

        gate.SetResult();
        await WaitUntil(() => !alice.IsDownloading);
        Assert.True(alice.CanDownload);
        Assert.Equal("下载", alice.DownloadButtonText);
        Assert.DoesNotContain("下载中", alice.Subtitle);
    }

    [Fact]
    public async Task DownloadOne_blocked_when_user_is_already_queued()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, ct) => await gate.Task.WaitAsync(ct);
        await _vm.RefreshCommand.ExecuteAsync(null);
        var alice = _vm.Users.Single(r => r.Model.ScreenName == "alice");
        await alice.DownloadCommand.ExecuteAsync(null);
        var jobs = _db.Jobs.Count();
        await alice.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(jobs, _db.Jobs.Count());
        Assert.Contains("已在下载队列", _vm.StatusMessage);
        gate.SetResult();
        await WaitUntil(() => !alice.IsDownloading);
    }

    [Fact]
    public async Task DownloadSelected_skips_users_already_in_queue()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, ct) => await gate.Task.WaitAsync(ct);
        await _vm.RefreshCommand.ExecuteAsync(null);
        var alice = _vm.Users.Single(r => r.Model.ScreenName == "alice");
        await alice.DownloadCommand.ExecuteAsync(null);
        var aliceJobs = _db.Jobs.Count(j => j.UserId == alice.Model.Id);

        var bobId = _vm.Users.Single(r => r.Model.ScreenName == "bob").Model.Id;
        _vm.SelectAll(true);
        await _vm.DownloadSelectedCommand.ExecuteAsync(null);
        Assert.Contains("跳过 1 个下载中", _vm.StatusMessage);
        Assert.Equal(aliceJobs, _db.Jobs.Count(j => j.UserId == alice.Model.Id));
        Assert.Equal(1, _db.Jobs.Count(j => j.UserId == bobId));
        Assert.Equal(0, _vm.SelectedCount);
        gate.SetResult();
        await WaitUntil(() => _vm.Users.All(r => !r.IsDownloading));
    }

    [Fact]
    public async Task Refresh_preserves_downloading_state()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, ct) => await gate.Task.WaitAsync(ct);
        await _vm.RefreshCommand.ExecuteAsync(null);
        await _vm.Users.Single(r => r.Model.ScreenName == "alice").DownloadCommand.ExecuteAsync(null);
        await _vm.RefreshCommand.ExecuteAsync(null);
        var alice = _vm.Users.Single(r => r.Model.ScreenName == "alice");
        Assert.True(alice.IsDownloading);
        Assert.Equal("下载中", alice.DownloadButtonText);
        gate.SetResult();
        await WaitUntil(() => !alice.IsDownloading);
    }

    [Fact]
    public async Task DownloadOne_blocked_when_user_is_skipped()
    {
        _db.Users.Single(u => u.ScreenName == "alice").IsSkipped = true;
        _db.SaveChanges();
        await _vm.RefreshCommand.ExecuteAsync(null);
        await _vm.Users.Single(r => r.Model.ScreenName == "alice").DownloadCommand.ExecuteAsync(null);
        Assert.Contains("已暂停下载", _vm.StatusMessage);
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
        Assert.Contains("已暂停", _vm.StatusMessage);
        Assert.Equal("恢复", _vm.Users.Single(r => r.Model.Id == id).SkipButtonText);

        _vm.Users.Single(r => r.Model.Id == id).IsSelected = true;
        await _vm.UnskipSelectedCommand.ExecuteAsync(null);
        Assert.False(_db.Users.AsNoTracking().Single(u => u.Id == id).IsSkipped);
        Assert.Contains("已恢复", _vm.StatusMessage);
        Assert.Equal("暂停", _vm.Users.Single(r => r.Model.Id == id).SkipButtonText);
    }

    [Fact]
    public async Task Download_pixiv_enqueues_artworks_and_novels()
    {
        var site = new FakeCurrentSite();
        await site.SelectAsync("pixiv");
        var factory = new SingleDbContextFactory(_db);
        var sites = new SiteRegistry([new PixivSiteProvider()]);
        var account = new Account
        {
            SiteId = "pixiv", CookiePath = "c", RestId = "1", ScreenName = "me",
            Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            SiteId = "pixiv", RestId = "12345", ScreenName = "foo", DisplayName = "Foo",
            Source = UserSource.Manual, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Accounts.Add(account);
        _db.Users.Add(user);
        _db.SaveChanges();
        var settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), sites, new SiteDbContextFactory(_paths));
        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, sites,
            NullLogger<DownloadQueueService>.Instance);
        var vm = new UsersViewModel(new UserQueryService(factory), new AccountQueryService(factory),
            new UserService(_db, _engine, _paths, sites, NullLogger<UserService>.Instance),
            queue, settings, new SyncDispatcher(), sites, site,
            new HistoryQueryService(factory));
        vm.Users.CollectionChanged += (_, _) => { };
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.Users.Single().DownloadCommand.ExecuteAsync(null);
        await WaitJobs(2, account.Id);
        Assert.Contains(_db.Jobs, j => j.TargetKind == TargetKind.UserMedia);
        Assert.Contains(_db.Jobs, j => j.TargetKind == TargetKind.UserNovels);
    }

    [Fact]
    public async Task Download_pixiv_both_toggles_off_prompts()
    {
        var site = new FakeCurrentSite();
        await site.SelectAsync("pixiv");
        var factory = new SingleDbContextFactory(_db);
        var sites = new SiteRegistry([new PixivSiteProvider()]);
        var account = new Account
        {
            SiteId = "pixiv", CookiePath = "c", RestId = "1", ScreenName = "me",
            Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            SiteId = "pixiv", RestId = "9", ScreenName = "z",
            Source = UserSource.Manual, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Accounts.Add(account);
        _db.Users.Add(user);
        _db.SaveChanges();
        var settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), sites, new SiteDbContextFactory(_paths));
        await settings.SetSiteOptionsAsync("pixiv", new Dictionary<string, object?>
        {
            ["download_artworks"] = false, ["download_novels"] = false,
        });
        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, sites,
            NullLogger<DownloadQueueService>.Instance);
        var vm = new UsersViewModel(new UserQueryService(factory), new AccountQueryService(factory),
            new UserService(_db, _engine, _paths, sites, NullLogger<UserService>.Instance),
            queue, settings, new SyncDispatcher(), sites, site,
            new HistoryQueryService(factory));
        vm.Users.CollectionChanged += (_, _) => { };
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.Users.Single().DownloadCommand.ExecuteAsync(null);
        Assert.Contains("至少启用一种作品类型", vm.StatusMessage);
        Assert.Empty(_db.Jobs.Where(j => j.AccountId == account.Id));
    }

    [Fact]
    public async Task ShowFollowingList_enabled_only_with_account()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(_vm.CanShowFollowingList);
        Assert.True(_vm.ShowFollowingListCommand.CanExecute(null));

        await _db.Accounts.ExecuteDeleteAsync();
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(_vm.CanShowFollowingList);
        Assert.False(_vm.ShowFollowingListCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenFolder_without_downloaded_files_sets_status()
    {
        await _settings.SetDownloadDirectoryAsync(Path.Combine(_paths.Root, "dl"));
        await _vm.RefreshCommand.ExecuteAsync(null);
        await _vm.Users[0].OpenFolderCommand.ExecuteAsync(null);
        Assert.Contains("还没有", _vm.StatusMessage);
    }

    [Fact]
    public async Task OpenFolder_with_recorded_but_missing_directory_sets_status()
    {
        await _settings.SetDownloadDirectoryAsync(Path.Combine(_paths.Root, "dl"));
        await _vm.RefreshCommand.ExecuteAsync(null);
        var alice = _vm.Users[0].Model;
        var job = new DownloadJob
        { AccountId = _account.Id, TargetKind = TargetKind.UserMedia, UserId = alice.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();
        var missing = Path.Combine(Path.GetTempPath(), "ggui-missing-user-dir-" + Guid.NewGuid().ToString("N"), "a.jpg");
        _db.Files.Add(new DownloadFile
        { JobId = job.Id, UserId = alice.Id, SourceItemId = "1", Url = "u", FilePath = missing, FileSize = 1, Status = FileStatus.Downloaded, CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        await _vm.Users[0].OpenFolderCommand.ExecuteAsync(null);
        Assert.Equal("目录不存在", _vm.StatusMessage);
    }

    // 修复波 F4：两态全选 → 逐行 IsSelected 且 SelectedCount 同步更新
    [Fact]
    public async Task NewMedia_badge_when_count_grew_since_last_download()
    {
        _db.Users.Single(u => u.ScreenName == "alice").MediaCount = 12;
        _db.Users.Single(u => u.ScreenName == "alice").MediaCountAtDownload = 10;
        _db.SaveChanges();
        await _vm.RefreshCommand.ExecuteAsync(null);
        var alice = _vm.Users.Single(r => r.Model.ScreenName == "alice");
        Assert.True(alice.HasNewMedia);
        Assert.Equal(2, alice.NewMediaCount);
        Assert.Equal("新 +2", alice.NewMediaText);
        Assert.True(alice.ShowHighlights);
    }

    [Fact]
    public async Task DownloadHighlights_enqueues_highlights_kind()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, ct) => await gate.Task.WaitAsync(ct);
        await _vm.RefreshCommand.ExecuteAsync(null);
        await _vm.Users.Single(r => r.Model.ScreenName == "alice").DownloadHighlightsCommand.ExecuteAsync(null);
        Assert.Contains("高光", _vm.StatusMessage);
        Assert.Contains(_db.Jobs, j => j.TargetKind == TargetKind.UserHighlights);
        gate.SetResult();
    }

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

    [Fact]
    public async Task InvertSelection_flips_each_row()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _vm.Users[0].IsSelected = true;
        _vm.InvertSelection();
        Assert.False(_vm.Users[0].IsSelected);
        Assert.True(_vm.Users[1].IsSelected);
        Assert.Equal(1, _vm.SelectedCount);
        _vm.InvertSelection();
        Assert.True(_vm.Users[0].IsSelected);
        Assert.False(_vm.Users[1].IsSelected);
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 5000)
    {
        var start = Environment.TickCount;
        while (!cond())
        {
            if (Environment.TickCount - start > timeoutMs) throw new TimeoutException("等待条件超时");
            await Task.Delay(20);
        }
    }

    private async Task WaitJobs(int count, long accountId, int timeoutMs = 5000)
    {
        var start = Environment.TickCount;
        while (_db.Jobs.Count(j => j.AccountId == accountId) < count)
        {
            if (Environment.TickCount - start > timeoutMs) throw new TimeoutException("等待入队超时");
            await Task.Delay(20);
        }
    }

    private sealed class ThrowingUserQuery : IUserQueryService
    {
        public Task<IReadOnlyList<User>> ListAsync(string siteId, UserFilter filter, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<User>> ListDownloadListAsync(string siteId, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}
