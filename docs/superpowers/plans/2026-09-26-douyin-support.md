# Douyin Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 增加抖音 Cookie、手动用户、发布作品和单条作品下载，支持最高质量视频开关。

**Architecture:** C# 站点提供器负责输入与下载计划；Python runner 的抖音分支调用 `third_party/TikTokDownloader` 接口并向既有 JSONL v1 协议发事件。现有队列、站点库和历史页继续使用。

**Tech Stack:** WinUI 3 / .NET 8 / xUnit / Python 3.13.7 / pytest / DouK-Downloader 5.8 (`473c90ff70c663cfb69310fff2b8d5192f200661`)

**Spec:** `docs/superpowers/specs/2026-09-26-douyin-support-design.md`

## Global Constraints

- 仅用户发布作品和单条作品链接；覆盖视频与图集。
- 不显示抖音关注列表、高光、喜欢、收藏、搜索和直播入口。
- `original_quality` 是布尔选项，默认 `false`。
- Cookie 和 `docs/*_cookies.txt` 不进入 Git、标准输出或日志。
- JSONL 保持协议 v1，诊断走 stderr，认证失效仅在明确证据下分类。
- 开发与发行构建的 Python 为 3.13.7，由 uv 管理解释器和 `engine/uv.lock` 依赖；第三方检出固定提交 `473c90ff70c663cfb69310fff2b8d5192f200661`。
- 修改代码后运行 `dotnet build src/GGdown.App/GGdown.App.csproj -c Debug -p:Platform=x64`；JSONL 边界变动同时运行两种语言的测试。
- 不改动或提交当前工作区既有的其他未提交文件、`AGENTS.md`、`CLAUDE.md`、`dist/`。

## File map

- `src/GGdown.Core/Sites/DouyinSiteProvider.cs`: 抖音输入、能力、选项和下载计划。
- `src/GGdown.Core/Sites/SiteCatalog.cs`, `SiteUiProfile.cs`, `DependencyInjection.cs`: 站点展示和注册。
- `src/GGdown.Core/Services/UserService.cs`: 抖音资料刷新按 `RestId` 寻址。
- `src/GGdown.Core/ViewModels/UsersViewModel.cs`, `DownloadsViewModel.cs`: 站点能力驱动按钮和链接入队。
- `src/GGdown.App/Views/UsersPage.xaml`, `DownloadsPage.xaml`, `MainWindow.xaml`, `Views/Settings/InterfaceSettingsPage.xaml`, `Infrastructure/UiConv.cs`: 抖音 UI 入口与图标。
- `engine/sites/douyin.py`: Cookie、账号、用户、作品请求与文件事件适配。
- `engine/runner.py`, `engine/sites/__init__.py`: 抖音路由，保留协议 v1。
- `engine/pyproject.toml`, `engine/uv.lock`, `engine/.python-version`, `scripts/run.ps1`, `scripts/build-engine.ps1`, `scripts/build.ps1`, `THIRD-PARTY.md`: uv 开发环境、内置 Python 依赖和许可。
- `src/GGdown.Tests/Sites/DouyinSiteProviderTests.cs`, `engine/tests/test_douyin.py`: 先于实现编写的边界测试。

---

### Task 1: 站点提供器与平台目录

**Interfaces:** `DouyinSiteProvider : ISiteProvider` 产出 `DownloadPlan`；`UserTarget.RestId` 是 `sec_user_id`；`Options["original_quality"]` 是布尔值。

