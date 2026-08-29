using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalleryGUI.Services;

public interface IUserService
{
    Task<int> ImportFollowingAsync(Account account, CancellationToken ct = default);
    Task<User> AddUserAsync(Account account, string input, CancellationToken ct = default);
    Task RemoveAsync(IReadOnlyList<long> userIds, CancellationToken ct = default);
    Task SetPinnedAsync(long userId, bool pinned, CancellationToken ct = default);
}

public sealed class UserService(
    GalleryDbContext db,
    IDownloadEngine engine,
    IAppPaths paths,
    SiteRegistry sites,
    ILogger<UserService> log) : IUserService
{
    public async Task<int> ImportFollowingAsync(Account account, CancellationToken ct = default)
    {
        var cookies = AccountService.AbsoluteCookiePath(paths, account);
        var followed = await engine.ListFollowingAsync(cookies, ct);
        var provider = sites.Get(account.SiteId);
        foreach (var info in followed)
            await UpsertAsync(account, info, UserSource.Following, provider, ct);
        return followed.Count;
    }

    public async Task<User> AddUserAsync(Account account, string input, CancellationToken ct = default)
    {
        var provider = sites.Get(account.SiteId);
        var parsed = provider.ParseInput(input);
        if (!parsed.Ok || parsed.ScreenName is null)
            throw new ArgumentException(parsed.Error ?? "无法识别输入", nameof(input));
        var info = await engine.GetUserInfoAsync(
            AccountService.AbsoluteCookiePath(paths, account), input, ct);
        return await UpsertAsync(account, info,
            input.Contains("://") ? UserSource.Link : UserSource.Manual,
            provider, ct);
    }

    public async Task RemoveAsync(IReadOnlyList<long> userIds, CancellationToken ct = default)
    {
        await db.Users.Where(u => userIds.Contains(u.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task SetPinnedAsync(long userId, bool pinned, CancellationToken ct = default)
    {
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPinned, pinned), ct);
    }

    private async Task<User> UpsertAsync(
        Account account, SiteUserInfo info, UserSource source,
        ISiteProvider provider, CancellationToken ct)
    {
        var user = await db.Users
            .SingleOrDefaultAsync(u => u.SiteId == account.SiteId && u.RestId == info.RestId, ct);
        if (user is null)
        {
            user = new User
            {
                SiteId = account.SiteId,
                RestId = info.RestId,
                Source = source,
                OwnerAccountId = account.Id,
                AddedAt = DateTime.UtcNow,
            };
            db.Users.Add(user);
        }
        user.ScreenName = info.ScreenName;
        user.DisplayName = info.DisplayName;
        user.AvatarUrl = info.AvatarUrl;
        user.ProfileUrl = provider.BuildProfileUrl(info.ScreenName);
        user.Source = source; // 适配：brief 测试断言同一 rest_id 重添加后 Source 以最后一次添加为准（brief 实现更新路径未刷新，以测试为准）
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }
}
