using GalleryGUI.Data;
using GalleryGUI.Services;
using Microsoft.Data.Sqlite; // 适配：字段元组类型 SqliteConnection 需要（同 UserServiceTests/SettingsStoreTests，brief 文件原文未含此 using）
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Tests.Services;

public class StatsAndRecoveryTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly GalleryDbContext _db;
    private readonly User _alice;
    private readonly DownloadJob _job;

    public StatsAndRecoveryTests()
    {
        _t = TestDb.Create();
        _db = _t.Item2;
        var account = new Account
        { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _alice = new User
        { SiteId = "twitter", RestId = "1", ScreenName = "alice", AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Accounts.Add(account);
        _db.Users.Add(_alice);
        _db.SaveChanges();
        _job = new DownloadJob
        { AccountId = account.Id, TargetKind = TargetKind.UserMedia, UserId = _alice.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
        _db.Jobs.Add(_job);
        _db.SaveChanges(); // 适配：SQLite 不在 Add 时分配临时键值（job.Id=0），先落 Job 取真实 Id 再挂 Files，否则 FK 约束失败
        _db.Files.AddRange(
            F(FileStatus.Downloaded, _alice.Id, "1"), F(FileStatus.Downloaded, _alice.Id, "2"),
            F(FileStatus.Downloaded, _alice.Id, "3"), F(FileStatus.Skipped, _alice.Id, "4"),
            F(FileStatus.Downloaded, null, "L1"));  // likes 文件：UserId 为空，不计入
        _db.SaveChanges();
    }
    public void Dispose() => _t.Item1.Dispose();

    private DownloadFile F(FileStatus status, long? userId, string itemId) => new()
    { JobId = _job.Id, UserId = userId, SourceItemId = itemId, Url = "u", FilePath = "p", Status = status, CreatedAt = DateTime.UtcNow };

    [Fact]
    public async Task Apply_counts_only_downloaded_user_files()
    {
        var stats = new StatsAggregator(new SingleDbContextFactory(_db));

        await stats.ApplyJobCompletionAsync(_job.Id);

        var user = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id); // 适配：ExecuteUpdate 绕过变更跟踪器，跟踪查询会返回陈旧实例（同 UserServiceTests）
        Assert.Equal(3, user.DownloadCount);
        Assert.NotNull(user.LastDownloadAt);
    }

    [Fact]
    public async Task Apply_is_incremental_across_jobs()
    {
        var stats = new StatsAggregator(new SingleDbContextFactory(_db));
        await stats.ApplyJobCompletionAsync(_job.Id);

        var job2 = new DownloadJob
        { AccountId = _job.AccountId, TargetKind = TargetKind.UserMedia, UserId = _alice.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
        _db.Jobs.Add(job2);
        _db.SaveChanges(); // 适配：同构造器，先落 job2 取真实 Id 再挂 File
        _db.Files.Add(new DownloadFile
        { JobId = job2.Id, UserId = _alice.Id, SourceItemId = "9", Url = "u", FilePath = "p", Status = FileStatus.Downloaded, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        await stats.ApplyJobCompletionAsync(job2.Id);
        Assert.Equal(4, (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _alice.Id)).DownloadCount); // 适配：同上，AsNoTracking 读库中真实状态
    }
}
