using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using Microsoft.Data.Sqlite; // 适配：字段元组类型 SqliteConnection 需要（同 SettingsStoreTests）
using Microsoft.EntityFrameworkCore; // 适配：AsNoTracking（ExecuteUpdate 绕过变更跟踪器，需绕开陈旧跟踪实例读库中真实状态）
using Microsoft.Extensions.Logging.Abstractions;

namespace GalleryGUI.Tests.Services;

public class AccountServiceTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
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
        var result = await _svc.ImportCookiesAsync("twitter", MakeCookiesFile());
        Assert.True(result.Ok);
        Assert.Equal(AccountStatus.Ok, result.Account.Status);
        Assert.Equal("stub_user", result.Account.ScreenName);
        Assert.True(result.Account.IsActive);
        // 文件已复制到 accounts 目录（不再引用原路径）
        Assert.True(File.Exists(AccountService.AbsoluteCookiePath(_paths, result.Account)));
        Assert.StartsWith($"twitter{Path.DirectorySeparatorChar}", result.Account.CookiePath);
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
        Assert.Single(all.Where(a => a.IsActive));
        Assert.Equal(second.Account.Id, all.Single(a => a.IsActive).Id);
    }
}
