using GGdown.App.Infrastructure;
using GGdown.App.Views.Dialogs;
using GGdown.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GGdown.App.Views.Settings;

public sealed partial class SiteSettingsPage : Page
{
    public SiteSettingsViewModel Vm { get; private set; } = null!;

    private readonly FileDialogService _files;

    public SiteSettingsPage()
    {
        Vm = App.Current.Services.GetRequiredService<SiteSettingsViewModel>();
        _files = App.Current.Services.GetRequiredService<FileDialogService>();
        InitializeComponent();
        Vm.AccountChanged += OnAccountChanged; // 裁定 8：刷新账号卡
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await App.Readiness; // 裁定 9：等建库+恢复完成后再加载
            Vm.Start();
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    // 导入/更换 Cookie 共用同一对话框流程：复用 B4 ImportCookieDialog（明文警告文案在对话框内逐字保留），
    // Primary 关闭后 → Vm.PendingCookieFile → ImportCookieCommand（VM 内导入+重载账号卡）
    private async void OnImportCookieClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ImportCookieDialog(_files, Vm.RequiresRefreshToken) { XamlRoot = App.Current.MainWindow.DialogXamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.CookiesPath is null) return;
            Vm.PendingCookieFile = dialog.CookiesPath;
            Vm.PendingRefreshToken = dialog.RefreshToken;
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
