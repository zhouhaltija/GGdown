namespace GGdown.Data;

public enum AccountStatus { Unverified, Ok, Invalid }
public enum UserSource { Following, Manual, Link }
public enum TargetKind
{
    UserMedia, AccountLikes, AccountBookmarks, UserNovels, AccountNovelBookmarks,
    UserHighlights, Permalink, Search,
}
public enum JobStatus { Pending, Running, Completed, Failed, Canceled }
public enum FileStatus { Downloaded, Skipped, Failed }

public sealed class Account
{
    public long Id { get; set; }
    public string SiteId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? ScreenName { get; set; }
    public string? RestId { get; set; }
    public string CookiePath { get; set; } = string.Empty;
    public AccountStatus Status { get; set; }
    public bool IsActive { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
}

public sealed class User
{
    public long Id { get; set; }
    public string SiteId { get; set; } = string.Empty;
    public string RestId { get; set; } = string.Empty;
    public string ScreenName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public string? BannerUrl { get; set; }
    public string? Bio { get; set; }
    public string? ProfileUrl { get; set; }
    public long? FollowersCount { get; set; }
    public long? MediaCount { get; set; }
    public long? MediaCountAtDownload { get; set; }
    public UserSource Source { get; set; }
    public long? OwnerAccountId { get; set; }
    public bool IsPinned { get; set; }
    public bool IsSkipped { get; set; }
    public bool InDownloadList { get; set; }
    public long DownloadCount { get; set; }
    public DateTime? LastDownloadAt { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class DownloadJob
{
    public long Id { get; set; }
    // 拆库后显式携带归属（UserId 可空不能作归属依据）；旧 GGdownDbContext 忽略该列
    public string SiteId { get; set; } = string.Empty;
    public long AccountId { get; set; }
    public TargetKind TargetKind { get; set; }
    public long? UserId { get; set; }
    public JobStatus Status { get; set; }
    public long TotalFiles { get; set; }
    public long DoneFiles { get; set; }
    public long SkippedFiles { get; set; }
    public long FailedFiles { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public List<DownloadFile> Files { get; set; } = []; // 控制器裁定：Task 10 测试使用 Include(j => j.Files)
}

public sealed class DownloadFile
{
    public long Id { get; set; }
    // 与所属 Job 同站点（经 JobId 回填）；旧 GGdownDbContext 忽略该列
    public string SiteId { get; set; } = string.Empty;
    public long JobId { get; set; }
    public long? UserId { get; set; }
    public string? SourceItemId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long? FileSize { get; set; }
    public FileStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class SettingEntry { public required string Key { get; set; } public string? Value { get; set; } }

/// <summary>平台库内的站点选项行（从全局库 site.&lt;id&gt;.options 键迁入）；复合主键 (SiteId, Key)。</summary>
public sealed class SiteSettingEntry
{
    public string SiteId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}

public sealed class FollowingCacheEntry
{
    public long Id { get; set; }
    public string SiteId { get; set; } = string.Empty;
    public string RestId { get; set; } = string.Empty;
    public string ScreenName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public int SortOrder { get; set; }
    public DateTime FetchedAt { get; set; }
}

public sealed class IgnoredFollowingEntry
{
    public long Id { get; set; }
    public string SiteId { get; set; } = string.Empty;
    public long AccountId { get; set; }
    public string RestId { get; set; } = string.Empty;
    public DateTime IgnoredAt { get; set; }
}
