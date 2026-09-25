using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Sites;
using GGdown.Threading;
using GGdown.ViewModels;
using Microsoft.Data.Sqlite; // 补 using：SqliteConnection 在 tuple 类型中未限定（同 UsersViewModelTests）
using Microsoft.EntityFrameworkCore; // 补 using：无账号用例用了 ExecuteDeleteAsync（同 UsersViewModelTests）
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.ViewModels;

/// <summary>
/// B4 Step 1：ImportViewModel 测试。组装与 UsersViewModelTests 同模式
/// （TestDb/SingleDbContextFactory/FakeEngine/TestPaths/SiteRegistry + 真服务）。
/// 播种账号 Status 用 Unverified 而非 UsersViewModelTests 的 Ok（偏离 1，见报告）：
/// 两个 Cookie 用例分别断言全库唯一 Ok / 唯一 Invalid 账号，播种 Ok 会让 Assert.Single 计数为 2；
/// AddUser/关注导入只要求活动账号存在（IsActive），不检查 Status。
/// </summary>
public class ImportViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GGdownSiteDbContext) _t;
    private readonly (SqliteConnection, GGdownGlobalDbContext) _global = TestDb.CreateGlobal();
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine = new();
    private readonly GGdownSiteDbContext _db;
    private readonly ImportViewModel _vm;

    public ImportViewModelTests()
    {
        _t = TestDb.CreateSite();
        _db = _t.Item2;
        var factory = new SingleDbContextFactory(_db);
        _db.Accounts.Add(new Account { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Unverified, IsActive = true, AddedAt = DateTime.UtcNow });
        _db.SaveChanges();
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        var accountSvc = new AccountService(new SingleSiteDbContextFactory(_db), _engine, _paths, sites, NullLogger<AccountService>.Instance);
        var userSvc = new UserService(new SingleSiteDbContextFactory(_db), _engine, _paths, sites, NullLogger<UserService>.Instance);
        var settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), sites, new SiteDbContextFactory(_paths));
        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, sites, NullLogger<DownloadQueueService>.Instance);
        _vm = new ImportViewModel(accountSvc, userSvc, new AccountQueryService(new SingleSiteDbContextFactory(_db)), queue, settings, new SyncDispatcher(), sites, new FakeCurrentSite());
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [Fact]
    public async Task AddUser_rejects_invalid_input_without_calling_service()
    {
        _vm.UserInput = "!!bad!!";
        _vm.ValidateUserInput();
        Assert.False(await _vm.AddUserAsync());
        Assert.NotNull(_vm.UserInputError);
        Assert.Empty(_db.Users); // 服务未被调用
    }

    [Fact]
    public async Task AddUser_tweet_url_enqueues_permalink_without_creating_user()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, ct) => await gate.Task.WaitAsync(ct);
        _vm.UserInput = "https://x.com/alice/status/12345";
        _vm.ValidateUserInput();
        Assert.Null(_vm.UserInputError);
        Assert.True(await _vm.AddUserAsync());
        Assert.Contains("已加入下载队列", _vm.ResultMessage);
        Assert.Empty(_db.Users);
        Assert.Single(_db.Jobs);
        Assert.Equal(TargetKind.Permalink, _db.Jobs.Single().TargetKind);
        gate.SetResult();
    }

    [Fact]
    public async Task AddUser_valid_input_creates_user_and_fires_import_completed()
    {
        var fired = false;
        _vm.ImportCompleted += () => fired = true;
        _vm.UserInput = "carol";
        _vm.ValidateUserInput();
        Assert.Null(_vm.UserInputError);
        Assert.True(await _vm.AddUserAsync());
        Assert.True(fired);
        Assert.Single(_db.Users);
    }

    [Fact]
    public async Task ImportCookies_copies_file_sets_account_and_reports()
    {
        var file = Path.Combine(_paths.TempDir, "cookies.txt");
        await File.WriteAllTextAsync(file, "# Netscape HTTP Cookie File");
        Assert.True(await _vm.ImportCookiesAsync(file));
        Assert.Contains("导入成功", _vm.ResultMessage);
        Assert.Single(_db.Accounts.Where(a => a.Status == AccountStatus.Ok));
    }

    [Fact]
    public async Task ImportCookies_invalid_cookie_reports_error_and_marks_invalid()
    {
        _engine.WhoAmIError = new AuthException("cookie 无效");
        var file = Path.Combine(_paths.TempDir, "bad.txt");
        await File.WriteAllTextAsync(file, "junk");
        Assert.False(await _vm.ImportCookiesAsync(file));
        Assert.Contains("Cookie", _vm.ResultMessage);
        Assert.Single(_db.Accounts.Where(a => a.Status == AccountStatus.Invalid));
    }

    [Fact]
    public async Task ImportCookies_pixiv_without_token_reports_error()
    {
        var factory = new SingleDbContextFactory(_db);
        var site = new FakeCurrentSite();
        await site.SelectAsync("pixiv");
        var sites = new SiteRegistry([new TwitterSiteProvider(), new PixivSiteProvider()]);
        var settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), sites, new SiteDbContextFactory(_paths));
        var queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, sites, NullLogger<DownloadQueueService>.Instance);
        var vm = new ImportViewModel(
            new AccountService(new SingleSiteDbContextFactory(_db), _engine, _paths, sites, NullLogger<AccountService>.Instance),
            new UserService(new SingleSiteDbContextFactory(_db), _engine, _paths, sites, NullLogger<UserService>.Instance),
            new AccountQueryService(new SingleSiteDbContextFactory(_db)), queue, settings, new SyncDispatcher(), sites, site);
        var file = Path.Combine(_paths.TempDir, "pixiv-cookies.txt");
        await File.WriteAllTextAsync(file, "# Netscape HTTP Cookie File");
        Assert.False(await vm.ImportCookiesAsync(file));
        Assert.Contains("refresh-token", vm.ResultMessage, StringComparison.OrdinalIgnoreCase);
    }
}
