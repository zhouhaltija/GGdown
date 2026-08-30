using GalleryGUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GalleryGUI.App.Views;

public sealed partial class UsersPage : Page
{
    public UsersViewModel Vm { get; private set; } = null!;

    public UsersPage()
    {
        // 控制器裁定 1：构造函数中解析 VM 并在 InitializeComponent 前赋 Vm（x:Bind 初始求值需要）
        Vm = App.Current.Services.GetRequiredService<UsersViewModel>();
        InitializeComponent();
        // B4 接线：占位留空，对话框落地后转 ContentDialog
        Vm.ShowAddUserRequested += () => { };
        Vm.ShowImportCookieRequested += () => { };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await App.Readiness; // 控制器裁定 2：等建库+恢复完成后再首刷
        await Vm.RefreshCommand.ExecuteAsync(null);
    }
}
