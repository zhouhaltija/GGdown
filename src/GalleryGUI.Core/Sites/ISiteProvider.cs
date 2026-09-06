namespace GalleryGUI.Sites;

public enum ContentKind
{
    UserMedia, AccountLikes, AccountBookmarks, UserNovels, AccountNovelBookmarks,
    UserHighlights, Permalink, Search,
}
public enum OptionKind { Boolean, Text, Choice }
public enum PasteKind { User, Tweet, List, Search }

public sealed record UserTarget(long? UserId, string? ScreenName, string? BaseDirectory = null, string? RestId = null, string? DirectUrl = null);
public sealed record UserInputParseResult(
    bool Ok, string? ScreenName = null, string? Error = null, string? RestId = null,
    PasteKind Kind = PasteKind.User, string? DirectUrl = null);
public sealed record DownloadPaths(string CookiesFile, string ArchiveFile);
public sealed record OptionField(string Key, OptionKind Kind, object? Default,
    string DisplayName, IReadOnlyList<string>? Choices = null);
public sealed record OptionSchema(IReadOnlyList<OptionField> Fields);

public interface ISiteProvider
{
    string SiteId { get; }
    string DisplayName { get; }
    IReadOnlyList<ContentKind> SupportedKinds { get; }
    OptionSchema OptionsSchema { get; }
    IReadOnlyDictionary<string, object?> DefaultOptions { get; }
    bool RequiresRefreshToken { get; }
    string InputHint { get; }
    UserInputParseResult ParseInput(string input);
    string BuildProfileUrl(string screenName, string? restId = null);
    string KindLabel(ContentKind kind);
    DownloadPlan BuildDownload(ContentKind kind, UserTarget target,
        IReadOnlyDictionary<string, object?> options, DownloadPaths paths);
}
