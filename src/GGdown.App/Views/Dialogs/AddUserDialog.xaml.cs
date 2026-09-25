using GGdown.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GGdown.App.Views.Dialogs;

/// <summary>
/// 添加用户对话框（B4）：Content = 输入框 + 红色错误 TextBlock（绑定 UserInputError）；
/// 主按钮"添加"随校验结果启停（IsPrimaryButtonEnabled）。Primary 关闭后由页面调
/// ImportViewModel.AddUserAsync（brief："DialogResult 后调 AddUserAsync"）。
/// </summary>
public sealed partial class AddUserDialog : ContentDialog
{
    public ImportViewModel Vm { get; }

    public AddUserDialog(ImportViewModel vm)
    {
        Vm = vm; // 须先于 InitializeComponent：x:Bind 初始求值需要（同 UsersPage 模式）
        InitializeComponent();
        IsPrimaryButtonEnabled = false; // 初始无输入禁用；键入后随校验结果启停
        // UserInput/UserInputError 任一变更都重算主按钮可用态（覆盖"输入为空"与"解析失败"两种禁用情形）
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ImportViewModel.UserInput) or nameof(ImportViewModel.UserInputError))
                IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(Vm.UserInput) && Vm.UserInputError is null;
        };
    }

    private Visibility HasError(string? error)
        => string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
}
