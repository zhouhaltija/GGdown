using System.ComponentModel;
using GGdown.App.Infrastructure;
using GGdown.App.Views.Dialogs;
using GGdown.ViewModels;
using GGdown.Sites;
using CommunityToolkit.WinUI.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace GGdown.App.Views;

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
        Vm.ShowSingleVideoDownloadRequested += OnShowSingleVideoDownloadRequested;
        Vm.ShowImportCookieRequested += OnShowImportCookieRequested;
        Vm.ShowFollowingListRequested += OnShowFollowingListRequested;
        Vm.EditUserDateRequested += OnEditUserDateRequested;
        Vm.ConfirmDeleteAsync = ConfirmDeleteAsync;
    }

    private async void OnShowSingleVideoDownloadRequested()
    {
        try
        {
            await App.Readiness;
            var nav = App.Current.Services.GetRequiredService<MainNavViewModel>();
            await nav.SelectTabAsync(SitePageKey.Downloads);
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = $"打开下载页失败：{ex.Message}";
        }
    }

    // 删除确认：删的是库里的用户记录，已下载的文件不受影响
    private async Task<bool> ConfirmDeleteAsync(int count)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = App.Current.MainWindow.DialogXamlRoot,
            Title = count == 1 ? "删除这个用户？" : $"删除 {count} 个用户？",
            Content = "将从列表中移除，已下载到磁盘的文件不会被删除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnEditUserDateRequested(UserRowViewModel row)
    {
        try
        {
            var picker = new CalendarDatePicker { PlaceholderText = "选择最早发布日期" };
            if (row.Model.DownloadSince is { } since)
                picker.Date = new DateTimeOffset(since.Year, since.Month, since.Day, 0, 0, 0, TimeSpan.Zero);
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = "只下载此日期及之后发布的作品。未单独设置时使用抖音站点选项中的日期。",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(picker);
            var dialog = new ContentDialog
            {
                XamlRoot = App.Current.MainWindow.DialogXamlRoot,
                Title = $"{row.Title} · 日期限制",
                Content = content,
                PrimaryButtonText = "保存",
                SecondaryButtonText = "使用站点默认",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
                await Vm.SetUserDownloadSinceAsync(row.Model.Id, null);
            else if (result == ContentDialogResult.Primary && picker.Date is { } selected)
                await Vm.SetUserDownloadSinceAsync(row.Model.Id, DateOnly.FromDateTime(selected.DateTime));
            else if (result == ContentDialogResult.Primary)
                Vm.StatusMessage = "请选择日期，或使用站点默认";
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = $"设置日期限制失败：{ex.Message}";
        }
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

    private async void OnPasteFromClipboardClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                Vm.StatusMessage = "剪贴板里没有文本";
                return;
            }
            var text = await content.GetTextAsync();
            var vm = App.Current.Services.GetRequiredService<ImportViewModel>();
            vm.ImportCompleted += OnImportCompleted;
            vm.UserInput = text;
            if (string.IsNullOrWhiteSpace(vm.UserInputError) && !string.IsNullOrWhiteSpace(text))
            {
                await vm.AddUserAsync();
                Vm.StatusMessage = vm.ResultMessage;
                return;
            }
            var dialog = new AddUserDialog(vm) { XamlRoot = App.Current.MainWindow.DialogXamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await vm.AddUserAsync();
            Vm.StatusMessage = vm.ResultMessage;
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = ex.Message;
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
            var dialog = new ImportCookieDialog(App.Current.Services.GetRequiredService<FileDialogService>(), vm.RequiresRefreshToken)
            {
                XamlRoot = App.Current.MainWindow.DialogXamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.CookiesPath is null) return;
            await vm.ImportCookiesAsync(dialog.CookiesPath, dialog.RefreshToken);
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

    private void OnEmptyStateFollowingListClick(object sender, RoutedEventArgs e)
        => OnShowFollowingListRequested();

    private async void OnShowFollowingListRequested()
    {
        try
        {
            var picker = App.Current.Services.GetRequiredService<FollowingPickerViewModel>();
            picker.ImportCompleted += OnImportCompleted;
            var dialog = new FollowingListDialog(picker) { XamlRoot = App.Current.MainWindow.DialogXamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await picker.AddSelectedAsync();
            Vm.StatusMessage = picker.ResultMessage;
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

    // ---- F4：排序下拉 ----

    // SelectedIndex 0/1/2 → SortBy 字符串；变更经 VM 与 SearchText 同机制的 300ms 防抖刷新。
    // 初始 SelectedIndex=0 触发的 SelectionChanged 赋回默认值 "last_download"，不产生属性变更、不触发刷新。
    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
        => Vm.SortBy = (sender as ComboBox)?.SelectedIndex switch
        {
            1 => "download_count",
            2 => "added_at",
            _ => "last_download",
        };

    // 勾选是真正的多选；表格自带的当前行高亮会另涂一层深灰，这里清掉。
    private bool _clearingGridSelection;
    private void OnUsersGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_clearingGridSelection || UsersGrid.SelectedItem is null) return;
        _clearingGridSelection = true;
        UsersGrid.SelectedItem = null;
        _clearingGridSelection = false;
    }

    private readonly Dictionary<DataGridRow, (UserRowViewModel Vm, PropertyChangedEventHandler Handler)> _rowBinds = [];
    private static readonly Brush SelectedRowBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x78, 0xD4));

    private void OnUsersGridLoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not UserRowViewModel vm) return;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName is null or nameof(UserRowViewModel.IsSelected))
                ApplyRowHighlight(e.Row, vm);
        };
        vm.PropertyChanged += handler;
        _rowBinds[e.Row] = (vm, handler);
        ApplyRowHighlight(e.Row, vm);
    }

    private void OnUsersGridUnloadingRow(object sender, DataGridRowEventArgs e)
    {
        if (!_rowBinds.Remove(e.Row, out var bind)) return;
        bind.Vm.PropertyChanged -= bind.Handler;
        e.Row.Background = null;
    }

    private static void ApplyRowHighlight(DataGridRow row, UserRowViewModel vm)
        => row.Background = vm.IsSelected ? SelectedRowBrush : null;
}
