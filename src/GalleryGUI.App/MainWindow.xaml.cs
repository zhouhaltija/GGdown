using GalleryGUI.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GalleryGUI.App;

public sealed partial class MainWindow : Window
{
    private readonly IServiceProvider _services;

    /// <summary>Picker 属主句柄（B4 控制器裁定：FileDialogService.SetOwner 消费，App.OnLaunched 注入）。</summary>
    public IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>ContentDialog.ShowAsync 前必须设置的 XamlRoot（B4 对话框消费，brief 定稿命名 DialogXamlRoot）。</summary>
    public XamlRoot DialogXamlRoot => Content.XamlRoot;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ContentFrame.Navigate(typeof(UsersPage), services);
        Nav.SelectedItem = Nav.MenuItems[0];
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
