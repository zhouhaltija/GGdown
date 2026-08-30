using GalleryGUI.App.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GalleryGUI.App.Views.Dialogs;

/// <summary>
/// 导入 Cookie 对话框（B4）：说明 + 明文警告（规格 §3.5 硬要求，文案逐字）+ 选文件按钮 +
/// 已选文件路径显示。"选择 cookies.txt 文件"经 FileDialogService（Filter ".txt"）；
/// 主按钮"导入"关闭后由页面调 ImportViewModel.ImportCookiesAsync(path)。
/// </summary>
public sealed partial class ImportCookieDialog : ContentDialog
{
    private readonly FileDialogService _files;
    private string? _cookiesPath;

    /// <summary>选定的 cookies.txt 全路径（未选择为 null；Primary 关闭后页面消费）。</summary>
    public string? CookiesPath => _cookiesPath;

    public ImportCookieDialog(FileDialogService files)
    {
        _files = files;
        InitializeComponent();
    }

    private async void OnPickFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await _files.PickFileAsync(".txt");
            if (string.IsNullOrEmpty(path)) return; // 用户取消
            _cookiesPath = path;
            PathText.Text = path;
            IsPrimaryButtonEnabled = true; // 选中文件后主按钮"导入"可用
        }
        catch (Exception ex) // 窗口未初始化等：不崩 UI 线程
        {
            PathText.Text = $"选择文件失败：{ex.Message}";
        }
    }
}
