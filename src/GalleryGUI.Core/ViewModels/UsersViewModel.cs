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

    public UserRowViewModel(
        User model,
        Func<UserRowViewModel, Task> downloadOne,
        Action<UserRowViewModel> togglePin,
        Action<UserRowViewModel> openProfile,
        Func<UserRowViewModel, Task> deleteOne)
    {
        Model = model;
        DownloadCommand = new AsyncRelayCommand(() => downloadOne(this));
        TogglePinCommand = new RelayCommand(() => togglePin(this));
        OpenProfileCommand = new AsyncRelayCommand(() => { openProfile(this); return Task.CompletedTask; });
        DeleteCommand = new AsyncRelayCommand(() => deleteOne(this));
    }

    public User Model { get; }
    public bool IsSelected { get => _isSelected; set { if (SetProperty(ref _isSelected, value)) SelectedChanged?.Invoke(); } }
    public event Action? SelectedChanged;

    public string Title => string.IsNullOrWhiteSpace(Model.DisplayName) ? Model.ScreenName : Model.DisplayName!;
    public string Subtitle => $"@{Model.ScreenName} · {SourceText}";
    public string DownloadCountText => Model.DownloadCount.ToString();
    public string LastDownloadText => Model.LastDownloadAt is { } t
        ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "—";
    public bool IsPinned => Model.IsPinned;
    public string PinButtonText => Model.IsPinned ? "取消置顶" : "置顶";
    public string? AvatarUrl => Model.AvatarUrl;

    public IAsyncRelayCommand DownloadCommand { get; }
    public IRelayCommand OpenProfileCommand { get; }
    public IRelayCommand TogglePinCommand { get; }
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
    private const string SiteId = "twitter";

    private readonly IUserQueryService _userQuery;
    private readonly IAccountQueryService _accountQuery;
    private readonly IUserService _users;
    private readonly IDownloadQueueService _queue;
    private readonly IAppSettings _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly SiteRegistry _sites;

    private CancellationTokenSource? _searchDebounce;
    private int _refreshId; // 并发刷新代数：过期刷新的集合变更丢弃
    private bool _hasUsers;
    private bool _hasAccount;

    public UsersViewModel(IUserQueryService userQuery, IAccountQueryService accountQuery,
        IUserService users, IDownloadQueueService queue, IAppSettings settings,
        IUiDispatcher dispatcher, SiteRegistry sites)
    {
        _userQuery = userQuery;
        _accountQuery = accountQuery;
        _users = users;
        _queue = queue;
        _settings = settings;
        _dispatcher = dispatcher;
        _sites = sites;

        Users = [];
        // AllowConcurrentExecutions：防抖触发的刷新不被默认的并发闸门静默丢弃，
        // 过期结果由 _refreshId 代数守卫在集合变更处丢弃
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
        DownloadSelectedCommand = new AsyncRelayCommand(DownloadSelectedAsync);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync);
        TogglePinCommand = new RelayCommand<UserRowViewModel>(u => _ = TogglePinCoreAsync(u));
        DownloadOneCommand = new AsyncRelayCommand<UserRowViewModel>(DownloadOneAsync);
        OpenProfileCommand = new RelayCommand<UserRowViewModel>(OpenProfileCore);
        ShowAddUserCommand = new RelayCommand(() => ShowAddUserRequested?.Invoke());
        ShowImportCookieCommand = new RelayCommand(() => ShowImportCookieRequested?.Invoke());

        _queue.AccountInvalid += OnAccountInvalid;
    }

    public ObservableCollection<UserRowViewModel> Users { get; }

    public int SelectedCount => Users.Count(r => r.IsSelected);
    public bool HasUsers => _hasUsers;
    public bool HasAccount => _hasAccount;               // 驱动空状态引导与导入按钮
    public bool ImportButtonVisible => !_hasAccount;     // 无账号时"导入关注列表"按钮显示引导
    public bool BottomBarVisible => SelectedCount > 0;
    public string SelectedCountText => $"已选 {SelectedCount} 个";

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private string? _statusMessage;   // 操作结果反馈（成功/失败一行话）

    public event Action? RequestReload;                     // B4 导入流程完成后通知刷新
    public event Action? ShowAddUserRequested;              // B4 接线：添加用户对话框
    public event Action? ShowImportCookieRequested;         // B4 接线：导入 Cookie 对话框

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand DownloadSelectedCommand { get; }
    public IAsyncRelayCommand DeleteSelectedCommand { get; }
    public IRelayCommand<UserRowViewModel> TogglePinCommand { get; }
    public IRelayCommand<UserRowViewModel> DownloadOneCommand { get; }
    public IRelayCommand<UserRowViewModel> OpenProfileCommand { get; }
    public IRelayCommand ShowAddUserCommand { get; }
    public IRelayCommand ShowImportCookieCommand { get; }

    // brief Step 4 XAML 绑定 ShowAddUserCommand/ShowImportCookieCommand（Produces 契约之外的占位命令，
    // "直接调 VM 内部占位命令——B4 接线"），命令转发为事件，页面订阅后转对话框。

    /// <summary>300ms 防抖：Task.Delay 期间新输入经 CTS 竞争取消旧等待，只有最后一次生效。</summary>
    partial void OnSearchTextChanged(string? value)
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        var cts = _searchDebounce = new CancellationTokenSource();
        _ = DebouncedRefreshAsync(value, cts.Token);
    }

    private async Task DebouncedRefreshAsync(string? value, CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            if (!string.Equals(value, SearchText)) return; // 双保险：值已变则丢弃（CTS 已保证）
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // 新输入取消了旧等待，静默退出
        }
    }

    public void SelectAll(bool selected)
        => _dispatcher.Post(() =>
        {
            foreach (var row in Users)
                row.IsSelected = selected;
        });

    private async Task RefreshAsync()
    {
        var gen = ++_refreshId;
        var filter = new UserFilter(SearchText);
        var list = await _userQuery.ListAsync(SiteId, filter);
        var account = await _accountQuery.GetActiveAsync(SiteId);
        var selectedIds = Users.Where(r => r.IsSelected).Select(r => r.Model.Id).ToHashSet();
        _dispatcher.Post(() =>
        {
            if (gen != _refreshId) return; // 过期刷新：已被更新的查询取代
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
            OnPropertyChanged(nameof(ImportButtonVisible));
            NotifySelection();
        });
    }

    private UserRowViewModel CreateRow(User user)
    {
        var row = new UserRowViewModel(user,
            downloadOne: DownloadOneAsync,
            togglePin: u => _ = TogglePinCoreAsync(u),
            openProfile: OpenProfileCore,
            deleteOne: DeleteOneAsync);
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
        await DownloadUsersAsync(users);
    }

    private Task DownloadOneAsync(UserRowViewModel row) => DownloadUsersAsync([row.Model]);

    private async Task DownloadUsersAsync(IReadOnlyList<User> users)
    {
        var account = await _accountQuery.GetActiveAsync(SiteId);
        if (account is null)
        {
            StatusMessage = "请先在设置中导入 Cookie";
            return;
        }
        try
        {
            var dir = await _settings.GetDownloadDirectoryAsync();
            var siteOptions = await _settings.GetSiteOptionsAsync(SiteId);
            await _queue.EnqueueUserMediaAsync(account, users, dir, siteOptions);
            StatusMessage = $"已加入下载队列（{users.Count} 个用户）";
        }
        catch (Exception ex) // EngineException 等
        {
            StatusMessage = ex.Message;
        }
    }

    // 控制器裁定 3（brief 定稿授权的简化替代）：V1 删除不做二次确认，直接删 + StatusMessage。
    // （删除的是库记录而非文件，风险低；页面层 ConfirmDeleteRequested 弹窗方案留待后续）
    private async Task DeleteSelectedAsync()
    {
        var ids = Users.Where(r => r.IsSelected).Select(r => r.Model.Id).ToList();
        if (ids.Count == 0) return;
        await _users.RemoveAsync(ids);
        StatusMessage = $"已删除 {ids.Count} 个用户";
        await RefreshAsync();
    }

    private async Task DeleteOneAsync(UserRowViewModel row)
    {
        await _users.RemoveAsync([row.Model.Id]);
        StatusMessage = "已删除 1 个用户";
        await RefreshAsync();
    }

    private async Task TogglePinCoreAsync(UserRowViewModel row)
    {
        await _users.SetPinnedAsync(row.Model.Id, !row.Model.IsPinned);
        await RefreshAsync(); // 置顶排序键变更，重建列表
    }

    // 控制器裁定 4：Process.Start(UseShellExecute=true) 放 Core（net8.0 可用）；ProfileUrl 空则用 BuildProfileUrl
    private void OpenProfileCore(UserRowViewModel row)
    {
        var url = row.Model.ProfileUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = _sites.Get(SiteId).BuildProfileUrl(row.Model.ScreenName);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OnAccountInvalid(long accountId, string reason)
        => _dispatcher.Post(() =>
        {
            StatusMessage = $"登录态失效，请重新导入 Cookie：{reason}";
            _ = RefreshAsync(); // 刷新 HasAccount 等派生态
        });
}
