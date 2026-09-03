using CommunityToolkit.Mvvm.ComponentModel;
using GalleryGUI.Services;
using GalleryGUI.Sites;
using GalleryGUI.Threading;

namespace GalleryGUI.ViewModels;

/// <summary>
/// 导入流程 VM（B4）：添加用户、Cookie 导入、关注导入共用一个实例（每个对话框新建）。
/// VM 只管调用服务与结果消息；Cookie 复制前的明文提示由对话框层展示（brief Step 3 说明）。
/// 偏离（见报告偏离 3）：brief Produces 构造签名没有站点依赖，但 ValidateUserInput 需要
/// ISiteProvider.ParseInput，故追加 SiteRegistry 参数（DI 已有单例注册，同 UsersViewModel 注入方式）。
/// </summary>
public partial class ImportViewModel : ObservableObject
{
    private readonly ICurrentSite _currentSite;
    private readonly IAccountService _accounts;
    private readonly IUserService _users;
    private readonly IAccountQueryService _accountQuery;
    private readonly IUiDispatcher _dispatcher;
    private readonly SiteRegistry _sites;

    private string SiteId => _currentSite.SiteId;

    public ImportViewModel(IAccountService accounts, IUserService users,
        IAccountQueryService accountQuery, IUiDispatcher dispatcher, SiteRegistry sites,
        ICurrentSite currentSite)
    {
        _accounts = accounts;
        _users = users;
        _accountQuery = accountQuery;
        _dispatcher = dispatcher;
        _sites = sites;
        _currentSite = currentSite;
    }

    [ObservableProperty]
    private string? _userInput;          // 添加用户对话框的输入

    [ObservableProperty]
    private string? _userInputError;     // 解析错误（ParseInput 结果）

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _resultMessage;

    /// <summary>任一导入成功 → 页面通知 UsersViewModel.Refresh（经 dispatcher 投递，保证订阅者在 UI 线程）。</summary>
    public event Action? ImportCompleted;

    /// <summary>输入即时校验（ParseInput）；UserInput 变更时自动触发（等价 brief 的"每次 TextChanged 调用"）。</summary>
    public void ValidateUserInput()
    {
        if (!_currentSite.IsAvailable || !_sites.IsRegistered(SiteId))
        {
            UserInputError = $"{_currentSite.Current.DisplayName} 即将支持";
            return;
        }
        var parsed = _sites.Get(SiteId).ParseInput(UserInput ?? "");
        UserInputError = parsed.Ok ? null : parsed.Error;
    }

    // 输入变化即校验：对话框侧 TextBox 绑定 UpdateSourceTrigger=PropertyChanged，键入即时反映错误与主按钮可用态
    partial void OnUserInputChanged(string? value) => ValidateUserInput();

    public async Task<bool> AddUserAsync(CancellationToken ct = default)
    {
        var account = await _accountQuery.GetActiveAsync(SiteId, ct);
        if (account is null)
        {
            ResultMessage = "请先导入 Cookie";
            return false;
        }
        ValidateUserInput(); // 双保险：AddUserAsync 自身入口也拦截非法输入
        if (UserInputError is not null) return false;
        try
        {
            IsBusy = true;
            var user = await _users.AddUserAsync(account, UserInput!.Trim(), ct);
            ResultMessage = $"已添加用户 @{user.ScreenName}";
            OnImportSucceeded();
            return true;
        }
        catch (Exception ex) // EngineException 等
        {
            ResultMessage = ex.Message;
            return false;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> ImportCookiesAsync(string cookiesFilePath, CancellationToken ct = default)
    {
        try
        {
            IsBusy = true;
            var result = await _accounts.ImportCookiesAsync(SiteId, cookiesFilePath, ct);
            if (!result.Ok)
            {
                ResultMessage = result.Error; // "Cookie 无效或已过期，请重新导出"（AccountService.VerifyAsync）
                return false;
            }
            ResultMessage = $"账号 @{result.Account.ScreenName} 导入成功，可到用户管理页从关注列表选择用户";
            OnImportSucceeded();
            return true;
        }
        catch (Exception ex)
        {
            ResultMessage = $"导入失败：{ex.Message}";
            return false;
        }
        finally { IsBusy = false; }
    }

    private void OnImportSucceeded()
        => _dispatcher.Post(() => ImportCompleted?.Invoke());
}
