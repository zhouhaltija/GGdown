using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalleryGUI.Data;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Services;
using GalleryGUI.Settings;
using GalleryGUI.Sites;
using GalleryGUI.Threading;

namespace GalleryGUI.ViewModels;

/// <summary>
/// 站点选项行 VM（B7）：OptionSchema 字段 + 当前值的可编辑包装。
/// Kind 决定编辑控件与 ToValue 回读类型（Boolean→bool，Text/Choice→string），
/// 值类型与站点 DefaultOptions 对齐（裁定 3）。
/// 页面按 IsBoolean/IsText 做单模板可见性切换渲染（裁定 2：简洁优先）。
/// </summary>
public sealed partial class OptionItemViewModel : ObservableObject
{
    public OptionItemViewModel(OptionField field, object? value)
    {
        Key = field.Key;
        DisplayName = field.DisplayName;
        Kind = field.Kind;
        // 当前值缺失/类型不符回退 Schema 默认值（GetSiteOptionsAsync 只回保存过的键）
        _boolValue = value as bool? ?? (field.Default as bool? ?? false);
        _textValue = value?.ToString() ?? field.Default?.ToString() ?? "";
    }

    public string Key { get; }
    public string DisplayName { get; }
    public OptionKind Kind { get; }

    private bool _boolValue;
    public bool BoolValue { get => _boolValue; set => SetProperty(ref _boolValue, value); }

    private string _textValue = "";
    public string TextValue { get => _textValue; set => SetProperty(ref _textValue, value); }

    // 页面渲染开关：Boolean→ToggleSwitch，Text/Choice→TextBox（V1 无 Choice 字段，TextBox 兜底）
    public bool IsBoolean => Kind == OptionKind.Boolean;
    public bool IsText => Kind != OptionKind.Boolean;

    /// <summary>按 Kind 回读当前值（bool/string，与 DefaultOptions 值类型对齐）。</summary>
    public object? ToValue() => Kind switch
    {
        OptionKind.Boolean => BoolValue,
        _ => TextValue,
    };
}

