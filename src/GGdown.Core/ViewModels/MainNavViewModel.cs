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

    private static readonly (GlobalSettingsPageKey Key, string Title)[] GlobalTabs =
    [
        (GlobalSettingsPageKey.General, "通用"),
        (GlobalSettingsPageKey.Network, "网络"),
        (GlobalSettingsPageKey.Engine, "引擎"),
        (GlobalSettingsPageKey.Interface, "界面"),
        (GlobalSettingsPageKey.About, "关于"),
    ];

    public ObservableCollection<PlatformItemViewModel> Platforms { get; } = [];
    public ObservableCollection<PageTabViewModel> CurrentTabs { get; } = [];

    [ObservableProperty]
    private bool _isGlobalSettings;

    [ObservableProperty]
    private int _activeDownloadCount;

    /// <summary>栏底「全部下载」入口的全局视图开关（Task 10）：选平台/进全局设置时复位。</summary>
    [ObservableProperty]
    private bool _isGlobalDownloads;

    /// <summary>平台页组标签点击 → MainWindow 导航到对应平台页。</summary>
    public event Action<SitePageKey>? TabNavigationRequested;

    /// <summary>全局设置页组标签点击 → MainWindow 导航到对应设置页（Task 9）。</summary>
    public event Action<GlobalSettingsPageKey>? GlobalTabNavigationRequested;

    /// <summary>进入全局设置（默认落通用页）→ MainWindow 导航。</summary>
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
        IsGlobalDownloads = false; // 平台页组导航复位全局下载视图
        if (current.SiteId != siteId) await current.SelectAsync(siteId);
        await settings.SetCurrentSiteIdAsync(siteId);
        foreach (var p in Platforms) p.IsSelected = p.SiteId == siteId;
        RebuildTabs(SiteUiProfiles.For(siteId), await settings.GetSiteLastPageAsync(siteId));
    }

    public async Task SelectTabAsync(SitePageKey key)
    {
        foreach (var t in CurrentTabs) t.IsSelected = Equals(t.Key, key);
        if (IsGlobalSettings) return; // 全局设置区的标签由 Task 9 的 GlobalSettingsPageKey 承载
        await settings.SetSiteLastPageAsync(current.SiteId, key.ToString().ToLowerInvariant());
        TabNavigationRequested?.Invoke(key);
    }

    public Task EnterGlobalSettingsAsync()
    {
        IsGlobalDownloads = false;
        IsGlobalSettings = true;
        foreach (var p in Platforms) p.IsSelected = false;
        CurrentTabs.Clear();
        foreach (var (key, title) in GlobalTabs)
            CurrentTabs.Add(new PageTabViewModel(key, title, key == GlobalSettingsPageKey.General));
        GlobalSettingsNavigationRequested?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>全局设置区内切标签（不持久化——全局设置无 lastPage 记忆，spec §2.6 只覆盖平台页）。</summary>
    public Task SelectGlobalTabAsync(GlobalSettingsPageKey key)
    {
        if (!IsGlobalSettings) return Task.CompletedTask;
        foreach (var t in CurrentTabs) t.IsSelected = Equals(t.Key, key);
        GlobalTabNavigationRequested?.Invoke(key);
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

/// <summary>
/// 页组标签行条目：Key 为 SitePageKey（平台模式）或 GlobalSettingsPageKey（全局设置模式），
/// 由 MainWindow 按 Key 类型分发导航。
/// </summary>
public partial class PageTabViewModel(object key, string title, bool isSelected) : ObservableObject
{
    public object Key { get; } = key;
    public string Title { get; } = title;

    [ObservableProperty]
    private bool _isSelected = isSelected;
}
