using System.Collections.Specialized;
using System.ComponentModel;
using GGdown.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GGdown.App.Views.Dialogs;

public sealed partial class FollowingListDialog : ContentDialog
{
    private ScrollViewer? _scroll;
    private bool _resetScroll;

    public FollowingPickerViewModel Vm { get; }

    public FollowingListDialog(FollowingPickerViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        Vm.PropertyChanged += OnVmPropertyChanged;
        Vm.VisibleItems.CollectionChanged += OnVisibleItemsChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scroll ??= FindDescendant<ScrollViewer>(FollowingList);
        await Vm.LoadCommand.ExecuteAsync(null);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FollowingPickerViewModel.CanAddSelected)
            or nameof(FollowingPickerViewModel.SelectedCount)
            or nameof(FollowingPickerViewModel.IsBusy))
            IsPrimaryButtonEnabled = Vm.CanAddSelected && !Vm.IsBusy;

        if (e.PropertyName is nameof(FollowingPickerViewModel.SearchText)
            or nameof(FollowingPickerViewModel.SelectedFilterIndex))
        {
            // 搜索词或筛选条件变化后结果集整体换掉，回到顶部更符合预期，也让下面的锚点逻辑不参与
            _resetScroll = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _resetScroll = false;
                _scroll?.ChangeView(null, 0, null, true);
            });
        }
    }

    /// <summary>
    /// 忽略/恢复只会删掉一行。ListView 删行时会把后面的内容整体上移，
    /// 这里记下视口顶部那一行，删除后把它拉回原来的位置，让上方内容保持不动。
    /// </summary>
    private void OnVisibleItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Remove || _resetScroll) return;
        if (_scroll is null || FollowingList.ItemsPanelRoot is not { } panel) return;

        var offset = _scroll.VerticalOffset;
        var remaining = new HashSet<FollowingRowViewModel>(Vm.VisibleItems);
        ListViewItem? anchor = null;
        var anchorTop = 0d;
        foreach (var child in panel.Children)
        {
            if (child is not ListViewItem item || item.Content is not FollowingRowViewModel row) continue;
            if (!remaining.Contains(row)) continue;
            var top = TopInPanel(item, panel);
            if (top + item.ActualHeight <= offset) continue;
            anchor = item;
            anchorTop = top;
            break;
        }
        if (anchor?.Content is not FollowingRowViewModel anchorRow) return;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_scroll is null || FollowingList.ItemsPanelRoot is not { } current) return;
            FollowingList.UpdateLayout();
            if (FollowingList.ContainerFromItem(anchorRow) is not ListViewItem container) return;
            var delta = TopInPanel(container, current) - anchorTop;
            if (Math.Abs(delta) < 0.5) return;
            _scroll.ChangeView(null, _scroll.VerticalOffset + delta, null, true);
        });
    }

    private static double TopInPanel(UIElement element, UIElement panel)
        => element.TransformToVisual(panel).TransformPoint(default).Y;

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
