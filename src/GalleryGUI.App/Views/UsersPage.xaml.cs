using GalleryGUI.App.Infrastructure;
using GalleryGUI.App.Views.Dialogs;
using GalleryGUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
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
        // B4 接线：占位事件 → ContentDialog（对话框为页面级 UI，不进 VM）
        Vm.ShowAddUserRequested += OnShowAddUserRequested;
        Vm.ShowImportCookieRequested += OnShowImportCookieRequested;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await App.Readiness; // 控制器裁定 2：等建库+恢复完成后再首刷
            await Vm.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex) // 控制器裁定 5（B3 审查 Minor）：坏库等异常不崩 UI 线程
        {
            Vm.StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    // ---- B4 对话框接线：每个对话框配一个新 ImportViewModel（transient，输入/结果消息不串台） ----

    private async void OnShowAddUserRequested()
    {
        try
        {
            var vm = App.Current.Services.GetRequiredService<ImportViewModel>();
            vm.ImportCompleted += OnImportCompleted;
            var dialog = new AddUserDialog(vm) { XamlRoot = App.Current.MainWindow.DialogXamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            // brief："DialogResult 后调 AddUserAsync"；ImportCompleted 已在成功分支触发刷新
            await vm.AddUserAsync();
            Vm.StatusMessage = vm.ResultMessage;
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = ex.Message;
        }
    }

    private async void OnShowImportCookieRequested()
    {
        try
        {
            var vm = App.Current.Services.GetRequiredService<ImportViewModel>();
            vm.ImportCompleted += OnImportCompleted;
            var dialog = new ImportCookieDialog(App.Current.Services.GetRequiredService<FileDialogService>())
            {
                XamlRoot = App.Current.MainWindow.DialogXamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.CookiesPath is null) return;
            await vm.ImportCookiesAsync(dialog.CookiesPath);
            Vm.StatusMessage = vm.ResultMessage;
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = ex.Message;
        }
    }

    // 空状态 ①："导入 Cookie"按钮 → 与工具栏同一对话框流程
    private void OnEmptyStateImportCookieClick(object sender, RoutedEventArgs e)
        => OnShowImportCookieRequested();

    // 空状态 ③："导入关注列表"按钮 → ImportViewModel.ImportFollowingAsync（无账号时按钮禁用，见 XAML）
    private async void OnEmptyStateImportFollowingClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = App.Current.Services.GetRequiredService<ImportViewModel>();
            vm.ImportCompleted += OnImportCompleted;
            await vm.ImportFollowingAsync();
            Vm.StatusMessage = vm.ResultMessage;
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = ex.Message;
        }
    }

    // 任一导入成功（VM 经 dispatcher 投递）→ 刷新用户列表（brief Step 4：ImportCompleted → RefreshCommand）
    private async void OnImportCompleted()
    {
        try
        {
            await Vm.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = ex.Message;
        }
    }
}
