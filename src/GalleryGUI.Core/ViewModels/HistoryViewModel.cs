using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalleryGUI.Data;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using GalleryGUI.Threading;

namespace GalleryGUI.ViewModels;

/// <summary>
/// 历史页行 VM（B6）。行内命令用行 VM 持回调委托模式（B3 UserRowViewModel 先例）：
/// WinUI 行模板（x:Bind x:DataType）无法绑定宿主命令，行按钮绑定行自身命令，
/// 回调委托回到 HistoryViewModel 的核心方法。
/// 全部展示字段为只读投影（历史记录不原地编辑，每次 Refresh 重建行）。
/// </summary>
public sealed partial class HistoryRowViewModel : ObservableObject
{
    private readonly Func<HistoryRowViewModel, Task> _openContainingFolder;
    private readonly Action<HistoryRowViewModel> _openSource;

    public HistoryRowViewModel(HistoryRow model,
        Func<HistoryRowViewModel, Task> openContainingFolder,
        Action<HistoryRowViewModel> openSource)
    {
        Model = model;
        _openContainingFolder = openContainingFolder;
        _openSource = openSource;
        OpenContainingFolderCommand = new AsyncRelayCommand(() => _openContainingFolder(this));
        OpenSourceCommand = new RelayCommand(() => _openSource(this));
    }

    public HistoryRow Model { get; }

    public string FileName => Path.GetFileName(Model.FilePath);                       // brief：取文件名部分
    public string UserText => Model.UserScreenName ?? "账号内容";                     // brief：likes/书签文件无用户归属
    public string? SourceUrl => Model.SourceItemId is null
        ? null
        : $"https://x.com/i/status/{Model.SourceItemId}";                             // brief 定稿构造
    public bool HasSourceUrl => SourceUrl is not null;                                // 行内"查看原文"显隐（页面 UiConv 映射）
    public string SizeText => FormatSize(Model.FileSize);
    public string StatusText => Model.Status switch
    {
        FileStatus.Downloaded => "已下载",
        FileStatus.Skipped => "已跳过",
        FileStatus.Failed => "失败",
        _ => Model.Status.ToString(),
    };
    // 同 UsersViewModel.LastDownloadText 的先例模式（SQLite 读回 Kind=Unspecified，ToLocalTime 语义一致）
    public string CreatedText => Model.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public IAsyncRelayCommand OpenContainingFolderCommand { get; }
    public IRelayCommand OpenSourceCommand { get; }

    /// <summary>brief Step 3：SizeText 人性化放 VM 内部 static（B/KB/MB/GB，F1 一位小数；无值 "—"）。</summary>
    public static string FormatSize(long? bytes) => bytes switch
    {
        null => "—",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
    };
}