- [ ] **Step 1: 先写失败的 C# 测试。** 在 `DouyinSiteProviderTests.cs` 断言主页 URL 解析返回 `RestId="MS4wLjABtest"`、单条 `https://www.douyin.com/video/1234567890123456789` 返回新枚举值 `PasteKind.Work` 及规范 URL、陌生域名被拒绝；用户作品计划 URL 为 `https://www.douyin.com/user/MS4wLjABtest`，单条计划沿用 `DirectUrl`，且最高质量默认 `false`。断言 `SiteCatalog.Get("douyin").Available` 且 `SiteUiProfiles.For("douyin")` 包含 Users、Downloads、History、SiteSettings。
- [ ] **Step 2: 运行 `dotnet test src/GGdown.Tests/GGdown.Tests.csproj --filter FullyQualifiedName~DouyinSiteProviderTests -p:Platform=x64`，确认因缺少提供器而失败。**
- [ ] **Step 3: 实现提供器并注册。** `SupportedKinds = [ContentKind.UserMedia, ContentKind.Permalink]`，`RequiresRefreshToken=false`，`OptionsSchema` 仅含 `new OptionField("original_quality", OptionKind.Boolean, false, "最高质量视频")`；`BuildDownload` 的 `Options` 采用 `{"douyin": {"original_quality": ...}, "base-directory": ...}`，并带归档路径。解析只接受抖音域名和明确的作品/用户模式，保留 Python 对短链接的后续解析。
- [ ] **Step 4: 跑该测试、完整 C# 测试和 Debug x64 构建，确认通过。**

### Task 2: Python 纯函数边界

**Interfaces:** `engine/sites/douyin.py` 提供 `read_netscape_cookies(path)->str`、`parse_target(value)->tuple[str,str]`、`map_user(raw)->dict`、`media_files(item,original_quality)->list[dict]`。`media_files` 的每项至少含 `item_id`、`url`、`suffix`、`index`。

- [ ] **Step 1: 先写失败的 `engine/tests/test_douyin.py`。** 用临时 Netscape Cookie 文件断言只保留有效的 `.douyin.com`、`www.douyin.com` 项；用字面量响应字典断言 `sec_uid`、昵称、头像和作品数映射；视频候选按分辨率、帧率、码率选取，`original_quality=true` 调用库的原质量规则；图集每张图片产生一项；无效或异域 URL 抛 `ValueError`。临时 Cookie 值仅用虚构值。
- [ ] **Step 2: 运行 `python -m pytest engine/tests/test_douyin.py -q`，确认接口缺失导致失败。**
- [ ] **Step 3: 实现纯函数。** Cookie 读取使用 `http.cookiejar.MozillaCookieJar`，只导出允许域且未过期项；链接使用 `urllib.parse` 校验 host 和 path 后取 ID；媒体映射复用库 `src.extract.Extractor` 的质量逻辑或与该模块同一解析接口，禁止写签名媒体 URL 到日志。
- [ ] **Step 4: 跑 Python 测试和 Debug x64 构建。**

### Task 3: Python 运行适配与协议

**Interfaces:** `douyin.whoami(cookies_path)->dict`、`douyin.user_info(cookies_path,input)->dict`、`douyin.download(cookies_path,spec)->None`；runner 继续发协议 v1 事件。`whoami` 只在可信认证响应给出当前用户身份时返回；网络或平台异常抛普通异常，明确未登录抛 `AuthError`。

- [ ] **Step 1: 写失败测试。** 在 `engine/tests/test_douyin.py` 注入受控第三方 API 响应验证账号和用户事件；构造临时媒体目录与假传输层，断言 `file-start`、`file-done(size)`、`file-skip`、`job-done` 的顺序和计数；传输失败断言失败数增加且没有 `file-done`；用户取消断言 `.part` 被清理。`engine/tests/test_runner.py` 断言 `load_site("douyin")` 与下载路由，未知站点仍拒绝。
- [ ] **Step 2: 运行上述 Python 测试，确认新路由/行为尚缺而失败。**
- [ ] **Step 3: 用第三方库 `Account`、`User`、`Detail`、`src.link.Extractor` 和 `src.extract.Extractor` 构成适配。** 每次任务独立创建并关闭 `curl_cffi.requests.AsyncSession`；使用最小参数对象提供库要求的 `headers`、`logger`、`douyin_params`、`client` 等字段；禁止第三方进度和日志写 stdout。用户作品按库分页结果处理，单条链接解析为作品 ID。文件先写 `.part` 再原子替换；归档记录在文件成功后写入。只在确切认证错误时抛 `AuthError`。
- [ ] **Step 4: 修改 `runner.py` 的 `cmd_download`，抖音走 `douyin.download`，X/Pixiv 仍走 gallery-dl；`whoami` / `user-info` 经 `load_site` 复用。跑 `python -m pytest engine/tests -q` 与 `dotnet test src/GGdown.sln -p:Platform=x64`，再跑 Debug x64 构建。**

