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
/// Task 9：平台设置页 VM 测试（自 SettingsViewModelTests 拆出——账号卡 + 站点选项）。
/// 播种活动账号 Status=Ok（ScreenName=alice/DisplayName=Alice）；无账号用例先 ExecuteDeleteAsync。
/// </summary>
public class SiteSettingsViewModelTests : IDisposable
{
    private readonly (SqliteConnection, GGdownSiteDbContext) _t;
    private readonly (SqliteConnection, GGdownGlobalDbContext) _global;
    private readonly AppPaths _paths = TestPaths.Create();
    private readonly FakeEngine _engine = new();
    private readonly GGdownSiteDbContext _db;
    private readonly AppSettings _settings;
    private readonly SiteRegistry _sites;
    private readonly SiteSettingsViewModel _vm;

    public SiteSettingsViewModelTests()
    {
        _t = TestDb.CreateSite();
        _global = TestDb.CreateGlobal();
        _db = _t.Item2;
        _db.Accounts.Add(new Account
        {
            SiteId = "twitter", CookiePath = "c", ScreenName = "alice", DisplayName = "Alice",
            Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        _sites = new SiteRegistry([new TwitterSiteProvider(), new PixivSiteProvider()]);
        _settings = new AppSettings(new SingleGlobalDbContextFactory(_global.Item2), _sites,
            new SiteDbContextFactory(_paths));
        _vm = new SiteSettingsViewModel(_settings,
            new AccountService(new SingleSiteDbContextFactory(_db), _engine, _paths, _sites, NullLogger<AccountService>.Instance),
            new AccountQueryService(new SingleSiteDbContextFactory(_db)),
            _sites, new SyncDispatcher(), new FakeCurrentSite());
    }

    public void Dispose()
    {
        _t.Item1.Dispose();
        _global.Item1.Dispose();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [Fact]
    public async Task Queued_old_site_load_cannot_replace_new_site_account_or_schema()
    {
        var site = new FakeCurrentSite();
        var dispatcher = new BufferedDispatcher();
        var sites = new SiteRegistry([new TwitterSiteProvider(), new PixivSiteProvider()]);
        var vm = new SiteSettingsViewModel(_settings,
            new AccountService(new SingleSiteDbContextFactory(_db), _engine, _paths, sites, NullLogger<AccountService>.Instance),
            new AccountQueryService(new SingleSiteDbContextFactory(_db)), sites, dispatcher, site);
        await vm.StartAsync();
        await site.SelectAsync("pixiv");
        await vm.StartAsync();
        dispatcher.DrainNewestFirst();
        Assert.Null(vm.ActiveAccount);
        Assert.DoesNotContain(vm.SiteOptions, o => o.Key == "videos");
    }

    [Fact]
    public async Task Pending_auto_save_writes_original_site_after_switch()
    {
        var site = new FakeCurrentSite();
        var sites = new SiteRegistry([new TwitterSiteProvider(), new PixivSiteProvider()]);
        var vm = new SiteSettingsViewModel(_settings,
            new AccountService(new SingleSiteDbContextFactory(_db), _engine, _paths, sites, NullLogger<AccountService>.Instance),
            new AccountQueryService(new SingleSiteDbContextFactory(_db)), sites, new SyncDispatcher(), site)
            { AutoSaveDelay = TimeSpan.FromMilliseconds(10) };
        await vm.StartAsync();
        vm.SiteOptions.Single(o => o.Key == "videos").BoolValue = false;
        await site.SelectAsync("pixiv");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        IReadOnlyDictionary<string, object?> saved;
        do
        {
            await Task.Delay(20);
            saved = await _settings.GetSiteOptionsAsync("twitter");
        } while (!Equals(saved.GetValueOrDefault("videos"), false) && DateTime.UtcNow < deadline);
        Assert.Equal(false, saved.GetValueOrDefault("videos"));
        Assert.DoesNotContain("videos", (await _settings.GetSiteOptionsAsync("pixiv")).Keys);
    }

    private sealed class BufferedDispatcher : IUiDispatcher
    {
        private readonly List<Action> _actions = [];
        public void Post(Action action) => _actions.Add(action);
        public void DrainNewestFirst()
        {
            while (_actions.Count > 0)
            {
                var action = _actions[^1];
                _actions.RemoveAt(_actions.Count - 1);
                action();
            }
        }
    }

    // ---- ⑥ 站点选项：Schema 初始化 OptionItemViewModel 集合 + 保存只含 Schema 键 ----

    [Fact]
    public async Task Shutdown_flush_returns_promptly_while_sqlite_writer_is_locked()
    {
        _vm.AutoSaveDelay = TimeSpan.FromSeconds(30);
        await _vm.StartAsync();
        _vm.SiteOptions.Single(o => o.Key == "videos").BoolValue = false;
        await using var db = await new SiteDbContextFactory(_paths).CreateAsync("twitter");
        await using var transaction = await db.Database.BeginTransactionAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Task.Run(() => returned.TrySetResult(_vm.FlushPendingSavesAsync(deadline.Token)));
        try
        {
            var flush = await returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
        }
        finally
        {
            await transaction.RollbackAsync();
            await invocation;
            await _vm.FlushPendingSavesAsync();
        }
    }

    [Fact]
    public async Task Reload_preserves_unsaved_edits_when_switching_back_to_a_site()
    {
        _vm.AutoSaveDelay = TimeSpan.FromSeconds(5);
        await _vm.StartAsync();
        _vm.SiteOptions.Single(o => o.Key == "videos").BoolValue = false;
        await _vm.StartAsync();
        var preserved = _vm.SiteOptions.Single(o => o.Key == "videos").BoolValue;
        _vm.SiteOptions.Single(o => o.Key == "sleep").TextValue = "3";
        await _vm.SaveOptionsCommand.ExecuteAsync(null);
        Assert.False(preserved);
        Assert.Equal(false, (await _settings.GetSiteOptionsAsync("twitter"))["videos"]);
    }

    [Fact]
    public async Task Start_builds_twitter_options_from_schema_with_defaults()
    {
        await _vm.StartAsync();
        var schema = new TwitterSiteProvider().OptionsSchema;
        Assert.Equal(schema.Fields.Count, _vm.SiteOptions.Count); // 数量=Schema 字段数
        Assert.True(_vm.SiteOptions.Single(o => o.Key == "videos").BoolValue);
        Assert.False(_vm.SiteOptions.Single(o => o.Key == "retweets").BoolValue);
        Assert.Equal("{tweet_id}_{author[name]}_{num}.{extension}",
            _vm.SiteOptions.Single(o => o.Key == "filename").TextValue);
        Assert.Equal("1", _vm.SiteOptions.Single(o => o.Key == "sleep").TextValue);
    }

    [Fact]
    public async Task Start_loads_saved_options_and_SaveOptions_persists_schema_keys_only()
    {
        await _settings.SetSiteOptionsAsync("twitter", new Dictionary<string, object?>
        {
            ["retweets"] = true, ["sleep"] = "3", ["bogus"] = "只存 Schema 外键不应回流",
        });
        await _vm.StartAsync();
        Assert.True(_vm.SiteOptions.Single(o => o.Key == "retweets").BoolValue);
        Assert.Equal("3", _vm.SiteOptions.Single(o => o.Key == "sleep").TextValue);

        _vm.SiteOptions.Single(o => o.Key == "videos").BoolValue = false;
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

    // 改动即自动保存（防抖后），无需点「保存」
    [Fact]
    public async Task Changing_option_auto_saves_after_debounce()
    {
        _vm.AutoSaveDelay = TimeSpan.FromMilliseconds(20);
        await _vm.StartAsync();
        _vm.SiteOptions.Single(o => o.Key == "videos").BoolValue = false;

        var deadline = Environment.TickCount + 5000;
        IReadOnlyDictionary<string, object?> saved;
        do
        {
            await Task.Delay(20);
            saved = await _settings.GetSiteOptionsAsync("twitter");
        } while (!(saved.TryGetValue("videos", out var v) && v is false) && Environment.TickCount < deadline);

        Assert.Equal(false, saved["videos"]);
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
}
