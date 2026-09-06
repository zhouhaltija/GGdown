using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalleryGUI.Services;

public sealed record FollowingCacheSnapshot(IReadOnlyList<SiteUserInfo> Users, DateTime FetchedAt);

public interface IUserService
{
    Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(Account account, CancellationToken ct = default);
    Task<FollowingCacheSnapshot?> GetFollowingCacheAsync(Account account, CancellationToken ct = default);
    Task SaveFollowingCacheAsync(Account account, IReadOnlyList<SiteUserInfo> users, CancellationToken ct = default);
    Task<int> AddFollowingUsersAsync(Account account, IReadOnlyList<SiteUserInfo> selected, CancellationToken ct = default);
    Task<User> AddUserAsync(Account account, string input, CancellationToken ct = default);
    Task<int> RefreshProfilesAsync(Account account, IReadOnlyList<long>? userIds = null, CancellationToken ct = default);
    Task RemoveAsync(IReadOnlyList<long> userIds, CancellationToken ct = default);
    Task SetPinnedAsync(long userId, bool pinned, CancellationToken ct = default);
    Task SetSkippedAsync(IReadOnlyList<long> userIds, bool skipped, CancellationToken ct = default);
    Task SetInDownloadListAsync(IReadOnlyList<long> userIds, bool inList, CancellationToken ct = default);
}

public sealed class UserService(
    GalleryDbContext db,
    IDownloadEngine engine,
    IAppPaths paths,
    SiteRegistry sites,
    ILogger<UserService> log) : IUserService
{
    public async Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(Account account, CancellationToken ct = default)
    {
        var cookies = AccountService.AbsoluteCookiePath(paths, account);
        return await engine.ListFollowingAsync(account.SiteId, cookies, ct);
    }

    public async Task<FollowingCacheSnapshot?> GetFollowingCacheAsync(Account account, CancellationToken ct = default)
    {
        var rows = await db.FollowingCache.AsNoTracking()
            .Where(x => x.SiteId == account.SiteId)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);
        if (rows.Count == 0) return null;
        return new FollowingCacheSnapshot(
            rows.Select(x => new SiteUserInfo(x.RestId, x.ScreenName, x.DisplayName, x.AvatarUrl)).ToList(),
            rows[0].FetchedAt);
    }

    public async Task SaveFollowingCacheAsync(
        Account account, IReadOnlyList<SiteUserInfo> users, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await db.FollowingCache.Where(x => x.SiteId == account.SiteId).ExecuteDeleteAsync(ct);
        for (var i = 0; i < users.Count; i++)
        {
            var u = users[i];
            db.FollowingCache.Add(new FollowingCacheEntry
            {
                SiteId = account.SiteId,
                RestId = u.RestId,
                ScreenName = u.ScreenName,
                DisplayName = u.DisplayName,
                AvatarUrl = u.AvatarUrl,
                SortOrder = i,
                FetchedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> AddFollowingUsersAsync(
        Account account, IReadOnlyList<SiteUserInfo> selected, CancellationToken ct = default)
    {
        var provider = sites.Get(account.SiteId);
        var added = 0;
        foreach (var info in selected)
        {
            var existed = await db.Users
                .AnyAsync(u => u.SiteId == account.SiteId && u.RestId == info.RestId, ct);
            await UpsertAsync(account, info, UserSource.Following, provider, ct);
            if (!existed) added++;
        }
        log.LogInformation("从关注列表添加用户：{SiteId}/{ScreenName} 选 {Selected} 人，新增 {Added} 人",
            account.SiteId, account.ScreenName, selected.Count, added);
        return added;
    }

    public async Task<User> AddUserAsync(Account account, string input, CancellationToken ct = default)
    {
        var provider = sites.Get(account.SiteId);
        var parsed = provider.ParseInput(input);
        if (!parsed.Ok || parsed.ScreenName is null)
            throw new ArgumentException(parsed.Error ?? "无法识别输入", nameof(input));
        var info = await engine.GetUserInfoAsync(
            account.SiteId, AccountService.AbsoluteCookiePath(paths, account), input, ct);
        return await UpsertAsync(account, info,
            input.Contains("://") ? UserSource.Link : UserSource.Manual,
            provider, ct);
    }

    public async Task<int> RefreshProfilesAsync(Account account, IReadOnlyList<long>? userIds = null, CancellationToken ct = default)
    {
        var provider = sites.Get(account.SiteId);
        var cookies = AccountService.AbsoluteCookiePath(paths, account);
        var query = db.Users.Where(u => u.SiteId == account.SiteId);
        if (userIds is { Count: > 0 })
            query = query.Where(u => userIds.Contains(u.Id));
        var list = await query.ToListAsync(ct);
        var updated = 0;
        foreach (var user in list)
        {
            var info = await engine.GetUserInfoAsync(account.SiteId, cookies, user.ScreenName, ct);
            await UpsertAsync(account, info, user.Source, provider, ct);
            updated++;
        }
        return updated;
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

    public async Task SetSkippedAsync(IReadOnlyList<long> userIds, bool skipped, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return;
        await db.Users.Where(u => userIds.Contains(u.Id))
            .ExecuteUpdateAsync(s => skipped
                ? s.SetProperty(u => u.IsSkipped, true).SetProperty(u => u.InDownloadList, false)
                : s.SetProperty(u => u.IsSkipped, false), ct);
    }

    public async Task SetInDownloadListAsync(IReadOnlyList<long> userIds, bool inList, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return;
        var q = db.Users.Where(u => userIds.Contains(u.Id));
        if (inList)
            q = q.Where(u => !u.IsSkipped);
        await q.ExecuteUpdateAsync(s => s.SetProperty(u => u.InDownloadList, inList), ct);
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
        user.BannerUrl = info.BannerUrl ?? user.BannerUrl;
        user.Bio = info.Bio ?? user.Bio;
        user.FollowersCount = info.FollowersCount ?? user.FollowersCount;
        user.MediaCount = info.MediaCount ?? user.MediaCount;
        user.ProfileUrl = provider.BuildProfileUrl(info.ScreenName, info.RestId);
        user.Source = source; // 适配：brief 测试断言同一 rest_id 重添加后 Source 以最后一次添加为准（brief 实现更新路径未刷新，以测试为准）
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }
}
