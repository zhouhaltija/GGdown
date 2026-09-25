using GGdown.Data;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Services;

public sealed record UserFilter(string? SearchText = null, string SortBy = "last_download", bool SortDesc = true);
public sealed record HistoryFilter(string? SiteId = null, long? UserId = null, DateTime? From = null, DateTime? To = null);
public sealed record HistoryRow(long FileId, long? UserId, string? UserScreenName, string? SourceItemId,
    string FilePath, long? FileSize, FileStatus Status, DateTime CreatedAt);

public interface IUserQueryService
{
    Task<IReadOnlyList<User>> ListAsync(string siteId, UserFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListDownloadListAsync(string siteId, CancellationToken ct = default);
}

public sealed class UserQueryService(ISiteDbContextFactory siteFactory) : IUserQueryService
{
    public async Task<IReadOnlyList<User>> ListAsync(string siteId, UserFilter filter, CancellationToken ct = default)
    {
        await using var db = await siteFactory.CreateAsync(siteId, ct);
        var query = db.Users.AsNoTracking().Where(u => u.SiteId == siteId);
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
            query = query.Where(u => u.ScreenName.Contains(filter.SearchText) ||
                                     (u.DisplayName != null && u.DisplayName.Contains(filter.SearchText)));
        // 控制器裁定：EF Core 中后一个 OrderBy 会替换前面的排序，示意段忽略——
        // 定稿把置顶 IsPinned DESC 放进主排序（第一排序键），次键按 SortBy/SortDesc。
        var q = query;
        IOrderedQueryable<User> ordered = filter.SortBy switch
        {
            "download_count" => q.OrderByDescending(u => u.IsPinned).ThenByDescending(u => u.DownloadCount),
            "added_at" => q.OrderByDescending(u => u.IsPinned).ThenByDescending(u => u.AddedAt),
            _ => q.OrderByDescending(u => u.IsPinned).ThenByDescending(u => u.LastDownloadAt),
        };
        if (!filter.SortDesc) // SortDesc=false 时次键升序（置顶仍第一）
        {
            ordered = filter.SortBy switch
            {
                "download_count" => q.OrderByDescending(u => u.IsPinned).ThenBy(u => u.DownloadCount),
                "added_at" => q.OrderByDescending(u => u.IsPinned).ThenBy(u => u.AddedAt),
                _ => q.OrderByDescending(u => u.IsPinned).ThenBy(u => u.LastDownloadAt),
            };
        }
        return await ordered.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<User>> ListDownloadListAsync(string siteId, CancellationToken ct = default)
    {
        await using var db = await siteFactory.CreateAsync(siteId, ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.SiteId == siteId && u.InDownloadList && !u.IsSkipped)
            .OrderByDescending(u => u.IsPinned)
            .ThenBy(u => u.ScreenName)
            .ToListAsync(ct);
    }
}

public interface IAccountQueryService
{
    Task<Account?> GetActiveAsync(string siteId, CancellationToken ct = default);
}

public sealed class AccountQueryService(ISiteDbContextFactory siteFactory) : IAccountQueryService
{
    public async Task<Account?> GetActiveAsync(string siteId, CancellationToken ct = default)
    {
        await using var db = await siteFactory.CreateAsync(siteId, ct);
        return await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.SiteId == siteId && a.IsActive, ct);
    }
}

public interface IHistoryQueryService
{
    Task<IReadOnlyList<HistoryRow>> ListAsync(HistoryFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListUsersAsync(string siteId, CancellationToken ct = default);
}

public sealed class HistoryQueryService(ISiteDbContextFactory siteFactory) : IHistoryQueryService
{
    public async Task<IReadOnlyList<HistoryRow>> ListAsync(HistoryFilter filter, CancellationToken ct = default)
    {
        // Task 4：SiteId 指定单库；null=全部平台（跨库聚合后按时间排序、总量截断）
        var siteIds = filter.SiteId is { Length: > 0 } s
            ? [s]
            : siteFactory.ExistingSites();
        var rows = new List<HistoryRow>();
        foreach (var siteId in siteIds)
        {
            await using var db = await siteFactory.CreateAsync(siteId, ct);
            // 控制器裁定：brief 主案的投影后 Where(new HistoryRow(...) 上过滤) 无法被 EF Core 翻译
            //（实测 InvalidOperationException），按 brief 括号内备选方案改为投影前过滤（语义等价）：
            // UserId 作用于 f.UserId，日期作用于 f.CreatedAt。
            var files = db.Files.AsNoTracking().AsQueryable();
            if (filter.SiteId is { Length: > 0 } sid) files = files.Where(f => f.SiteId == sid);
            if (filter.UserId is { } uid) files = files.Where(f => f.UserId == uid);
            if (filter.From is { } from) files = files.Where(f => f.CreatedAt >= from);
            if (filter.To is { } to) files = files.Where(f => f.CreatedAt < to);
            var query = from f in files
                        join j in db.Jobs.AsNoTracking() on f.JobId equals j.Id
                        join u in db.Users.AsNoTracking() on f.UserId equals u.Id into gj
                        from user in gj.DefaultIfEmpty()
                        orderby f.CreatedAt descending
                        select new HistoryRow(
                            f.Id, f.UserId, user != null ? user.ScreenName : null, f.SourceItemId,
                            f.FilePath, f.FileSize, f.Status, f.CreatedAt);
            rows.AddRange(await query.Take(2000).ToListAsync(ct));
        }
        return [.. rows.OrderByDescending(r => r.CreatedAt).Take(2000)];
    }

    public async Task<IReadOnlyList<User>> ListUsersAsync(string siteId, CancellationToken ct = default)
    {
        await using var db = await siteFactory.CreateAsync(siteId, ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.SiteId == siteId)
            .OrderBy(u => u.ScreenName).ToListAsync(ct);
    }
}
