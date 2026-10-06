# GGdown

[![CI](https://github.com/zhouhaltija/gallery-gui/actions/workflows/ci.yml/badge.svg)](https://github.com/zhouhaltija/gallery-gui/actions/workflows/ci.yml)
[![Windows Release](https://github.com/zhouhaltija/gallery-gui/actions/workflows/release.yml/badge.svg)](https://github.com/zhouhaltija/gallery-gui/actions/workflows/release.yml)

GGdown 是面向 **X（Twitter）、Pixiv 和抖音**的中文 Windows 下载工具。使用 WinUI 3 构建界面，以 gallery-dl 和 TikTokDownloader 作为下载引擎，提供账号管理、作者批量下载、单条作品下载和下载记录管理。

## 功能

| 平台 | 已支持的主要功能 |
| --- | --- |
| X（Twitter） | 作者媒体、关注列表导入、增量下载、单条推文与列表链接、搜索、账号喜欢和书签 |
| Pixiv | 作者插画与漫画、小说、账号收藏；可为作者选择下载内容 |
| 抖音 | 作者发布作品、增量下载、视频或图集筛选、最早发布日期、单条作品；支持短链接和整段分享文本 |

- 多平台下载队列、并发控制、取消任务、失败原因显示和打开下载目录。
- 用户资料、置顶、跳过、批量选择和下载历史管理。
- 下载归档，重复下载时跳过已经处理的文件。
- 全局下载目录、HTTP/SOCKS 代理和总下载限速。
- 所有并发任务共享限速额度；设置 `0` 表示不限速，`1024 KB/s` 约为 `1 MB/s`，修改后对运行中的任务生效。

## 安装与使用

支持 Windows 10 1809 及更新版本、Windows 11，**x64** 架构。

1. 在 [Releases](https://github.com/zhouhaltija/gallery-gui/releases) 下载 `GGdown-Setup-<版本>.exe` 并安装。
2. 打开软件，选择平台，在「账号与选项」导入该平台的 Netscape 格式 `cookies.txt`。
3. 添加作者主页或用户 ID，在「用户管理」下载作品；也可在「下载」页粘贴作品链接。
4. 在左下角「设置 → 通用」调整下载目录、并发数和总下载限速；代理位于「网络」页。

安装包包含 .NET、Windows App SDK 和 Python 引擎，无需另行安装开发工具。安装位置为 `%LOCALAPPDATA%\Programs\GGdown`，使用当前用户权限。

**Pixiv** 还需要 refresh token。在开发环境可运行：

```powershell
uv run --project engine --locked gallery-dl oauth:pixiv
```

按 gallery-dl 的 OAuth 流程取得 token，并填入软件的 Pixiv 导入对话框。

**抖音单个视频**可以粘贴纯链接，也可以粘贴包含 `https://v.douyin.com/.../` 的整段分享文本。在用户页点击「下载单个视频」即可进入下载页，无需先添加作者。

默认下载目录为 `%USERPROFILE%\Downloads\GGdown`。账号 Cookie、设置、数据库、归档和日志保存在 `%LOCALAPPDATA%\GGdown`，卸载软件会保留这些数据。Cookie 和 refresh token 属于登录凭据，请不要上传到仓库或公开分享。

## 本地开发

需要 Git、PowerShell 7、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 和 [uv](https://docs.astral.sh/uv/getting-started/installation/)。构建 WinUI 应用需要 Windows。

```powershell
git clone https://github.com/zhouhaltija/gallery-gui.git
cd gallery-gui
pwsh scripts/prepare-dependencies.ps1
uv sync --project engine --locked
dotnet build src/GGdown.App/GGdown.App.csproj -c Debug -p:Platform=x64
& ./src/GGdown.App/bin/x64/Debug/net8.0-windows10.0.22621.0/GGdown.exe
```

`uv` 根据 `engine/.python-version` 安装 Python 3.13.7，并根据 `engine/uv.lock` 创建 `engine/.venv`。开发版直接使用这份虚拟环境和仓库内的 Python 源码。

抖音第三方源码由脚本检出到 `third_party/TikTokDownloader` 的固定提交。该目录不提交到 Git；如果已有目录的版本或内容不匹配，脚本会停止，避免覆盖本地修改。

### 测试

```powershell
dotnet test src/GGdown.sln
uv run --project engine --locked python -m pytest engine/tests -q
```

部分真实网络烟测会在缺少所需源码或运行环境时跳过。测试不需要真实账号 Cookie。

## 本地打包

另外安装 [Inno Setup 6 或 7](https://jrsoftware.org/isinfo.php)，然后运行：

```powershell
pwsh scripts/build.ps1 -AppVersion 1.0.0
```

生成：

```text
dist/
├── app/                          # 独立运行的 Release x64 应用
├── engine/                       # Python 3.13.7、依赖和下载适配器
├── licenses/                     # 项目与第三方许可证
├── GGdown-Setup-1.0.0.exe         # 安装包
└── GGdown-Setup-1.0.0.exe.sha256  # SHA-256 校验值
```

构建会重新生成 `dist/`。依赖使用锁文件及其哈希，抖音源码固定到指定提交。只验证应用与引擎、暂不编译安装包时，可使用 `-SkipInstaller`；验证输出也可指定为 `.cache` 下的目录：

```powershell
pwsh scripts/build.ps1 -AppVersion 1.0.0 -SkipInstaller -OutputDirectory .cache/release-check
```

## CI/CD 与 tag 发布

仓库提供两条 GitHub Actions 工作流：

- **CI**：分支提交、Pull Request 和手动运行时，安装锁定依赖，执行 C# / Python 测试并构建 Windows 应用。测试报告保存在工作流的 Artifacts 中。
- **Windows Release**：推送 `v*` tag 后，先执行 CI；通过后构建 Windows x64 `.exe` 安装包和 SHA-256 文件，再创建 GitHub Release 并上传产物。

### 首次启用

1. 将 `.github/workflows/`、`scripts/`、README 和功能源码提交并推送到 GitHub。确认新的 Python 模块也已提交；工作流只构建 tag 指向的提交。
2. 在仓库 **Settings → Actions → General** 确认 GitHub Actions 可运行这些官方 actions。若组织限制 token 写入，也需要允许发布工作流的 `contents: write`。
3. 在 **Actions → CI** 确认测试与构建通过，再推送版本 tag。

发布使用 GitHub 自动提供的 `GITHUB_TOKEN`，无需另外配置个人 token 或第三方账号密钥。普通 CI 只有只读权限；只有上传 Release 的 job 具有仓库内容写权限。

### 发布正式版

先提交并推送要发布的代码，再给该提交打 tag：

```powershell
git push origin HEAD
git tag -a v1.0.0 -m "发布 GGdown 1.0.0"
git push origin v1.0.0
```

前往 **Actions → Windows Release** 查看构建进度。成功后，安装包出现在 [Releases](https://github.com/zhouhaltija/gallery-gui/releases)。构建 job 同时保留 `windows-installer` Artifact，便于在发布上传失败时取回产物。

### 发布预览版

```powershell
git tag -a v1.1.0-rc.1 -m "发布 GGdown 1.1.0 候选版"
git push origin v1.1.0-rc.1
```

带后缀的版本会标记为 GitHub Pre-release。版本号使用 `v主版本.次版本.修订号`，可追加 `-rc.1` 等后缀；每段数字不超过 `65534`。安装器显示完整发布版本，应用程序集和 Windows 文件版本使用对应的纯数字版本。

构建失败时修复代码并使用新版本 tag；同一 tag 的工作流重跑会更新已有 Release 的同名安装包。安装包当前未配置代码签名。

## 项目结构

```text
src/GGdown.App/     WinUI 3 界面、XAML 与资源
src/GGdown.Core/    数据库、站点配置、下载队列和视图模型
src/GGdown.Tests/   C# xUnit 测试
engine/            Python 下载引擎、站点适配与测试
installer/         Inno Setup 安装配置
scripts/           依赖准备及发行构建
.github/workflows/  CI 和 tag 发布工作流
```

## 许可证

GGdown 使用 [GPL-3.0](LICENSE)。第三方组件的来源、固定版本及许可证见 [THIRD-PARTY.md](THIRD-PARTY.md)。
