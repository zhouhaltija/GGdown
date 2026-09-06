using GalleryGUI.App.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GalleryGUI.App.Views.Dialogs;

/// <summary>
/// 导入 Cookie 对话框（B4）：说明 + 明文警告（规格 §3.5 硬要求，文案逐字）+ 选文件按钮 +
/// 已选文件路径显示。Pixiv 额外显示 refresh-token 输入。
/// 主按钮"导入"关闭后由页面调 ImportViewModel.ImportCookiesAsync(path, token)。
/// </summary>
public sealed partial class ImportCookieDialog : ContentDialog
{
    private readonly FileDialogService _files;
    private readonly bool _requireRefreshToken;
    private string? _cookiesPath;

    public string? CookiesPath => _cookiesPath;
    public string? RefreshToken => string.IsNullOrWhiteSpace(TokenBox.Text) ? null : TokenBox.Text.Trim();

    public ImportCookieDialog(FileDialogService files, bool requireRefreshToken = false)
    {
        _files = files;
        _requireRefreshToken = requireRefreshToken;
        InitializeComponent();
        if (_requireRefreshToken)
        {
            Title = "导入账号";
            TokenPanel.Visibility = Visibility.Visible;
            IntroText.Text = "选择从浏览器导出的 cookies.txt（须含 PHPSESSID），并填入 refresh-token。导入后将验证账号并设为活动账号。";
        }
    }

    private void UpdatePrimaryEnabled()
    {
        var hasCookies = !string.IsNullOrEmpty(_cookiesPath);
        var hasToken = !_requireRefreshToken || !string.IsNullOrWhiteSpace(TokenBox.Text);
        IsPrimaryButtonEnabled = hasCookies && hasToken;
    }

    private async void OnPickFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await _files.PickFileAsync(".txt");
            if (string.IsNullOrEmpty(path)) return;
            _cookiesPath = path;
            PathText.Text = path;
            UpdatePrimaryEnabled();
        }
        catch (Exception ex)
        {
            PathText.Text = $"选择文件失败：{ex.Message}";
        }
    }

    private void OnTokenChanged(object sender, TextChangedEventArgs e) => UpdatePrimaryEnabled();
}
