using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalleryGUI.Data;
using GalleryGUI.Services;
using GalleryGUI.Settings;
using GalleryGUI.Sites;
using GalleryGUI.Threading;

namespace GalleryGUI.ViewModels;

/// <summary>
/// 用户管理页行 VM：由父 VM 构造时注入回调委托（brief Step 4 末段）。
/// 控制器裁定 5：AvatarSource 是 UI 类型（ImageSource），Core 不能引用——
/// 行 VM 只暴露 AvatarUrl（string?），页面模板用 Image + 内置 string→ImageSource 类型转换绑定。
/// </summary>
public sealed partial class UserRowViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isDownloading;

    public UserRowViewModel(
        User model,
        Func<UserRowViewModel, Task> downloadOne,
        Action<UserRowViewModel> togglePin,
        Action<UserRowViewModel> toggleSkip,
        Action<UserRowViewModel> openProfile,
        Func<UserRowViewModel, Task> openFolder,
        Func<UserRowViewModel, Task> deleteOne,
        Func<UserRowViewModel, Task>? downloadHighlights = null,
        bool supportsHighlights = false)
    {
        Model = model;
        SupportsHighlights = supportsHighlights;
        DownloadCommand = new AsyncRelayCommand(() => downloadOne(this));
        TogglePinCommand = new RelayCommand(() => togglePin(this));
        ToggleSkipCommand = new RelayCommand(() => toggleSkip(this));
        OpenProfileCommand = new AsyncRelayCommand(() => { openProfile(this); return Task.CompletedTask; });
        OpenFolderCommand = new AsyncRelayCommand(() => openFolder(this));
        DeleteCommand = new AsyncRelayCommand(() => deleteOne(this));
        DownloadHighlightsCommand = new AsyncRelayCommand(
            () => downloadHighlights?.Invoke(this) ?? Task.CompletedTask,
            () => supportsHighlights && CanDownload);
    }

    public User Model { get; }
    public bool IsSelected { get => _isSelected; set { if (SetProperty(ref _isSelected, value)) SelectedChanged?.Invoke(); } }
    public event Action? SelectedChanged;

    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (!SetProperty(ref _isDownloading, value)) return;
            OnPropertyChanged(nameof(CanDownload));
            OnPropertyChanged(nameof(DownloadButtonText));
            OnPropertyChanged(nameof(Subtitle));
            DownloadHighlightsCommand.NotifyCanExecuteChanged();
        }
    }

    public string Title => string.IsNullOrWhiteSpace(Model.DisplayName) ? Model.ScreenName : Model.DisplayName!;
    public string Subtitle
    {
        get
        {
            var extra = IsDownloading ? " · 下载中" : Model.IsSkipped ? " · 已暂停" : "";
            return $"@{Model.ScreenName} · {SourceText}{extra}";
        }
    }
    public string DownloadCountText => Model.DownloadCount.ToString();
    public string LastDownloadText => Model.LastDownloadAt is { } t
        ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "—";
    public bool IsPinned => Model.IsPinned;
    public bool IsSkipped => Model.IsSkipped;
    public bool CanDownload => !Model.IsSkipped && !IsDownloading;
    public string DownloadButtonText => IsDownloading ? "下载中" : "下载";
    public string PinButtonText => Model.IsPinned ? "取消置顶" : "置顶";
    public string SkipButtonText => Model.IsSkipped ? "恢复" : "暂停";
    public string? AvatarUrl => Model.AvatarUrl;
    public string? BannerUrl => Model.BannerUrl;
    public string? Bio => Model.Bio;
    public bool SupportsHighlights { get; }
    public bool ShowHighlights => SupportsHighlights;
    public long NewMediaCount
    {
        get
        {
            if (Model.MediaCount is not long current) return 0;
            var baseline = Model.MediaCountAtDownload ?? current;
            return Math.Max(0, current - baseline);
        }
    }
    public bool HasNewMedia => NewMediaCount > 0;
    public string NewMediaText => HasNewMedia ? $"新 +{NewMediaCount}" : "";
    public string FollowersText => Model.FollowersCount is long n
        ? n >= 10_000 ? $"{n / 10_000.0:F1}万 关注者" : $"{n} 关注者"
        : "";

    public IAsyncRelayCommand DownloadCommand { get; }
    public IAsyncRelayCommand DownloadHighlightsCommand { get; }
    public IRelayCommand OpenProfileCommand { get; }
    public IAsyncRelayCommand OpenFolderCommand { get; }
    public IRelayCommand TogglePinCommand { get; }
    public IRelayCommand ToggleSkipCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }

    private string SourceText => Model.Source switch
    {
        UserSource.Following => "关注导入",
        UserSource.Manual => "手动添加",
        UserSource.Link => "链接导入",
        _ => "手动添加",
    };
}

