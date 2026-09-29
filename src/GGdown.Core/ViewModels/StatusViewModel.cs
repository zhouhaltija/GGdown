using CommunityToolkit.Mvvm.ComponentModel;

namespace GGdown.ViewModels;

/// <summary>通知级别：窗口级浮动通知据此选择 InfoBar 严重度与自动关闭时长。</summary>
public enum NoticeLevel { Info, Success, Warning, Error }

/// <summary>
/// 应用内统一反馈通道：各页 VM 的操作结果一行话经此发布，由主窗口统一显示为浮动通知，
/// 避免反馈散落在各页角落。无订阅者时（单元测试）发布为空操作。
/// </summary>
public static class StatusHub
{
    public static event Action<NoticeLevel, string>? Published;

    public static void Publish(string message, NoticeLevel? level = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Published?.Invoke(level ?? Infer(message), message);
    }

    /// <summary>按文案推断级别：各 VM 的反馈文案风格统一（"已…"成功、"…失败"/"请先…"提醒），据此分级。</summary>
    public static NoticeLevel Infer(string message)
    {
        if (message.Contains("失败") || message.Contains("失效") || message.Contains("异常"))
            return NoticeLevel.Error;
        if (message.StartsWith("请") || message.Contains("不存在") || message.Contains("无效")
            || message.Contains("没有") || message.Contains("还没有") || message.Contains("即将支持")
            || message.Contains("均为") || message.Contains("均已") || message.Contains("均在"))
            return NoticeLevel.Warning;
        if (message.StartsWith("已") || message.Contains("成功") || message.Contains("通过"))
            return NoticeLevel.Success;
        return NoticeLevel.Info;
    }
}

/// <summary>
/// 带操作反馈的 VM 基类：StatusMessage 每次赋值都发变更通知并发布到 <see cref="StatusHub"/>
/// （同一句话连续出现也要再提示一次，故不做相等短路）。
/// </summary>
public abstract class StatusViewModel : ObservableObject
{
    private string? _statusMessage;

    /// <summary>操作结果反馈（成功/失败一行话）。</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            _statusMessage = value;
            OnPropertyChanged();
            if (!string.IsNullOrWhiteSpace(value)) StatusHub.Publish(value);
        }
    }
}
