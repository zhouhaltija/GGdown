using System.Diagnostics;

namespace GGdown.App.Infrastructure;

/// <summary>Shell 启动封装（B7「打开日志文件夹」等消费）：在资源管理器中打开目录、用默认程序打开 URL。</summary>
public sealed class LauncherService
{
    public void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { path } });

    public void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
