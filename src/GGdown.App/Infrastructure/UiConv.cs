using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GGdown.App.Infrastructure;

/// <summary>
/// x:Bind 函数绑定用的轻量转换器。
/// 背景（B3 控制器裁定 5 适配）：UsersViewModel 在 Core（net8.0）中无法引用
/// Microsoft.UI.Xaml.Visibility，VM 只暴露 bool 语义属性，页面层在此映射为 Visibility。
/// </summary>
public static class UiConv
{
    public static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ToInverseVisibility(bool visible) => visible ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility ToTwitterVisibility(string siteId) =>
        siteId == "twitter" ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ToPixivVisibility(string siteId) =>
        siteId == "pixiv" ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ToDouyinVisibility(string siteId) =>
        siteId == "douyin" ? Visibility.Visible : Visibility.Collapsed;

    public static ImageSource? ToSiteIcon(string siteId) =>
        siteId == "pixiv" ? new SvgImageSource(new Uri("ms-appx:///Assets/Brands/pixiv.svg")) : null;

    public static ImageSource? ToImageSource(string? pathOrUri)
    {
        if (string.IsNullOrWhiteSpace(pathOrUri)) return null;
        try
        {
            Uri uri;
            if (pathOrUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                || pathOrUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                uri = new Uri(pathOrUri);
            else
            {
                var full = Path.GetFullPath(pathOrUri);
                if (!File.Exists(full)) return null;
                uri = new Uri("file:///" + full.Replace('\\', '/'));
            }
            return new BitmapImage(uri);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
