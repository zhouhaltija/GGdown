namespace GalleryGUI.Engine;

public sealed record EngineEvent(
    string Event,
    string? Url = null, string? Path = null, string? ItemId = null, string? User = null,
    long? Size = null, string? Reason = null, string? Level = null, string? Message = null, string? Kind = null,
    long? Total = null, long? Skipped = null, long? Failed = null,
    int? Protocol = null, string? RunnerVersion = null, string? GalleryDlVersion = null,
    string? RestId = null, string? ScreenName = null, string? DisplayName = null, string? AvatarUrl = null,
    string? BannerUrl = null, string? Bio = null, long? FollowersCount = null, long? MediaCount = null);
