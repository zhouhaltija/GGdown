using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using GalleryGUI.Threading;
using GalleryGUI.ViewModels;
using Microsoft.Data.Sqlite; // 补 using：SqliteConnection 在 tuple 类型中未限定（同 UsersViewModelTests）
using Microsoft.EntityFrameworkCore; // 补 using：无账号用例用了 ExecuteDeleteAsync（同 UsersViewModelTests）
using Microsoft.Extensions.Logging.Abstractions;

namespace GalleryGUI.Tests.ViewModels;

/// <summary>
/// B4 Step 1：ImportViewModel 测试。组装与 UsersViewModelTests 同模式
/// （TestDb/SingleDbContextFactory/FakeEngine/TestPaths/SiteRegistry + 真服务）。
/// 播种账号 Status 用 Unverified 而非 UsersViewModelTests 的 Ok（偏离 1，见报告）：
/// 两个 Cookie 用例分别断言全库唯一 Ok / 唯一 Invalid 账号，播种 Ok 会让 Assert.Single 计数为 2；
/// AddUser/关注导入只要求活动账号存在（IsActive），不检查 Status。
/// </summary>
public class ImportViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine = new();
    private readonly GalleryDbContext _db;
    private readonly ImportViewModel _vm;

    public ImportViewModelTests()
    {
        _t = TestDb.Create();
        _db = _t.Item2;
        var factory = new SingleDbContextFactory(_db);
        _db.Accounts.Add(new Account { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Unverified, IsActive = true, AddedAt = DateTime.UtcNow });
        _db.SaveChanges();
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        var accountSvc = new AccountService(_db, _engine, _paths, sites, NullLogger<AccountService>.Instance);
        var userSvc = new UserService(_db, _engine, _paths, sites, NullLogger<UserService>.Instance);
        _vm = new ImportViewModel(accountSvc, userSvc, new AccountQueryService(factory), new SyncDispatcher(), sites);
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
    public async Task ImportFollowing_without_account_is_blocked()
    {
        await _db.Accounts.ExecuteDeleteAsync(); // 偏离 2（见报告）：brief 用例体未清账号，但组装播种了活动账号——同 UsersViewModelTests 无账号用例先清空
        Assert.False(await _vm.ImportFollowingAsync());
        Assert.Contains("Cookie", _vm.ResultMessage);
    }
}
