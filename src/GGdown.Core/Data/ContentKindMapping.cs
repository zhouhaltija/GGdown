using GGdown.Sites;

namespace GGdown.Data;

/// <summary>持久化类型与站点能力显式转换，避免枚举顺序变更改变历史含义。</summary>
public static class ContentKindMapping
{
    public static ContentKind ToContentKind(TargetKind kind) => kind switch
    {
        TargetKind.UserMedia => ContentKind.UserMedia,
        TargetKind.AccountLikes => ContentKind.AccountLikes,
        TargetKind.AccountBookmarks => ContentKind.AccountBookmarks,
        TargetKind.UserNovels => ContentKind.UserNovels,
        TargetKind.AccountNovelBookmarks => ContentKind.AccountNovelBookmarks,
        TargetKind.UserHighlights => ContentKind.UserHighlights,
        TargetKind.Permalink => ContentKind.Permalink,
        TargetKind.Search => ContentKind.Search,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "未知下载内容类型"),
    };

    public static TargetKind ToTargetKind(ContentKind kind) => kind switch
    {
        ContentKind.UserMedia => TargetKind.UserMedia,
        ContentKind.AccountLikes => TargetKind.AccountLikes,
        ContentKind.AccountBookmarks => TargetKind.AccountBookmarks,
        ContentKind.UserNovels => TargetKind.UserNovels,
        ContentKind.AccountNovelBookmarks => TargetKind.AccountNovelBookmarks,
        ContentKind.UserHighlights => TargetKind.UserHighlights,
        ContentKind.Permalink => TargetKind.Permalink,
        ContentKind.Search => TargetKind.Search,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "未知下载内容类型"),
    };
}
