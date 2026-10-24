using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GGdown.Services;

public sealed record ImportCookiesResult(Account Account, string? Error = null)
{ public bool Ok => Error is null; }

public interface IAccountService
{
    Task<ImportCookiesResult> ImportCookiesAsync(string siteId, string? cookiesFilePath,
        string? refreshToken = null, CancellationToken ct = default);
    Task<ImportCookiesResult> VerifyAsync(Account account, CancellationToken ct = default);
    Task DeleteAsync(Account account, CancellationToken ct = default);
}

public sealed class AccountService(
    ISiteDbContextFactory siteFactory,
    IDownloadEngine engine,
    IAppPaths paths,
    SiteRegistry sites,
    ILogger<AccountService> log) : IAccountService
{
    public static string AbsoluteCookiePath(IAppPaths paths, Account account) =>
        Path.Combine(paths.AccountsDir, account.CookiePath);

    public async Task<ImportCookiesResult> ImportCookiesAsync(
        string siteId, string? cookiesFilePath, string? refreshToken = null, CancellationToken ct = default)
    {
        var provider = sites.Get(siteId); // 校验站点存在
        if (provider.RequiresRefreshToken && string.IsNullOrWhiteSpace(refreshToken))
            return new ImportCookiesResult(new Account { SiteId = siteId },
                "缺少 refresh-token（请运行 gallery-dl oauth:pixiv 后填入）");
        if (string.IsNullOrWhiteSpace(cookiesFilePath) && siteId != PixivSiteProvider.Id)
            return new ImportCookiesResult(new Account { SiteId = siteId }, "请选择 Cookie 文件");

        var id = Guid.NewGuid().ToString("N");
        var relative = Path.Combine(siteId, id, "cookies.txt");
        var target = Path.Combine(paths.AccountsDir, relative);
        var stagedDirectory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(stagedDirectory);
        var committed = false;
        try
        {
            if (string.IsNullOrWhiteSpace(cookiesFilePath))
                await File.WriteAllTextAsync(target, "# Netscape HTTP Cookie File\n", ct);
            else
                File.Copy(cookiesFilePath, target, overwrite: true);
            if (!string.IsNullOrWhiteSpace(refreshToken))
                await File.WriteAllTextAsync(Path.Combine(stagedDirectory, "refresh-token.txt"), refreshToken.Trim(), ct);
            await using var db = await siteFactory.CreateAsync(siteId, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Accounts.Where(a => a.SiteId == siteId && a.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false), ct);
            db.Accounts.Add(new Account
            {
                SiteId = siteId, CookiePath = relative, Status = AccountStatus.Unverified,
                IsActive = true, AddedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                // 仅清理本次 GUID 目录内已知文件，不触碰旧账号凭据。
                try
                {
                    File.Delete(target);
                    File.Delete(Path.Combine(stagedDirectory, "refresh-token.txt"));
                    Directory.Delete(stagedDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { log.LogWarning(ex, "清理未提交的账号凭据失败"); }
            }
        }
        var imported = await FindByCookieAsync(siteId, relative, ct);
        var verify = await VerifyAsync(imported!, ct);
        return verify.Ok
            ? new ImportCookiesResult(verify.Account)
            : new ImportCookiesResult(verify.Account, verify.Error);
    }

    public async Task<ImportCookiesResult> VerifyAsync(Account account, CancellationToken ct = default)
    {
        // Task 4：方法内自开站点库上下文（无 scoped 依赖后本服务不再需要 Scoped 生命周期，
        // 但沿用原注册防 VM 生命周期涟漪）。account 常为游离实体（ImportCookies 的导入路径已
        // 落库取 Id；查询路径产自 AsNoTracking 上下文）——先 Attach 使 SaveChanges 生效，
        // 再按 Id ExecuteUpdate 兜底（原 B7 裁定语义）。
        await using var db = await siteFactory.CreateAsync(account.SiteId, ct);
        db.Attach(account);
        try
        {
            var who = await engine.WhoAmIAsync(account.SiteId, AbsoluteCookiePath(paths, account), ct);
            account.ScreenName = who.ScreenName;
            account.DisplayName = who.DisplayName;
            account.RestId = who.RestId;
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
        // B7 审查 Important 修复：account 可能是游离实体（IAccountQueryService 产自独立 factory 上下文 +
        // AsNoTracking，设置页"重新验证"路径），SaveChanges 对其零变更、结果不落库——
        // 再按 Id ExecuteUpdateAsync 持久化四属性（与上方停用更新、DownloadQueueService 标记 Invalid 同模式）。
        // 导入路径的新实体此时已被 SaveChanges 写入并取得 Id，本句命中同一行重写相同值，幂等无副作用。
        await db.Accounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.Status, account.Status)
            .SetProperty(a => a.ScreenName, account.ScreenName)
            .SetProperty(a => a.DisplayName, account.DisplayName)
            .SetProperty(a => a.RestId, account.RestId)
            .SetProperty(a => a.VerifiedAt, account.VerifiedAt), ct);
        return account.Status == AccountStatus.Ok
            ? new(account)
            : new(account, "Cookie 无效或已过期，请重新导出");
    }

    public async Task DeleteAsync(Account account, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(AbsoluteCookiePath(paths, account));
        await using var db = await siteFactory.CreateAsync(account.SiteId, ct);
        db.Attach(account);
        db.Accounts.Remove(account);
        await db.SaveChangesAsync(ct);
        if (dir is not null && Directory.Exists(dir))
            try { Directory.Delete(dir, true); } catch { /* 尽力清理 */ }
    }

    private async Task<Account?> FindByCookieAsync(string siteId, string cookiePath, CancellationToken ct = default)
    {
        await using var db = await siteFactory.CreateAsync(siteId, ct);
        return await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.CookiePath == cookiePath, ct);
    }
}
