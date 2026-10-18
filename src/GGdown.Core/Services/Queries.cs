using GGdown.Data;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Services;

public sealed record UserFilter(string? SearchText = null, string SortBy = "last_download", bool SortDesc = true);
public sealed record UserCursor(string SiteId, UserFilter Filter, bool IsPinned, long Id,
    DateTime? LastDownloadAt, long DownloadCount, DateTime AddedAt);
public sealed record UserPage(IReadOnlyList<User> Items, UserCursor? NextCursor);
public sealed record HistoryCursor(DateTime CreatedAt, long FileId, string SiteId);
public sealed record HistoryFilter(string? SiteId = null, long? UserId = null, DateTime? From = null, DateTime? To = null,
    HistoryCursor? Cursor = null, int PageSize = 200);
public sealed record HistoryRow(long FileId, long? UserId, string? UserScreenName, string? SourceItemId,
    string FilePath, long? FileSize, FileStatus Status, DateTime CreatedAt, string SiteId = "twitter",
    TargetKind Kind = TargetKind.UserMedia);

public interface IUserQueryService
{
    Task<IReadOnlyList<User>> ListAsync(string siteId, UserFilter filter, CancellationToken ct = default);
    Task<UserPage> ListPageAsync(string siteId, UserFilter filter, UserCursor? cursor = null,
        int pageSize = 200, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListDownloadListAsync(string siteId, CancellationToken ct = default);
}

public sealed class UserQueryService(ISiteDbContextFactory siteFactory) : IUserQueryService
{
    // 全量查询保留给批量下载等操作，页面单独使用 ListPageAsync。
    public async Task<IReadOnlyList<User>> ListAsync(string siteId, UserFilter filter, CancellationToken ct = default)
    {
        await using var db = await siteFactory.CreateAsync(siteId, ct);
        return await OrderUsers(FilterUsers(db, siteId, filter), filter).ToListAsync(ct);
    }

    public async Task<UserPage> ListPageAsync(string siteId, UserFilter filter, UserCursor? cursor = null,
        int pageSize = 200, CancellationToken ct = default)
    {
        if (cursor is not null && (cursor.SiteId != siteId || cursor.Filter != filter))
            throw new ArgumentException("用户分页游标与当前站点或筛选条件不匹配", nameof(cursor));
        pageSize = Math.Clamp(pageSize, 1, 2000);
        await using var db = await siteFactory.CreateAsync(siteId, ct);
        var query = FilterUsers(db, siteId, filter);
        if (cursor is not null)
        {
            // 置顶优先；相同排序值用 Id 升序打破平局，避免翻页重复或遗漏。
            var samePin = query.Where(u => u.IsPinned == cursor.IsPinned);
            var afterPin = query.Where(u => cursor.IsPinned && !u.IsPinned);
            IQueryable<User> afterValue;
            if (filter.SortBy == "download_count")
                afterValue = filter.SortDesc
                    ? samePin.Where(u => u.DownloadCount < cursor.DownloadCount || (u.DownloadCount == cursor.DownloadCount && u.Id > cursor.Id))
                    : samePin.Where(u => u.DownloadCount > cursor.DownloadCount || (u.DownloadCount == cursor.DownloadCount && u.Id > cursor.Id));
            else if (filter.SortBy == "added_at")
                afterValue = filter.SortDesc
                    ? samePin.Where(u => u.AddedAt < cursor.AddedAt || (u.AddedAt == cursor.AddedAt && u.Id > cursor.Id))
                    : samePin.Where(u => u.AddedAt > cursor.AddedAt || (u.AddedAt == cursor.AddedAt && u.Id > cursor.Id));
            else if (cursor.LastDownloadAt is { } last)
                afterValue = filter.SortDesc
                    ? samePin.Where(u => u.LastDownloadAt == null || u.LastDownloadAt < last || (u.LastDownloadAt == last && u.Id > cursor.Id))
                    : samePin.Where(u => u.LastDownloadAt > last || (u.LastDownloadAt == last && u.Id > cursor.Id));
            else
                // SQLite 升序时空值在前，降序时空值在后。
                afterValue = filter.SortDesc
                    ? samePin.Where(u => u.LastDownloadAt == null && u.Id > cursor.Id)
                    : samePin.Where(u => u.LastDownloadAt != null || (u.LastDownloadAt == null && u.Id > cursor.Id));
            query = afterPin.Concat(afterValue);
        }
        var items = await OrderUsers(query, filter).Take(pageSize + 1).ToListAsync(ct);
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        var lastUser = items.LastOrDefault();
        var next = hasMore && lastUser is not null
            ? new UserCursor(siteId, filter, lastUser.IsPinned, lastUser.Id, lastUser.LastDownloadAt,
                lastUser.DownloadCount, lastUser.AddedAt)
            : null;
        return new UserPage(items, next);
    }

    private static IQueryable<User> FilterUsers(GGdownSiteDbContext db, string siteId, UserFilter filter)
    {
        var query = db.Users.AsNoTracking().Where(u => u.SiteId == siteId);
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
            query = query.Where(u => u.ScreenName.Contains(filter.SearchText) ||
                                     (u.DisplayName != null && u.DisplayName.Contains(filter.SearchText)));
        return query;
    }

    private static IOrderedQueryable<User> OrderUsers(IQueryable<User> query, UserFilter filter)
    {
        var pinned = query.OrderByDescending(u => u.IsPinned);
        var ordered = (filter.SortBy, filter.SortDesc) switch
        {
            ("download_count", true) => pinned.ThenByDescending(u => u.DownloadCount),
            ("download_count", false) => pinned.ThenBy(u => u.DownloadCount),
            ("added_at", true) => pinned.ThenByDescending(u => u.AddedAt),
            ("added_at", false) => pinned.ThenBy(u => u.AddedAt),
            (_, true) => pinned.ThenByDescending(u => u.LastDownloadAt),
            _ => pinned.ThenBy(u => u.LastDownloadAt),
        };
        return ordered.ThenBy(u => u.Id);
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
        var pageSize = Math.Clamp(filter.PageSize, 1, 2000);
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
            if (filter.Cursor is { } cursor)
            {
                var includeSameId = string.CompareOrdinal(siteId, cursor.SiteId) < 0;
                files = includeSameId
                    ? files.Where(f => f.CreatedAt < cursor.CreatedAt || (f.CreatedAt == cursor.CreatedAt && f.Id <= cursor.FileId))
                    : files.Where(f => f.CreatedAt < cursor.CreatedAt || (f.CreatedAt == cursor.CreatedAt && f.Id < cursor.FileId));
            }
            var query = from f in files
                        join j in db.Jobs.AsNoTracking() on f.JobId equals j.Id
                        join u in db.Users.AsNoTracking() on f.UserId equals u.Id into gj
                        from user in gj.DefaultIfEmpty()
                        orderby f.CreatedAt descending, f.Id descending
                        select new HistoryRow(
                            f.Id, f.UserId, user != null ? user.ScreenName : null, f.SourceItemId,
                            f.FilePath, f.FileSize, f.Status, f.CreatedAt, siteId, j.TargetKind);
            rows.AddRange(await query.Take(pageSize).ToListAsync(ct));
        }
        return [.. rows.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.FileId)
            .ThenByDescending(r => r.SiteId, StringComparer.Ordinal).Take(pageSize)];
    }

    public async Task<IReadOnlyList<User>> ListUsersAsync(string siteId, CancellationToken ct = default)
    {
        await using var db = await siteFactory.CreateAsync(siteId, ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.SiteId == siteId)
            .OrderBy(u => u.ScreenName).ToListAsync(ct);
    }
}
