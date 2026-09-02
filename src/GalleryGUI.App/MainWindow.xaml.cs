using System.IO;
using GalleryGUI.App.Views;
using GalleryGUI.Data;
using GalleryGUI.Services;
using GalleryGUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GalleryGUI.App;

public sealed partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly DispatcherQueue _dispatcherQueue; // B8：队列事件自后台线程触发，UI 操作一律 TryEnqueue 到本窗口 UI 线程
    private DispatcherQueueTimer? _autoCloseTimer;     // B8：完成通知自动关闭计时器（仅 UI 线程访问）
    public SiteSwitcherViewModel SiteSwitcher { get; }

    /// <summary>Picker 属主句柄（B4 控制器裁定：FileDialogService.SetOwner 消费，App.OnLaunched 注入）。</summary>
    public IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>ContentDialog.ShowAsync 前必须设置的 XamlRoot（B4 对话框消费，brief 定稿命名 DialogXamlRoot）。</summary>
    public XamlRoot DialogXamlRoot => Content.XamlRoot;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        SiteSwitcher = services.GetRequiredService<SiteSwitcherViewModel>();
        // 构造发生在 App.OnLaunched（UI 线程），GetForCurrentThread 取到的即本窗口 UI 线程的队列
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
        ContentFrame.Navigate(typeof(UsersPage), services);
        Nav.SelectedItem = Nav.MenuItems[0];

        // B8 全局通知：应用生命周期订阅、不退订（窗口与应用同生命周期，控制器裁定）。
        // 事件处理先在后台线程做廉价过滤，再经 TryEnqueue 上 UI 线程改 InfoBar。
        var queue = services.GetRequiredService<IDownloadQueueService>();
        queue.AccountInvalid += OnAccountInvalid;
        queue.JobChanged += OnJobChanged;
    }

    private void SiteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SiteFlyout.IsOpen)
            SiteFlyout.Hide();
    }

    // ---- B8 全局通知 ----

    private void OnAccountInvalid(long accountId, string reason)
        => _dispatcherQueue.TryEnqueue(() => ShowNotice(
            InfoBarSeverity.Error,
            $"登录态失效，请重新导入 Cookie：{reason}",
            showGoSettings: true)); // 错误通知不自动关闭，须人工处置/关闭

    private void OnJobChanged(JobSnapshot snapshot)
    {
        if (snapshot.Status != JobStatus.Completed) return; // 进度事件高频，后台线程先过滤，Completed 才入队
        _dispatcherQueue.TryEnqueue(() => ShowNotice(
            InfoBarSeverity.Success,
            $"任务完成：{snapshot.Title}",
            showGoSettings: false,
            autoCloseSeconds: 5)); // 完成通知 5 秒自动关闭
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
        // 经 Nav.SelectedItem 触发 Nav_SelectionChanged → ContentFrame.Navigate(SettingsPage)
        if (Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == "settings") is { } item)
            Nav.SelectedItem = item;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (string)((NavigationViewItem)args.SelectedItem).Tag;
        var pageType = tag switch
        {
            "users" => typeof(UsersPage),
            "downloads" => typeof(DownloadsPage),
            "history" => typeof(HistoryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(UsersPage),
        };
        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, _services);
    }
}
