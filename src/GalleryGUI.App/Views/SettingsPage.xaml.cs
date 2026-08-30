using GalleryGUI.App.Infrastructure;
using GalleryGUI.App.Views.Dialogs;
using GalleryGUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GalleryGUI.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel Vm { get; private set; } = null!;

    private readonly FileDialogService _files;
    private readonly LauncherService _launcher;

    public SettingsPage()
    {
        // 同 UsersPage/DownloadsPage（B3 控制器裁定 1）：构造函数解析 VM 并在 InitializeComponent 前赋 Vm（x:Bind 初始求值需要）
        Vm = App.Current.Services.GetRequiredService<SettingsViewModel>();
        _files = App.Current.Services.GetRequiredService<FileDialogService>();
        _launcher = App.Current.Services.GetRequiredService<LauncherService>();
        InitializeComponent();
        // B7 接线：VM 事件 → Shell 服务（Core 不引用 UI/Shell，brief Interfaces 节）
        Vm.OpenFolderPickerRequested += OnOpenFolderPickerRequested; // 裁定 5：浏览 → PickFolderAsync → SetDownloadDirectoryAsync
        Vm.OpenFolderRequested += OnOpenFolderRequested;             // 裁定 9：打开日志 → LauncherService
        Vm.AccountChanged += OnAccountChanged;                        // 裁定 8：刷新账号卡
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await App.Readiness; // 裁定 9：等建库+恢复完成后再加载（async void 包 try/catch，B4 模式）
            Vm.Start();
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    // 浏览下载目录：FolderPicker → VM.SetDownloadDirectoryAsync（保存 + 更新显示）
    private async void OnOpenFolderPickerRequested()
    {
        try
        {
            var path = await _files.PickFolderAsync();
            if (string.IsNullOrEmpty(path)) return; // 用户取消
            await Vm.SetDownloadDirectoryAsync(path);
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = $"选择文件夹失败：{ex.Message}";
        }
    }

    private void OnOpenFolderRequested(string path)
    {
        try
        {
            _launcher.OpenFolder(path);
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = $"打开文件夹失败：{ex.Message}";
        }
    }

    // 导入/更换 Cookie 共用同一对话框流程：复用 B4 ImportCookieDialog（明文警告文案在对话框内逐字保留），
    // Primary 关闭后 → Vm.PendingCookieFile → ImportCookieCommand（VM 内导入+重载账号卡）
    private async void OnImportCookieClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ImportCookieDialog(_files) { XamlRoot = App.Current.MainWindow.DialogXamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.CookiesPath is null) return;
            Vm.PendingCookieFile = dialog.CookiesPath;
            await Vm.ImportCookieCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = ex.Message;
        }
    }

    // 账号卡刷新：VM 派生属性变更已推 x:Bind，此处全量兜底（含非 INPC 子路径）
    private void OnAccountChanged() => Bindings.Update();
}
