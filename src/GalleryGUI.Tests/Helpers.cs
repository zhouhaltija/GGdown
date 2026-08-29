using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Sites;
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
