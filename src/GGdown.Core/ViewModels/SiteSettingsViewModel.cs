using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GGdown.Data;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Sites;
using GGdown.Threading;

namespace GGdown.ViewModels;

/// <summary>
/// 平台设置页 VM（Task 9，自原 SettingsViewModel 拆分）：当前平台的账号卡（导入/更换 Cookie、
/// 重新验证、状态徽标）与站点选项（OptionSchema 自动渲染）。
/// 注册为 Transient 且页面 NavigationCacheMode=Enabled（构造函数解析一次，实例与页面同生命周期）——
/// 依赖 Scoped 的 IAccountService，singleton 会形成 captive dependency（B7 裁定沿用）。
/// </summary>
public partial class SiteSettingsViewModel : StatusViewModel
{
    private readonly ICurrentSite _currentSite;
    private readonly IAppSettings _settings;
    private readonly IAccountService _accounts;
    private readonly IAccountQueryService _accountQuery;
    private readonly SiteRegistry _sites;
    private readonly IUiDispatcher _dispatcher;

    public SiteSettingsViewModel(IAppSettings settings, IAccountService accounts, IAccountQueryService accountQuery,
        SiteRegistry sites, IUiDispatcher dispatcher, ICurrentSite currentSite)
    {
        _currentSite = currentSite;
        _currentSite.Changed += () => _ = StartAsync(); // 平台切换即重载（共享页面实例，B7 语义保留）
        _settings = settings;
        _accounts = accounts;
        _accountQuery = accountQuery;
        _sites = sites;
        _dispatcher = dispatcher;

        SaveOptionsCommand = new AsyncRelayCommand(SaveOptionsAsync);
        VerifyAccountCommand = new AsyncRelayCommand(VerifyAccountAsync);
        ImportCookieCommand = new AsyncRelayCommand(ImportCookieAsync);
    }

    // —— 账号 ——

    private Account? _activeAccount;
    public Account? ActiveAccount { get => _activeAccount; private set => SetProperty(ref _activeAccount, value); } // null → 页面显示导入按钮

    private string SiteId => _currentSite.SiteId;

    public bool IsSiteAvailable => _currentSite.IsAvailable;
    public bool ShowComingSoon => !_currentSite.IsAvailable;
    public string SiteDisplayName => _currentSite.Current.DisplayName;
    public string AccountSiteHeader => $"{_currentSite.Current.DisplayName} 账号";
    public string OptionsHeader => $"站点选项 · {_currentSite.Current.DisplayName}";
    public string ComingSoonMessage => $"{_currentSite.Current.DisplayName} 即将支持，该站点尚未开放导入账号与站点选项。";

    public bool HasAccount => ActiveAccount is not null;
    public bool ShowAccountImport => IsSiteAvailable && !HasAccount;
    public bool ShowAccountCard => IsSiteAvailable && HasAccount;
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

    public bool RequiresRefreshToken =>
        IsSiteAvailable && _sites.IsRegistered(SiteId) && _sites.Get(SiteId).RequiresRefreshToken;

    [ObservableProperty]
    private string? _pendingCookieFile;   // 对话框选择结果（页面赋值后执行 ImportCookieCommand）

    [ObservableProperty]
    private string? _pendingRefreshToken;

    public event Action? AccountChanged;  // 导入/验证后通知（页面刷新账号卡，裁定 8）

    // —— 站点选项 ——

    public ObservableCollection<OptionItemViewModel> SiteOptions { get; } = [];

    public IAsyncRelayCommand SaveOptionsCommand { get; }
    public IAsyncRelayCommand VerifyAccountCommand { get; }
    public IAsyncRelayCommand ImportCookieCommand { get; }

    /// <summary>页面入口（Produces 签名沿用）：异步加载账号/站点选项。幂等。</summary>
    public void Start() => _ = StartAsync();

