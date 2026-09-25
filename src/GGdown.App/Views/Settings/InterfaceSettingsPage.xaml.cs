using GGdown.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GGdown.App.Views.Settings;

public sealed partial class InterfaceSettingsPage : Page
{
    public InterfaceSettingsViewModel Vm { get; private set; } = null!;

    public InterfaceSettingsPage()
    {
        Vm = App.Current.Services.GetRequiredService<InterfaceSettingsViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await App.Readiness;
            await Vm.StartAsync();
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = $"加载失败：{ex.Message}";
        }
    }
}
