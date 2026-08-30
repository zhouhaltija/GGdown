using System.Runtime.InteropServices;
using Windows.Storage.Pickers;

namespace GalleryGUI.App.Infrastructure;

/// <summary>
/// 文件/文件夹选择对话框封装（B4 导入 cookies.txt、B7 浏览目录消费）。
/// 解包应用（WindowsPackageType=None）中 Picker 必须经 InitializeWithWindow 绑定 HWND，
/// 这里在调用时（UI 线程事件处理中）取当前线程的活动窗口句柄。
/// 若后续任务（B4/B7）brief 给出更具体的签名需求，可在此基础上调整。
/// </summary>
public sealed class FileDialogService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>选择单个文件。patterns 为扩展名过滤器（如 ".txt"）。</summary>
    public async Task<string?> PickFileAsync(string displayName, params string[] patterns)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        foreach (var pattern in patterns) picker.FileTypeFilter.Add(pattern);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetWindowHandle());
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>选择单个文件夹。</summary>
    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetWindowHandle());
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static IntPtr GetWindowHandle() => GetActiveWindow() != IntPtr.Zero ? GetActiveWindow() : GetForegroundWindow();
}
