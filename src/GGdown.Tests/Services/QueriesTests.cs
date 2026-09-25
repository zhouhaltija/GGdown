using GGdown.Data;
using GGdown.Services;
using Microsoft.Data.Sqlite; // 补 using：SqliteConnection 在 tuple 类型中未限定（brief 已知偏离模式）
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Services;

public class QueriesTests : IDisposable
{
    private readonly (SqliteConnection, GGdownSiteDbContext) _t;
    private readonly GGdownSiteDbContext _db;
    private readonly UserQueryService _users;
    private readonly AccountQueryService _accounts;
    private readonly HistoryQueryService _history;

    public QueriesTests()
    {
        _t = TestDb.CreateSite();
        _db = _t.Item2;
        var account = new Account { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        var pinned = NewUser("p1", "alice", isPinned: true, lastDownload: DateTime.UtcNow.AddDays(-1), count: 5);
        var recent = NewUser("u2", "bob", lastDownload: DateTime.UtcNow, count: 1);
        var old = NewUser("u3", "carol", lastDownload: DateTime.UtcNow.AddDays(-30), count: 9);
        _db.Accounts.Add(account);
        _db.Users.AddRange(pinned, recent, old);
        _db.SaveChanges();
        var job = new DownloadJob { AccountId = account.Id, TargetKind = TargetKind.UserMedia, UserId = recent.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
        _db.Jobs.Add(job);
        _db.SaveChanges(); // 偏离（最小修正，已记录）：brief 原文在 job 落库前取 job.Id 构造 Files，
        // 此时是 EF 临时负值，SaveChanges 后固化导致 FOREIGN KEY constraint failed；先落库取得真实 Id。
        _db.Files.AddRange(
            new DownloadFile { JobId = job.Id, UserId = recent.Id, SourceItemId = "11", Url = "u", FilePath = @"D:\dl\bob\11_1.jpg", FileSize = 100, Status = FileStatus.Downloaded, CreatedAt = DateTime.UtcNow },
            new DownloadFile { JobId = job.Id, UserId = null, SourceItemId = null, Url = "u", FilePath = @"D:\dl\_likes\1.jpg", FileSize = 200, Status = FileStatus.Downloaded, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        _users = new UserQueryService(new SingleSiteDbContextFactory(_db));
        _accounts = new AccountQueryService(new SingleSiteDbContextFactory(_db));
        _history = new HistoryQueryService(new SingleSiteDbContextFactory(_db, "twitter"));
    }
    public void Dispose() => _t.Item1.Dispose();

    private static User NewUser(string restId, string name, bool isPinned = false, DateTime? lastDownload = null, long count = 0) => new()
    { SiteId = "twitter", RestId = restId, ScreenName = name, Source = UserSource.Following, IsPinned = isPinned, LastDownloadAt = lastDownload, DownloadCount = count, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

    [Fact]
    public async Task List_sorts_pinned_first_then_by_key()
    {
        var byCount = await _users.ListAsync("twitter", new UserFilter(SortBy: "download_count"));
        Assert.Equal(["alice", "carol", "bob"], byCount.Select(u => u.ScreenName)); // 置顶优先，其余按计数降序
        var byLast = await _users.ListAsync("twitter", new UserFilter());
        Assert.Equal(["alice", "bob", "carol"], byLast.Select(u => u.ScreenName));
    }

    [Fact]
    public async Task List_filters_by_search_text()
    {
        var r = await _users.ListAsync("twitter", new UserFilter(SearchText: "ali"));
        Assert.Equal(["alice"], r.Select(u => u.ScreenName)); // 匹配 ScreenName 或 DisplayName
    }

    [Fact]
    public async Task ListDownloadList_returns_listed_non_skipped_pinned_first()
    {
        var alice = _db.Users.Single(u => u.ScreenName == "alice");
        var bob = _db.Users.Single(u => u.ScreenName == "bob");
        var carol = _db.Users.Single(u => u.ScreenName == "carol");
        alice.InDownloadList = true;
        bob.InDownloadList = true;
        bob.IsSkipped = true;
        carol.InDownloadList = true;
        _db.SaveChanges();

        var list = await _users.ListDownloadListAsync("twitter");
        Assert.Equal(["alice", "carol"], list.Select(u => u.ScreenName));
        Assert.Empty(await _users.ListDownloadListAsync("pixiv"));
    }

    [Fact]
    public async Task GetActive_returns_active_account_only()
    {
        var acc = await _accounts.GetActiveAsync("twitter");
        Assert.NotNull(acc);
        Assert.Equal(AccountStatus.Ok, acc!.Status);
        Assert.Null(await _accounts.GetActiveAsync("nope"));
    }

    [Fact]
    public async Task History_joins_user_and_filters()
    {
        var all = await _history.ListAsync(new HistoryFilter());
        Assert.Equal(2, all.Count);
        Assert.Equal("bob", all.Single(r => r.SourceItemId == "11").UserScreenName);
        Assert.Null(all.Single(r => r.SourceItemId is null).UserScreenName); // likes 文件无用户

        var byUser = await _history.ListAsync(new HistoryFilter(UserId: (await _db.Users.SingleAsync(u => u.ScreenName == "bob")).Id));
        Assert.Single(byUser);
        Assert.All(byUser, r => Assert.Equal(100, r.FileSize));
    }
}
