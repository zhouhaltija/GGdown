using GGdown.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GGdown.App.Views;

public sealed partial class DownloadsPage : Page
{
    public DownloadsViewModel Vm { get; private set; } = null!;

    public DownloadsPage()
    {
        // 同 UsersPage（B3 控制器裁定 1）：构造函数解析 VM 并在 InitializeComponent 前赋 Vm（x:Bind 初始求值需要）
        Vm = App.Current.Services.GetRequiredService<DownloadsViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await App.Readiness; // 控制器裁定 5：等建库+恢复完成后再 Start（async void 包 try/catch，B4 模式）
            // Task 10：平台页组导航（IsGlobalDownloads=false）复位全局视图；栏底入口进入时置位
            var nav = App.Current.Services.GetRequiredService<MainNavViewModel>();
            Vm.SetGlobalView(nav.IsGlobalDownloads);
            Vm.Start();
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Vm.Stop(); // 与 Start 对称退订（页面缓存 NavigationCacheMode=Enabled，Start/Stop 随导航成对触发）
    }
}