/// <summary>
/// 用户管理页 VM（主页）。列表/多选/搜索防抖/底部操作条数据源。
/// 控制器裁定 5 适配：Visibility 是 Microsoft.UI.Xaml 类型，Core(net8.0) 无法引用，
/// 故 ImportButtonVisibility/BottomBarVisibility 以 bool 语义暴露（ImportButtonVisible/BottomBarVisible），
/// 由页面层用静态转换函数映射为 Visibility；SelectedCountText 为纯字符串，直接绑定。
/// </summary>
public partial class UsersViewModel : ObservableObject
{
    private readonly ICurrentSite _currentSite;
    private readonly IUserQueryService _userQuery;
    private readonly IAccountQueryService _accountQuery;
    private readonly IHistoryQueryService _history;
    private readonly IUserService _users;
    private readonly IDownloadQueueService _queue;
    private readonly IAppSettings _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly SiteRegistry _sites;

    private string SiteId => _currentSite.SiteId;

    private CancellationTokenSource? _refreshDebounce; // SearchText/SortBy 共用的防抖等待
    private int _refreshId; // 并发刷新代数：过期刷新的集合变更丢弃
    private bool _hasUsers;
    private bool _hasAccount;

    public UsersViewModel(IUserQueryService userQuery, IAccountQueryService accountQuery,
        IUserService users, IDownloadQueueService queue, IAppSettings settings,
        IUiDispatcher dispatcher, SiteRegistry sites, ICurrentSite currentSite,
        IHistoryQueryService history)
    {
        _userQuery = userQuery;
        _accountQuery = accountQuery;
        _history = history;
        _users = users;
        _queue = queue;
        _settings = settings;
        _dispatcher = dispatcher;
        _sites = sites;
        _currentSite = currentSite;
        _currentSite.Changed += () => _ = RefreshAsync();

        Users = [];
        // AllowConcurrentExecutions：防抖触发的刷新不被默认的并发闸门静默丢弃，
        // 过期结果由 _refreshId 代数守卫在集合变更处丢弃
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
        DownloadSelectedCommand = new AsyncRelayCommand(DownloadSelectedAsync);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync);
        SkipSelectedCommand = new AsyncRelayCommand(() => SetSelectedSkippedAsync(true));
        UnskipSelectedCommand = new AsyncRelayCommand(() => SetSelectedSkippedAsync(false));
        TogglePinCommand = new RelayCommand<UserRowViewModel>(u => _ = TogglePinCoreAsync(u));
        DownloadOneCommand = new AsyncRelayCommand<UserRowViewModel>(DownloadOneAsync);
        OpenProfileCommand = new RelayCommand<UserRowViewModel>(OpenProfileCore);
        OpenFolderCommand = new AsyncRelayCommand<UserRowViewModel>(OpenFolderCoreAsync);
        ShowAddUserCommand = new RelayCommand(() => ShowAddUserRequested?.Invoke());
        ShowImportCookieCommand = new RelayCommand(() => ShowImportCookieRequested?.Invoke());
        ShowFollowingListCommand = new RelayCommand(() => ShowFollowingListRequested?.Invoke(), () => _hasAccount && _currentSite.IsAvailable);
        SelectAllCommand = new RelayCommand(() => SelectAll(true));
        DeselectAllCommand = new RelayCommand(() => SelectAll(false));
        InvertSelectionCommand = new RelayCommand(InvertSelection);
        RefreshProfilesCommand = new AsyncRelayCommand(RefreshProfilesAsync);

