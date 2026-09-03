using GalleryGUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace GalleryGUI.App.Views.Dialogs;

public sealed partial class FollowingListDialog : ContentDialog
{
    public FollowingPickerViewModel Vm { get; }

    public FollowingListDialog(FollowingPickerViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FollowingPickerViewModel.CanAddSelected)
                or nameof(FollowingPickerViewModel.SelectedCount)
                or nameof(FollowingPickerViewModel.IsBusy))
                IsPrimaryButtonEnabled = Vm.CanAddSelected && !Vm.IsBusy;
        };
        Loaded += async (_, _) => await Vm.LoadCommand.ExecuteAsync(null);
    }
}
