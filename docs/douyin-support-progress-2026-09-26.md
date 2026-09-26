# 抖音支持：当前进度与交接（2026-09-26）

## 已确认范围

- 沿用 Cookie 导入和手动添加用户。
- 下载用户发布作品与单条作品链接，包含视频、图集；不做关注列表、喜欢、收藏、搜索或直播。
- 站点选项「最高质量视频」默认关闭；启用时使用 TikTokDownloader 库的原质量选择规则。
- 开发态改用项目内 `engine/.venv/Scripts/python.exe`，由 uv 按 `engine/uv.lock` 管理 Python 3.13.7 和依赖。
- 代码修改后构建；纯讨论不重复构建。

设计与实施计划分别在 `docs/superpowers/specs/2026-09-26-douyin-support-design.md` 和 `docs/superpowers/plans/2026-09-26-douyin-support.md`。`docs/superpowers/` 当前被 Git 忽略，文件仍保留在本地。

## 已写入但尚未提交

- C#：`DouyinSiteProvider`、站点目录与 DI 注册、账号/用户页入口、单条作品链接入队、按 `sec_user_id` 刷新用户资料、隐藏抖音不支持的关注列表和高光入口；相关测试已添加。
- Python：`engine/sites/douyin.py` 提供 Cookie 与链接解析、用户/作品映射、第三方 `Account`/`User`/`Detail`/`Extractor` 调用、媒体流写入、归档和 JSONL 文件事件；`engine/runner.py`、`engine/sites/__init__.py` 已接入抖音路由。
- uv：`engine/.python-version`、`pyproject.toml`、`uv.lock` 和 `scripts/run.ps1` 已接入项目虚拟环境；Debug 的 `App.xaml.cs` 指向项目内解释器。
- 发行：`scripts/build-engine.ps1` 在清理输出前校验第三方检出为提交 `473c90ff70c663cfb69310fff2b8d5192f200661`，用 uv 锁文件安装依赖并验证内置 Python 导入；`scripts/build.ps1` 拷贝 GPL 许可证，`THIRD-PARTY.md` 已更新。
- `third_party/TikTokDownloader` 是本机被忽略的独立检出，不会随本仓库提交；发布构建要求该检出存在。

## 最近验证结果

- `engine/.venv/Scripts/python.exe -m pytest engine/tests -q --basetemp=.cache/pytest-engine-after --tb=short`：**51 passed**。
- `dotnet test src/GGdown.sln -p:Platform=x64 --no-restore --nologo`：**279 passed，1 skipped**。
- `dotnet build src/GGdown.App/GGdown.App.csproj -c Debug -p:Platform=x64 --no-restore`：**成功，0 错误**；输出仍在快捷方式指向的 Debug x64 目录。
- `uv sync --project engine --locked --offline`：成功，检查 39 个包。
- 发行引擎曾在 `.cache` 下的临时目录冒烟构建成功，安装 34 个锁定依赖并通过第三方模块导入和协议 v1 `hello` 检查；未清理或覆盖 `dist/`。
- 常规 `dotnet test` 的还原步骤因系统拒绝读取用户级 `NuGet.Config` 而失败；以上 C# 测试与构建使用既有还原结果的 `--no-restore` 完成。

## 复查发现、尚未修复

1. ~~**API 失败可能误报成功。**~~ `engine/sites/douyin.py` 的 `DouyinClient.posts()` 已改为 `single_page=True` 并在空列表时抛出普通错误；`_download_with_client` 也补充了空结果保护。已补测试。
2. ~~**损坏的 Cookie 文件可能泄露值。**~~ `read_netscape_cookies` 已捕获 `MozillaCookieJar.load()` 异常并改抛通用 `AuthError("Cookie 文件解析失败")`。已补测试。
3. ~~**进程被取消时 `.part` 可能残留。**~~ 下载前会清理同目标已有的 `.part` 文件，避免被 C# 终止进程树后残留。已补测试。
4. ~~**部分分享短链未覆盖。**~~ Python `parse_target` 已接受 `www.iesdouyin.com/share/user/...` 与 `share/(video|note|slides)/...`；C# `DouyinSiteProvider.ParseInput` 同步支持并区分用户/作品短链。已补测试。
5. ~~**已安装用户升级后可能继续使用旧引擎。**~~ `App.xaml.cs` 的首启播种逻辑已改为读取安装目录与 AppData 内的 `engine.json` 版本戳，不一致时重新复制引擎。

## 未验证与接续建议

- 本地 `docs` 下的测试 Cookie 曾返回抖音 `status_code=8`（未登录）；尚未完成真实账号、用户作品、图集和单条链接的在线端到端验证。不要输出或提交 Cookie 内容。
- 上述 5 项回归测试与代码修复已补齐并验证通过；Python 全套 58 passed，C# 全套 285 passed / 1 skipped，Debug x64 构建成功。
- 工作区此前已有大量未提交的其他变更（侧栏、图标、关注列表等），未清理、未提交；续作时按文件核对差异，避免混入或覆盖。`AGENTS.md`、`CLAUDE.md`、Cookie、`dist/` 均不得提交。
