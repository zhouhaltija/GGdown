using GGdown.App.Infrastructure;
using GGdown.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GGdown.App.Views.Settings;

public sealed partial class GeneralSettingsPage : Page
{
    public GlobalSettingsViewModel Vm { get; private set; } = null!;

    private readonly FileDialogService _files;
    private readonly LauncherService _launcher;

    public GeneralSettingsPage()
    {
        Vm = App.Current.Services.GetRequiredService<GlobalSettingsViewModel>();
        _files = App.Current.Services.GetRequiredService<FileDialogService>();
        _launcher = App.Current.Services.GetRequiredService<LauncherService>();
        InitializeComponent();
        // B7 接线沿用：VM 事件 → Shell 服务（Core 不引用 UI/Shell）
        Vm.OpenFolderPickerRequested += OnOpenFolderPickerRequested;
        Vm.OpenFolderRequested += OnOpenFolderRequested;
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
}
