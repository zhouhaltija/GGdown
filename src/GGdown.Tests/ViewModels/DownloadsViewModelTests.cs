using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Sites;
using GGdown.Threading;
using GGdown.ViewModels;
using Microsoft.Data.Sqlite; // 补 using：SqliteConnection 在 tuple 类型中未限定（同 UsersViewModelTests，brief 未列）
using Microsoft.EntityFrameworkCore; // 补 using：测试体用了 ExecuteDeleteAsync/AsNoTracking（同 UsersViewModelTests）
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.ViewModels;

/// <summary>
/// B5：下载页 VM 测试。队列用真 DownloadQueueService + FakeEngine（brief Step 1），
/// 测事件驱动的卡片生命周期：入队卡片出现（Title/KindBadge）、file-done 后 ProgressText、
/// 终态后卡片移除 + HasActive=false、取消路径、无账号拦截。
/// 组装同 UsersViewModelTests 模式（TestDb/SingleDbContextFactory/TestPaths/SiteRegistry + SyncDispatcher）。
/// </summary>
public class DownloadsViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GGdownSiteDbContext) _t;
    private readonly (SqliteConnection, GGdownGlobalDbContext) _global = TestDb.CreateGlobal();
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine = new();
    private readonly GGdownSiteDbContext _db;
    private readonly DownloadQueueService _queue;
    private readonly DownloadsViewModel _vm;
    private readonly FakeCurrentSite _site = new();
    private readonly Account _account;
    private readonly User _alice;
    private readonly SingleDbContextFactory _factory;
    private readonly AppSettings _settings;

    public DownloadsViewModelTests()
    {
        _t = TestDb.CreateSite();
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
        _settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), sites, new SiteDbContextFactory(_paths));
        _queue = new DownloadQueueService(new SingleSiteDbContextFactory(_db), _engine, new FakeStats(), _paths, sites,
            NullLogger<DownloadQueueService>.Instance);
        _vm = CreateVm(_site);
        _vm.Start(); // 页面 OnNavigatedTo 的等价调用：订阅 + 播种 + HasAccount 刷新
    }

    private DownloadsViewModel CreateVm(ICurrentSite site) =>
        new(_queue, new AccountQueryService(new SingleSiteDbContextFactory(_db)), new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), new SiteRegistry([new TwitterSiteProvider()]), new SiteDbContextFactory(_paths)),
            new SyncDispatcher(), site, new SiteRegistry([new TwitterSiteProvider()]));

    public void Dispose()
    {
        _t.Item1.Dispose();
        _global.Item1.Dispose();
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
        Assert.False(vm.ShowComingSoon);
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

    [Fact]
    public async Task Search_enqueues_permalink_job()
    {
        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, ct) => await gate.Task.WaitAsync(ct);
        await WaitUntil(() => _vm.HasAccount);
        Assert.True(_vm.SupportsSearch);
        _vm.SearchQuery = "from:alice filter:media";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Contains("已加入下载队列", _vm.StatusMessage);
        Assert.Contains(_queue.Active, j => j.Kind == TargetKind.Search);
        gate.SetResult();
        await WaitUntil(() => _queue.Active.Count == 0);
        Assert.Contains(_db.Jobs.AsNoTracking(), j => j.TargetKind == TargetKind.Search);
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

    // ---- Task 10：全局下载视图（栏底入口）——平台模式只见本站，全局模式全见 ----

    [Fact]
    public async Task Global_view_shows_all_site_jobs()
    {
        var pixivAccount = new Account
        { SiteId = "pixiv", CookiePath = "p", RestId = "1", ScreenName = "me", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        var pixivUser = new User
        { SiteId = "pixiv", RestId = "99", ScreenName = "pico", Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Accounts.Add(pixivAccount);
        _db.Users.Add(pixivUser);
        await _db.SaveChangesAsync();

        var sites = new SiteRegistry([new TwitterSiteProvider(), new PixivSiteProvider()]);
        var queue = new DownloadQueueService(new SingleSiteDbContextFactory(_db), _engine, new FakeStats(), _paths,
            sites, NullLogger<DownloadQueueService>.Instance);
        var vm = new DownloadsViewModel(queue, new AccountQueryService(new SingleSiteDbContextFactory(_db)),
            _settings, new SyncDispatcher(), new FakeCurrentSite(), sites);
        vm.Start();
        await WaitUntil(() => vm.HasAccount);

        var gate = new TaskCompletionSource();
        _engine.OnDownload = async (_, _, _, _) => await gate.Task; // 挂起保持卡片活跃
        await queue.EnqueueUserMediaAsync(pixivAccount, [pixivUser], @"D:\dl", new Dictionary<string, object?>());
        await WaitUntil(() => queue.Active.Count == 1);

        // 平台模式（当前 twitter）：pixiv 任务不进卡片
        await WaitUntil(() => vm.Jobs.Count == 0);

        // 全局模式：全部平台任务可见
        vm.SetGlobalView(true);
        await WaitUntil(() => vm.Jobs.Count == 1);
        Assert.Contains("pico", vm.Jobs[0].Title);

        vm.SetGlobalView(false); // 回平台模式 → 卡片再度隐藏
        await WaitUntil(() => vm.Jobs.Count == 0);

        gate.SetResult();
        await WaitUntil(() => queue.Active.Count == 0);
        vm.Stop();
    }
}