        _queue.AccountInvalid += OnAccountInvalid;
        _queue.JobChanged += OnJobSnapshot;
        _queue.JobRemoved += OnJobSnapshot;
    }

    public ObservableCollection<UserRowViewModel> Users { get; }

    public int SelectedCount => Users.Count(r => r.IsSelected);
    public bool HasUsers => _hasUsers;
    public bool HasAccount => _hasAccount;               // 驱动空状态引导与导入按钮
    public bool CanShowFollowingList => _hasAccount && _currentSite.IsAvailable;
    public bool BottomBarVisible => SelectedCount > 0;
    public bool IsSiteAvailable => _currentSite.IsAvailable;
    public bool ShowComingSoon => !_currentSite.IsAvailable;
    public bool ShowEmptyGuide => _currentSite.IsAvailable && !_hasUsers;
    public bool ShowUserList => _currentSite.IsAvailable;
    public string SiteDisplayName => _currentSite.Current.DisplayName;
    public string ComingSoonMessage => $"{_currentSite.Current.DisplayName} 即将支持，该站点尚未开放下载。";
    public string SelectedCountText => $"已选 {SelectedCount} 个";

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private string _sortBy = "last_download"; // F4 排序维度：last_download / download_count / added_at

    [ObservableProperty]
    private string? _statusMessage;   // 操作结果反馈（成功/失败一行话）

    public event Action? RequestReload;                     // B4 导入流程完成后通知刷新
    public event Action? ShowAddUserRequested;              // B4 接线：添加用户对话框
    public event Action? ShowImportCookieRequested;         // B4 接线：导入 Cookie 对话框
    public event Action? ShowFollowingListRequested;        // 关注列表勾选添加

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand DownloadSelectedCommand { get; }
    public IAsyncRelayCommand DeleteSelectedCommand { get; }
    public IAsyncRelayCommand SkipSelectedCommand { get; }
    public IAsyncRelayCommand UnskipSelectedCommand { get; }
    public IRelayCommand<UserRowViewModel> TogglePinCommand { get; }
    public IRelayCommand<UserRowViewModel> DownloadOneCommand { get; }
    public IRelayCommand<UserRowViewModel> OpenProfileCommand { get; }
    public IAsyncRelayCommand<UserRowViewModel> OpenFolderCommand { get; }
    public IRelayCommand ShowAddUserCommand { get; }
    public IRelayCommand ShowImportCookieCommand { get; }
    public IRelayCommand ShowFollowingListCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand DeselectAllCommand { get; }
    public IRelayCommand InvertSelectionCommand { get; }
    public IAsyncRelayCommand RefreshProfilesCommand { get; }

    // brief Step 4 XAML 绑定 ShowAddUserCommand/ShowImportCookieCommand（Produces 契约之外的占位命令，
    // "直接调 VM 内部占位命令——B4 接线"），命令转发为事件，页面订阅后转对话框。

    /// <summary>300ms 防抖（SearchText/SortBy 共用）：Task.Delay 期间新变更经 CTS 竞争取消旧等待，只有最后一次生效。</summary>
    partial void OnSearchTextChanged(string? value) => StartDebouncedRefresh();

    partial void OnSortByChanged(string value) => StartDebouncedRefresh();

    private void StartDebouncedRefresh()
    {
        _refreshDebounce?.Cancel();
        _refreshDebounce?.Dispose();
        var cts = _refreshDebounce = new CancellationTokenSource();
        _ = DebouncedRefreshAsync(cts.Token);
    }

    private async Task DebouncedRefreshAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // 新变更取消了旧等待，静默退出
        }
    }

    public void SelectAll(bool selected)
        => _dispatcher.Post(() =>
        {
            foreach (var row in Users)
                row.IsSelected = selected;
        });

    public void InvertSelection()
        => _dispatcher.Post(() =>
        {
            foreach (var row in Users)
                row.IsSelected = !row.IsSelected;
        });

    private async Task RefreshAsync()
    {
        try
        {
            var gen = ++_refreshId;
            var filter = new UserFilter(SearchText, SortBy);
            var list = await _userQuery.ListAsync(SiteId, filter);
            var account = await _accountQuery.GetActiveAsync(SiteId);
            _dispatcher.Post(() =>
            {
                if (gen != _refreshId) return; // 过期刷新：已被更新的查询取代
                var selectedIds = Users.Where(r => r.IsSelected).Select(r => r.Model.Id).ToHashSet();
                Users.Clear();
                foreach (var user in list)
                {
                    var row = CreateRow(user);
                    if (selectedIds.Contains(user.Id)) row.IsSelected = true; // 保留同 Id 行的选中态
                    Users.Add(row);
                }
                _hasUsers = list.Count > 0;
                _hasAccount = account is not null;
                OnPropertyChanged(nameof(HasUsers));
                OnPropertyChanged(nameof(HasAccount));
                OnPropertyChanged(nameof(CanShowFollowingList));
                OnPropertyChanged(nameof(IsSiteAvailable));
                OnPropertyChanged(nameof(ShowComingSoon));
                OnPropertyChanged(nameof(ShowEmptyGuide));
                OnPropertyChanged(nameof(ShowUserList));
                OnPropertyChanged(nameof(SiteDisplayName));
                OnPropertyChanged(nameof(ComingSoonMessage));
                ShowFollowingListCommand.NotifyCanExecuteChanged();
                NotifySelection();
                ApplyDownloadingState();
            });
        }
        // 覆盖防抖/页面 OnNavigatedTo/OnAccountInvalid/命令四个入口：坏库等异常落 StatusMessage 不崩线程
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新失败：{ex.Message}";
        }
    }

    private UserRowViewModel CreateRow(User user)
    {
        var supportsHighlights = _sites.IsRegistered(SiteId)
            && _sites.Get(SiteId).SupportedKinds.Contains(ContentKind.UserHighlights);
        var row = new UserRowViewModel(user,
            downloadOne: DownloadOneAsync,
            togglePin: u => _ = TogglePinCoreAsync(u),
            toggleSkip: u => _ = ToggleSkipCoreAsync(u),
            openProfile: OpenProfileCore,
            openFolder: OpenFolderCoreAsync,
            deleteOne: DeleteOneAsync,
            downloadHighlights: DownloadHighlightsAsync,
            supportsHighlights: supportsHighlights);
        row.SelectedChanged += OnRowSelectionChanged;
        return row;
    }

    private void OnRowSelectionChanged()
        => _dispatcher.Post(NotifySelection);

    /// <summary>选中计数等派生属性的变更通知经 dispatcher（brief Step 3 要点）。</summary>
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(BottomBarVisible));
        OnPropertyChanged(nameof(SelectedCountText));
    }

    private async Task DownloadSelectedAsync()
    {
        var users = Users.Where(r => r.IsSelected).Select(r => r.Model).ToList();
        if (users.Count == 0) return;
        if (await DownloadUsersAsync(users))
            SelectAll(false);
    }

    private Task DownloadOneAsync(UserRowViewModel row) => DownloadUsersAsync([row.Model]);

    private Task DownloadHighlightsAsync(UserRowViewModel row) => DownloadUsersAsync([row.Model], ContentKind.UserHighlights);

    /// <returns>true：入队完成或全部被跳过（可清选中）；false：硬失败，保留勾选以便重试。</returns>
    private async Task<bool> DownloadUsersAsync(IReadOnlyList<User> users, ContentKind? onlyKind = null)
    {
        if (!_currentSite.IsAvailable)
        {
            StatusMessage = ComingSoonMessage;
            return false;
        }
        var downloadingIds = DownloadingUserIds();
        var skippedPaused = users.Count(u => u.IsSkipped);
        var skippedDownloading = users.Count(u => !u.IsSkipped && downloadingIds.Contains(u.Id));
        var toDownload = users.Where(u => !u.IsSkipped && !downloadingIds.Contains(u.Id)).ToList();
        if (toDownload.Count == 0)
        {
            StatusMessage = users.Count == 1
                ? downloadingIds.Contains(users[0].Id) && !users[0].IsSkipped
                    ? "该用户已在下载队列"
                    : "该用户已暂停下载"
                : skippedDownloading > 0 && skippedPaused == 0
                    ? "选中用户均在下载队列中"
                    : skippedDownloading > 0
                        ? "选中用户均已暂停或已在下载队列"
                        : "选中用户均已暂停下载";
            return true;
        }

        Account? account;
        try
        {
            // 与 DownloadsViewModel.DownloadAccountContentAsync 对齐：坏库等异常落 StatusMessage 不崩线程
            account = await _accountQuery.GetActiveAsync(SiteId);
        }
        catch (Exception ex)
        {
            StatusMessage = $"账号状态获取失败：{ex.Message}";
            return false;
        }
        if (account is null)
        {
            StatusMessage = "请先在设置中导入 Cookie";
            return false;
        }
        try
        {
            var dir = await _settings.GetDownloadDirectoryAsync();
            var siteOptions = await _settings.GetSiteOptionsAsync(SiteId);
            var provider = _sites.IsRegistered(SiteId) ? _sites.Get(SiteId) : null;
            if (onlyKind is ContentKind.UserHighlights)
            {
                await _queue.EnqueueUserContentAsync(account, toDownload, ContentKind.UserHighlights, dir, siteOptions);
                StatusMessage = $"已加入高光队列（{toDownload.Count} 个用户）";
                return true;
            }
            var enqueueArtworks = provider is null
                || !provider.SupportedKinds.Contains(ContentKind.UserNovels)
                || OptionBool(siteOptions, "download_artworks", true);
            var enqueueNovels = provider is not null
                && provider.SupportedKinds.Contains(ContentKind.UserNovels)
                && OptionBool(siteOptions, "download_novels", true);
            if (!enqueueArtworks && !enqueueNovels)
            {
                StatusMessage = "请在设置中至少启用一种作品类型";
                return false;
            }
            if (enqueueArtworks)
                await _queue.EnqueueUserMediaAsync(account, toDownload, dir, siteOptions);
            if (enqueueNovels)
                await _queue.EnqueueUserContentAsync(account, toDownload, ContentKind.UserNovels, dir, siteOptions);
            var extras = new List<string>();
            if (skippedPaused > 0) extras.Add($"暂停 {skippedPaused} 个");
            if (skippedDownloading > 0) extras.Add($"跳过 {skippedDownloading} 个下载中");
            StatusMessage = extras.Count == 0
                ? $"已加入下载队列（{toDownload.Count} 个用户）"
                : $"已加入下载队列（{toDownload.Count} 个用户，{string.Join("，", extras)}）";
            return true;
        }
        catch (Exception ex) // EngineException 等
        {
            StatusMessage = ex.Message;
            return false;
        }
    }

    // 控制器裁定 3（brief 定稿授权的简化替代）：V1 删除不做二次确认，直接删 + StatusMessage。
    // （删除的是库记录而非文件，风险低；页面层 ConfirmDeleteRequested 弹窗方案留待后续）
    private async Task DeleteSelectedAsync()
    {
        var ids = Users.Where(r => r.IsSelected).Select(r => r.Model.Id).ToList();
        if (ids.Count == 0) return;
        try
        {
            await _users.RemoveAsync(ids);
            StatusMessage = $"已删除 {ids.Count} 个用户";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    private async Task DeleteOneAsync(UserRowViewModel row)
    {
        try
        {
            await _users.RemoveAsync([row.Model.Id]);
            StatusMessage = "已删除 1 个用户";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    private async Task TogglePinCoreAsync(UserRowViewModel row)
    {
        try
        {
            await _users.SetPinnedAsync(row.Model.Id, !row.Model.IsPinned);
            await RefreshAsync(); // 置顶排序键变更，重建列表
        }
        catch (Exception ex) // 控制器裁定 5（B3 审查授权）：fire-and-forget 路径异常不丢出崩 UI 线程
        {
            StatusMessage = $"置顶失败：{ex.Message}";
        }
    }

    private async Task ToggleSkipCoreAsync(UserRowViewModel row)
    {
        try
        {
            await _users.SetSkippedAsync([row.Model.Id], !row.Model.IsSkipped);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"暂停失败：{ex.Message}";
        }
    }

    private async Task SetSelectedSkippedAsync(bool skipped)
    {
        var ids = Users.Where(r => r.IsSelected).Select(r => r.Model.Id).ToList();
        if (ids.Count == 0) return;
        try
        {
            await _users.SetSkippedAsync(ids, skipped);
            StatusMessage = skipped ? $"已暂停 {ids.Count} 个用户" : $"已恢复 {ids.Count} 个用户";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"暂停失败：{ex.Message}";
        }
    }

    // 控制器裁定 4：Process.Start(UseShellExecute=true) 放 Core（net8.0 可用）；ProfileUrl 空则用 BuildProfileUrl
    private void OpenProfileCore(UserRowViewModel row)
    {
        var url = row.Model.ProfileUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            if (!_sites.IsRegistered(SiteId)) return;
            url = _sites.Get(SiteId).BuildProfileUrl(row.Model.ScreenName, row.Model.RestId);
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task OpenFolderCoreAsync(UserRowViewModel row)
    {
        try
        {
            var files = await _history.ListAsync(new HistoryFilter(UserId: row.Model.Id));
            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file.FilePath)) continue;
                string path;
                try { path = Path.GetFullPath(file.FilePath); }
                catch (Exception) { continue; }

                if (File.Exists(path))
                {
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                    return;
                }
                var dir = Path.GetDirectoryName(path);
                if (dir is not null && Directory.Exists(dir))
                {
                    OpenExplorerFolder(dir);
                    return;
                }
            }

            var baseDir = await _settings.GetDownloadDirectoryAsync();
            var name = row.Model.ScreenName;
            foreach (var candidate in new[]
            {
                Path.Combine(baseDir, "twitter", name),
                Path.Combine(baseDir, "x", name),
                Path.Combine(baseDir, name),
            })
            {
                if (Directory.Exists(candidate))
                {
                    OpenExplorerFolder(candidate);
                    return;
                }
            }

            StatusMessage = files.Count > 0
                ? "目录不存在"
                : "还没有下载过这个用户的文件";
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开目录失败：{ex.Message}";
        }
    }

    private async Task RefreshProfilesAsync()
    {
        try
        {
            var account = await _accountQuery.GetActiveAsync(SiteId);
            if (account is null)
            {
                StatusMessage = "请先在设置中导入 Cookie";
                return;
            }
            var selected = Users.Where(r => r.IsSelected).Select(r => r.Model.Id).ToList();
            var n = await _users.RefreshProfilesAsync(account, selected.Count > 0 ? selected : null);
            StatusMessage = $"已刷新 {n} 个用户资料";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新资料失败：{ex.Message}";
        }
    }

    private static void OpenExplorerFolder(string path)
        => Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { path } });

    private void OnJobSnapshot(JobSnapshot snapshot)
        => _dispatcher.Post(ApplyDownloadingState);

    private HashSet<long> DownloadingUserIds()
        => _queue.Active
            .Where(j => j.UserId is not null && j.Status is JobStatus.Pending or JobStatus.Running)
            .Select(j => j.UserId!.Value)
            .ToHashSet();

    private void ApplyDownloadingState()
    {
        var downloading = DownloadingUserIds();
        foreach (var row in Users)
            row.IsDownloading = downloading.Contains(row.Model.Id);
    }

    private void OnAccountInvalid(long accountId, string reason)
        => _dispatcher.Post(async () =>
        {
            StatusMessage = $"登录态失效，请重新导入 Cookie：{reason}";
            try
            {
                await RefreshAsync(); // 刷新 HasAccount 等派生态
            }
            catch (Exception ex) // 控制器裁定 5（B3 审查授权）：fire-and-forget 路径异常不崩 UI 线程
            {
                StatusMessage = ex.Message;
            }
        });

    private static bool OptionBool(IReadOnlyDictionary<string, object?> options, string key, bool fallback)
        => options.TryGetValue(key, out var v) && v is bool b ? b : fallback;
}
