using GGdown.Data;

namespace GGdown.Services;

/// <summary>恢复格式 v1。与调度器内部 WorkItem 分离，内容类型按稳定名称保存。</summary>
internal sealed record DownloadRequestDto(
    long AccountId, string SiteId, string ContentType, long? UserId, string TargetScreenName,
    string? TargetRestId, string Title, string BaseDirectory, IReadOnlyDictionary<string, object?> SiteOptions,
    string CookieAbsolutePath, string ArchiveFile, string? DirectUrl,
    UserContentSelection ContentSelection, DateOnly? DownloadSince);

internal sealed record QueueRecoveryData(int SchemaVersion, DownloadRequestDto Request);
