using System.IO;
using GGdown.App.Views;
using GGdown.App.Views.Settings;
using GGdown.Data; // JobStatus（B8 通知与徽标计数用）
using GGdown.Services;
using GGdown.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GGdown.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<GGdown.Sites.SitePageKey, Type> SitePages = new()
    {
        [GGdown.Sites.SitePageKey.Users] = typeof(UsersPage),
        [GGdown.Sites.SitePageKey.Downloads] = typeof(DownloadsPage),
        [GGdown.Sites.SitePageKey.History] = typeof(HistoryPage),
        [GGdown.Sites.SitePageKey.SiteSettings] = typeof(SiteSettingsPage), // Task 9：账号/站点选项下沉为平台设置页
    };

    private static readonly Dictionary<GGdown.Sites.GlobalSettingsPageKey, Type> GlobalSettingsPages = new()
    {
        [GGdown.Sites.GlobalSettingsPageKey.General] = typeof(GeneralSettingsPage),
        [GGdown.Sites.GlobalSettingsPageKey.Network] = typeof(NetworkSettingsPage),
        [GGdown.Sites.GlobalSettingsPageKey.Engine] = typeof(EngineSettingsPage),
        [GGdown.Sites.GlobalSettingsPageKey.Interface] = typeof(InterfaceSettingsPage),
        [GGdown.Sites.GlobalSettingsPageKey.About] = typeof(AboutPage),
    };

    private readonly IServiceProvider _services;
    private readonly IDownloadQueueService _queue;
    private readonly DispatcherQueue _dispatcherQueue; // B8：队列事件自后台线程触发，UI 操作一律 TryEnqueue 到本窗口 UI 线程
    private DispatcherQueueTimer? _autoCloseTimer;     // B8：完成通知自动关闭计时器（仅 UI 线程访问）
    public MainNavViewModel Nav { get; }

    /// <summary>Picker 属主句柄（B4 控制器裁定：FileDialogService.SetOwner 消费，App.OnLaunched 注入）。</summary>
    public IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>ContentDialog.ShowAsync 前必须设置的 XamlRoot（B4 对话框消费，brief 定稿命名 DialogXamlRoot）。</summary>
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

        // —— Task 8 导航接线：VM 发意图，本类映射页面类型并导航 ——
        Nav.TabNavigationRequested += k => Navigate(SitePages[k]);
        Nav.GlobalTabNavigationRequested += k => Navigate(GlobalSettingsPages[k]); // Task 9：全局五页
        Nav.GlobalSettingsNavigationRequested += () => Navigate(typeof(GeneralSettingsPage)); // 进设置默认落通用页
        Nav.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Nav.IsGlobalSettings)) UpdateHeaderTitle();
        };
        // Readiness 放行后启动导航（可见平台依赖 ui.visibleSites，首查前库必须就绪）
        _ = App.Readiness.ContinueWith(_ =>
        {
            _dispatcherQueue.TryEnqueue(async () =>
            {
                await Nav.StartAsync();
                SyncPlatformSelection();
                NavigateInitialPage();
            });
        }, TaskScheduler.Default);

        // B8 全局通知 + Task 8 栏底指示器：应用生命周期订阅、不退订（窗口与应用同生命周期，控制器裁定）。
        // 事件处理先在后台线程做廉价过滤，再经 TryEnqueue 上 UI 线程。
        _queue = services.GetRequiredService<IDownloadQueueService>();
        _queue.AccountInvalid += OnAccountInvalid;
        _queue.JobChanged += OnJobChanged;
        _queue.JobRemoved += OnJobRemoved; // 终态移除也刷新徽标（完成通知经 JobChanged 的 Completed 分支）
    }

    // —— Task 8 导航 ——

    private void Navigate(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, _services);
    }

    /// <summary>启动/换平台后的首个页面：页组中选中标签对应的页（无选中回 Users）。</summary>
    private void NavigateInitialPage()
    {
        var selectedTab = Nav.CurrentTabs.FirstOrDefault(t => t.IsSelected);
        var page = selectedTab?.Key switch
        {
            GGdown.Sites.SitePageKey siteKey when SitePages.TryGetValue(siteKey, out var p) => p,
            GGdown.Sites.GlobalSettingsPageKey globalKey when GlobalSettingsPages.TryGetValue(globalKey, out var p) => p,
            _ => typeof(UsersPage),
        };
        Navigate(page);
    }

    private void UpdateHeaderTitle()
    {
        HeaderTitle.Text = Nav.IsGlobalSettings
            ? "设置"
            : Nav.Platforms.FirstOrDefault(p => p.IsSelected)?.DisplayName ?? "GGdown";
    }

    /// <summary>VM 侧选择变化同步到 PlatformList（用户点击路径由 SelectionChanged 反向驱动，双向各走一条）。</summary>
    private void SyncPlatformSelection()
    {
        var selected = Nav.Platforms.FirstOrDefault(p => p.IsSelected);
        if (!ReferenceEquals(PlatformList.SelectedItem, selected))
            PlatformList.SelectedItem = selected;
        UpdateHeaderTitle();
    }

    private void PlatformList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlatformList.SelectedItem is PlatformItemViewModel item)
            _ = Nav.SelectPlatformAsync(item.SiteId).ContinueWith(_ =>
                _dispatcherQueue.TryEnqueue(() =>
                {
                    SyncPlatformSelection();
                    SyncTabSelection();
                    NavigateInitialPage();
                }), TaskScheduler.Default);
    }

    private void PageTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageTabs.SelectedItem is not PageTabViewModel tab) return;
        // Key 类型区分平台页/全局设置页（页组标签行两模式共用）
        switch (tab.Key)
        {
            case GGdown.Sites.SitePageKey siteKey:
                _ = Nav.SelectTabAsync(siteKey); // TabNavigationRequested → Navigate
                break;
            case GGdown.Sites.GlobalSettingsPageKey globalKey:
                _ = Nav.SelectGlobalTabAsync(globalKey); // GlobalTabNavigationRequested → Navigate
                break;
        }
    }

    /// <summary>页组重建后把 ListView 选中项对齐 VM 的 IsSelected。</summary>
    private void SyncTabSelection()
    {
        var selected = Nav.CurrentTabs.FirstOrDefault(t => t.IsSelected);
        if (!ReferenceEquals(PageTabs.SelectedItem, selected))
            PageTabs.SelectedItem = selected;
    }

    private void GlobalSettingsButton_Click(object sender, RoutedEventArgs e) =>
        _ = Nav.EnterGlobalSettingsAsync();

    private void GlobalDownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        // Task 10：全局下载视图——置位后 DownloadsPage OnNavigatedTo 同步 VM 筛选；
        // 已在下载页时 Navigate 短路（同页类型不触发 OnNavigatedTo），直接推 VM（Final review Important 2b）
        Nav.IsGlobalDownloads = true;
        if (ContentFrame.CurrentSourcePageType == typeof(DownloadsPage))
        {
            if (_services.GetRequiredService<DownloadsViewModel>() is { } vm)
                vm.SetGlobalView(true);
            return;
        }
        Navigate(typeof(DownloadsPage));
    }

    // ---- B8 全局通知 ----

    private void OnAccountInvalid(long accountId, string reason)
        => _dispatcherQueue.TryEnqueue(() => ShowNotice(
            InfoBarSeverity.Error,
            $"登录态失效，请重新导入 Cookie：{reason}",
            showGoSettings: true)); // 错误通知不自动关闭，须人工处置/关闭

    private void OnJobChanged(JobSnapshot snapshot)
    {
        UpdateActiveCountBadge(); // 每次变化都刷新进行中计数（后台算好数量再上 UI 线程）
        if (snapshot.Status != JobStatus.Completed) return; // 进度事件高频，后台线程先过滤，Completed 才入队
        _dispatcherQueue.TryEnqueue(() => ShowNotice(
            InfoBarSeverity.Success,
            $"任务完成：{snapshot.Title}",
            showGoSettings: false,
            autoCloseSeconds: 5)); // 完成通知 5 秒自动关闭
    }

    private void OnJobRemoved(JobSnapshot snapshot) => UpdateActiveCountBadge();

    /// <summary>栏底徽标：进行中（Running/Pending）任务总数；0 时隐藏。</summary>
    private void UpdateActiveCountBadge()
    {
        var count = _queue.Active.Count(s => s.Status is JobStatus.Running or JobStatus.Pending);
        _dispatcherQueue.TryEnqueue(() =>
        {
            Nav.ActiveDownloadCount = count;
            ActiveCountText.Text = count.ToString();
            ActiveCountBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    /// <summary>新通知覆盖旧内容（同一 InfoBar 实例整体覆写）；autoCloseSeconds&gt;0 时定时自动关闭。</summary>
    private void ShowNotice(InfoBarSeverity severity, string message, bool showGoSettings, int autoCloseSeconds = 0)
    {
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

    private void GoSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        StopAutoCloseTimer();
        NoticeBar.IsOpen = false;
        // 登录态失效发生在平台上下文内：跳当前平台的设置页（Task 9 起为 SiteSettingsPage）
        Navigate(SitePages[GGdown.Sites.SitePageKey.SiteSettings]);
    }
}
