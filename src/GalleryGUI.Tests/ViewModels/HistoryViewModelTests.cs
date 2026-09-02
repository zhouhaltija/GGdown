using GalleryGUI.Data;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using GalleryGUI.Threading;
using GalleryGUI.ViewModels;
using Microsoft.Data.Sqlite; // 同 UsersViewModelTests：SqliteConnection 在 tuple 类型中未限定
using Microsoft.EntityFrameworkCore;

namespace GalleryGUI.Tests.ViewModels;

/// <summary>
/// B6：历史页 VM 测试。真 HistoryQueryService + TestDb 播种（两个用户 + 各自文件 + likes 文件），
/// 组装同 UsersViewModelTests 模式（SingleDbContextFactory/SyncDispatcher/SiteRegistry）。
/// 覆盖（控制器裁定 4 类）：①行映射（FileName 取 Path.GetFileName、UserText 空则"账号内容"、
/// SourceUrl 构造 https://x.com/i/status/{SourceItemId}、SizeText 人性化、StatusText 三态、
/// CreatedText 本地时间格式）②UserId 筛选 ③From/To 日期筛选（To 含当日）④OpenSource 无
/// SourceItemId → StatusMessage；另覆盖筛选变更自动刷新与 OpenContainingFolder 文件缺失路径。
/// 播种 CreatedAt 用 Kind=Unspecified 的固定日期（SQLite 存取均按墙钟字符串比较，与时区无关，全机确定）。
/// </summary>
public class HistoryViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly GalleryDbContext _db;
    private readonly HistoryViewModel _vm;
    private readonly User _alice;
    private readonly User _bob;
    private readonly long _jobId;

    public HistoryViewModelTests()
    {
        _t = TestDb.Create();
        _db = _t.Item2;
        var factory = new SingleDbContextFactory(_db);
        var account = new Account
        { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _alice = new User
        { SiteId = "twitter", RestId = "1", ScreenName = "alice", Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _bob = new User
        { SiteId = "twitter", RestId = "2", ScreenName = "bob", Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Accounts.Add(account);
        _db.Users.AddRange(_alice, _bob);
        _db.SaveChanges();
        var job = new DownloadJob
        { AccountId = account.Id, TargetKind = TargetKind.UserMedia, UserId = _alice.Id, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow };
        _db.Jobs.Add(job);
        _db.SaveChanges(); // 同 QueriesTests：先落库取得真实 Id 再挂 Files（临时负值会 FK 失败）
        _jobId = job.Id;
        _db.Files.AddRange(
            new DownloadFile
            { JobId = _jobId, UserId = _alice.Id, SourceItemId = "11", Url = "u", FilePath = @"C:\dl\alice\11_1.jpg", FileSize = 100, Status = FileStatus.Downloaded, CreatedAt = new DateTime(2026, 8, 10, 12, 0, 0) },
            new DownloadFile
            { JobId = _jobId, UserId = _bob.Id, SourceItemId = "22", Url = "u", FilePath = @"C:\dl\bob\22_1.mp4", FileSize = 1536, Status = FileStatus.Skipped, CreatedAt = new DateTime(2026, 8, 11, 12, 0, 0) },
            new DownloadFile
            { JobId = _jobId, UserId = null, SourceItemId = null, Url = "u", FilePath = @"C:\dl\_likes\1.jpg", FileSize = null, Status = FileStatus.Failed, CreatedAt = new DateTime(2026, 8, 12, 12, 0, 0) });
        _db.SaveChanges();
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        _vm = new HistoryViewModel(new HistoryQueryService(factory), new SyncDispatcher(),
            new AccountQueryService(factory), sites, new FakeCurrentSite());
    }

    public void Dispose()
    {
        _t.Item1.Dispose();
    }

    // ① 行映射：FileName 取 Path.GetFileName、UserText 空则"账号内容"、SourceUrl 构造、
    //    SizeText 人性化、StatusText 三态、CreatedText 本地时间格式
    [Fact]
    public async Task Refresh_maps_row_fields()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.Rows.Count); // CreatedAt DESC：likes(08-12) → bob(08-11) → alice(08-10)

        var alice = _vm.Rows.Single(r => r.FileName == "11_1.jpg");
        Assert.Equal("11_1.jpg", alice.FileName);                    // 取自 FilePath 的文件名部分
        Assert.Equal("alice", alice.UserText);
        Assert.Equal("https://x.com/i/status/11", alice.SourceUrl);  // 由 SourceItemId 构造
        Assert.True(alice.HasSourceUrl);
        Assert.Equal("100 B", alice.SizeText);
        Assert.Equal("已下载", alice.StatusText);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", alice.CreatedText); // "yyyy-MM-dd HH:mm" 格式

        var bob = _vm.Rows.Single(r => r.FileName == "22_1.mp4");
        Assert.Equal("1.5 KB", bob.SizeText);                        // 1536B 人性化 1 位小数
        Assert.Equal("已跳过", bob.StatusText);

        var likes = _vm.Rows.Single(r => r.FileName == "1.jpg");
        Assert.Equal("账号内容", likes.UserText);                    // likes 文件无用户 → 账号内容
        Assert.Null(likes.SourceUrl);                                // 无 SourceItemId
        Assert.False(likes.HasSourceUrl);
        Assert.Equal("—", likes.SizeText);                           // 无大小
        Assert.Equal("失败", likes.StatusText);
    }

    // SizeText 全量级：B/KB/MB/GB（F1 一位小数）与 null 占位
    [Fact]
    public void FormatSize_covers_all_units()
    {
        Assert.Equal("—", HistoryRowViewModel.FormatSize(null));
        Assert.Equal("1023 B", HistoryRowViewModel.FormatSize(1023));
        Assert.Equal("1.0 KB", HistoryRowViewModel.FormatSize(1024));
        Assert.Equal("1.5 KB", HistoryRowViewModel.FormatSize(1536));
        Assert.Equal("1.0 MB", HistoryRowViewModel.FormatSize(1024 * 1024));
        Assert.Equal("5.0 MB", HistoryRowViewModel.FormatSize(5 * 1024 * 1024));
        Assert.Equal("3.0 GB", HistoryRowViewModel.FormatSize(3L * 1024 * 1024 * 1024));
    }

    // ② UserId 筛选：选中 bob → 只剩 bob 行；清除（null=全部）→ 恢复
    [Fact]
    public async Task SelectedUserFilter_filters_rows()
    {
        await _vm.LoadFilterUsersAsync();
        Assert.Equal(["alice", "bob"], _vm.FilterUsers.Select(u => u.ScreenName)); // ListUsersAsync 按 ScreenName 排序
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.Rows.Count);

        _vm.SelectedUserFilter = _vm.FilterUsers.Single(u => u.ScreenName == "bob");
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Single(_vm.Rows);
        Assert.All(_vm.Rows, r => Assert.Equal("bob", r.UserText));

        _vm.SelectedUserFilter = null;
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.Rows.Count);
    }

    // 筛选三元组任一变更自动触发 Refresh（不手动执行命令，等待行数变化）
    [Fact]
    public async Task Filter_property_changes_refresh_rows_automatically()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.Rows.Count);

        _vm.SelectedUserFilter = _bob;
        await WaitUntil(() => _vm.Rows.Count == 1);
        Assert.All(_vm.Rows, r => Assert.Equal("bob", r.UserText));

        _vm.SelectedUserFilter = null;
        await WaitUntil(() => _vm.Rows.Count == 3);
    }

    // ③ From/To 日期筛选：To 含当日（HistoryFilter.To = ToDate.Date.AddDays(1) 开区间上界）
    [Fact]
    public async Task FromDate_and_ToDate_filter_rows_with_to_day_inclusive()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.Rows.Count);

        _vm.FromDate = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, _vm.Rows.Count); // 08-11 与 08-12
        Assert.DoesNotContain(_vm.Rows, r => r.FileName == "11_1.jpg"); // 08-10 被排除

        _vm.ToDate = new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, _vm.Rows.Count); // 08-11 与 08-12（含当日）
        Assert.Contains(_vm.Rows, r => r.FileName == "22_1.mp4");
        Assert.Contains(_vm.Rows, r => r.FileName == "1.jpg"); // 08-12 当日仍包含

        _vm.FromDate = null;
        _vm.ToDate = null;
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.Rows.Count);
    }

    // ④ OpenSource：无 SourceItemId → StatusMessage"该记录无来源链接"（宿主命令与行内命令同核）
    [Fact]
    public async Task OpenSource_without_source_item_sets_status_message()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        var likes = _vm.Rows.Single(r => r.FileName == "1.jpg");

        _vm.OpenSourceCommand.Execute(likes);
        Assert.Equal("该记录无来源链接", _vm.StatusMessage);

        likes.OpenSourceCommand.Execute(null); // 行 VM 回调命令走同一核心方法
        Assert.Equal("该记录无来源链接", _vm.StatusMessage);
    }

    // OpenContainingFolder：文件不存在 → StatusMessage"文件已被移动或删除"（不启动 explorer）
    [Fact]
    public async Task OpenContainingFolder_with_missing_file_sets_status_message()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ggui-missing-" + Guid.NewGuid().ToString("N") + ".jpg");
        _db.Files.Add(new DownloadFile
        { JobId = _jobId, UserId = _alice.Id, SourceItemId = "33", Url = "u", FilePath = missing, FileSize = 5 * 1024 * 1024, Status = FileStatus.Downloaded, CreatedAt = new DateTime(2026, 8, 13, 12, 0, 0) });
        await _db.SaveChangesAsync();

        await _vm.RefreshCommand.ExecuteAsync(null);
        var row = _vm.Rows.Single(r => r.Model.FilePath == missing);

        _vm.OpenContainingFolderCommand.Execute(row);
        Assert.Equal("文件已被移动或删除", _vm.StatusMessage);

        row.OpenContainingFolderCommand.Execute(null); // 行 VM 回调命令走同一核心方法
        Assert.Equal("文件已被移动或删除", _vm.StatusMessage);
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 5000)
    {
        var start = Environment.TickCount;
        while (!cond())
        {
            if (Environment.TickCount - start > timeoutMs) throw new TimeoutException("条件等待超时");
            await Task.Delay(20);
        }
    }
}
