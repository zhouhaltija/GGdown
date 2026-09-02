using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Settings;
using GalleryGUI.Sites;
using GalleryGUI.Threading;
using GalleryGUI.ViewModels;
using Microsoft.Data.Sqlite; // 补 using：SqliteConnection 在 tuple 类型中未限定（同 UsersViewModelTests，brief 未列）
using Microsoft.EntityFrameworkCore; // 补 using：测试体用了 ExecuteDeleteAsync/AsNoTracking（同 UsersViewModelTests）
using Microsoft.Extensions.Logging.Abstractions;

namespace GalleryGUI.Tests.ViewModels;

/// <summary>
/// B5：下载页 VM 测试。队列用真 DownloadQueueService + FakeEngine（brief Step 1），
/// 测事件驱动的卡片生命周期：入队卡片出现（Title/KindBadge）、file-done 后 ProgressText、
/// 终态后卡片移除 + HasActive=false、取消路径、无账号拦截。
/// 组装同 UsersViewModelTests 模式（TestDb/SingleDbContextFactory/TestPaths/SiteRegistry + SyncDispatcher）。
/// </summary>
public class DownloadsViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GalleryDbContext) _t;
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine = new();
    private readonly GalleryDbContext _db;
    private readonly DownloadQueueService _queue;
    private readonly DownloadsViewModel _vm;
    private readonly FakeCurrentSite _site = new();
    private readonly Account _account;
    private readonly User _alice;
    private readonly SingleDbContextFactory _factory;

    public DownloadsViewModelTests()
    {
        _t = TestDb.Create();
        _db = _t.Item2;
        _factory = new SingleDbContextFactory(_db);
        _account = new Account
        { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _alice = new User
        { SiteId = "twitter", RestId = "1", ScreenName = "alice", Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Accounts.Add(_account);
        _db.Users.Add(_alice);
        _db.SaveChanges();
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        var settings = new AppSettings(_factory, sites);
        _queue = new DownloadQueueService(_factory, _engine, new FakeStats(), _paths, sites,
            NullLogger<DownloadQueueService>.Instance);
        _vm = CreateVm(_site);
        _vm.Start(); // 页面 OnNavigatedTo 的等价调用：订阅 + 播种 + HasAccount 刷新
    }

    private DownloadsViewModel CreateVm(ICurrentSite site) =>
        new(_queue, new AccountQueryService(_factory), new AppSettings(_factory, new SiteRegistry([new TwitterSiteProvider()])),
            new SyncDispatcher(), site, new UserQueryService(_factory), _factory);

    public void Dispose()
    {
        _t.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private static IReadOnlyDictionary<string, object?> Opts() => new Dictionary<string, object?> { ["videos"] = true };

    // ① 入队后卡片出现（Title/KindBadge 正确：用户媒体/账号喜欢/账号书签）
    [Fact]
    public async Task Enqueue_creates_cards_with_title_and_kind_badge()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, _) => await gate.Task; // 首任务（FIFO=用户媒体）挂起，保持 3 卡片活跃

        await WaitUntil(() => _vm.HasAccount); // Start() 的异步 HasAccount 刷新
        await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await _vm.DownloadLikesCommand.ExecuteAsync(null);
        await _vm.DownloadBookmarksCommand.ExecuteAsync(null);
        await WaitUntil(() => _vm.Jobs.Count == 3);

        Assert.True(_vm.HasActive);
        Assert.Equal("用户媒体", _vm.Jobs[0].KindBadge); // JobSnapshot.Kind 是 Data.TargetKind
        Assert.Equal("alice", _vm.Jobs[0].Title);
        Assert.Equal("账号喜欢", _vm.Jobs[1].KindBadge);
        Assert.Contains("喜欢", _vm.Jobs[1].Title);
        Assert.Equal("账号书签", _vm.Jobs[2].KindBadge);
        Assert.Contains("书签", _vm.Jobs[2].Title);
        Assert.All(_vm.Jobs, c => Assert.False(c.IsFinished));
        Assert.Contains("已加入下载队列", _vm.StatusMessage);

        gate.SetResult(); // 收尾：释放挂起的引擎调用，任务跑完卡片清空
        await WaitUntil(() => _vm.Jobs.Count == 0);
    }

    // ② file-done 事件后 ProgressText 更新（"N/M · 跳过 X"格式）+ 当前文件/不确定态
    [Fact]
    public async Task FileDone_updates_progress_text_and_current_file()
    {
        var reported = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, progress, _) =>
        {
            progress.Report(new EngineEvent("url-start", Url: "https://x.com/alice/media"));
            progress.Report(new EngineEvent("file-start", Path: "D:/x/1_a_1.jpg", ItemId: "1"));
            progress.Report(new EngineEvent("file-done", Path: "D:/x/1_a_1.jpg", Size: 100));
            progress.Report(new EngineEvent("file-skip", Path: "D:/x/2_b_1.jpg"));
            reported.SetResult();
            await release.Task; // 挂在 job-done 之前，让运行中快照可断言
        };

        await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await reported.Task;
        await WaitUntil(() => "已完成 1/2 · 跳过 1" == _vm.Jobs.Single().ProgressText);
        var card = _vm.Jobs.Single();

        Assert.Equal("下载中", card.StatusText);
        Assert.False(card.IsFinished);
        Assert.True(card.IsIndeterminate);        // Total==0（队列仅在终态回填 Total）且 Running
        Assert.Equal(0, card.ProgressPercent);
        Assert.Equal("已完成 1/2 · 跳过 1", card.ProgressText);
        Assert.Equal("D:/x/1_a_1.jpg", card.CurrentFile);

        release.SetResult();
        await WaitUntil(() => _vm.Jobs.Count == 0);
    }

    // ③ job-done 终态后卡片从 Jobs 移除且 HasActive 变 false（JobsChanged 供测试：验证移除前终态快照已刷进卡片）
    [Fact]
    public async Task JobDone_removes_card_and_clears_HasActive()
    {
        var seenTexts = new List<string>();
        _vm.JobsChanged += () => { if (_vm.Jobs.Count == 1) seenTexts.Add(_vm.Jobs[0].ProgressText); };
        _engine.OnDownload = (plan, cookies, progress, _) =>
        {
            progress.Report(new EngineEvent("file-done", Path: "D:/x/1.jpg", Size: 10));
            progress.Report(new EngineEvent("job-done", Total: 1, Skipped: 0, Failed: 0));
            return Task.CompletedTask;
        };

        var jobId = await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _vm.Jobs.Count == 0);

        Assert.False(_vm.HasActive);
        Assert.Empty(_vm.Jobs);
        Assert.Contains("已完成 1/1 · 跳过 0", seenTexts); // 终态 JobChanged 先于 JobRemoved 刷新卡片
        var job = await _db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(JobStatus.Completed, job.Status);
    }

    // ④ CancelAsync 路径（OnDownload 挂起 + 卡片取消按钮 → 卡片移除、任务标 Canceled）
    [Fact]
    public async Task Cancel_from_card_removes_card_and_cancels_job()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (plan, cookies, progress, ct) =>
        {
            progress.Report(new EngineEvent("file-start", Path: "D:/x/1.jpg", ItemId: "1"));
            await gate.Task.WaitAsync(ct); // 可被取消唤醒（同 DownloadQueueServiceTests.Cancel_moves_job_to_canceled 适配）
            ct.ThrowIfCancellationRequested();
        };

        var jobId = await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _vm.Jobs.Count == 1);
        var card = _vm.Jobs.Single();
        Assert.Equal(jobId, card.JobId);

        card.CancelCommand.Execute(null);
        await WaitUntil(() => _vm.Jobs.Count == 0);

        Assert.False(_vm.HasActive);
        Assert.Equal(JobStatus.Canceled, (await _db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).Status);
    }

    // ⑤ 无账号时 DownloadLikes/Bookmarks 被拦截（StatusMessage 含"请先"）
    [Fact]
    public async Task Account_content_commands_blocked_without_account()
    {
        await _db.Accounts.ExecuteDeleteAsync();

        await _vm.DownloadLikesCommand.ExecuteAsync(null);
        Assert.Contains("请先在设置中导入 Cookie", _vm.StatusMessage);
        await _vm.DownloadBookmarksCommand.ExecuteAsync(null);
        Assert.Contains("请先在设置中导入 Cookie", _vm.StatusMessage);

        Assert.Empty(_vm.Jobs);          // 未产生任何任务
        Assert.False(_vm.HasActive);
        Assert.Empty(_engine.Downloads); // 引擎未被触达
    }

    [Fact]
    public async Task Start_loads_download_list_excluding_skipped()
    {
        _alice.InDownloadList = true;
        _db.Users.AddRange(
            new User { SiteId = "twitter", RestId = "2", ScreenName = "bob", Source = UserSource.Following, InDownloadList = true, IsSkipped = true, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new User { SiteId = "twitter", RestId = "3", ScreenName = "carol", DisplayName = "Carol", Source = UserSource.Following, InDownloadList = true, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        await _site.SelectAsync("twitter");
        await WaitUntil(() => _vm.DownloadList.Count == 2);

        Assert.True(_vm.HasDownloadList);
        Assert.Equal("待下载清单（2）", _vm.DownloadListHeader);
        Assert.Equal(["alice", "Carol"], _vm.DownloadList.Select(i => i.Title));
        Assert.Equal("@carol", _vm.DownloadList.Single(i => i.Title == "Carol").Subtitle);
    }

    [Fact]
    public async Task StartDownloadList_enqueues_all_and_keeps_list()
    {
        _alice.InDownloadList = true;
        _db.Users.Add(new User { SiteId = "twitter", RestId = "2", ScreenName = "bob", Source = UserSource.Following, InDownloadList = true, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        await _site.SelectAsync("twitter");
        await WaitUntil(() => _vm.DownloadList.Count == 2);

        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, _) => await gate.Task;

        await _vm.StartDownloadListCommand.ExecuteAsync(null);
        await WaitUntil(() => _vm.Jobs.Count == 2);

        Assert.Contains("已加入下载队列", _vm.StatusMessage);
        Assert.Contains("2 个用户", _vm.StatusMessage);
        Assert.Equal(2, _vm.DownloadList.Count);
        Assert.All(_db.Users.AsNoTracking().Where(u => u.InDownloadList), u => Assert.True(u.InDownloadList));
        Assert.Equal(2, _db.Jobs.Count());

        gate.SetResult();
        await WaitUntil(() => _vm.Jobs.Count == 0);
    }

    [Fact]
    public async Task Remove_from_download_list_clears_flag()
    {
        _alice.InDownloadList = true;
        _db.SaveChanges();
        await _site.SelectAsync("twitter");
        await WaitUntil(() => _vm.DownloadList.Count == 1);

        await _vm.DownloadList[0].RemoveCommand.ExecuteAsync(null);
        await WaitUntil(() => _vm.DownloadList.Count == 0);

        Assert.False(_vm.HasDownloadList);
        Assert.Equal("待下载清单（0）", _vm.DownloadListHeader);
        Assert.False(_db.Users.AsNoTracking().Single(u => u.Id == _alice.Id).InDownloadList);
    }

    [Fact]
    public async Task StartDownloadList_empty_sets_message()
    {
        await WaitUntil(() => _vm.HasAccount);
        await _vm.StartDownloadListCommand.ExecuteAsync(null);
        Assert.Contains("清单为空", _vm.StatusMessage);
        Assert.Empty(_vm.Jobs);
        Assert.Empty(_engine.Downloads);
    }

    [Fact]
    public async Task StartDownloadList_blocked_without_account()
    {
        _alice.InDownloadList = true;
        await _db.Accounts.ExecuteDeleteAsync();
        _db.SaveChanges();
        await _site.SelectAsync("twitter");
        await WaitUntil(() => _vm.DownloadList.Count == 1);

        await _vm.StartDownloadListCommand.ExecuteAsync(null);
        Assert.Contains("请先在设置中导入 Cookie", _vm.StatusMessage);
        Assert.Empty(_vm.Jobs);
        Assert.Empty(_engine.Downloads);
    }

    [Fact]
    public async Task Switching_site_reloads_download_list()
    {
        _alice.InDownloadList = true;
        _db.SaveChanges();
        var site = new FakeCurrentSite();
        var vm = CreateVm(site);
        vm.Start();
        await WaitUntil(() => vm.DownloadList.Count == 1);

        await site.SelectAsync("pixiv");
        Assert.Empty(vm.DownloadList);
        Assert.False(vm.HasDownloadList);

        await site.SelectAsync("twitter");
        Assert.Single(vm.DownloadList);
    }

    [Fact]
    public async Task Switching_site_hides_other_site_jobs_without_canceling()
    {
        var site = new FakeCurrentSite();
        var vm = CreateVm(site);
        vm.Start();

        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, _) => await gate.Task;

        await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => vm.Jobs.Count == 1);

        await site.SelectAsync("pixiv");
        Assert.Empty(vm.Jobs);
        Assert.False(vm.HasActive);
        Assert.True(vm.ShowComingSoon);
        Assert.Single(_queue.Active); // 后台仍在跑，只是 UI 不展示

        await site.SelectAsync("twitter");
        Assert.Single(vm.Jobs);
        Assert.True(vm.HasActive);

        gate.SetResult();
        await WaitUntil(() => vm.Jobs.Count == 0);
    }

    // 审查 Important-1 回归：Stop 窗口内任务终态（JobChanged/JobRemoved 均丢失）→ 重新 Start 后无幽灵卡片残留
    [Fact]
    public async Task Start_prunes_ghost_cards_from_missed_removals_during_stop()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, _) => await gate.Task;

        var jobId = await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _vm.Jobs.Count == 1 && _vm.HasActive); // 卡片已建（ctor 已 Start 订阅）

        _vm.Stop();       // 离页：退订
        gate.SetResult(); // 任务在 Stop 窗口内跑到终态，JobChanged/JobRemoved 均错过
        await WaitUntil(() => _queue.Active.Count == 0);

        // 修复前 bug 的 precondition：卡片滞留"下载中"、HasActive 卡 true
        Assert.Single(_vm.Jobs);
        Assert.True(_vm.HasActive);

        _vm.Start(); // 回页：播种 + 剪除
        await WaitUntil(() => _vm.Jobs.Count == 0);
        Assert.False(_vm.HasActive);
        Assert.DoesNotContain(_vm.Jobs, c => c.JobId == jobId);
    }

    // 修复波 B5 flaky 处置：隔离复跑 3/3 通过、全量负载下偶发 5s 超时（累计 3 次）——负载敏感而非回归，
    // 按最终审查建议把默认 timeoutMs 5000→15000（纯测试参数，断言不变）
    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 15000)
    {
        var start = Environment.TickCount;
        while (!cond())
        {
            if (Environment.TickCount - start > timeoutMs) throw new TimeoutException("条件等待超时");
            await Task.Delay(20);
        }
    }
}
