using CommunityToolkit.Mvvm.ComponentModel;
using GalleryGUI.Sites;

namespace GalleryGUI.ViewModels;

public partial class SiteSwitcherViewModel : ObservableObject
{
    private readonly ICurrentSite _current;
    private bool _suppress;

    public SiteSwitcherViewModel(ICurrentSite current)
    {
        _current = current;
        _selected = current.Current;
        current.Changed += OnCurrentChanged;
    }

    public IReadOnlyList<SiteInfo> Sites => SiteCatalog.All;

    [ObservableProperty]
    private SiteInfo _selected = SiteCatalog.Default;

    public string SelectedDisplayName => Selected.DisplayName;

    private void OnCurrentChanged()
    {
        _suppress = true;
        Selected = _current.Current;
        _suppress = false;
    }

    partial void OnSelectedChanged(SiteInfo value)
    {
        OnPropertyChanged(nameof(SelectedDisplayName));
        if (_suppress || value.SiteId == _current.SiteId) return;
        _ = _current.SelectAsync(value.SiteId);
    }
}
