using GGdown.Data;
using GGdown.Engine;
using GGdown.Services;
using GGdown.Sites;
using GGdown.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests;

public static class TestDb
{
    public static (SqliteConnection Connection, GGdownDbContext Db) Create()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<GGdownDbContext>()
            .UseSqlite(conn).Options;
        var db = new GGdownDbContext(options);
        db.Database.EnsureCreated();
        return (conn, db);
    }

    public static (SqliteConnection Connection, GGdownSiteDbContext Db) CreateSite()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var db = new GGdownSiteDbContext(new DbContextOptionsBuilder<GGdownSiteDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (conn, db);
    }

    public static (SqliteConnection Connection, GGdownGlobalDbContext Db) CreateGlobal()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var db = new GGdownGlobalDbContext(new DbContextOptionsBuilder<GGdownGlobalDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (conn, db);
    }
}

public static class TestPaths
{
    public static GGdown.Paths.AppPaths Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "ggui-" + Guid.NewGuid().ToString("N"));
        var p = new GGdown.Paths.AppPaths(root); // 限定：GGdown.Tests.Paths（AppPathsTests.cs）会遮蔽短写 Paths
        p.EnsureCreated();
        return p;
    }
}

public sealed class FakeEngine : IDownloadEngine
{
    public EngineHello Hello { get; set; } = new(1, "fake", "fake");
    public AccountInfo WhoAmI { get; set; } = new("stub_user", "Stub User", "99");
    public Exception? WhoAmIError { get; set; }
    public IReadOnlyList<SiteUserInfo> NextFollowing { get; set; } =
        [new("1", "alice", "Alice", "https://x/a.png"), new("2", "bob", "Bob", "https://x/b.png")];
    public SiteUserInfo NextUserInfo { get; set; } = new("42", "carol", "Carol", "https://x/c.png");
    public Func<DownloadPlan, string, IProgress<EngineEvent>, CancellationToken, Task>? OnDownload { get; set; }
    public List<(DownloadPlan Plan, string CookiesFile, IProgress<EngineEvent> Progress)> Downloads { get; } = [];

    public Task<EngineHello> HelloAsync(CancellationToken ct = default) => Task.FromResult(Hello);

    public string? LastSiteId { get; private set; }
    public string? LastUserInfoInput { get; private set; }

    public Task<AccountInfo> WhoAmIAsync(string siteId, string cookiesFile, CancellationToken ct = default)
    {
        LastSiteId = siteId;
        return WhoAmIError is not null ? Task.FromException<AccountInfo>(WhoAmIError) : Task.FromResult(WhoAmI);
    }

    public TaskCompletionSource<IReadOnlyList<SiteUserInfo>>? FollowingDelay { get; set; }
    public Exception? ListFollowingError { get; set; }
    public int ListFollowingCalls { get; private set; }

    public async Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(string siteId, string cookiesFile, CancellationToken ct = default)
    {
        LastSiteId = siteId;
        ListFollowingCalls++;
        if (ListFollowingError is not null)
            return await Task.FromException<IReadOnlyList<SiteUserInfo>>(ListFollowingError);
        if (FollowingDelay is not null)
            return await FollowingDelay.Task.WaitAsync(ct);
        return NextFollowing;
    }

    public Task<SiteUserInfo> GetUserInfoAsync(string siteId, string cookiesFile, string input, CancellationToken ct = default)
    {
        LastSiteId = siteId;
        LastUserInfoInput = input;
        return Task.FromResult(NextUserInfo);
    }

    public Task DownloadAsync(DownloadPlan plan, string cookiesFile, IProgress<EngineEvent> progress, CancellationToken ct = default)
    {
        Downloads.Add((plan, cookiesFile, progress));
        return OnDownload?.Invoke(plan, cookiesFile, progress, ct) ?? Task.CompletedTask;
    }
}

/// <summary>
/// 包装测试持有的单连接 DbContext 的 IDbContextFactory（Task 11 落位，供 Task 10 测试复用）。
/// 每次返回共享同一 SqliteConnection 的新上下文：仍落在同一个 :memory: 库上，但被调用方
/// （StatsAggregator 等）`await using` 后 dispose 的不是测试持有的 _db 实例，_db 保持可用。
/// </summary>
public sealed class SingleDbContextFactory(DbContext db) : IDbContextFactory<GGdownDbContext>
{
    // Task 4 泛化：参数放宽到 DbContext 基类——队列（Task 5 前仍用旧模型）经同一站点库连接建旧上下文
    public GGdownDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<GGdownDbContext>().UseSqlite(db.Database.GetDbConnection()).Options);
}

/// <summary>
/// IUiDispatcher 的测试同步实现（Task B2）：Post 在当前线程立即执行，便于 ViewModel 单测同步断言。
/// </summary>
public sealed class SyncDispatcher : IUiDispatcher
{
    private readonly object _gate = new();

    public void Post(Action action)
    {
        // 真实 UI 队列会串行执行；测试中也不能让后台事件同时修改绑定集合。
        lock (_gate) action();
    }
}