    public async Task StartAsync()
    {
        try
        {
            var account = _currentSite.IsAvailable ? await _accountQuery.GetActiveAsync(SiteId) : null;
            var options = _currentSite.IsAvailable && _sites.IsRegistered(SiteId)
                ? await _settings.GetSiteOptionsAsync(SiteId)
                : null;
            _dispatcher.Post(() =>
            {
                SetActiveAccount(account);
                if (options is null) SiteOptions.Clear();
                else RebuildOptions(options);
                OnPropertyChanged(nameof(IsSiteAvailable));
                OnPropertyChanged(nameof(ShowComingSoon));
                OnPropertyChanged(nameof(SiteDisplayName));
                OnPropertyChanged(nameof(AccountSiteHeader));
                OnPropertyChanged(nameof(OptionsHeader));
                OnPropertyChanged(nameof(ComingSoonMessage));
                OnPropertyChanged(nameof(RequiresRefreshToken));
            });
        }
        catch (Exception ex) // 坏库等异常不崩线程（B5 模式）
        {
            StatusMessage = $"加载账号失败：{ex.Message}";
        }
    }

    // 按 Schema 字段建行：保存值优先，缺省回退 Schema 默认值
    private void RebuildOptions(IReadOnlyDictionary<string, object?> options)
    {
        var schema = _sites.Get(SiteId).OptionsSchema;
        _autoSave?.Cancel();
        SiteOptions.Clear();
        foreach (var field in schema.Fields)
        {
            options.TryGetValue(field.Key, out var value);
            var item = new OptionItemViewModel(field, value ?? field.Default);
            item.PropertyChanged += (_, _) => ScheduleAutoSave(); // 改动即保存，与界面设置"勾选即生效"一致
            SiteOptions.Add(item);
        }
    }

    // —— 自动保存：开关立即生效，文本输入 600ms 防抖后保存（不再需要单独的「保存」按钮）——

    private CancellationTokenSource? _autoSave;

    /// <summary>自动保存防抖时长（测试可调小）。</summary>
    public TimeSpan AutoSaveDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    private void ScheduleAutoSave()
    {
        _autoSave?.Cancel();
        _autoSave?.Dispose();
        var cts = _autoSave = new CancellationTokenSource();
        _ = AutoSaveAsync(cts.Token);
    }

    private async Task AutoSaveAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AutoSaveDelay, ct);
            _dispatcher.Post(() => { if (!ct.IsCancellationRequested) _ = SaveOptionsAsync(); });
        }
        catch (OperationCanceledException)
        {
            // 新改动取代了旧的等待
        }
    }

    private void SetActiveAccount(Account? account)
    {
        ActiveAccount = account;
        OnPropertyChanged(nameof(HasAccount));
        OnPropertyChanged(nameof(ShowAccountImport));
        OnPropertyChanged(nameof(ShowAccountCard));
        OnPropertyChanged(nameof(AccountTitle));
        OnPropertyChanged(nameof(AccountHandle));
        OnPropertyChanged(nameof(AccountStatusText));
    }

    // 裁定 3：只保存 Schema 中存在的键，值类型与 DefaultOptions 对齐（bool/string，经 OptionItemViewModel.ToValue）
    private async Task SaveOptionsAsync()
    {
        if (!_currentSite.IsAvailable || !_sites.IsRegistered(SiteId))
        {
            StatusMessage = ComingSoonMessage;
            return;
        }
        try
        {
            var schema = _sites.Get(SiteId).OptionsSchema;
            var byKey = SiteOptions.ToDictionary(o => o.Key);
            var dict = new Dictionary<string, object?>();
            foreach (var field in schema.Fields)
                if (byKey.TryGetValue(field.Key, out var item))
                    dict[field.Key] = item.ToValue();
            if (SiteId == DouyinSiteProvider.Id && dict.TryGetValue("earliest_date", out var dateValue)
                && dateValue is string dateText && !string.IsNullOrWhiteSpace(dateText))
            {
                if (!DateOnly.TryParseExact(dateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _))
                {
                    StatusMessage = "最早发布日期须为 yyyy-MM-dd";
                    return;
                }
                dict["earliest_date"] = dateText.Trim();
            }
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
            var result = await _accounts.ImportCookiesAsync(SiteId, PendingCookieFile, PendingRefreshToken);
            StatusMessage = result.Ok ? $"账号 @{result.Account.ScreenName} 导入成功" : (result.Error ?? "导入失败");
            PendingCookieFile = null;
            PendingRefreshToken = null;
            SetActiveAccount(await _accountQuery.GetActiveAsync(SiteId)); // 新账号（含验证失败标记 Invalid 的情况）入卡
            AccountChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败：{ex.Message}";
        }
    }
}
