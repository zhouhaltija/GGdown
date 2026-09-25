using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GGdown.Settings;
using GGdown.Sites;

namespace GGdown.ViewModels;

/// <summary>
/// 主窗口导航 VM（Task 8）：平台栏（可见∩可用的站点）+ 当前平台页组标签 + 全局设置入口。
/// Core 不引用 App 页面类型：导航意图经事件（TabNavigationRequested / GlobalSettingsNavigationRequested）
/// 发给 MainWindow，由其映射 SitePageKey → 页面 Type 并 ContentFrame.Navigate。
/// 持久化：ui.currentSite（沿用）+ ui.site.&lt;id&gt;.lastPage（切回平台恢复停留页）。
/// </summary>
public partial class MainNavViewModel(ICurrentSite current, IAppSettings settings) : ObservableObject
{
    private static readonly Dictionary<SitePageKey, string> Titles = new()
    {
        [SitePageKey.Users] = "用户管理",
        [SitePageKey.Downloads] = "下载",
        [SitePageKey.History] = "历史",
        [SitePageKey.SiteSettings] = "设置",
    };

    public ObservableCollection<PlatformItemViewModel> Platforms { get; } = [];
    public ObservableCollection<PageTabViewModel> CurrentTabs { get; } = [];

    [ObservableProperty]
    private bool _isGlobalSettings;

    [ObservableProperty]
    private int _activeDownloadCount;

    /// <summary>页组标签点击 → MainWindow 导航到对应平台页。</summary>
    public event Action<SitePageKey>? TabNavigationRequested;

    /// <summary>进入全局设置 → MainWindow 导航到设置区（Task 9 起为五页组）。</summary>
    public event Action? GlobalSettingsNavigationRequested;

    /// <summary>App.Readiness 放行后的启动入口：载入可见平台并选中当前平台。</summary>
    public async Task StartAsync()
    {
        await ReloadVisibleAsync();
        if (Platforms.Count == 0) return;
        var selected = Platforms.FirstOrDefault(p => p.SiteId == current.SiteId) ?? Platforms[0];
        await SelectPlatformInternalAsync(selected.SiteId);
    }

    /// <summary>界面设置勾选变化后刷新平台栏（当前平台被隐藏时回退到首个可见平台）。</summary>
    public async Task ReloadVisibleAsync()
    {
        var visible = (await settings.GetVisibleSitesAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedId = current.SiteId;
        Platforms.Clear();
        foreach (var s in SiteCatalog.All.Where(s => s.Available && visible.Contains(s.SiteId)))
            Platforms.Add(new PlatformItemViewModel(s.SiteId, s.DisplayName, s.IconGlyph, s.SiteId == selectedId));
        if (Platforms.Count == 0)
        {
            CurrentTabs.Clear();
            return;
        }
        if (Platforms.All(p => p.SiteId != selectedId))
            await SelectPlatformInternalAsync(Platforms[0].SiteId);
    }

    public async Task SelectPlatformAsync(string siteId)
    {
        if (IsGlobalSettings) IsGlobalSettings = false;
        await SelectPlatformInternalAsync(siteId);
    }

    private async Task SelectPlatformInternalAsync(string siteId)
    {
        if (current.SiteId != siteId) await current.SelectAsync(siteId);
        await settings.SetCurrentSiteIdAsync(siteId);
        foreach (var p in Platforms) p.IsSelected = p.SiteId == siteId;
        RebuildTabs(SiteUiProfiles.For(siteId), await settings.GetSiteLastPageAsync(siteId));
    }

    public async Task SelectTabAsync(SitePageKey key)
    {
        foreach (var t in CurrentTabs) t.IsSelected = t.Key == key;
        if (IsGlobalSettings) return; // 全局设置区的标签由 Task 9 的 GlobalSettingsPageKey 承载
        await settings.SetSiteLastPageAsync(current.SiteId, key.ToString().ToLowerInvariant());
        TabNavigationRequested?.Invoke(key);
    }

    public Task EnterGlobalSettingsAsync()
    {
        IsGlobalSettings = true;
        foreach (var p in Platforms) p.IsSelected = false;
        GlobalSettingsNavigationRequested?.Invoke();
        return Task.CompletedTask;
    }

    private void RebuildTabs(IReadOnlyList<SitePageKey> keys, string lastPage)
    {
        CurrentTabs.Clear();
        var last = Enum.TryParse<SitePageKey>(lastPage, ignoreCase: true, out var lp) && keys.Contains(lp)
            ? lp
            : keys.FirstOrDefault();
        foreach (var k in keys)
            CurrentTabs.Add(new PageTabViewModel(k, Titles[k], k == last));
    }
}

public partial class PlatformItemViewModel(string siteId, string displayName, string iconGlyph, bool isSelected)
    : ObservableObject
{
    public string SiteId { get; } = siteId;
    public string DisplayName { get; } = displayName;
    public string IconGlyph { get; } = iconGlyph;

    [ObservableProperty]
    private bool _isSelected = isSelected;
}

public partial class PageTabViewModel(SitePageKey key, string title, bool isSelected) : ObservableObject
{
    public SitePageKey Key { get; } = key;
    public string Title { get; } = title;

    [ObservableProperty]
    private bool _isSelected = isSelected;
}
