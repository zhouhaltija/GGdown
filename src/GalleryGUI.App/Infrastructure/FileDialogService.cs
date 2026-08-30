using Windows.Storage.Pickers;

namespace GalleryGUI.App.Infrastructure;

/// <summary>
/// 文件/文件夹选择对话框封装（B4 导入 cookies.txt、B7 浏览目录消费）。
/// 解包应用（WindowsPackageType=None）中 Picker 必须经 InitializeWithWindow 绑定 HWND——
/// B1 版本在调用时用 GetActiveWindow/GetForegroundWindow 启发式取句柄（B1 审查遗留问题：
/// 死参数 displayName + 不可靠的活动窗口探测），控制器裁定改为属主注入：
/// App.OnLaunched 创建 MainWindow 后调用 SetOwner 注入主窗口句柄，Pick 系列用该句柄初始化 Picker。
/// </summary>
public sealed class FileDialogService
{
    private IntPtr _owner;

    /// <summary>注入 Picker 的属主窗口句柄（MainWindow.WindowHandle，App.OnLaunched 调用）。</summary>
    public void SetOwner(IntPtr hwnd) => _owner = hwnd;

    /// <summary>选择单个文件。patterns 为扩展名过滤器（如 ".txt"）。</summary>
    public async Task<string?> PickFileAsync(params string[] patterns)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        foreach (var pattern in patterns) picker.FileTypeFilter.Add(pattern);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, RequireOwner());
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
        WinRT.Interop.InitializeWithWindow.Initialize(picker, RequireOwner());
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private IntPtr RequireOwner() => _owner != IntPtr.Zero
        ? _owner
        : throw new InvalidOperationException("窗口未初始化");
}
