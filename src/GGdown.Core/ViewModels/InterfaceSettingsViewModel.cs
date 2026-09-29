using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GGdown.Settings;
using GGdown.Sites;

namespace GGdown.ViewModels;

/// <summary>
/// 界面设置 VM（Task 9）：控制平台栏显示哪些平台。
/// 规则（spec §2.3）：Available=false 的站点不可勾选（开关禁用，仅作预告展示），
/// 勾选即时生效（保存 → MainNavViewModel.ReloadVisibleAsync 刷新平台栏）。
/// </summary>
public partial class InterfaceSettingsViewModel(IAppSettings settings, MainNavViewModel nav) : StatusViewModel
{
    public ObservableCollection<VisibleSiteItemViewModel> Items { get; } = [];


    public async Task StartAsync()
    {
        var visible = (await settings.GetVisibleSitesAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Items.Clear();
        foreach (var s in SiteCatalog.All)
            Items.Add(new VisibleSiteItemViewModel(s.SiteId, s.DisplayName, s.IconGlyph, s.Available,
                s.Available && visible.Contains(s.SiteId), OnItemToggled));
    }

    private void OnItemToggled(VisibleSiteItemViewModel item)
    {
        if (!item.Available) return; // 不可用站点勾选无效（UI 侧 IsEnabled=false，双保险）
        _ = SaveChangesAsync();
    }

    /// <summary>保存勾选集合并刷新平台栏（ItemsControl 的 IsVisible 双向绑定在 UI 线程触发）。</summary>
    public async Task SaveChangesAsync()
    {
        try
        {
            var checkedSites = Items.Where(i => i.Available && i.IsVisible).Select(i => i.SiteId).ToList();
            await settings.SetVisibleSitesAsync(checkedSites);
            await nav.ReloadVisibleAsync();
            StatusMessage = "已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }
}

public partial class VisibleSiteItemViewModel(
    string siteId, string displayName, string iconGlyph, bool available, bool isVisible,
    Action<VisibleSiteItemViewModel>? toggled) : ObservableObject
{
    public string SiteId { get; } = siteId;
    public string DisplayName { get; } = displayName;
    public string IconGlyph { get; } = iconGlyph;
    public bool Available { get; } = available;

    [ObservableProperty]
    private bool _isVisible = isVisible;

    partial void OnIsVisibleChanged(bool value) => toggled?.Invoke(this);
}
