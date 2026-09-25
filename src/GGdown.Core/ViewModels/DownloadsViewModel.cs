using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GGdown.Data;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Sites;
using GGdown.Threading;

namespace GGdown.ViewModels;

/// <summary>
/// 下载任务卡片行 VM（B5）。宿主（DownloadsViewModel）负责把队列事件经 IUiDispatcher 投递到 UI
/// 线程后调用 Update(JobSnapshot)——同线程约定由宿主保证（snapshot 是 record，换新实例安全）。
/// 取消经构造注入的回调委托回到宿主（同 UserRowViewModel 的回调注入模式），卡片不持有队列。
/// </summary>
public sealed partial class JobCardViewModel : ObservableObject
{
    private readonly Func<long, Task> _cancel;

    public JobCardViewModel(JobSnapshot snapshot, Func<long, Task> cancel)
    {
        JobId = snapshot.JobId;
        _cancel = cancel;
        CancelCommand = new RelayCommand(() => _ = _cancel(JobId));
        Update(snapshot);
    }

    public long JobId { get; }

    // —— 以下全部字段由 Update(JobSnapshot) 刷新（Produces："全字段刷新"）——

    private string _title = "";
    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    private string _kindBadge = "";
    public string KindBadge { get => _kindBadge; private set => SetProperty(ref _kindBadge, value); }