/// <summary>
/// 设置页 VM（B7）：通用（下载目录/并发）、账号卡（导入/更换 Cookie、重新验证、状态徽标）、
/// 站点选项（OptionSchema 自动渲染）、引擎卡与关于卡。
/// Shell 交互经事件解耦（Core 不引用 UI/Shell）：OpenFolderPickerRequested→页面 FileDialogService、
/// OpenFolderRequested→页面 LauncherService（brief Interfaces 节）。
/// 异步加载入口 StartAsync（页面经 Start 触发）；集合/属性变更经 IUiDispatcher 投递（B5 模式）。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private const string SiteId = "twitter"; // 同 Users/Downloads/History：Phase B 仅接入 X (Twitter)，V1 取唯一注册站点

    private readonly IAppSettings _settings;
    private readonly IAccountService _accounts;
    private readonly IAccountQueryService _accountQuery;
    private readonly IDownloadEngine _engine;
    private readonly IDownloadQueueService _queue;
    private readonly IAppPaths _paths;
    private readonly SiteRegistry _sites;
    private readonly IUiDispatcher _dispatcher;

    public SettingsViewModel(IAppSettings settings, IAccountService accounts, IAccountQueryService accountQuery,
        IDownloadEngine engine, IDownloadQueueService queue, IAppPaths paths, SiteRegistry sites, IUiDispatcher dispatcher)
    {
        _settings = settings;
        _accounts = accounts;
        _accountQuery = accountQuery;
        _engine = engine;
        _queue = queue;
        _paths = paths;
        _sites = sites;
        _dispatcher = dispatcher;

        SaveGeneralCommand = new AsyncRelayCommand(SaveGeneralAsync);
        SaveOptionsCommand = new AsyncRelayCommand(SaveOptionsAsync);
        VerifyAccountCommand = new AsyncRelayCommand(VerifyAccountAsync);
        ImportCookieCommand = new AsyncRelayCommand(ImportCookieAsync); // Produces 之外补充（B6 ClearFilters 先例）：对话框选定后触发
        BrowseDownloadDirectoryCommand = new RelayCommand(() => OpenFolderPickerRequested?.Invoke()); // 同上：触发浏览事件
        OpenLogsCommand = new RelayCommand(() => OpenFolderRequested?.Invoke(_paths.LogsDir));
    }

    // —— 通用 ——

    private string _downloadDirectory = "";
    public string DownloadDirectory { get => _downloadDirectory; private set => SetProperty(ref _downloadDirectory, value); }

    public event Action? OpenFolderPickerRequested;   // 浏览按钮 → 页面 FileDialogService.PickFolderAsync → SetDownloadDirectoryAsync
    public event Action<string>? OpenFolderRequested; // 打开日志文件夹 → 页面 LauncherService.OpenFolder

    [ObservableProperty]
    private double _concurrency;          // NumberBox 绑定，保存时取整钳制

    [ObservableProperty]
    private string? _statusMessage;       // 操作结果反馈（成功/失败一行话）

    // —— 账号 ——

    private Account? _activeAccount;
    public Account? ActiveAccount { get => _activeAccount; private set => SetProperty(ref _activeAccount, value); } // null → 页面显示导入按钮

    public bool HasAccount => ActiveAccount is not null;
    public string AccountTitle => ActiveAccount is null ? ""
        : string.IsNullOrWhiteSpace(ActiveAccount.DisplayName) ? ActiveAccount.ScreenName ?? ""
        : ActiveAccount.DisplayName!;
    public string AccountHandle => ActiveAccount is null ? "" : $"@{ActiveAccount.ScreenName}";
    // 徽标文字：未验证/有效/失效（brief Produces）
    public string AccountStatusText => ActiveAccount?.Status switch
    {
        AccountStatus.Ok => "有效",
        AccountStatus.Invalid => "失效",
        AccountStatus.Unverified => "未验证",
        _ => "",
    };

    [ObservableProperty]
    private string? _pendingCookieFile;   // 对话框选择结果（页面赋值后执行 ImportCookieCommand）

    public event Action? AccountChanged;  // 导入/验证后通知（页面刷新账号卡，裁定 8）

    // —— 站点选项 ——

    public ObservableCollection<OptionItemViewModel> TwitterOptions { get; } = [];

    // —— 引擎/关于 ——

    private string _engineVersion = "检测中…";
    public string EngineVersion { get => _engineVersion; private set => SetProperty(ref _engineVersion, value); } // HelloAsync 后回填（brief get-only 的异步适配，B6 FilterUsers 先例）

    public string AppVersion => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    public IAsyncRelayCommand SaveGeneralCommand { get; }
    public IAsyncRelayCommand SaveOptionsCommand { get; }
    public IAsyncRelayCommand VerifyAccountCommand { get; }
    public IAsyncRelayCommand ImportCookieCommand { get; }
    public IRelayCommand BrowseDownloadDirectoryCommand { get; }
    public IRelayCommand OpenLogsCommand { get; }

    /// <summary>页面入口（Produces 签名）：异步加载设置/账号/引擎版本。幂等，可随导航重复调用。</summary>
    public void Start() => _ = StartAsync();

    /// <summary>Start 的可等待核心（测试确定性等待用，HistoryViewModel.LoadFilterUsersAsync 先例）。</summary>
    public async Task StartAsync()
    {
        try
        {
            var concurrency = await _settings.GetConcurrencyAsync();
            _queue.Concurrency = concurrency; // 裁定 4：启动时应用并发（Phase A setter）
            var directory = await _settings.GetDownloadDirectoryAsync();
            var account = await _accountQuery.GetActiveAsync(SiteId);
            var options = await _settings.GetSiteOptionsAsync(SiteId);
            var engineVersion = await ProbeEngineAsync();
            _dispatcher.Post(() =>
            {
                Concurrency = concurrency;
                DownloadDirectory = directory;
                SetActiveAccount(account);
                EngineVersion = engineVersion;
                RebuildOptions(options);
            });
        }
        catch (Exception ex) // 坏库等异常不崩线程（B5 模式）
        {
            StatusMessage = $"加载设置失败：{ex.Message}";
        }
    }

    // 裁定 6：HelloAsync → "gallery-dl {版本 ?? 未知}（runner {runner}）"；失败 → "引擎未就绪（{Message}）"
    private async Task<string> ProbeEngineAsync()
    {
        try
        {
            var hello = await _engine.HelloAsync();
            return $"gallery-dl {hello.GalleryDlVersion ?? "未知"}（runner {hello.RunnerVersion}）";
        }
        catch (Exception ex) // EngineException（未安装/python 缺失）等
        {
            return $"引擎未就绪（{ex.Message}）";
        }
    }

    // 按 Schema 字段建行：保存值优先，缺省回退 Schema 默认值
    private void RebuildOptions(IReadOnlyDictionary<string, object?> options)
    {
        var schema = _sites.Get(SiteId).OptionsSchema;
        TwitterOptions.Clear();
        foreach (var field in schema.Fields)
        {
            options.TryGetValue(field.Key, out var value);
            TwitterOptions.Add(new OptionItemViewModel(field, value ?? field.Default));
        }
    }

    private void SetActiveAccount(Account? account)
    {
        ActiveAccount = account;
        OnPropertyChanged(nameof(HasAccount));
        OnPropertyChanged(nameof(AccountTitle));
        OnPropertyChanged(nameof(AccountHandle));
        OnPropertyChanged(nameof(AccountStatusText));
    }

    /// <summary>浏览对话框选定后调用（裁定 5）：保存 + 更新属性显示；空路径（取消）不动作。</summary>
    public async Task SetDownloadDirectoryAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await _settings.SetDownloadDirectoryAsync(path);
            DownloadDirectory = path;
            StatusMessage = "下载目录已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    private async Task SaveGeneralAsync()
    {
        try
        {
            var n = Math.Max(1, (int)Math.Round(Concurrency)); // <1 钳制（裁定 4/测试规格②）
            await _settings.SetConcurrencyAsync(n);
            _queue.Concurrency = n; // 保存通用设置同步 setter（裁定 4）
            if (!string.IsNullOrWhiteSpace(DownloadDirectory))
                await _settings.SetDownloadDirectoryAsync(DownloadDirectory);
            StatusMessage = "通用设置已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    // 裁定 3：只保存 Schema 中存在的键，值类型与 DefaultOptions 对齐（bool/string，经 OptionItemViewModel.ToValue）
    private async Task SaveOptionsAsync()
    {
        try
        {
            var schema = _sites.Get(SiteId).OptionsSchema;
            var byKey = TwitterOptions.ToDictionary(o => o.Key);
            var dict = new Dictionary<string, object?>();
            foreach (var field in schema.Fields)
                if (byKey.TryGetValue(field.Key, out var item))
                    dict[field.Key] = item.ToValue();
            await _settings.SetSiteOptionsAsync(SiteId, dict);
            StatusMessage = "站点选项已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    // 裁定 8：VerifyAsync 原地改写账号 Status → 重发账号派生属性刷新徽标
    private async Task VerifyAccountAsync()
    {
        if (ActiveAccount is null) return;
        try
        {
            var result = await _accounts.VerifyAsync(ActiveAccount);
            StatusMessage = result.Ok ? $"账号 {AccountHandle} 验证通过" : (result.Error ?? "验证失败");
            SetActiveAccount(ActiveAccount);
            AccountChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"验证失败：{ex.Message}";
        }
    }

    // 裁定 8：导入/更换 Cookie 共用（页面 ImportCookieDialog 选定 → PendingCookieFile → 本命令）
    private async Task ImportCookieAsync()
    {
        if (string.IsNullOrEmpty(PendingCookieFile)) return;
        try
        {
            var result = await _accounts.ImportCookiesAsync(SiteId, PendingCookieFile);
            StatusMessage = result.Ok ? $"账号 @{result.Account.ScreenName} 导入成功" : (result.Error ?? "导入失败");
            PendingCookieFile = null;
            SetActiveAccount(await _accountQuery.GetActiveAsync(SiteId)); // 新账号（含验证失败标记 Invalid 的情况）入卡
            AccountChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败：{ex.Message}";
        }
    }
}
