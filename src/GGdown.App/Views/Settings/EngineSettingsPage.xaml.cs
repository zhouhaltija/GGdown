using GGdown.App.Infrastructure;
using GGdown.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GGdown.App.Views.Settings;

public sealed partial class EngineSettingsPage : Page
{
    public GlobalSettingsViewModel Vm { get; private set; } = null!;

    private readonly LauncherService _launcher;

    public EngineSettingsPage()
    {
        Vm = App.Current.Services.GetRequiredService<GlobalSettingsViewModel>();
        _launcher = App.Current.Services.GetRequiredService<LauncherService>();
        InitializeComponent();
        // 打开日志文件夹（OpenLogsCommand 触发的事件在此落地 Shell 调用）
        Vm.OpenFolderRequested += OnOpenFolderRequested;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await App.Readiness;
            Vm.Start();
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = $"加载失败：{ex.Message}";
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