    private double _progressPercent;
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }

    private bool _isIndeterminate;
    public bool IsIndeterminate { get => _isIndeterminate; private set => SetProperty(ref _isIndeterminate, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    private string? _currentFile;
    public string? CurrentFile { get => _currentFile; private set => SetProperty(ref _currentFile, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private bool _isFinished;
    public bool IsFinished { get => _isFinished; private set => SetProperty(ref _isFinished, value); }

    public IRelayCommand CancelCommand { get; }

    public void Update(JobSnapshot snapshot)
    {
        Title = snapshot.Title;
        KindBadge = !string.IsNullOrEmpty(snapshot.KindLabel)
            ? snapshot.KindLabel
            : snapshot.Kind switch
            {
                TargetKind.AccountLikes => "账号喜欢",
                TargetKind.AccountBookmarks => "账号书签",
                TargetKind.UserNovels => "小说",
                TargetKind.AccountNovelBookmarks => "收藏小说",
                _ => "用户媒体",
            };
        // 控制器裁定 4：ProgressPercent = Total>0 ? (Done+Skipped+Failed)*100.0/Total : 0
        ProgressPercent = snapshot.Total > 0
            ? (snapshot.Done + snapshot.Skipped + snapshot.Failed) * 100.0 / snapshot.Total
            : 0;
        IsIndeterminate = snapshot.Total == 0 && snapshot.Status == JobStatus.Running;
        // 队列仅在终态回填 Total（job-done 的 TotalFiles=Done+Skipped+Failed）；运行中 Total==0 时
        // 分母回退为已知计数，保持 "N/M · 跳过 X" 文案运行中可读（终态两种取值相同，见适配说明）
        var total = snapshot.Total > 0 ? snapshot.Total : snapshot.Done + snapshot.Skipped + snapshot.Failed;
        ProgressText = $"已完成 {snapshot.Done}/{total} · 跳过 {snapshot.Skipped}";
        CurrentFile = snapshot.CurrentFile;
        StatusText = snapshot.Status switch // 控制器裁定 3：状态映射
        {
            JobStatus.Pending => "排队中",
            JobStatus.Running => "下载中",
            JobStatus.Completed => "已完成",
            JobStatus.Canceled => "已取消",
            JobStatus.Failed => "失败",
            _ => snapshot.Status.ToString(),
        };
        IsFinished = snapshot.Status is JobStatus.Completed or JobStatus.Canceled or JobStatus.Failed; // 终态（取消按钮随之隐藏，页面 UiConv 转换）
        var file = snapshot.CurrentFile;
        var ext = file is null ? "" : Path.GetExtension(file).ToLowerInvariant();
        HasPreview = file is not null
            && File.Exists(file)
            && ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";
        PreviewPath = HasPreview ? file : null;
    }

    private string? _previewPath;
    public string? PreviewPath { get => _previewPath; private set => SetProperty(ref _previewPath, value); }

    private bool _hasPreview;
    public bool HasPreview { get => _hasPreview; private set => SetProperty(ref _hasPreview, value); }
}

/// <summary>下载页账号内容按钮（喜欢/书签/收藏）。</summary>
public sealed class AccountContentActionViewModel
{
    public AccountContentActionViewModel(string label, IAsyncRelayCommand command)
    {
        Label = label;
        Command = command;
    }

    public string Label { get; }
    public IAsyncRelayCommand Command { get; }
}

/// <summary>
/// 下载页 VM（B5）：活动任务卡片列表 + 账号内容（喜欢/书签）入口。
/// 事件处理器先经 _dispatcher.Post 再操作集合与 Update（控制器裁定 2）。
/// </summary>
public partial class DownloadsViewModel : ObservableObject
{
    private static readonly ContentKind[] AccountKinds =
        [ContentKind.AccountLikes, ContentKind.AccountBookmarks, ContentKind.AccountNovelBookmarks];

    private readonly ICurrentSite _currentSite;
    private readonly IDownloadQueueService _queue;
    private readonly IAccountQueryService _accountQuery;
    private readonly IAppSettings _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly SiteRegistry _sites;
    private bool _started; // NavigationCacheMode=Enabled 时 OnNavigatedTo 每次导航触发，Start/Stop 须对称

    private string SiteId => _currentSite.SiteId;

    /// <summary>全局下载视图（栏底入口进入）：true 显示全部平台任务，false 只显示当前平台（Task 10）。</summary>
    public bool IsGlobalView { get; private set; }

    /// <summary>当前生效的站点筛选：null=全部平台（全局视图）。</summary>
    private string? SiteFilter => IsGlobalView ? null : SiteId;

    private bool MatchesFilter(JobSnapshot s) =>
        SiteFilter is null || string.Equals(s.SiteId, SiteFilter, StringComparison.OrdinalIgnoreCase);

    /// <summary>页面 OnNavigatedTo 同步全局视图开关（栏底「全部下载」入口置位，平台页组导航复位）。</summary>
    public void SetGlobalView(bool value)
    {
        if (IsGlobalView == value) return;
        IsGlobalView = value;
        _dispatcher.Post(RebuildVisibleJobs);
    }

    public DownloadsViewModel(IDownloadQueueService queue, IAccountQueryService accountQuery,
        IAppSettings settings, IUiDispatcher dispatcher, ICurrentSite currentSite, SiteRegistry sites)
    {
        _queue = queue;
        _accountQuery = accountQuery;
        _settings = settings;
        _dispatcher = dispatcher;
        _currentSite = currentSite;
        _sites = sites;
        _currentSite.Changed += () => _dispatcher.Post(() =>
        {
            IsGlobalView = false; // 平台切换复位全局下载视图（单平台头下不得显示全平台任务）
            NotifySite();
        });

        DownloadLikesCommand = new AsyncRelayCommand(() => DownloadAccountContentAsync(ContentKind.AccountLikes));
        DownloadBookmarksCommand = new AsyncRelayCommand(() => DownloadAccountContentAsync(ContentKind.AccountBookmarks));
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        RebuildAccountActions();
    }

    public ObservableCollection<JobCardViewModel> Jobs { get; } = [];

    private bool _hasActive;
    public bool HasActive { get => _hasActive; private set => SetProperty(ref _hasActive, value); }

    private bool _hasAccount;
    public bool HasAccount { get => _hasAccount; private set => SetProperty(ref _hasAccount, value); }

    [ObservableProperty]
    private string? _statusMessage;   // 操作结果反馈（成功/失败一行话）

    [ObservableProperty]
    private string? _searchQuery;

    public event Action? JobsChanged; // 页面无需订阅；供测试（任何 Jobs 变更后触发）

    public IAsyncRelayCommand DownloadLikesCommand { get; }
    public IAsyncRelayCommand DownloadBookmarksCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public ObservableCollection<AccountContentActionViewModel> AccountActions { get; } = [];

    public bool IsSiteAvailable => _currentSite.IsAvailable;
    public bool SupportsAccountContent => _currentSite.IsAvailable && AccountActions.Count > 0;
    public bool SupportsSearch => _currentSite.IsAvailable && _sites.IsRegistered(SiteId)
        && _sites.Get(SiteId).SupportedKinds.Contains(ContentKind.Search);
    public bool ShowComingSoon => !_currentSite.IsAvailable;
    public string ComingSoonMessage => $"{_currentSite.Current.DisplayName} 即将支持，该站点尚未开放下载。";

    private void NotifySite()
    {
        OnPropertyChanged(nameof(IsSiteAvailable));
        OnPropertyChanged(nameof(SupportsAccountContent));
        OnPropertyChanged(nameof(SupportsSearch));
        OnPropertyChanged(nameof(ShowComingSoon));
        OnPropertyChanged(nameof(ComingSoonMessage));
        RebuildAccountActions();
        RebuildVisibleJobs();
        _ = RefreshHasAccountAsync();
    }

    private void RebuildAccountActions()
    {
        AccountActions.Clear();
        if (!_currentSite.IsAvailable || !_sites.IsRegistered(SiteId)) return;
        var provider = _sites.Get(SiteId);
        foreach (var kind in AccountKinds)
        {
            if (!provider.SupportedKinds.Contains(kind)) continue;
            var captured = kind;
            AccountActions.Add(new AccountContentActionViewModel(
                provider.KindLabel(captured),
                new AsyncRelayCommand(() => DownloadAccountContentAsync(captured))));
        }
        OnPropertyChanged(nameof(SupportsAccountContent));
    }

    /// <summary>
    /// 订阅 queue.JobChanged/JobRemoved + 播种 Active 现值 + 剪除 Stop 窗口内错失终态的幽灵卡片
    /// （审查 Important-1）+ 刷新 HasAccount。先订阅后播种（防漏事件）。
    /// 页面 OnNavigatedTo 调用；幂等（重复调用无副作用）。
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _queue.JobChanged += OnJobChanged;
        _queue.JobRemoved += OnJobRemoved;
        // 审查 Important-1：NavigationCacheMode=Enabled + singleton 下，离页 Stop 后任务在后台跑到终态
        // 时 JobChanged/JobRemoved 均丢失，残留卡片会永久停在"下载中"且 HasActive 卡 true（取消对已结束
        // 任务也是空操作）——播种时按 Active 的 JobId 集合剪除已不存在者（同批经 dispatcher）并重算 HasActive
        _dispatcher.Post(RebuildVisibleJobs);
        _ = RefreshHasAccountAsync(); // fire-and-forget：内部自捕获异常（B4 模式，不崩线程）
    }

    /// <summary>退订队列事件（页面 OnNavigatedFrom 调用，与 Start 对称）。</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _queue.JobChanged -= OnJobChanged;
        _queue.JobRemoved -= OnJobRemoved;
    }

    // 控制器裁定 2：事件处理器内先 _dispatcher.Post 再操作集合与 Update（snapshot 是 record，换新实例安全）
    private void OnJobChanged(JobSnapshot snapshot)
        => _dispatcher.Post(() => AddOrUpdateCard(snapshot));

    private void OnJobRemoved(JobSnapshot snapshot)
        => _dispatcher.Post(() =>
        {
            if (Jobs.FirstOrDefault(c => c.JobId == snapshot.JobId) is { } card)
                Jobs.Remove(card);
            UpdateHasActive();
            JobsChanged?.Invoke();
        });

    private void RebuildVisibleJobs()
    {
        var mine = _queue.Active.Where(MatchesFilter).ToList();
        var ids = mine.Select(s => s.JobId).ToHashSet();
        for (var i = Jobs.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Jobs[i].JobId))
                Jobs.RemoveAt(i);
        }
        foreach (var snapshot in mine)
            UpsertCard(snapshot);
        UpdateHasActive();
        JobsChanged?.Invoke();
    }

    private void AddOrUpdateCard(JobSnapshot snapshot)
    {
        if (!MatchesFilter(snapshot))
        {
            if (Jobs.FirstOrDefault(c => c.JobId == snapshot.JobId) is { } stray)
            {
                Jobs.Remove(stray);
                UpdateHasActive();
                JobsChanged?.Invoke();
            }
            return;
        }
        UpsertCard(snapshot);
        UpdateHasActive();
        JobsChanged?.Invoke();
    }

    private void UpsertCard(JobSnapshot snapshot)
    {
        if (Jobs.FirstOrDefault(c => c.JobId == snapshot.JobId) is { } card)
            card.Update(snapshot);
        else
            Jobs.Add(new JobCardViewModel(snapshot, CancelJobAsync));
    }

    private void UpdateHasActive() => HasActive = Jobs.Count > 0;

    // 卡片取消按钮回调（卡片 RelayCommand 内 fire-and-forget），异常落 StatusMessage 不崩线程
    private async Task CancelJobAsync(long jobId)
    {
        try
        {
            await _queue.CancelAsync(jobId);
        }
        catch (Exception ex)
        {
            StatusMessage = $"取消失败：{ex.Message}";
        }
    }

    private async Task SearchAsync()
    {
        if (!SupportsSearch)
        {
            StatusMessage = ComingSoonMessage;
            return;
        }
        var q = SearchQuery?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            StatusMessage = "请输入搜索内容或链接";
            return;
        }
        Account? account;
        try { account = await _accountQuery.GetActiveAsync(SiteId); }
        catch (Exception ex)
        {
            StatusMessage = $"账号状态获取失败：{ex.Message}";
            return;
        }
        if (account is null)
        {
            StatusMessage = "请先在设置中导入 Cookie";
            return;
        }
        try
        {
            var provider = _sites.Get(SiteId);
            var parsed = provider.ParseInput(q);
            string url;
            string title;
            var kind = ContentKind.Search;
            if (parsed.Ok && parsed.DirectUrl is not null && parsed.Kind is not PasteKind.User)
            {
                url = parsed.DirectUrl;
                kind = parsed.Kind == PasteKind.Search ? ContentKind.Search : ContentKind.Permalink;
                title = parsed.Kind switch
                {
                    PasteKind.Tweet => $"推文 {parsed.RestId}",
                    PasteKind.List => $"列表 {parsed.RestId}",
                    _ => "搜索",
                };
            }
            else
            {
                url = "https://x.com/search?q=" + Uri.EscapeDataString(q);
                title = q.Length > 24 ? "搜索 " + q[..24] + "…" : "搜索 " + q;
            }
            var dir = await _settings.GetDownloadDirectoryAsync();
            var siteOptions = await _settings.GetSiteOptionsAsync(SiteId);
            await _queue.EnqueuePermalinkAsync(account, url, title, kind, dir, siteOptions);
            StatusMessage = $"已加入下载队列：{title}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    // 控制器裁定 6：解析账号 → IAppSettings 目录与 siteOptions → EnqueueAccountContentAsync
    private async Task DownloadAccountContentAsync(ContentKind kind)
    {
        if (!_currentSite.IsAvailable)
        {
            StatusMessage = ComingSoonMessage;
            return;
        }
        Account? account;
        try
        {
            // 审查 Minor-1：与 RefreshHasAccountAsync 一致，坏库等异常落 StatusMessage 不崩线程
            account = await _accountQuery.GetActiveAsync(SiteId);
        }
        catch (Exception ex)
        {
            StatusMessage = $"账号状态获取失败：{ex.Message}";
            return;
        }
        if (account is null)
        {
            StatusMessage = "请先在设置中导入 Cookie";
            return;
        }
        try
        {
            var dir = await _settings.GetDownloadDirectoryAsync();
            var siteOptions = await _settings.GetSiteOptionsAsync(SiteId);
            await _queue.EnqueueAccountContentAsync(account, kind, dir, siteOptions);
            StatusMessage = "已加入下载队列";
        }
        catch (Exception ex) // EngineException 等
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task RefreshHasAccountAsync()
    {
        Account? account = null;
        Exception? error = null;
        try
        {
            account = await _accountQuery.GetActiveAsync(SiteId);
        }
        catch (Exception ex) // 坏库等异常不崩线程（B4 模式）
        {
            error = ex;
        }
        _dispatcher.Post(() =>
        {
            HasAccount = error is null && account is not null;
            // 审查 Minor-2：错误提示不覆写已有的操作反馈（StatusMessage 非空时跳过）
            if (error is not null && StatusMessage is null)
                StatusMessage = $"账号状态获取失败：{error.Message}";
        });
    }
}
