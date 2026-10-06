using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Threading;

namespace GGdown.ViewModels;

/// <summary>
/// 全局设置 VM（Task 9，自原 SettingsViewModel 拆分）：通用（下载目录/并发）、网络（代理）、
/// 引擎（版本/日志）、关于（版本）。四个全局设置页共享同一实例（singleton）。
/// Shell 交互经事件解耦（Core 不引用 UI/Shell）：OpenFolderPickerRequested→页面 FileDialogService、
/// OpenFolderRequested→页面 LauncherService。
/// </summary>
public partial class GlobalSettingsViewModel : StatusViewModel
{
    private readonly IAppSettings _settings;
    private readonly IDownloadEngine _engine;
    private readonly IDownloadQueueService _queue;
    private readonly IAppPaths _paths;
    private readonly IUiDispatcher _dispatcher;

    public GlobalSettingsViewModel(IAppSettings settings, IDownloadEngine engine,
        IDownloadQueueService queue, IAppPaths paths, IUiDispatcher dispatcher)
    {
        _settings = settings;
        _engine = engine;
        _queue = queue;
        _paths = paths;
        _dispatcher = dispatcher;
        SaveGeneralCommand = new AsyncRelayCommand(SaveGeneralAsync);
        BrowseDownloadDirectoryCommand = new RelayCommand(() => OpenFolderPickerRequested?.Invoke());
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
    private double _downloadRateLimitKiB;

    public IReadOnlyList<string> ProxySchemeChoices { get; } = ["http", "socks5", "socks5h"];

    [ObservableProperty]
    private string _proxyScheme = "http";

    [ObservableProperty]
    private string _proxyHost = "";

    [ObservableProperty]
    private double _proxyPort;

    [ObservableProperty]
    private string _proxyUsername = "";

    [ObservableProperty]
    private string _proxyPassword = "";

    // —— 引擎/关于 ——

    private string _engineVersion = "检测中…";
    public string EngineVersion { get => _engineVersion; private set => SetProperty(ref _engineVersion, value); } // HelloAsync 后回填

    public string AppVersion => typeof(GlobalSettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    public IAsyncRelayCommand SaveGeneralCommand { get; }
    public IRelayCommand BrowseDownloadDirectoryCommand { get; }
    public IRelayCommand OpenLogsCommand { get; }

    /// <summary>页面入口（Produces 签名沿用）：异步加载设置/引擎版本。幂等，可随导航重复调用。</summary>
    public void Start() => _ = StartAsync();

    public async Task StartAsync()
    {
        try
        {
            var concurrency = await _settings.GetConcurrencyAsync();
            _queue.Concurrency = concurrency; // 裁定 4：启动时应用并发
            var directory = await _settings.GetDownloadDirectoryAsync();
            var rateLimit = await _settings.GetDownloadRateLimitAsync();
            var proxy = await _settings.GetProxyAsync();
            var proxyUrl = proxy.ToUrl();
            var proxyScheme = proxyUrl is null ? "http"
                : proxyUrl.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase) ? "socks5h"
                : proxyUrl.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ? "socks5"
                : "http";
            var engineVersion = await ProbeEngineAsync();
            _dispatcher.Post(() =>
            {
                _loadingConcurrency = true; // 载入回填不算用户修改，不触发自动保存
                Concurrency = concurrency;
                _loadingConcurrency = false;
                _loadingRateLimit = true;
                DownloadRateLimitKiB = rateLimit / 1024.0;
                _loadingRateLimit = false;
                DownloadDirectory = directory;
                ProxyScheme = proxyScheme;
                ProxyHost = proxy.Host;
                ProxyPort = proxy.Port;
                ProxyUsername = proxy.Username ?? "";
                ProxyPassword = proxy.Password ?? "";
                EngineVersion = engineVersion;
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

    /// <summary>浏览对话框选定后调用（裁定 5）：保存 + 更新属性显示；空路径（取消）不动作。</summary>
    public async Task SetDownloadDirectoryAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        await _saveGate.WaitAsync();
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
        finally { _saveGate.Release(); }
    }

    // —— 并发数改动即保存（通用页无「保存」按钮）——

    private bool _loadingConcurrency;
    private bool _loadingRateLimit;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    partial void OnDownloadRateLimitKiBChanged(double value)
    {
        if (_loadingRateLimit || !double.IsFinite(value)) return;
        _ = SaveDownloadRateLimitAsync(value);
    }

    private static long RateLimitBytes(double value) =>
        (long)Math.Round(Math.Clamp(value, 0, AppSettings.MaxDownloadRateLimit / 1024.0) * 1024);

    private async Task SaveDownloadRateLimitAsync(double value)
    {
        await _saveGate.WaitAsync();
        try
        {
            var bytes = RateLimitBytes(value);
            await _settings.SetDownloadRateLimitAsync(bytes);
            StatusMessage = bytes == 0 ? "已保存：下载不限速" : $"已保存：总下载限速 {bytes / 1024.0:0.##} KB/s";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
        finally { _saveGate.Release(); }
    }

    partial void OnConcurrencyChanged(double value)
    {
        if (_loadingConcurrency || double.IsNaN(value)) return; // NumberBox 清空时为 NaN：不保存
        _ = SaveConcurrencyAsync(value);
    }

    private async Task SaveConcurrencyAsync(double value)
    {
        await _saveGate.WaitAsync(); // 串行化各处保存：自动保存与按钮保存不并发写库
        try
        {
            var n = Math.Max(1, (int)Math.Round(value));
            await _settings.SetConcurrencyAsync(n);
            _queue.Concurrency = n;
            StatusMessage = $"已保存：并发下载数 {n}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
        finally { _saveGate.Release(); }
    }

    /// <summary>网络页「保存」：只保存代理（不连带写入其他页的字段）。</summary>
    public IAsyncRelayCommand SaveProxyCommand => _saveProxyCommand ??= new AsyncRelayCommand(SaveProxyAsync);
    private IAsyncRelayCommand? _saveProxyCommand;

    private async Task SaveProxyAsync()
    {
        await _saveGate.WaitAsync(); // 串行化各处保存：自动保存与按钮保存不并发写库
        try
        {
            await _settings.SetProxyAsync(new ProxyConfig(
                ProxyScheme,
                ProxyHost.Trim(),
                (int)Math.Round(double.IsNaN(ProxyPort) ? 0 : ProxyPort),
                string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyUsername.Trim(),
                string.IsNullOrWhiteSpace(ProxyPassword) ? null : ProxyPassword));
            StatusMessage = string.IsNullOrWhiteSpace(ProxyHost) ? "已保存：直连（不使用代理）" : "已保存代理设置";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
        finally { _saveGate.Release(); }
    }

    private async Task SaveGeneralAsync()
    {
        await _saveGate.WaitAsync(); // 串行化各处保存：自动保存与按钮保存不并发写库
        try
        {
            var n = Math.Max(1, (int)Math.Round(Concurrency)); // <1 钳制（裁定 4）
            await _settings.SetConcurrencyAsync(n);
            _queue.Concurrency = n; // 保存通用设置同步 setter（裁定 4）
            if (double.IsFinite(DownloadRateLimitKiB))
                await _settings.SetDownloadRateLimitAsync(RateLimitBytes(DownloadRateLimitKiB));
            if (!string.IsNullOrWhiteSpace(DownloadDirectory))
                await _settings.SetDownloadDirectoryAsync(DownloadDirectory);
            await _settings.SetProxyAsync(new ProxyConfig(
                ProxyScheme,
                ProxyHost.Trim(),
                (int)Math.Round(ProxyPort),
                string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyUsername.Trim(),
                string.IsNullOrWhiteSpace(ProxyPassword) ? null : ProxyPassword));
            StatusMessage = "通用设置已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
        finally { _saveGate.Release(); }
    }
}