public sealed class FakeCurrentSite : GGdown.Sites.ICurrentSite
{
    public GGdown.Sites.SiteInfo Current { get; private set; } = GGdown.Sites.SiteCatalog.Default;
    public string SiteId => Current.SiteId;
    public bool IsAvailable => Current.Available;
    public event Action? Changed;
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SelectAsync(string siteId, CancellationToken ct = default)
    {
        Current = GGdown.Sites.SiteCatalog.Get(siteId);
        Changed?.Invoke();
        return Task.CompletedTask;
    }
}

/// <summary>
/// 包装测试内存全局库的 IDbContextFactory（Task 3 起供 AppSettings 等注入用）。
/// 语义同 SingleDbContextFactory：共享同一 SqliteConnection，返回新 context。
/// </summary>
public sealed class SingleGlobalDbContextFactory(GGdownGlobalDbContext db) : IDbContextFactory<GGdownGlobalDbContext>
{
    public GGdownGlobalDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<GGdownGlobalDbContext>().UseSqlite(db.Database.GetDbConnection()).Options);
}

/// <summary>
/// 包装测试共享单连接站点库的 ISiteDbContextFactory（Task 4 起：站点服务均经此取上下文）。
/// 对任意 siteId 返回共享连接上的新 GGdownSiteDbContext（数据按行内 SiteId 值区分）。
/// </summary>
public sealed class SingleSiteDbContextFactory(GGdownSiteDbContext db, params string[] knownSites)
    : ISiteDbContextFactory
{
    private readonly HashSet<string> _sites = new(knownSites, StringComparer.OrdinalIgnoreCase);

    public Task<GGdownSiteDbContext> CreateAsync(string siteId, CancellationToken ct = default)
    {
        _sites.Add(siteId);
        return Task.FromResult(new GGdownSiteDbContext(
            new DbContextOptionsBuilder<GGdownSiteDbContext>().UseSqlite(db.Database.GetDbConnection()).Options));
    }

    public IReadOnlyList<string> ExistingSites() => [.. _sites];
}

/// <summary>
/// 继承 StatsAggregator、覆写 ApplyJobCompletionAsync 只记录不落库（Task 10 的 DownloadQueueService 测试依赖）。
/// 空注入 base(null!)：覆写路径不会触碰 factory，安全（brief Step 3 括号说明）。
/// </summary>
public sealed class FakeStats : StatsAggregator
{
    public List<long> AppliedJobIds { get; } = [];

    public FakeStats() : base(null!) { }

    public override Task ApplyJobCompletionAsync(long jobId, string siteId, CancellationToken ct = default)
    {
        AppliedJobIds.Add(jobId);
        return Task.CompletedTask;
    }

    public override Task RecalculateDownloadCountsAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// IAppSettings 的内存假实现（Task 8：MainNavViewModel 测试用；只覆盖导航相关键）。
/// </summary>
public sealed class FakeAppSettings : GGdown.Settings.IAppSettings
{
    public string? SavedCurrentSiteId { get; private set; }
    public Dictionary<string, string> LastPages { get; } = [];
    private IReadOnlyList<string> _visible = ["twitter", "pixiv"];

    public Task<string> GetDownloadDirectoryAsync(CancellationToken ct = default) =>
        Task.FromResult(@"D:\dl");
    public Task SetDownloadDirectoryAsync(string directory, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetConcurrencyAsync(CancellationToken ct = default) => Task.FromResult(1);
    public Task SetConcurrencyAsync(int concurrency, CancellationToken ct = default) => Task.CompletedTask;
    public Task<long> GetDownloadRateLimitAsync(CancellationToken ct = default) => Task.FromResult(0L);
    public Task SetDownloadRateLimitAsync(long bytesPerSecond, CancellationToken ct = default) => Task.CompletedTask;
    public Task<GGdown.Settings.ProxyConfig> GetProxyAsync(CancellationToken ct = default) =>
        Task.FromResult(new GGdown.Settings.ProxyConfig());
    public Task SetProxyAsync(GGdown.Settings.ProxyConfig proxy, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetCurrentSiteIdAsync(CancellationToken ct = default) =>
        Task.FromResult(SavedCurrentSiteId ?? "twitter");
    public Task SetCurrentSiteIdAsync(string siteId, CancellationToken ct = default)
    {
        SavedCurrentSiteId = siteId;
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<string>> GetVisibleSitesAsync(CancellationToken ct = default) =>
        Task.FromResult(_visible);
    public Task SetVisibleSitesAsync(IReadOnlyList<string> siteIds, CancellationToken ct = default)
    {
        _visible = siteIds;
        return Task.CompletedTask;
    }
    public Task<string> GetSiteLastPageAsync(string siteId, CancellationToken ct = default) =>
        Task.FromResult(LastPages.GetValueOrDefault(siteId, "users"));
    public Task SetSiteLastPageAsync(string siteId, string pageKey, CancellationToken ct = default)
    {
        LastPages[siteId] = pageKey;
        return Task.CompletedTask;
    }
    public Task<IReadOnlyDictionary<string, object?>> GetSiteOptionsAsync(string siteId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    public Task SetSiteOptionsAsync(string siteId, IReadOnlyDictionary<string, object?> options, CancellationToken ct = default) =>
        Task.CompletedTask;
}
