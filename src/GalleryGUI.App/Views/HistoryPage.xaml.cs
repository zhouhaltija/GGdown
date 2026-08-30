using GalleryGUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GalleryGUI.App.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryViewModel Vm { get; private set; } = null!;

    public HistoryPage()
    {
        // 同 UsersPage/DownloadsPage（B3 控制器裁定 1）：构造函数解析 VM 并在 InitializeComponent 前赋 Vm（x:Bind 初始求值需要）
        Vm = App.Current.Services.GetRequiredService<HistoryViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await App.Readiness; // 控制器裁定 5：等建库+恢复完成后再首查（async void 包 try/catch，B4 模式）
            await Vm.LoadFilterUsersAsync();
            await Vm.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = $"加载失败：{ex.Message}";
        }
    }
}
