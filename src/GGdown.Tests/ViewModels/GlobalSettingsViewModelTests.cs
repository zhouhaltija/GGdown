using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Sites;
using GGdown.Threading;
using GGdown.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.ViewModels;

/// <summary>
/// Task 9：全局设置 VM 测试（自 SettingsViewModelTests 拆出——通用/网络/引擎/关于）。
/// 组装沿用原模式（TestDb/SingleSiteDbContextFactory/FakeEngine/TestPaths/SiteRegistry + SyncDispatcher）。
/// </summary>
public class GlobalSettingsViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GGdownSiteDbContext) _t;
    private readonly (SqliteConnection, GGdownGlobalDbContext) _global;
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine = new();
    private readonly AppSettings _settings;
    private readonly DownloadQueueService _queue;
    private readonly SiteRegistry _sites;
    private readonly GlobalSettingsViewModel _vm;

    public GlobalSettingsViewModelTests()
    {
        _t = TestDb.CreateSite();
        _global = TestDb.CreateGlobal();
        _db = _t.Item2;
        _sites = new SiteRegistry([new TwitterSiteProvider()]);
        _settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), _sites,
            new SiteDbContextFactory(_paths));
        _queue = new DownloadQueueService(new SingleSiteDbContextFactory(_db), _engine, new FakeStats(), _paths,
            _sites, NullLogger<DownloadQueueService>.Instance);
        _vm = new GlobalSettingsViewModel(_settings, _engine, _queue, _paths, new SyncDispatcher());
    }

    private readonly GGdownSiteDbContext _db;

    [Fact]
    public async Task Download_rate_loads_in_kib_and_auto_saves_changes()
    {
        await _settings.SetDownloadRateLimitAsync(524288);
        await _vm.StartAsync();
        Assert.Equal(512, _vm.DownloadRateLimitKiB);
        _vm.DownloadRateLimitKiB = 1024;
        for (var i = 0; i < 100 && await _settings.GetDownloadRateLimitAsync() != 1048576; i++)
            await Task.Delay(10);
        Assert.Equal(1048576, await _settings.GetDownloadRateLimitAsync());
        _vm.DownloadRateLimitKiB = double.NaN;
        Assert.Equal(1048576, await _settings.GetDownloadRateLimitAsync());
        _vm.DownloadRateLimitKiB = 0;
        await _vm.SaveGeneralCommand.ExecuteAsync(null);
        Assert.Equal(0, await _settings.GetDownloadRateLimitAsync());
    }

    public void Dispose()
    {
        _t.Item1.Dispose();
        _global.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    // ---- ② 并发：保存后 GetConcurrencyAsync 一致、<1 钳制、同步 queue setter（裁定 4） ----

    [Fact]
    public async Task SaveGeneral_persists_concurrency_and_applies_to_queue_setter()
    {
        _vm.Concurrency = 3;
        await _vm.SaveGeneralCommand.ExecuteAsync(null);
        Assert.Equal(3, await _settings.GetConcurrencyAsync());
        Assert.Equal(3, _queue.Concurrency);

        _vm.Concurrency = 0; // NumberBox Minimum=1 拦不住 VM 直设——VM 侧钳制到 1
        await _vm.SaveGeneralCommand.ExecuteAsync(null);
        Assert.Equal(1, await _settings.GetConcurrencyAsync());
        Assert.Equal(1, _queue.Concurrency);
    }

    [Fact]
    public async Task Start_applies_concurrency_to_queue_and_loads_directory()
    {
        await _settings.SetConcurrencyAsync(2);
        await _vm.StartAsync();
        Assert.Equal(2, _vm.Concurrency);
        Assert.Equal(2, _queue.Concurrency); // 启动即应用（裁定 4）
        Assert.Equal(AppSettings.DefaultDownloadDirectory, _vm.DownloadDirectory);
    }

    // ---- ③ 代理：保存 round-trip + 重开应用重载 ----

    [Fact]
    public async Task SaveGeneral_persists_proxy_and_Start_reloads_it()
    {
        await _vm.StartAsync();
        _vm.ProxyScheme = "socks5h";
        _vm.ProxyHost = "127.0.0.1";
        _vm.ProxyPort = 1080;
        _vm.ProxyUsername = "u";
        _vm.ProxyPassword = "p@ss";
        await _vm.SaveGeneralCommand.ExecuteAsync(null);

        var saved = await _settings.GetProxyAsync();
        Assert.Equal("socks5h://u:p%40ss@127.0.0.1:1080", saved.ToUrl());

        var vm2 = new GlobalSettingsViewModel(_settings, _engine, _queue, _paths, new SyncDispatcher());
        await vm2.StartAsync();
        Assert.Equal("socks5h", vm2.ProxyScheme);
        Assert.Equal("127.0.0.1", vm2.ProxyHost);
        Assert.Equal(1080, vm2.ProxyPort);
        Assert.Equal("u", vm2.ProxyUsername);
        Assert.Equal("p@ss", vm2.ProxyPassword);
    }

    [Fact]
    public async Task SetDownloadDirectory_persists_and_updates_display()
    {
        var dir = Path.Combine(_paths.Root, "dl-custom");
        await _vm.SetDownloadDirectoryAsync(dir);
        Assert.Equal(dir, _vm.DownloadDirectory);
        Assert.Equal(dir, await _settings.GetDownloadDirectoryAsync());

        await _vm.SetDownloadDirectoryAsync(""); // 空路径（用户取消）不动作
        Assert.Equal(dir, _vm.DownloadDirectory);
    }

    // ---- ⑤ 引擎版本：HelloAsync 正常/未知版本/异常三路径（裁定 6） ----

    [Fact]
    public async Task Start_shows_engine_version_from_hello()
    {
        _engine.Hello = new EngineHello(1, "runner-9", "12.4.1");
        await _vm.StartAsync();
        Assert.Contains("gallery-dl 12.4.1", _vm.EngineVersion);
        Assert.Contains("runner runner-9", _vm.EngineVersion);
    }

    [Fact]
    public async Task Start_shows_placeholder_when_gallerydl_version_missing()
    {
        _engine.Hello = new EngineHello(1, "runner-9", null);
        await _vm.StartAsync();
        Assert.Contains("gallery-dl 未知", _vm.EngineVersion);
    }

    /// <summary>HelloAsync 注入异常的包装引擎（其余转发 FakeEngine）——引擎卡失败文案用。</summary>
    private sealed class HelloErrorEngine(Exception error) : IDownloadEngine
    {
        private readonly FakeEngine _inner = new();
        public Task<EngineHello> HelloAsync(CancellationToken ct = default) => Task.FromException<EngineHello>(error);
        public Task<AccountInfo> WhoAmIAsync(string siteId, string cookiesFile, CancellationToken ct = default) => _inner.WhoAmIAsync(siteId, cookiesFile, ct);
        public Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(string siteId, string cookiesFile, CancellationToken ct = default) => _inner.ListFollowingAsync(siteId, cookiesFile, ct);
        public Task<SiteUserInfo> GetUserInfoAsync(string siteId, string cookiesFile, string input, CancellationToken ct = default) => _inner.GetUserInfoAsync(siteId, cookiesFile, input, ct);
        public Task DownloadAsync(DownloadPlan plan, string cookiesFile, IProgress<EngineEvent> progress, CancellationToken ct = default) => _inner.DownloadAsync(plan, cookiesFile, progress, ct);
    }

    [Fact]
    public async Task Start_engine_failure_shows_not_ready_message()
    {
        var vm = new GlobalSettingsViewModel(_settings,
            new HelloErrorEngine(new EngineException("python 未找到")),
            _queue, _paths, new SyncDispatcher());
        await vm.StartAsync();
        Assert.StartsWith("引擎未就绪", vm.EngineVersion);
        Assert.Contains("python 未找到", vm.EngineVersion);
    }

    // ---- 事件解耦与关于卡 ----

    [Fact]
    public void OpenLogs_requests_logs_dir()
    {
        string? received = null;
        _vm.OpenFolderRequested += p => received = p;
        _vm.OpenLogsCommand.Execute(null);
        Assert.Equal(_paths.LogsDir, received);
    }

    [Fact]
    public void Browse_raises_folder_picker_request()
    {
        var fired = false;
        _vm.OpenFolderPickerRequested += () => fired = true;
        _vm.BrowseDownloadDirectoryCommand.Execute(null);
        Assert.True(fired);
    }

    [Fact]
    public void AppVersion_is_three_part_version()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", _vm.AppVersion); // Assembly 版本 ToString(3)
    }
}
