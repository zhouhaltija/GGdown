using Microsoft.UI.Xaml;

namespace GalleryGUI.App.Infrastructure;

/// <summary>
/// x:Bind 函数绑定用的轻量转换器。
/// 背景（B3 控制器裁定 5 适配）：UsersViewModel 在 Core（net8.0）中无法引用
/// Microsoft.UI.Xaml.Visibility，VM 只暴露 bool 语义属性，页面层在此映射为 Visibility。
/// </summary>
public static class UiConv
{
    public static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ToInverseVisibility(bool visible) => visible ? Visibility.Collapsed : Visibility.Visible;
}
