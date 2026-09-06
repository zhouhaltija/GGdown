using GalleryGUI.Sites;

namespace GalleryGUI.Engine;

public sealed record EngineHello(int Protocol, string RunnerVersion, string? GalleryDlVersion);
public sealed record AccountInfo(string ScreenName, string? DisplayName, string? RestId = null);
public sealed record SiteUserInfo(
    string RestId, string ScreenName, string? DisplayName, string? AvatarUrl,
    string? BannerUrl = null, string? Bio = null, long? FollowersCount = null, long? MediaCount = null);

public interface IDownloadEngine
{
    Task<EngineHello> HelloAsync(CancellationToken ct = default);
    Task<AccountInfo> WhoAmIAsync(string siteId, string cookiesFile, CancellationToken ct = default);
    Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(string siteId, string cookiesFile, CancellationToken ct = default);
    Task<SiteUserInfo> GetUserInfoAsync(string siteId, string cookiesFile, string input, CancellationToken ct = default);
    Task DownloadAsync(DownloadPlan plan, string cookiesFile, IProgress<EngineEvent> progress, CancellationToken ct = default);
}

public class EngineException(string message, string? kind = null) : Exception(message)
{ public string? Kind { get; } = kind; }
public sealed class AuthException(string message) : EngineException(message, "auth") { }
