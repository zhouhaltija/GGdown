using GGdown.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GGdown.App.Views.Settings;

public sealed partial class AboutPage : Page
{
    public GlobalSettingsViewModel Vm { get; private set; } = null!;

    public AboutPage()
    {
        Vm = App.Current.Services.GetRequiredService<GlobalSettingsViewModel>();
        InitializeComponent();
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
}
