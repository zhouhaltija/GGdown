using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GGdown.Data;
using GGdown.Engine;
using GGdown.Services;
using GGdown.Sites;
using GGdown.Threading;

namespace GGdown.ViewModels;

public sealed partial class FollowingRowViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isIgnored;

    public FollowingRowViewModel(SiteUserInfo info, bool alreadyAdded, bool isIgnored,
        Func<FollowingRowViewModel, Task> toggleIgnore)
    {
        Info = info;
        AlreadyAdded = alreadyAdded;
        _isIgnored = isIgnored;
        _isSelected = alreadyAdded;
        ToggleIgnoreCommand = new AsyncRelayCommand(() => toggleIgnore(this));
    }

    public SiteUserInfo Info { get; }
    public bool AlreadyAdded { get; }
    public bool CanSelect => !AlreadyAdded && !IsIgnored;
    public bool CanIgnore => !AlreadyAdded;
    public bool IsIgnored
    {
        get => _isIgnored;
        set
        {
            if (!SetProperty(ref _isIgnored, value)) return;
            OnPropertyChanged(nameof(IgnoreActionText));
            OnPropertyChanged(nameof(CanSelect));
        }
    }
    public string IgnoreActionText => IsIgnored ? "恢复" : "忽略";
    public IAsyncRelayCommand ToggleIgnoreCommand { get; }
    public string Title => string.IsNullOrWhiteSpace(Info.DisplayName) ? Info.ScreenName : Info.DisplayName!;
    public string Subtitle => AlreadyAdded
        ? $"@{Info.ScreenName} · 已添加"
        : $"@{Info.ScreenName}";
    public string? AvatarUrl => Info.AvatarUrl;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanSelect || !SetProperty(ref _isSelected, value)) return;
            SelectedChanged?.Invoke();
        }
    }

    public event Action? SelectedChanged;
}

public partial class FollowingPickerViewModel : ObservableObject
{
    private readonly ICurrentSite _currentSite;
    private readonly IUserService _users;
    private readonly IUserQueryService _userQuery;
    private readonly IAccountQueryService _accountQuery;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<FollowingRowViewModel> _all = [];
    private HashSet<string> _ignoredIds = new(StringComparer.Ordinal);
    private Account? _activeAccount;

    private string SiteId => _currentSite.SiteId;

    public FollowingPickerViewModel(
        IUserService users, IUserQueryService userQuery, IAccountQueryService accountQuery,
        IUiDispatcher dispatcher, ICurrentSite currentSite)
    {
        _users = users;
        _userQuery = userQuery;
        _accountQuery = accountQuery;
        _dispatcher = dispatcher;
        _currentSite = currentSite;
        VisibleItems = [];
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        AddSelectedCommand = new AsyncRelayCommand(AddSelectedAsync);
    }

    public ObservableCollection<FollowingRowViewModel> VisibleItems { get; }

    public int SelectedCount => SelectedFilterIndex == 0
        ? _all.Count(r => r.IsSelected && !r.AlreadyAdded && !r.IsIgnored) : 0;
    public bool CanAddSelected => SelectedCount > 0;

    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private int _selectedFilterIndex;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _resultMessage;
    [ObservableProperty] private string? _statusText;

    public event Action? ImportCompleted;

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand AddSelectedCommand { get; }

    partial void OnSearchTextChanged(string? value) => ApplyFilter();
    partial void OnSelectedFilterIndexChanged(int value) => ApplyFilter();

    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        var account = await _accountQuery.GetActiveAsync(SiteId, ct);
        if (account is null)
        {
            ResultMessage = "请先导入 Cookie";
            StatusText = ResultMessage;
            return false;
        }

        HashSet<string> addedIds;
        try
        {
            var existing = await _userQuery.ListAsync(SiteId, new UserFilter(), ct);
            addedIds = existing.Select(u => u.RestId).ToHashSet(StringComparer.Ordinal);
            var ignored = await _users.GetIgnoredFollowingIdsAsync(account, ct);
            if (_activeAccount?.Id != account.Id)
                _all.Clear();
            _activeAccount = account;
            _ignoredIds = ignored.ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            ResultMessage = ex.Message;
            StatusText = $"加载失败：{ex.Message}";
            return false;
        }

        var cache = await _users.GetFollowingCacheAsync(account, ct);
        var hasCache = cache is { Users.Count: > 0 };
        if (hasCache)
        {
            _dispatcher.Post(() =>
            {
                if (!DisplayedEquals(cache!.Users))
                    ShowFollowing(cache.Users, addedIds);
                StatusText = "正在刷新关注列表…";
                IsBusy = false;
            });
        }
        else
        {
            IsBusy = true;
            StatusText = "正在拉取关注列表…";
        }