### Task 4: UI 入口与资料刷新

**Interfaces:** `DownloadsViewModel.SupportsPermalink` 及 `DownloadPermalinkCommand`；抖音用户管理隐藏关注列表和高光。账号导入继续使用 `SiteSettingsViewModel`。

- [ ] **Step 1: 写失败 C# 测试。** `DownloadsViewModelTests` 断言抖音链接进入 `EnqueuePermalinkAsync(..., ContentKind.Permalink, ...)`，非法输入不入队且显示错误；`UsersViewModelTests` 断言抖音不提供关注列表/高光；`UserServiceTests` 断言资料刷新将 `https://www.douyin.com/user/<RestId>` 交给引擎。
- [ ] **Step 2: 运行三个定向测试，确认因缺少抖音路径而失败。**
- [ ] **Step 3: 实现 UI。** 在下载页新增独立链接输入，按 `SupportedKinds.Contains(ContentKind.Permalink)` 显示；调用站点提供器解析并入队，不落入 X 搜索逻辑。用户页关注按钮绑定能力可见性，高光按钮按行能力隐藏。为平台栏和界面设置补抖音图标/占位字形的可见规则。资料刷新按站点生成正确主页 URL。Cookie 对话框文案继续是 Netscape 格式说明。
- [ ] **Step 4: 运行定向与完整 C# 测试、Debug x64 构建。**

### Task 5: 发行包、许可与集成验证

**Interfaces:** `scripts/build-engine.ps1` 要求本地第三方检出为指定提交，内置 Python 可导入抖音适配和 `curl_cffi`；`scripts/build.ps1` 在安装资产中提供该库 GPL-3.0 许可证。

- [ ] **Step 1: 先写可执行构建检查。** 构建脚本对缺失目录/错误提交返回清楚错误；安装依赖后执行内置 Python 的 `import curl_cffi; import src.interface.account; import src.interface.detail` 与 runner `hello`。脚本测试通过临时路径运行失败分支，不删除现有 `dist/`。
- [ ] **Step 2: 跑失败分支，确认当前脚本未执行版本/导入检查。**
- [ ] **Step 3: 修改脚本与 `THIRD-PARTY.md`。** 用项目锁定的 Python 依赖安装到内置 `site-packages`；把第三方 `src/` 复制到该目录可导入的位置；拷贝 `third_party/TikTokDownloader/license`，列出实际打包依赖与许可；构建说明明确本地检出的取得与固定提交。开发态为 `RunnerEngineOptions.GalleryDlPath` 追加第三方父目录，同时保持既有 gallery-dl 路径。
- [ ] **Step 4: 运行脚本检查、Python/C# 全套测试和最终 Debug x64 构建。在能联网的环境用测试 Cookie 本地核验账号、用户视频、图集与单条链接，并确认输出/日志不含 Cookie；若网络或依赖不可用，记录未验证项，不将其写成通过。**

## Self-review

- 范围、Cookie、画质、JSONL、UI、打包与许可均有对应任务。
- 每项测试先失败再实施。后续任务沿用 `DouyinSiteProvider`、`RestId=sec_user_id`、`original_quality` 和 `ContentKind.Permalink`，名称一致。
- `third_party/` 是本机忽略目录，发行构建必须显式检查它，不能假定干净克隆拥有源码。
