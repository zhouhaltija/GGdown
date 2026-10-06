using System.IO;
using GGdown.App.Views;
using GGdown.App.Views.Settings;
using GGdown.Data; // JobStatus（通知与徽标计数用）
using GGdown.Services;
using GGdown.Sites;
using GGdown.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GGdown.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<SitePageKey, Type> SitePages = new()
    {
        [SitePageKey.Users] = typeof(UsersPage),
        [SitePageKey.Downloads] = typeof(DownloadsPage),
        [SitePageKey.History] = typeof(HistoryPage),
        [SitePageKey.SiteSettings] = typeof(SiteSettingsPage), // 平台「账号与选项」页
    };

    private static readonly Dictionary<GlobalSettingsPageKey, Type> GlobalSettingsPages = new()
    {
        [GlobalSettingsPageKey.General] = typeof(GeneralSettingsPage),
        [GlobalSettingsPageKey.Network] = typeof(NetworkSettingsPage),
        [GlobalSettingsPageKey.Engine] = typeof(EngineSettingsPage),
        [GlobalSettingsPageKey.Interface] = typeof(InterfaceSettingsPage),
        [GlobalSettingsPageKey.About] = typeof(AboutPage),
    };

    private readonly IServiceProvider _services;
    private readonly IDownloadQueueService _queue;
    private readonly DispatcherQueue _dispatcherQueue; // 队列事件自后台线程触发，UI 操作一律 TryEnqueue 到本窗口 UI 线程
    private DispatcherQueueTimer? _autoCloseTimer;     // 通知自动关闭计时器（仅 UI 线程访问）
    private bool _syncing;                             // SyncChrome 回写控件选中态时屏蔽 SelectionChanged 反向驱动

    // 批次完成汇总：队列排空时一次性提示，而不是每个任务弹一次（仅 UI 线程访问）
    private int _batchCompleted;
    private int _batchFailed;

    public MainNavViewModel Nav { get; }

    /// <summary>Picker 属主句柄（FileDialogService.SetOwner 消费，App.OnLaunched 注入）。</summary>
    public IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>ContentDialog.ShowAsync 前必须设置的 XamlRoot。</summary>
    public XamlRoot DialogXamlRoot => Content.XamlRoot;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        Nav = services.GetRequiredService<MainNavViewModel>();
        // 构造发生在 App.OnLaunched（UI 线程），GetForCurrentThread 取到的即本窗口 UI 线程的队列
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // —— 导航接线：VM 发意图，本类映射页面类型并导航 ——
        Nav.TabNavigationRequested += k =>
        {
            Navigate(SitePages[k]);
            SyncChrome(); // 页面内的快捷入口也要同步顶部标签选中态。
        };
        Nav.GlobalTabNavigationRequested += k => Navigate(GlobalSettingsPages[k]);
        Nav.GlobalSettingsNavigationRequested += () => Navigate(typeof(GeneralSettingsPage)); // 进设置默认落通用页
        Nav.CurrentTabs.CollectionChanged += (_, _) => RebuildTabs();
        Nav.PropertyChanged += (_, e) =>
        {
            // 全局下载开关直推下载页 VM：同页类型导航会短路 OnNavigatedTo，不能只靠页面同步
            if (e.PropertyName == nameof(Nav.IsGlobalDownloads))
                services.GetRequiredService<DownloadsViewModel>().SetGlobalView(Nav.IsGlobalDownloads);
        };

        // 全局库就绪即载入侧栏，并恢复上次停留的平台与页面（页面内部再等 App.Readiness）
        _ = App.GlobalReadiness.ContinueWith(_ =>
        {
            _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await Nav.StartAsync();
                    if (await Nav.RestoreLastPlatformAsync() is not null)
                        NavigateInitialPage();
                    SyncChrome();
                }
                catch (Exception ex) { Serilog.Log.Error(ex, "载入平台栏失败"); }
            });
        }, TaskScheduler.Default);

        // 全局通知 + 栏底指示器：应用生命周期订阅、不退订（窗口与应用同生命周期）
        _queue = services.GetRequiredService<IDownloadQueueService>();
        _queue.AccountInvalid += OnAccountInvalid;
        _queue.JobChanged += OnJobChanged;
        _queue.JobRemoved += OnJobRemoved;
        StatusHub.Published += OnStatusPublished;
    }

    // —— 导航 ——

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        Nav.IsSidebarExpanded = !Nav.IsSidebarExpanded;
        PlatformRailColumn.Width = new GridLength(Nav.IsSidebarExpanded ? 192 : 72);
        var labelVisibility = Nav.IsSidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
        GlobalDownloadsLabel.Visibility = labelVisibility;
        GlobalSettingsLabel.Visibility = labelVisibility;
        var label = Nav.IsSidebarExpanded ? "收起侧栏" : "展开侧栏";
        ToolTipService.SetToolTip(SidebarToggleButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SidebarToggleButton, label);
    }

    private void Navigate(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, _services);
    }

    /// <summary>换平台后的首个页面：页组中选中标签对应的页（无选中回 Users）。</summary>
    private void NavigateInitialPage()
    {
        var selectedTab = Nav.CurrentTabs.FirstOrDefault(t => t.IsSelected);
        var page = selectedTab?.Key switch
        {
            SitePageKey siteKey when SitePages.TryGetValue(siteKey, out var p) => p,
            GlobalSettingsPageKey globalKey when GlobalSettingsPages.TryGetValue(globalKey, out var p) => p,
            _ => typeof(UsersPage),
        };
        Navigate(page);
    }

    /// <summary>
    /// 把壳层控件对齐到 VM 当前状态：侧栏选中项、页组标签、标题、栏底入口高亮、空状态提示。
    /// 每条导航路径末尾都调用，保证"显示的位置 = 实际所在的位置"。
    /// </summary>
    private void SyncChrome()
    {
        _syncing = true;
        try
        {
            var selected = Nav.Platforms.FirstOrDefault(p => p.IsSelected);
            if (!ReferenceEquals(PlatformList.SelectedItem, selected))
                PlatformList.SelectedItem = selected; // null 时清空高亮（进入全局下载/设置）
            RebuildTabs();
        }
        finally { _syncing = false; }

        HeaderTitle.Text = Nav.IsGlobalSettings ? "设置"
            : Nav.IsGlobalDownloads ? "全部下载"
            : Nav.Platforms.FirstOrDefault(p => p.IsSelected)?.DisplayName ?? "";
        GlobalDownloadsIndicator.Visibility = Nav.IsGlobalDownloads ? Visibility.Visible : Visibility.Collapsed;
        GlobalSettingsIndicator.Visibility = Nav.IsGlobalSettings ? Visibility.Visible : Visibility.Collapsed;
        var nothingSelected = !Nav.IsGlobalSettings && !Nav.IsGlobalDownloads
            && Nav.Platforms.All(p => !p.IsSelected);
        EmptyHint.Visibility = nothingSelected && Nav.Platforms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ContentFrame.Visibility = nothingSelected ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>按 Nav.CurrentTabs 重建 SelectorBar 条目（SelectorBar 无 ItemsSource）。</summary>
    private void RebuildTabs()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            PageTabs.Items.Clear();
            SelectorBarItem? selected = null;
            foreach (var tab in Nav.CurrentTabs)
            {
                var item = new SelectorBarItem { Text = tab.Title, Tag = tab };
                PageTabs.Items.Add(item);
                if (tab.IsSelected) selected = item;
            }
            PageTabs.SelectedItem = selected;
            PageTabs.Visibility = Nav.CurrentTabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _syncing = wasSyncing; }
    }

    private async void PlatformList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || PlatformList.SelectedItem is not PlatformItemViewModel item) return;
        try
        {
            await Nav.SelectPlatformAsync(item.SiteId);
            NavigateInitialPage();
            SyncChrome();
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "切换平台失败"); }
    }

    private void PageTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncing || sender.SelectedItem?.Tag is not PageTabViewModel tab) return;
        // Key 类型区分平台页/全局设置页（页组标签两模式共用）
        switch (tab.Key)
        {
            case SitePageKey siteKey:
                _ = Nav.SelectTabAsync(siteKey); // TabNavigationRequested → Navigate
                break;
            case GlobalSettingsPageKey globalKey:
                _ = Nav.SelectGlobalTabAsync(globalKey); // GlobalTabNavigationRequested → Navigate
                break;
        }
    }

    private async void GlobalSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await Nav.EnterGlobalSettingsAsync();
        SyncChrome();
    }

    private void GlobalDownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        Nav.EnterGlobalDownloads(); // IsGlobalDownloads 变更经 PropertyChanged 推给下载页 VM
        Navigate(typeof(DownloadsPage));
        SyncChrome();
    }

    /// <summary>跳到指定平台的指定页（通知里的"去重新导入"等）：走与用户点击相同的 VM 路径。</summary>
    private async Task GoToSitePageAsync(string siteId, SitePageKey key)
    {
        if (Nav.Platforms.All(p => !string.Equals(p.SiteId, siteId, StringComparison.OrdinalIgnoreCase)))
        {
            Navigate(SitePages[key]); // 平台被隐藏：退化为直接打开页面
            return;
        }
        await Nav.SelectPlatformAsync(siteId);
        await Nav.SelectTabAsync(key); // 持久化 lastPage 并发导航意图
        SyncChrome();
    }

    // ---- 通知 ----

    private void OnStatusPublished(NoticeLevel level, string message)
        => _dispatcherQueue.TryEnqueue(() =>
        {
            var (severity, seconds) = level switch
            {
                NoticeLevel.Success => (InfoBarSeverity.Success, 4),
                NoticeLevel.Warning => (InfoBarSeverity.Warning, 6),
                NoticeLevel.Error => (InfoBarSeverity.Error, 0), // 错误不自动关闭
                _ => (InfoBarSeverity.Informational, 4),
            };
            ShowNotice(severity, message, showGoSettings: false, autoCloseSeconds: seconds);
        });

    private void OnAccountInvalid(long accountId, string reason)
        => _dispatcherQueue.TryEnqueue(() => ShowNotice(
            InfoBarSeverity.Error,
            $"登录态失效，请重新导入 Cookie：{reason}",
            showGoSettings: true)); // 错误通知不自动关闭，须人工处置/关闭

    private void OnJobChanged(JobSnapshot snapshot) => UpdateActiveCountBadge();

    private void OnJobRemoved(JobSnapshot snapshot)
    {
        var remaining = _queue.Active.Count(s => s.Status is JobStatus.Running or JobStatus.Pending);
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (snapshot.Status == JobStatus.Completed) _batchCompleted++;
            else if (snapshot.Status == JobStatus.Failed) _batchFailed++;
            if (remaining > 0) return;
            // 队列排空：汇总一次，避免批量下载时每个任务弹一条
            var (done, failed) = (_batchCompleted, _batchFailed);
            _batchCompleted = _batchFailed = 0;
            if (done == 0 && failed == 0) return; // 只有取消的任务：无需提示
            if (failed > 0)
                ShowNotice(InfoBarSeverity.Warning,
                    $"下载结束：完成 {done} 个，失败 {failed} 个。失败原因见「下载」页的最近完成",
                    showGoSettings: false, autoCloseSeconds: 8);
            else
                ShowNotice(InfoBarSeverity.Success,
                    done == 1 ? $"任务完成：{snapshot.Title}" : $"全部 {done} 个任务已完成",
                    showGoSettings: false, autoCloseSeconds: 5);
        });
        UpdateActiveCountBadge();
    }

    /// <summary>栏底徽标：进行中（Running/Pending）任务总数；0 时隐藏。</summary>
    private void UpdateActiveCountBadge()
    {
        var count = _queue.Active.Count(s => s.Status is JobStatus.Running or JobStatus.Pending);
        _dispatcherQueue.TryEnqueue(() =>
        {
            Nav.ActiveDownloadCount = count;
            ActiveCountText.Text = count > 99 ? "99+" : count.ToString();
            ActiveCountBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    /// <summary>新通知覆盖旧内容（同一 InfoBar 实例整体覆写）；autoCloseSeconds&gt;0 时定时自动关闭。</summary>
    private void ShowNotice(InfoBarSeverity severity, string message, bool showGoSettings, int autoCloseSeconds = 0)
    {
        // 未处理的登录失效提示优先：普通反馈不覆盖它（否则"去重新导入"按钮会被顶掉）
        if (!showGoSettings && NoticeBar.IsOpen && GoSettingsButton.Visibility == Visibility.Visible
            && severity != InfoBarSeverity.Error)
            return;
        StopAutoCloseTimer(); // 新通知接管：作废上一条可能残留的自动关闭计时器
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        GoSettingsButton.Visibility = showGoSettings ? Visibility.Visible : Visibility.Collapsed;
        NoticeBar.IsOpen = true;

        if (autoCloseSeconds <= 0) return;
        var timer = _dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(autoCloseSeconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            StopAutoCloseTimer();
            NoticeBar.IsOpen = false;
        };
        _autoCloseTimer = timer;
        timer.Start();
    }

    private void StopAutoCloseTimer()
    {
        _autoCloseTimer?.Stop();
        _autoCloseTimer = null;
    }

    private void NoticeBar_CloseButtonClick(InfoBar sender, object args)
    {
        StopAutoCloseTimer();
        sender.IsOpen = false; // 手动关闭（InfoBar 不会自动收起，需显式置 IsOpen）
    }

    private async void GoSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        StopAutoCloseTimer();
        NoticeBar.IsOpen = false;
        GoSettingsButton.Visibility = Visibility.Collapsed;
        try
        {
            // 登录态失效发生在平台上下文内：跳该平台的「账号与选项」页，并同步侧栏/标签高亮
            var siteId = Nav.Platforms.FirstOrDefault(p => p.IsSelected)?.SiteId
                ?? _services.GetRequiredService<ICurrentSite>().SiteId;
            await GoToSitePageAsync(siteId, SitePageKey.SiteSettings);
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "跳转平台设置失败"); }
    }
}