        try
        {
            var live = await _users.ListFollowingAsync(account, ct);
            var previous = cache?.Users;
            var ordered = PinNewToTop(previous, live);
            var newCount = CountNewFollows(previous, live);
            await _users.SaveFollowingCacheAsync(account, ordered, ct);
            _dispatcher.Post(() =>
            {
                if (!DisplayedEquals(ordered))
                    ShowFollowing(ordered, addedIds);
                StatusText = FormatStatus(ordered.Count, newCount, _all.Count(r => r.AlreadyAdded));
                IsBusy = false;
            });
            return true;
        }
        catch (Exception ex)
        {
            if (hasCache)
            {
                StatusText = "刷新失败，显示上次保存的列表";
                IsBusy = false;
                return true;
            }
            ResultMessage = ex.Message;
            StatusText = $"拉取失败：{ex.Message}";
            IsBusy = false;
            return false;
        }
    }

    public async Task<bool> AddSelectedAsync(CancellationToken ct = default)
    {
        var selected = SelectedFilterIndex == 0
            ? _all.Where(r => r.IsSelected && !r.AlreadyAdded && !r.IsIgnored).Select(r => r.Info).ToList()
            : [];
        if (selected.Count == 0)
        {
            ResultMessage = "请选择要添加的用户";
            return false;
        }
        var account = await _accountQuery.GetActiveAsync(SiteId, ct);
        if (account is null)
        {
            ResultMessage = "请先导入 Cookie";
            return false;
        }
        try
        {
            IsBusy = true;
            var added = await _users.AddFollowingUsersAsync(account, selected, ct);
            ResultMessage = $"已添加 {added} 个用户";
            _dispatcher.Post(() => ImportCompleted?.Invoke());
            return true;
        }
        catch (Exception ex)
        {
            ResultMessage = ex.Message;
            return false;
        }
        finally { IsBusy = false; }
    }

    private bool DisplayedEquals(IReadOnlyList<SiteUserInfo> live)
        => _all.Select(r => r.Info).SequenceEqual(live);

    private static IReadOnlyList<SiteUserInfo> PinNewToTop(
        IReadOnlyList<SiteUserInfo>? previous, IReadOnlyList<SiteUserInfo> live)
    {
        if (previous is null || previous.Count == 0)
            return live;

        var liveById = new Dictionary<string, SiteUserInfo>(StringComparer.Ordinal);
        foreach (var user in live)
            liveById[user.RestId] = user;

        var previousIds = previous.Select(u => u.RestId).ToHashSet(StringComparer.Ordinal);
        var pinned = live.Where(u => !previousIds.Contains(u.RestId));
        var kept = previous
            .Where(u => liveById.ContainsKey(u.RestId))
            .Select(u => liveById[u.RestId]);
        return pinned.Concat(kept).ToList();
    }

    private static int CountNewFollows(IReadOnlyList<SiteUserInfo>? previous, IReadOnlyList<SiteUserInfo> live)
    {
        if (previous is null || previous.Count == 0)
            return 0;
        var previousIds = previous.Select(u => u.RestId).ToHashSet(StringComparer.Ordinal);
        return live.Count(u => !previousIds.Contains(u.RestId));
    }

    private static string FormatStatus(int total, int newCount, int alreadyAdded)
    {
        if (total == 0)
            return "关注列表为空";
        var summary = $"共 {total} 人，已添加 {alreadyAdded} 人";
        return newCount > 0 ? $"新关注 {newCount} 人已置顶，{summary}" : summary;
    }

    private void ShowFollowing(IReadOnlyList<SiteUserInfo> followed, IReadOnlySet<string> addedIds)
    {
        var selected = _all.Where(r => r.IsSelected && r.CanSelect)
            .Select(r => r.Info.RestId)
            .ToHashSet(StringComparer.Ordinal);
        _all.Clear();
        foreach (var info in followed)
        {
            var row = new FollowingRowViewModel(info, addedIds.Contains(info.RestId),
                _ignoredIds.Contains(info.RestId), ToggleIgnoreAsync);
            if (selected.Contains(info.RestId) && row.CanSelect)
                row.IsSelected = true;
            row.SelectedChanged += NotifySelection;
            _all.Add(row);
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = SearchText?.Trim();
        IEnumerable<FollowingRowViewModel> rows = SelectedFilterIndex switch
        {
            1 => _all.Where(r => r.AlreadyAdded),
            2 => _all.Where(r => !r.AlreadyAdded && r.IsIgnored),
            _ => _all.Where(r => !r.AlreadyAdded && !r.IsIgnored),
        };
        if (!string.IsNullOrEmpty(q))
        {
            rows = rows.Where(r =>
                r.Info.ScreenName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (r.Info.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        VisibleItems.Clear();
        foreach (var row in rows)
            VisibleItems.Add(row);
        NotifySelection();
    }

    private async Task ToggleIgnoreAsync(FollowingRowViewModel row)
    {
        if (row.AlreadyAdded || _activeAccount is null) return;
        try
        {
            var ignored = !_ignoredIds.Contains(row.Info.RestId);
            await _users.SetFollowingIgnoredAsync(_activeAccount, row.Info.RestId, ignored);
            foreach (var current in _all.Where(r => r.Info.RestId == row.Info.RestId))
            {
                current.IsSelected = false;
                current.IsIgnored = ignored;
            }
            if (ignored) _ignoredIds.Add(row.Info.RestId);
            else _ignoredIds.Remove(row.Info.RestId);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            ResultMessage = ex.Message;
            StatusText = $"操作失败：{ex.Message}";
        }
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanAddSelected));
    }
}
