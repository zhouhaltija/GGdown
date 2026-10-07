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
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace GGdown.Tests.ViewModels;

/// <summary>
/// B5：下载页 VM 测试。队列用真 DownloadQueueService + FakeEngine（brief Step 1），
/// 测事件驱动的卡片生命周期：入队卡片出现（Title/KindBadge）、file-done 后 ProgressText、
/// 终态后卡片移除 + HasActive=false、取消路径、无账号拦截。
/// 平台数据库使用 WAL 文件库和独立连接，模拟账号查询与后台下载的并发。
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
    private readonly IsolatedSiteDbContextFactory _siteFactory;
    private readonly List<DownloadQueueService> _queues = [];
    private readonly List<DownloadsViewModel> _viewModels = [];
    private readonly List<TaskCompletionSource> _engineGates = [];
    private readonly AppSettings _settings;
    private readonly QueueLogger _queueLog = new();

    [Fact]
    public void Database_uses_production_WAL_mode_for_concurrent_readers_and_writers()
    {
        using var command = _t.Item1.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", command.ExecuteScalar());
    }

    [Fact]
    public async Task Account_query_is_isolated_from_another_contexts_uncommitted_changes()
    {
        var factory = _siteFactory;
        await using var writer = await factory.CreateAsync("twitter");
        await using var transaction = await writer.Database.BeginTransactionAsync();
        var account = await writer.Accounts.SingleAsync(a => a.Id == _account.Id);
        account.IsActive = false;
        await writer.SaveChangesAsync();

        var active = await new AccountQueryService(factory).GetActiveAsync("twitter");
        Assert.NotNull(active);
        Assert.Equal(_account.Id, active.Id);
    }

    [Fact]
    public async Task Fixture_cleanup_releases_waiting_downloads_and_database_files()
    {
        var fixture = new DownloadsViewModelTests();
        try
        {
            var gate = fixture.CreateGate();
            fixture._engine.OnDownload = async (_, _, _, _) => await gate.Task;
            await fixture._queue.EnqueueUserMediaAsync(fixture._account, [fixture._alice], @"D:\dl", Opts());
        }
        finally
        {
            // 模拟测试主体提前结束，没有走到释放 gate 的正常路径。
            fixture.Dispose();
        }
        Assert.Empty(fixture._queue.Active);
        Assert.False(Directory.Exists(fixture._paths.Root));
    }

    public DownloadsViewModelTests()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_paths.Root, "downloads-tests.db"),
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var db = new GGdownSiteDbContext(new DbContextOptionsBuilder<GGdownSiteDbContext>()
            .UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        // 外部已打开的连接不会由 EnsureCreated 自动启用 WAL；与生产工厂保持一致。
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        _t = (connection, db);
        _db = _t.Item2;
        _siteFactory = new IsolatedSiteDbContextFactory(connectionString);
        _account = new Account
        { SiteId = "twitter", CookiePath = "c", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _alice = new User
        { SiteId = "twitter", RestId = "1", ScreenName = "alice", Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Accounts.Add(_account);
        _db.Users.Add(_alice);
        _db.SaveChanges();
        var sites = new SiteRegistry([new TwitterSiteProvider()]);
        _settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), sites, new SiteDbContextFactory(_paths));
        _queue = CreateQueue(sites);
        _vm = CreateVm(_site);
        _vm.Start(); // 页面 OnNavigatedTo 的等价调用：订阅 + 播种 + HasAccount 刷新
    }

    private DownloadsViewModel CreateVm(ICurrentSite site) =>
        TrackVm(new(_queue, new AccountQueryService(_siteFactory), new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), new SiteRegistry([new TwitterSiteProvider()]), new SiteDbContextFactory(_paths)),
            new SyncDispatcher(), site, new SiteRegistry([new TwitterSiteProvider()])));

    private DownloadsViewModel TrackVm(DownloadsViewModel vm)
    {
        _viewModels.Add(vm);
        return vm;
    }

    private TaskCompletionSource CreateGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engineGates.Add(gate);
        return gate;
    }

    private DownloadQueueService CreateQueue(SiteRegistry sites)
    {
        var queue = new DownloadQueueService(_siteFactory, _engine, new FakeStats(), _paths, sites,
            _queueLog);
        _queues.Add(queue);
        return queue;
    }

    public void Dispose()
    {
        try
        {
            foreach (var gate in _engineGates) gate.TrySetResult();
            foreach (var vm in _viewModels) vm.Stop();
            // 断言失败也释放挂起的引擎；后台退出 await using 后再删除数据库。
            Assert.True(SpinWait.SpinUntil(() => _queues.All(q => q.Active.Count == 0)
                && !_siteFactory.HasOpenConnections, TimeSpan.FromSeconds(5)), "测试结束时仍有下载任务或数据库连接未释放");
        }
        finally
        {
            _db.Dispose();
            _t.Item1.Dispose();
            _global.Item2.Dispose();
            _global.Item1.Dispose();
            if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
        }
    }

    private static IReadOnlyDictionary<string, object?> Opts() => new Dictionary<string, object?> { ["videos"] = true };

    // ① 入队后卡片出现（Title/KindBadge 正确：用户媒体/账号喜欢/账号书签）
    [Fact]
    public async Task Enqueue_creates_cards_with_title_and_kind_badge()
    {
        var gate = CreateGate();
        _engine.OnDownload = async (_, _, _, _) => await gate.Task; // 首任务（FIFO=用户媒体）挂起，保持 3 卡片活跃

        await WaitUntil(() => _vm.HasAccount || _vm.StatusMessage is not null);
        Assert.True(_vm.HasAccount, _vm.StatusMessage); // 账号查询失败时直接报告原因，避免只看到等待超时。
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
        var release = CreateGate();
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

    // 终态任务进入「最近完成」（离页期间结束的也算）；清除记录只清当前视图
    [Fact]
    public async Task Finished_jobs_are_kept_in_recent_list_until_cleared()
    {
        _engine.OnDownload = (plan, cookies, progress, _) =>
        {
            progress.Report(new EngineEvent("job-done", Total: 0, Skipped: 0, Failed: 0));
            return Task.CompletedTask;
        };
        _vm.Stop(); // 离页状态下结束
        await _queue.EnqueueUserMediaAsync(_account, [_alice], @"D:\dl", Opts());
        await WaitUntil(() => _vm.Finished.Count == 1);
        Assert.True(_vm.HasFinished);
        Assert.True(_vm.Finished[0].IsFinished);
        Assert.Contains("1", _vm.FinishedHeader);
        Assert.False(_vm.ShowEmptyState);

        _vm.ClearFinishedCommand.Execute(null);
        Assert.Empty(_vm.Finished);
        Assert.True(_vm.ShowEmptyState);
    }

    [Fact]
    public void Twitter_shows_single_link_input()
    {
        Assert.True(_vm.SupportsSearch);
        Assert.True(_vm.SupportsPermalink);
        Assert.False(_vm.ShowPermalinkInput); // 搜索框已兼容推文/列表链接
        Assert.True(_vm.ShowNewDownloadPanel);
        _vm.SetGlobalView(true);
        Assert.False(_vm.ShowNewDownloadPanel); // 全局视图只管队列
    }

    // ④ CancelAsync 路径（OnDownload 挂起 + 卡片取消按钮 → 卡片移除、任务标 Canceled）
    [Fact]
    public async Task Cancel_from_card_removes_card_and_cancels_job()
    {
        var gate = CreateGate();
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

        var gate = CreateGate();
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
        var gate = CreateGate();
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
        var gate = CreateGate();
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

    [Theory]
    [InlineData("https://www.douyin.com/video/1234567890123456789", "https://www.douyin.com/video/1234567890123456789")]
    [InlineData("0.71 o@q.eb 02/16 lcn:/ :5pm 天赋不会给你刀刻般的肌肉💪🐱 # 无敌小猫拳 # 哈基米  https://v.douyin.com/uJS3Tm5L5iI/ 复制此链接，打开Dou音搜索，直接观看视频！", "https://v.douyin.com/uJS3Tm5L5iI/")]
    public async Task Douyin_work_link_enqueues_permalink_without_search(string input, string expectedUrl)
    {
        var douyin = new Account
        { SiteId = "douyin", CookiePath = "d", Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow };
        _db.Accounts.Add(douyin);
        await _db.SaveChangesAsync();
        var sites = new SiteRegistry([new TwitterSiteProvider(), new DouyinSiteProvider()]);
        var queue = CreateQueue(sites);
        var site = new FakeCurrentSite();
        await site.SelectAsync("douyin");
        var vm = TrackVm(new DownloadsViewModel(queue, new AccountQueryService(_siteFactory),
            new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), sites, new SiteDbContextFactory(_paths)),
            new SyncDispatcher(), site, sites));
        var gate = CreateGate();
        var planReady = new TaskCompletionSource<DownloadPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnDownload = async (plan, _, progress, ct) =>
        {
            planReady.SetResult(plan);
            await gate.Task.WaitAsync(ct);
            progress.Report(new EngineEvent("job-done", Total: 0, Skipped: 0, Failed: 0));
        };

        Assert.True(vm.SupportsPermalink);
        Assert.False(vm.SupportsSearch);
        Assert.Contains("分享文本", vm.PermalinkInputHint);
        vm.PermalinkInput = input;
        await vm.DownloadPermalinkCommand.ExecuteAsync(null);

        try
        {
            var plan = await planReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // 引擎就绪后查询，确保后台任务已进入下载阶段。
            var job = Assert.Single(_db.Jobs.Where(j => j.SiteId == "douyin"));
            Assert.Equal(TargetKind.Permalink, job.TargetKind);
            Assert.Null(job.UserId);
            Assert.Null(vm.PermalinkInput);
            Assert.Equal(expectedUrl, plan.Urls.Single());
        }
        finally
        {
            gate.TrySetResult();
        }
        await WaitUntil(() => queue.Active.Count == 0);
    }

    [Fact]
    public async Task Douyin_user_link_is_rejected_without_enqueuing()
    {
        var sites = new SiteRegistry([new TwitterSiteProvider(), new DouyinSiteProvider()]);
        var site = new FakeCurrentSite();
        await site.SelectAsync("douyin");
        var vm = TrackVm(new DownloadsViewModel(_queue, new AccountQueryService(_siteFactory),
            _settings, new SyncDispatcher(), site, sites));

        vm.PermalinkInput = "https://www.douyin.com/user/MS4wLjABtest";
        await vm.DownloadPermalinkCommand.ExecuteAsync(null);

        Assert.Contains("作品链接", vm.StatusMessage);
        Assert.Empty(_db.Jobs.Where(j => j.SiteId == "douyin"));
    }

    // 超时同时报告后台任务状态和异常，避免并发故障只留下等待超时。
    private async Task WaitUntil(Func<bool> cond, int timeoutMs = 15000)
    {
        var start = Environment.TickCount;
        while (!cond())
        {
            if (Environment.TickCount - start > timeoutMs)
                throw new TimeoutException($"条件等待超时；活动任务：{string.Join(", ", _queues.SelectMany(q => q.Active))}；引擎调用：{_engine.Downloads.Count}；日志：{string.Join(Environment.NewLine, _queueLog.Messages)}");
            await Task.Delay(20);
        }
    }

    private sealed class QueueLogger : ILogger<DownloadQueueService>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue($"{formatter(state, exception)} {exception}");
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
        var queue = CreateQueue(sites);
        var vm = TrackVm(new DownloadsViewModel(queue, new AccountQueryService(_siteFactory),
            _settings, new SyncDispatcher(), new FakeCurrentSite(), sites));
        vm.Start();
        await WaitUntil(() => vm.HasAccount);

        var gate = CreateGate();
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

    // ---- Final review Important 2：切平台必须复位全局下载视图（否则单平台头下显示全平台任务） ----

    [Fact]
    public async Task Site_change_resets_global_view()
    {
        var site = new FakeCurrentSite();
        var vm = CreateVm(site);
        vm.Start();
        vm.SetGlobalView(true);
        Assert.True(vm.IsGlobalView);

        await site.SelectAsync("pixiv"); // 平台切换（ICurrentSite.Changed）
        await Task.Delay(50);            // 异步刷新落地

        Assert.False(vm.IsGlobalView);   // 复位：平台页组内只见本站
        vm.Stop();
    }
}
