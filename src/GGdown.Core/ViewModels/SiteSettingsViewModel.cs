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
public partial class SiteSettingsViewModel : StatusViewModel, IDisposable
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
        _currentSite.Changed += OnSiteChanged;
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

    private int _loadVersion;
    private CancellationTokenSource? _loadCancellation;
    private string? _optionsSiteId;
    private bool _listening = true;
    private readonly Dictionary<string, CancellationTokenSource> _autoSaves = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _editVersions = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        (long Version, IReadOnlyDictionary<string, object?> Values)> _drafts = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private void OnSiteChanged() => _ = StartAsync();

    public void Resume()
    {
        if (_listening) return;
        _listening = true;
        _currentSite.Changed += OnSiteChanged;
    }

    public void Stop()
    {
        if (!_listening) return;
        _listening = false;
        _currentSite.Changed -= OnSiteChanged;
        ++_loadVersion;
        _loadCancellation?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _loadCancellation?.Dispose();
    }

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
        var siteId = SiteId;
        var available = _currentSite.IsAvailable;
        var version = ++_loadVersion;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var ct = (_loadCancellation = new CancellationTokenSource()).Token;
        bool IsCurrent() => version == _loadVersion && siteId == SiteId && !ct.IsCancellationRequested;
        try
        {
            var account = available ? await _accountQuery.GetActiveAsync(siteId, ct) : null;
            var options = available && _sites.IsRegistered(siteId)
                ? await _settings.GetSiteOptionsAsync(siteId, ct) : null;
            _dispatcher.Post(() =>
            {
                if (!IsCurrent()) return;
                SetActiveAccount(account);
                _optionsSiteId = siteId;
                if (options is null) SiteOptions.Clear();
                else RebuildOptions(siteId, _drafts.TryGetValue(siteId, out var draft) ? draft.Values : options);
                OnPropertyChanged(nameof(IsSiteAvailable));
                OnPropertyChanged(nameof(ShowComingSoon));
                OnPropertyChanged(nameof(SiteDisplayName));
                OnPropertyChanged(nameof(AccountSiteHeader));
                OnPropertyChanged(nameof(OptionsHeader));
                OnPropertyChanged(nameof(ComingSoonMessage));
                OnPropertyChanged(nameof(RequiresRefreshToken));
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _dispatcher.Post(() => { if (IsCurrent()) StatusMessage = $"加载账号失败：{ex.Message}"; });
        }
    }

    private void RebuildOptions(string siteId, IReadOnlyDictionary<string, object?> options)
    {
        var schema = _sites.Get(siteId).OptionsSchema;
        SiteOptions.Clear();
        foreach (var field in schema.Fields)
        {
            options.TryGetValue(field.Key, out var value);
            var item = new OptionItemViewModel(field, value ?? field.Default);
            item.PropertyChanged += (_, _) => ScheduleAutoSave(siteId);
            SiteOptions.Add(item);
        }
    }

    public TimeSpan AutoSaveDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    private Dictionary<string, object?> CaptureOptions(string siteId)
    {
        var byKey = SiteOptions.ToDictionary(o => o.Key);
        return _sites.Get(siteId).OptionsSchema.Fields.Where(f => byKey.ContainsKey(f.Key))
            .ToDictionary(f => f.Key, f => byKey[f.Key].ToValue());
    }

    private long NextEditVersion(string siteId)
        => _editVersions[siteId] = _editVersions.GetValueOrDefault(siteId) + 1;

    private void ScheduleAutoSave(string siteId)
    {
        if (_optionsSiteId != siteId) return;
        if (_autoSaves.Remove(siteId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        var cts = new CancellationTokenSource();
        _autoSaves[siteId] = cts;
        var values = CaptureOptions(siteId);
        var version = NextEditVersion(siteId);
        _drafts[siteId] = (version, values);
        _ = AutoSaveAsync(siteId, values, version, cts.Token);
    }

    private async Task AutoSaveAsync(string siteId, Dictionary<string, object?> values, long version, CancellationToken ct)
    {
        try
        {
            await Task.Delay(AutoSaveDelay, ct);
            await SaveSnapshotAsync(siteId, values, version);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
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
    private Task SaveOptionsAsync()
    {
        var siteId = _optionsSiteId;
        if (siteId is null || !_sites.IsRegistered(siteId)) return Task.CompletedTask;
        var values = CaptureOptions(siteId);
        var version = NextEditVersion(siteId);
        _drafts[siteId] = (version, values);
        return SaveSnapshotAsync(siteId, values, version);
    }

    public async Task FlushPendingSavesAsync(CancellationToken ct = default)
    {
        foreach (var cts in _autoSaves.Values) cts.Cancel();
        var pending = _drafts.Select(d => (SiteId: d.Key, d.Value.Version,
            Values: new Dictionary<string, object?>(d.Value.Values))).ToArray();
        // SQLite 的异步 API 也可能同步等待写锁；调用方 UI 只等待后台工作与期限。
        await Task.Run(async () =>
        {
            foreach (var draft in pending)
                await SaveSnapshotAsync(draft.SiteId, draft.Values, draft.Version, ct);
        }, ct).WaitAsync(ct);
    }

    private async Task SaveSnapshotAsync(string siteId, Dictionary<string, object?> dict, long version,
        CancellationToken ct = default)
    {
        void Report(string message) => _dispatcher.Post(() => { if (siteId == SiteId) StatusMessage = message; });
        try
        {
            if (siteId == DouyinSiteProvider.Id && dict.TryGetValue("earliest_date", out var dateValue)
                && dateValue is string dateText && !string.IsNullOrWhiteSpace(dateText))
            {
                if (!DateOnly.TryParseExact(dateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _))
                {
                    Report("最早发布日期须为 yyyy-MM-dd");
                    return;
                }
                dict["earliest_date"] = dateText.Trim();
            }
            // 保存按请求顺序串行化；已排队的旧版本跳过，在途旧写入完成后才允许新写入。
            await _saveGate.WaitAsync(ct);
            try
            {
                if (version != _editVersions.GetValueOrDefault(siteId)) return;
                await _settings.SetSiteOptionsAsync(siteId, dict, ct);
            }
            finally { _saveGate.Release(); }
            Report("站点选项已保存");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Report($"保存失败：{ex.Message}"); }
    }

    // 裁定 8：VerifyAsync 原地改写账号 Status → 重发账号派生属性刷新徽标
    private async Task VerifyAccountAsync()
    {
        var account = ActiveAccount;
        if (account is null) return;
        var siteId = account.SiteId;
        try
        {
            var result = await _accounts.VerifyAsync(account);
            if (siteId != SiteId || !ReferenceEquals(account, ActiveAccount)) return;
            StatusMessage = result.Ok ? $"账号 {AccountHandle} 验证通过" : (result.Error ?? "验证失败");
            SetActiveAccount(account);
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
        var siteId = SiteId;
        var cookieFile = PendingCookieFile;
        var refreshToken = PendingRefreshToken;
        if (string.IsNullOrWhiteSpace(cookieFile)
            && !(RequiresRefreshToken && !string.IsNullOrWhiteSpace(refreshToken))) return;
        try
        {
            var result = await _accounts.ImportCookiesAsync(siteId, cookieFile, refreshToken);
            var account = await _accountQuery.GetActiveAsync(siteId);
            if (siteId != SiteId) return;
            StatusMessage = result.Ok ? $"账号 @{result.Account.ScreenName} 导入成功" : (result.Error ?? "导入失败");
            PendingCookieFile = null;
            PendingRefreshToken = null;
            SetActiveAccount(account); // 新账号（含验证失败标记 Invalid 的情况）入卡
            AccountChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败：{ex.Message}";
        }
    }
}