/// <summary>
/// 历史页 VM（B6）：文件历史列表 + 筛选三元组（用户/起止日期）+ 行内溯源与打开所在文件夹。
/// 筛选任一变更自动触发 Refresh（AllowConcurrentExecutions + 刷新代数守卫，UsersViewModel 先例）；
/// 就绪门（App.Readiness）由页面 OnNavigatedTo await，VM 内不用（控制器裁定 3）。
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly ICurrentSite _currentSite;
    private readonly IHistoryQueryService _history;
    private readonly IUiDispatcher _dispatcher;
    private int _refreshId; // 并发刷新代数：过期刷新的集合变更丢弃

    private string SiteId => _currentSite.SiteId;

    public HistoryViewModel(IHistoryQueryService history, IUiDispatcher dispatcher,
        IAccountQueryService accountQuery, SiteRegistry sites, ICurrentSite currentSite)
    {
        _history = history;
        _dispatcher = dispatcher;
        _currentSite = currentSite;
        _currentSite.Changed += () => _ = RefreshAsync();
        // accountQuery/sites 按 Produces 契约保留构造签名（当前无消费点：状态链接取 brief 定稿的
        // 字面构造，FilterUsers 走 IHistoryQueryService.ListUsersAsync(SiteId)），故不存储避免 CS0414。

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
        OpenContainingFolderCommand = new AsyncRelayCommand<HistoryRowViewModel>(OpenContainingFolderCoreAsync);
        OpenSourceCommand = new RelayCommand<HistoryRowViewModel>(OpenSourceCore);
        ClearFiltersCommand = new RelayCommand(() =>
        {
            // 三个 null 赋值各自触发一次自动刷新，代数守卫保证最终结果一致
            SelectedUserFilter = null;
            FromDate = null;
            ToDate = null;
        });
    }

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];

    /// <summary>筛选下拉数据（ListUsersAsync 按 ScreenName 排序）。异步加载，页面 OnNavigatedTo 时刷新。</summary>
    public IReadOnlyList<User> FilterUsers { get; private set; } = [];

    [ObservableProperty]
    private User? _selectedUserFilter;   // null=全部用户（占位显示）

    [ObservableProperty]
    private DateTimeOffset? _fromDate;   // 仅日期部分：DatePicker.SelectedDate 绑定

    [ObservableProperty]
    private DateTimeOffset? _toDate;     // 含当日：HistoryFilter.To = Date.AddDays(1) 开区间上界

    [ObservableProperty]
    private string? _statusMessage;      // 操作结果反馈（成功/失败一行话）

    public IAsyncRelayCommand RefreshCommand { get; }
    // brief 原文声明非泛型 IAsyncRelayCommand，但其注释"参数：HistoryRowViewModel（行内按钮）"要求携带行——
    // 取注释语义（非泛型无法传参）；行内按钮实际绑定行 VM 命令，此为宿主/测试侧 API
    public IAsyncRelayCommand<HistoryRowViewModel> OpenContainingFolderCommand { get; }
    public IRelayCommand<HistoryRowViewModel> OpenSourceCommand { get; }
    public IRelayCommand ClearFiltersCommand { get; } // 页面"清除筛选"按钮（brief 页面规格，Produces 之外补充）

    partial void OnSelectedUserFilterChanged(User? value) => _ = RefreshCommand.ExecuteAsync(null);
    partial void OnFromDateChanged(DateTimeOffset? value) => _ = RefreshCommand.ExecuteAsync(null);
    partial void OnToDateChanged(DateTimeOffset? value) => _ = RefreshCommand.ExecuteAsync(null);

    private async Task RefreshAsync()
    {
        try
        {
            var gen = ++_refreshId;
            var filter = new HistoryFilter(SelectedUserFilter?.Id, FromDate?.Date, ToDate?.Date.AddDays(1));
            var list = await _history.ListAsync(filter);
            _dispatcher.Post(() =>
            {
                if (gen != _refreshId) return; // 过期刷新：已被更新的查询取代
                Rows.Clear();
                foreach (var r in list)
                    Rows.Add(new HistoryRowViewModel(r, OpenContainingFolderCoreAsync, OpenSourceCore));
            });
        }
        catch (Exception ex) // 坏库等异常不崩线程（B5 模式）
        {
            StatusMessage = $"加载历史失败：{ex.Message}";
        }
    }

    /// <summary>加载筛选下拉用户列表（页面 OnNavigatedTo 调用；异常落 StatusMessage 不崩线程）。</summary>
    public async Task LoadFilterUsersAsync()
    {
        try
        {
            FilterUsers = await _history.ListUsersAsync(SiteId);
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载用户列表失败：{ex.Message}";
        }
        OnPropertyChanged(nameof(FilterUsers));
    }

    // 控制器裁定 2：文件不存在先判断 → StatusMessage；不经 IUiDispatcher（同步快速）。
    // Windows 专属 explorer.exe 调用放 VM 可接受（应用本身 Windows-only），Core 用 Process 可行（B3 OpenProfile 先例）。
    private Task OpenContainingFolderCoreAsync(HistoryRowViewModel row)
    {
        var path = Path.GetFullPath(row.Model.FilePath);
        if (!File.Exists(path))
        {
            StatusMessage = "文件已被移动或删除";
            return Task.CompletedTask;
        }
        try
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开文件夹失败：{ex.Message}";
        }
        return Task.CompletedTask;
    }

    private void OpenSourceCore(HistoryRowViewModel row)
    {
        if (row.SourceUrl is not { } url)
        {
            StatusMessage = "该记录无来源链接";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); // B3 OpenProfile 同款
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开浏览器失败：{ex.Message}";
        }
    }
}
