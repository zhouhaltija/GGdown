using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using GalleryGUI.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Tests;

public static class TestDb
{
    public static (SqliteConnection Connection, GalleryDbContext Db) Create()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<GalleryDbContext>()
            .UseSqlite(conn).Options;
        var db = new GalleryDbContext(options);
        db.Database.EnsureCreated();
        return (conn, db);
    }
}

public static class TestPaths
{
    public static GalleryGUI.Paths.AppPaths Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "ggui-" + Guid.NewGuid().ToString("N"));
        var p = new GalleryGUI.Paths.AppPaths(root); // 限定：GalleryGUI.Tests.Paths（AppPathsTests.cs）会遮蔽短写 Paths
        p.EnsureCreated();
        return p;
    }
}

public sealed class FakeEngine : IDownloadEngine
{
    public EngineHello Hello { get; set; } = new(1, "fake", "fake");
    public AccountInfo WhoAmI { get; set; } = new("stub_user", "Stub User");
    public Exception? WhoAmIError { get; set; }
    public IReadOnlyList<SiteUserInfo> NextFollowing { get; set; } =
        [new("1", "alice", "Alice", "https://x/a.png"), new("2", "bob", "Bob", "https://x/b.png")];
    public SiteUserInfo NextUserInfo { get; set; } = new("42", "carol", "Carol", "https://x/c.png");
    public Func<DownloadPlan, string, IProgress<EngineEvent>, CancellationToken, Task>? OnDownload { get; set; }
    public List<(DownloadPlan Plan, string CookiesFile, IProgress<EngineEvent> Progress)> Downloads { get; } = [];

    public Task<EngineHello> HelloAsync(CancellationToken ct = default) => Task.FromResult(Hello);

    public Task<AccountInfo> WhoAmIAsync(string cookiesFile, CancellationToken ct = default)
        => WhoAmIError is not null ? Task.FromException<AccountInfo>(WhoAmIError) : Task.FromResult(WhoAmI);

    public Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(string cookiesFile, CancellationToken ct = default)
        => Task.FromResult(NextFollowing);

    public Task<SiteUserInfo> GetUserInfoAsync(string cookiesFile, string input, CancellationToken ct = default)
        => Task.FromResult(NextUserInfo);

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
public sealed class SingleDbContextFactory(GalleryDbContext db) : IDbContextFactory<GalleryDbContext>
{
    public GalleryDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<GalleryDbContext>().UseSqlite(db.Database.GetDbConnection()).Options);
}

/// <summary>
/// IUiDispatcher 的测试同步实现（Task B2）：Post 在当前线程立即执行，便于 ViewModel 单测同步断言。
/// </summary>
public sealed class SyncDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>
/// 继承 StatsAggregator、覆写 ApplyJobCompletionAsync 只记录不落库（Task 10 的 DownloadQueueService 测试依赖）。
/// 空注入 base(null!)：覆写路径不会触碰 factory，安全（brief Step 3 括号说明）。
/// </summary>
public sealed class FakeStats : StatsAggregator
{
    public List<long> AppliedJobIds { get; } = [];

    public FakeStats() : base(null!) { }

    public override Task ApplyJobCompletionAsync(long jobId, CancellationToken ct = default)
    {
        AppliedJobIds.Add(jobId);
        return Task.CompletedTask;
    }
}
