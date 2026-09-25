using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Sites;
using Microsoft.Data.Sqlite; // 适配：字段元组类型 SqliteConnection 需要（同 SettingsStoreTests）
using Microsoft.EntityFrameworkCore; // 适配：AsNoTracking（ExecuteUpdate 绕过变更跟踪器，需绕开陈旧跟踪实例读库中真实状态）
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.Services;

public class AccountServiceTests : IDisposable
{
    private readonly (SqliteConnection, GGdownDbContext) _t;
    private readonly AppPaths _paths;
    private readonly FakeEngine _engine;
    private readonly AccountService _svc;

    public AccountServiceTests()
    {
        _t = TestDb.Create();
        _paths = TestPaths.Create();
        _engine = new FakeEngine();
        _svc = new AccountService(_t.Item2, _engine, _paths,
            new SiteRegistry([new TwitterSiteProvider()]),
            NullLogger<AccountService>.Instance);
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private string MakeCookiesFile()
    {
        var file = Path.Combine(_paths.TempDir, $"cookies-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "# Netscape HTTP Cookie File\n");
        return file;
    }

    [Fact]
    public async Task Import_copies_file_creates_account_and_verifies()
    {
        _engine.WhoAmI = new AccountInfo("stub_user", "Stub User", "99");
        var result = await _svc.ImportCookiesAsync("twitter", MakeCookiesFile());
        Assert.True(result.Ok);
        Assert.Equal(AccountStatus.Ok, result.Account.Status);
        Assert.Equal("stub_user", result.Account.ScreenName);
        Assert.Equal("99", result.Account.RestId);
        Assert.True(result.Account.IsActive);
        // 文件已复制到 accounts 目录（不再引用原路径）
        Assert.True(File.Exists(AccountService.AbsoluteCookiePath(_paths, result.Account)));
        Assert.StartsWith($"twitter{Path.DirectorySeparatorChar}", result.Account.CookiePath);
    }

    [Fact]
    public async Task Import_writes_refresh_token_beside_cookies()
    {
        var result = await _svc.ImportCookiesAsync("twitter", MakeCookiesFile(), "secret-token");
        Assert.True(result.Ok);
        var tokenPath = Path.Combine(
            Path.GetDirectoryName(AccountService.AbsoluteCookiePath(_paths, result.Account))!,
            "refresh-token.txt");
        Assert.True(File.Exists(tokenPath));
        Assert.Equal("secret-token", File.ReadAllText(tokenPath).Trim());
    }

    [Fact]
    public async Task Import_pixiv_without_refresh_token_fails()
    {
        var svc = new AccountService(_t.Item2, _engine, _paths,
            new SiteRegistry([new TwitterSiteProvider(), new PixivSiteProvider()]),
            NullLogger<AccountService>.Instance);
        var result = await svc.ImportCookiesAsync("pixiv", MakeCookiesFile());
        Assert.False(result.Ok);
        Assert.Contains("refresh-token", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_with_invalid_cookie_marks_Invalid()
    {
        _engine.WhoAmIError = new AuthException("cookie 无效");
        var result = await _svc.ImportCookiesAsync("twitter", MakeCookiesFile());
        Assert.False(result.Ok);
        Assert.Equal(AccountStatus.Invalid, result.Account.Status);
        Assert.Contains("Cookie", result.Error);
    }

    [Fact]
    public async Task New_import_deactivates_previous_account()
    {
        await _svc.ImportCookiesAsync("twitter", MakeCookiesFile());
        var second = await _svc.ImportCookiesAsync("twitter", MakeCookiesFile());
        // 适配：ExecuteUpdate 绕过变更跟踪器，同一 DbContext 的跟踪查询会返回陈旧实例，用 AsNoTracking 读库中真实状态
        var all = _t.Item2.Accounts.AsNoTracking().Where(a => a.SiteId == "twitter").ToList();
        Assert.Equal(2, all.Count);
        Assert.Single(all, a => a.IsActive); // B8 警告清理：xUnit2031（Where 后接 Assert.Single → 用带谓词重载）
        Assert.Equal(second.Account.Id, all.Single(a => a.IsActive).Id);
    }

    // ---- B7 审查 Important 回归：VerifyAsync 对游离实体（GetActiveAsync 产出的 AsNoTracking 实例）须落库 ----

    /// <summary>复刻 IAccountQueryService.GetActiveAsync 的产出方式：独立 factory 上下文 + AsNoTracking → 游离实体。</summary>
    private Account GetDetachedActiveAccount()
    {
        var factory = new SingleDbContextFactory(_t.Item2);
        using var queryDb = factory.CreateDbContext();
        return queryDb.Accounts.AsNoTracking().Single(a => a.SiteId == "twitter" && a.IsActive);
    }

    [Fact]
    public async Task Verify_persists_invalid_status_for_detached_entity()
    {
        await _svc.ImportCookiesAsync("twitter", MakeCookiesFile()); // 先导入，取得活动账号
        _engine.WhoAmIError = new AuthException("cookie 已过期");
        var result = await _svc.VerifyAsync(GetDetachedActiveAccount());
        Assert.False(result.Ok);
        Assert.Equal(AccountStatus.Invalid, result.Account.Status);
        // 游离实例的改写必须落库：AsNoTracking 重读断言（SaveChanges 对游离实体是空操作，修复前此处失败）
        var reread = _t.Item2.Accounts.AsNoTracking().Single(a => a.SiteId == "twitter" && a.IsActive);
        Assert.Equal(AccountStatus.Invalid, reread.Status);
    }

    [Fact]
    public async Task Verify_persists_ok_status_for_detached_entity()
    {
        await _svc.ImportCookiesAsync("twitter", MakeCookiesFile());
        var account = GetDetachedActiveAccount();
        // 模拟导入即失效后重新验证成功：库中先落 Invalid，再以正常 WhoAmI 重验
        _engine.WhoAmIError = new AuthException("临时失效");
        await _svc.VerifyAsync(account);
        _engine.WhoAmIError = null;
        var result = await _svc.VerifyAsync(account);
        Assert.True(result.Ok);
        Assert.Equal("stub_user", result.Account.ScreenName);
        Assert.Equal("99", result.Account.RestId);
        Assert.NotNull(result.Account.VerifiedAt);
        var reread = _t.Item2.Accounts.AsNoTracking().Single(a => a.SiteId == "twitter" && a.IsActive);
        Assert.Equal(AccountStatus.Ok, reread.Status);
        Assert.Equal("stub_user", reread.ScreenName);
        Assert.Equal("99", reread.RestId);
    }
}
