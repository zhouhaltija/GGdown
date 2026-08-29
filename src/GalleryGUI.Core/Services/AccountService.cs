using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalleryGUI.Services;

public sealed record ImportCookiesResult(Account Account, string? Error = null)
{ public bool Ok => Error is null; }

public interface IAccountService
{
    Task<ImportCookiesResult> ImportCookiesAsync(string siteId, string cookiesFilePath, CancellationToken ct = default);
    Task<ImportCookiesResult> VerifyAsync(Account account, CancellationToken ct = default);
    Task DeleteAsync(Account account, CancellationToken ct = default);
}

public sealed class AccountService(
    GalleryDbContext db,
    IDownloadEngine engine,
    IAppPaths paths,
    SiteRegistry sites,
    ILogger<AccountService> log) : IAccountService
{
    public static string AbsoluteCookiePath(IAppPaths paths, Account account) =>
        Path.Combine(paths.AccountsDir, account.CookiePath);

    public async Task<ImportCookiesResult> ImportCookiesAsync(
        string siteId, string cookiesFilePath, CancellationToken ct = default)
    {
        sites.Get(siteId); // 校验站点存在
        var id = Guid.NewGuid().ToString("N");
        var relative = Path.Combine(siteId, id, "cookies.txt");
        var target = Path.Combine(paths.AccountsDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(cookiesFilePath, target, overwrite: true);

        // 每站点仅一个活动账号：先全部停用
        await db.Accounts
            .Where(a => a.SiteId == siteId && a.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false), ct);

        var account = new Account
        {
            SiteId = siteId,
            CookiePath = relative,
            Status = AccountStatus.Unverified,
            IsActive = true,
            AddedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);

        var verify = await VerifyAsync(account, ct);
        return verify.Ok
            ? new ImportCookiesResult(verify.Account)
            : new ImportCookiesResult(verify.Account, verify.Error);
    }

    public async Task<ImportCookiesResult> VerifyAsync(Account account, CancellationToken ct = default)
    {
        try
        {
            var who = await engine.WhoAmIAsync(AbsoluteCookiePath(paths, account), ct);
            account.ScreenName = who.ScreenName;
            account.DisplayName = who.DisplayName;
            account.Status = AccountStatus.Ok;
            account.VerifiedAt = DateTime.UtcNow;
        }
        catch (AuthException e)
        {
            log.LogWarning(e, "账号验证失败（cookie 无效）");
            account.Status = AccountStatus.Invalid;
        }
        catch (EngineException e)
        {
            log.LogWarning(e, "账号验证失败（引擎错误）");
            return new(account, $"引擎错误：{e.Message}");
        }
        await db.SaveChangesAsync(ct);
        return account.Status == AccountStatus.Ok
            ? new(account)
            : new(account, "Cookie 无效或已过期，请重新导出");
    }

    public async Task DeleteAsync(Account account, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(AbsoluteCookiePath(paths, account));
        db.Accounts.Remove(account);
        await db.SaveChangesAsync(ct);
        if (dir is not null && Directory.Exists(dir))
            try { Directory.Delete(dir, true); } catch { /* 尽力清理 */ }
    }
}
