using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Settings;
using GGdown.Sites;
using GGdown.Threading;
using GGdown.ViewModels;
using Microsoft.Data.Sqlite; // 补 using：SqliteConnection 在 tuple 类型中未限定（同 UsersViewModelTests）
using Microsoft.EntityFrameworkCore; // 补 using：无账号用例用了 ExecuteDeleteAsync（同 UsersViewModelTests）
using Microsoft.Extensions.Logging.Abstractions;

namespace GGdown.Tests.ViewModels;

/// <summary>
/// B7 Step 1：SettingsViewModel/OptionItemViewModel 测试。组装与 UsersViewModelTests 同模式
/// （TestDb/SingleDbContextFactory/FakeEngine/TestPaths/SiteRegistry + 真服务）。
/// 播种活动账号 Status=Ok（ScreenName=alice/DisplayName=Alice）覆盖账号卡显示与重新验证正路径；
/// 无账号用例先 ExecuteDeleteAsync（ImportViewModelTests 先例）。
/// </summary>
public class SettingsViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GGdownSiteDbContext) _t;
    private readonly (SqliteConnection, GGdownGlobalDbContext) _global = TestDb.CreateGlobal();
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine = new();
    private readonly GGdownSiteDbContext _db;
    private readonly AppSettings _settings;
    private readonly DownloadQueueService _queue;
    private readonly SiteRegistry _sites;
    private readonly SettingsViewModel _vm;

    public SettingsViewModelTests()
    {
        _t = TestDb.CreateSite();
        _db = _t.Item2;
        var factory = new SingleDbContextFactory(_db);
        _db.Accounts.Add(new Account
        {
            SiteId = "twitter", CookiePath = "c", ScreenName = "alice", DisplayName = "Alice",
            Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        _sites = new SiteRegistry([new TwitterSiteProvider()]);
        _settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), _sites, new SiteDbContextFactory(_paths));
        _queue = new DownloadQueueService(factory, _engine, new FakeStats(), _paths, _sites, NullLogger<DownloadQueueService>.Instance);
        var accountSvc = new AccountService(new SingleSiteDbContextFactory(_db), _engine, _paths, _sites, NullLogger<AccountService>.Instance);
        _vm = new SettingsViewModel(_settings, accountSvc, new AccountQueryService(new SingleSiteDbContextFactory(_db)),
            _engine, _queue, _paths, _sites, new SyncDispatcher(), new FakeCurrentSite());
    }
    public void Dispose()
    {
        _t.Item1.Dispose();
        _global.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    // ---- ① OptionItemViewModel：三种 Kind 的 ToValue 往返 + 元数据映射 ----

    [Fact]
    public void OptionItem_ToValue_roundtrips_three_kinds()
    {
        var boolItem = new OptionItemViewModel(
            new OptionField("videos", OptionKind.Boolean, true, "同时下载视频和动图"), true);
        Assert.Equal("videos", boolItem.Key);
        Assert.Equal("同时下载视频和动图", boolItem.DisplayName);
        Assert.Equal(OptionKind.Boolean, boolItem.Kind);
        Assert.True(boolItem.BoolValue);
        Assert.Equal(true, boolItem.ToValue());
        boolItem.BoolValue = false;
        Assert.Equal(false, boolItem.ToValue()); // 编辑后按 Kind 回读 bool

        var textItem = new OptionItemViewModel(
            new OptionField("filename", OptionKind.Text, "{tweet_id}", "文件命名模板"), "abc_{num}");
        Assert.Equal("abc_{num}", textItem.TextValue);
        Assert.Equal("abc_{num}", textItem.ToValue());
        textItem.TextValue = "{x}_{y}";
        Assert.Equal("{x}_{y}", textItem.ToValue()); // 编辑后按 Kind 回读 string

        var choiceItem = new OptionItemViewModel(
            new OptionField("rate", OptionKind.Choice, "1", "速率", ["1", "2"]), "2");
        Assert.Equal("2", choiceItem.TextValue);
        Assert.Equal("2", choiceItem.ToValue());
        Assert.True(choiceItem.IsChoice);
        Assert.False(choiceItem.IsText);
        Assert.Equal(["1", "2"], choiceItem.Choices);
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

    // ---- ③ 下载目录：Set 后读取一致 ----

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

        var vm2 = new SettingsViewModel(_settings,
            new AccountService(new SingleSiteDbContextFactory(_db), _engine, _paths, _sites, NullLogger<AccountService>.Instance),
            new AccountQueryService(new SingleSiteDbContextFactory(_db)),
            _engine, _queue, _paths, _sites, new SyncDispatcher(), new FakeCurrentSite());
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

    // ---- ⑥ 站点选项：Schema 初始化 OptionItemViewModel 集合 + 保存只含 Schema 键 ----

    [Fact]
    public async Task Start_builds_twitter_options_from_schema_with_defaults()
    {
        await _vm.StartAsync();
        var schema = new TwitterSiteProvider().OptionsSchema;
        Assert.Equal(schema.Fields.Count, _vm.TwitterOptions.Count); // 数量=Schema 字段数
        Assert.True(_vm.TwitterOptions.Single(o => o.Key == "videos").BoolValue);
        Assert.False(_vm.TwitterOptions.Single(o => o.Key == "retweets").BoolValue);
        Assert.Equal("{tweet_id}_{author[name]}_{num}.{extension}",
            _vm.TwitterOptions.Single(o => o.Key == "filename").TextValue);
        Assert.Equal("1", _vm.TwitterOptions.Single(o => o.Key == "sleep").TextValue);
    }

    [Fact]
    public async Task Start_loads_saved_options_and_SaveOptions_persists_schema_keys_only()
    {
        await _settings.SetSiteOptionsAsync("twitter", new Dictionary<string, object?>
        {
            ["retweets"] = true, ["sleep"] = "3", ["bogus"] = "只存 Schema 外键不应回流",
        });
        await _vm.StartAsync();
        Assert.True(_vm.TwitterOptions.Single(o => o.Key == "retweets").BoolValue);
        Assert.Equal("3", _vm.TwitterOptions.Single(o => o.Key == "sleep").TextValue);

        _vm.TwitterOptions.Single(o => o.Key == "videos").BoolValue = false;
        await _vm.SaveOptionsCommand.ExecuteAsync(null);
        var saved = await _settings.GetSiteOptionsAsync("twitter");
        Assert.Equal(new TwitterSiteProvider().OptionsSchema.Fields.Count, saved.Count); // 只保存 Schema 中存在的键
        Assert.Equal(false, saved["videos"]);
        Assert.Equal(true, saved["retweets"]);
        Assert.Equal("3", saved["sleep"]);
        Assert.False(saved.ContainsKey("bogus"));
        Assert.IsType<bool>(saved["videos"]); // 值类型与 DefaultOptions 对齐（bool/string）
        Assert.IsType<string>(saved["sleep"]);
    }

    // ---- ④/⑧ 账号卡：加载/徽标/重新验证（FakeEngine.WhoAmIError 驱动）/导入 ----

    [Fact]
    public async Task Start_loads_active_account_display_fields()
    {
        await _vm.StartAsync();
        Assert.True(_vm.HasAccount);
        Assert.Equal("Alice", _vm.AccountTitle);          // DisplayName 非空优先
        Assert.Equal("@alice", _vm.AccountHandle);
        Assert.Equal("有效", _vm.AccountStatusText);
    }

    [Fact]
    public async Task Start_without_account_shows_import_state()
    {
        await _db.Accounts.ExecuteDeleteAsync();
        await _vm.StartAsync();
        Assert.False(_vm.HasAccount);
        Assert.Equal(string.Empty, _vm.AccountStatusText);
    }

    [Fact]
    public async Task VerifyAccount_ok_shows_valid_badge()
    {
        await _vm.StartAsync();
        await _vm.VerifyAccountCommand.ExecuteAsync(null);
        Assert.Contains("有效", _vm.AccountStatusText);
    }

    [Fact]
    public async Task VerifyAccount_auth_error_shows_invalid_badge()
    {
        await _vm.StartAsync();
        _engine.WhoAmIError = new AuthException("cookie 无效");
        await _vm.VerifyAccountCommand.ExecuteAsync(null);
        Assert.Contains("失效", _vm.AccountStatusText);
    }

    [Fact]
    public async Task ImportCookie_imports_and_fires_account_changed()
    {
        await _db.Accounts.ExecuteDeleteAsync();
        await _vm.StartAsync();
        Assert.False(_vm.HasAccount);
        var fired = false;
        _vm.AccountChanged += () => fired = true;

        var file = Path.Combine(_paths.TempDir, "cookies.txt");
        await File.WriteAllTextAsync(file, "# Netscape HTTP Cookie File");
        _vm.PendingCookieFile = file;
        await _vm.ImportCookieCommand.ExecuteAsync(null);

        Assert.True(fired);
        Assert.True(_vm.HasAccount);
        Assert.Contains("有效", _vm.AccountStatusText); // FakeEngine WhoAmI 正常 → 导入即验证通过
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
        var factory = new SingleDbContextFactory(_db);
        var vm = new SettingsViewModel(_settings,
            new AccountService(new SingleSiteDbContextFactory(_db), _engine, _paths, _sites, NullLogger<AccountService>.Instance),
            new AccountQueryService(new SingleSiteDbContextFactory(_db)),
            new HelloErrorEngine(new EngineException("python 未找到")),
            _queue, _paths, _sites, new SyncDispatcher(), new FakeCurrentSite());
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
