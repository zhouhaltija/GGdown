# GalleryGUI 设计规格

- 日期：2026-08-29
- 状态：已与需求方逐节确认
- 工作代号：GalleryGUI（产品名后续可再定）
- 关联文档：`docs/x.md`（gallery-dl 下载 X 的使用指南，本设计的用户流程参照）

## 1. 概述

基于 gallery-dl 开发的 Windows 桌面下载器，提供现代 GUI（C# + WinUI 3 + MVVM），用于方便地按用户批量下载网站媒体。第一版支持 X (Twitter)：用户媒体、账号喜欢、账号书签。架构以"站点抽象 + 站点无关引擎"为核心，后续可扩展其他 gallery-dl 支持的站点（如 Pixiv）。

**产品定位**：公开发布（GitHub Releases 分发）。
**许可**：应用与 runner 采用 GPL-3.0 开源（详见 §10）。

**V1 下载范围**（已确认）：用户媒体（图片+视频）+ 账号喜欢 + 账号书签。

## 2. 总体架构

### 2.1 技术栈

| 层 | 选型 |
|---|---|
| UI 框架 | WinUI 3 / Windows App SDK 1.6+，self-contained 部署 |
| 运行时 | .NET 8 (LTS) |
| MVVM | CommunityToolkit.Mvvm（源生成器） |
| DI | Microsoft.Extensions.DependencyInjection |
| 数据 | EF Core 8 + SQLite（WAL）+ Migrations |
| 日志 | Serilog，滚动文件 |
| 测试 | xUnit |
| 引擎 | gallery-dl（Python）+ 嵌入式 Python 3.12 + 自研 runner.py 适配器 |

### 2.2 与 gallery-dl 的集成方式（关键决策）

采用**方案 B：嵌入式 Python 运行时 + 薄 Runner 脚本**。C# 不直接解析 gallery-dl 的人读输出，不直接调 X 的 GraphQL queryId 接口；一切引擎交互经 runner 的稳定 JSONL 协议。关注列表、认证处理、queryId 轮换等站点易变逻辑由 gallery-dl 上游维护兜底。

已否决的方案：A（捆绑 gallery-dl.exe 解析 stdout——关注列表无干净出口、进度解析易碎）；C（C# 重写 X API 客户端——与站点变动对抗不可维护）。

### 2.3 部署形态与目录策略

- **非打包 WinUI3 应用 + Inno Setup 每用户安装**（安装到 `%LOCALAPPDATA%\Programs`，无需管理员）。不走 MSIX：旁加载需用户信任签名证书，公开分发体验差。
- 通过 GitHub Releases 分发：便携 zip + 安装器。
- **引擎不放在安装目录**：首次运行（或引擎目录缺失/版本不匹配）时从安装目录播种到应用数据目录，因为 gallery-dl 需要应用内热更新，安装目录不可写。

应用数据布局：

```
%LOCALAPPDATA%\GalleryGUI\
├── data\gallery.db            # SQLite
├── engine\                    # python\ + site-packages\ + runner.py + sites\
├── accounts\<site>\<id>\cookies.txt
├── archive\<account-id>.txt   # gallery-dl download-archive（每账号一个）
├── logs\                      # Serilog 滚动日志
└── temp\                      # 运行期临时文件
```

下载根目录由用户在设置中选择，默认 `%USERPROFILE%\Downloads\GalleryGUI`。

### 2.4 项目结构与依赖方向

```
E:\Projects\gallery-gui\
├── gallery-dl\                  # 上游源码（构建脚本从此打包引擎）
├── src\
│   ├── GalleryGUI.App\          # WinUI3 壳：Views、资源、DI 装配（唯一含 XAML 的项目）
│   ├── GalleryGUI.Core\         # 领域层：实体、服务接口、站点抽象、引擎适配器、ViewModels
│   └── GalleryGUI.Tests\        # xUnit
├── engine\                      # runner.py + sites\（Python，随包分发）
├── installer\                   # Inno Setup 脚本
└── docs\
```

依赖方向：App → Core，单向。Core 不引用任何 UI 框架；ViewModel 全部在 Core，便于单元测试。引擎细节（进程、JSONL）封装在 `Core\Engine` 之后，Core 其余部分只面对 `IDownloadEngine` 接口。

## 3. 引擎集成（Runner 协议）

### 3.1 engine 目录

`engine\python\`（嵌入式 Python 3.12）、`engine\site-packages\`（gallery_dl 及依赖）、`engine\runner.py`（约 200 行适配器）、`engine\sites\`（每站点一个模块）。

### 3.2 子命令

所有输出走 stdout JSONL（每行一个 JSON 对象）；诊断日志走 stderr。每次调用第一行输出握手事件。

| 命令 | 用途 | 实现依据 |
|---|---|---|
| `whoami --site twitter --cookies <file>` | 校验 cookie、返回登录账号昵称/handle | X v1.1 `account/settings` 端点（比 GraphQL queryId 稳定；Authorization 用 X 公共 web Bearer token，X-CSRF-Token 取自 cookie 的 `ct0`） |
| `list-following --site twitter --cookies <file>` | 输出关注用户数组（含 rest_id、screen_name、display_name、avatar_url） | gallery-dl `TwitterAPI.user_following()`，上游维护 |
| `user-info --site twitter --cookies <file> --input <用户名或链接>` | 解析单个用户资料（手动添加用户用） | gallery-dl `user_by_screen_name` |
| `download --cookies <file> --job <job.json>` | 执行下载 | gallery-dl job API + output 模块包装 |

握手事件：`{"ev":"hello","protocol":1,"runner":"1.0.0","gallery_dl":"<版本>"}`。C# 校验 `protocol` 兼容性。

### 3.3 下载事件流

runner 包装 gallery-dl 的 output 模块，把下载动作翻译为结构化事件（不解析人读文本）：

```jsonl
{"ev":"url-start","url":"https://x.com/USER/media"}
{"ev":"file-start","path":"...","item_id":"1234","user":"USER"}
{"ev":"file-done","path":"...","size":48213}
{"ev":"file-skip","path":"...","reason":"archive"}
{"ev":"log","level":"warning|error","msg":"..."}
{"ev":"job-done","total":120,"skipped":80,"failed":0}
{"ev":"fatal","msg":"..."}
```

- `item_id` 为来源内容 ID（X 上即 tweet_id），可获取时附带；
- `file-skip.reason`：`archive`（download-archive 命中）/ `exists`（文件已存在）；
- 事件即进度：UI 显示文件计数 + 当前文件名（任务级粒度，已确认）。

### 3.4 job.json（C# 生成）

```json
{
  "urls": ["https://x.com/USER/media"],
  "base_directory": "D:/Downloads/GalleryGUI",
  "options": {
    "cookies": "<账号 cookies.txt 绝对路径>",
    "videos": true, "retweets": false, "quoted": false, "replies": false,
    "filename": "{tweet_id}_{author[name]}_{num}.{extension}",
    "download_archive": "<archive 绝对路径>",
    "sleep_request": "1"
  }
}
```

选项由 `ISiteProvider.BuildDownload` 按 `SiteOptions` 映射生成（见 §5）。

### 3.5 Cookie 安全

- 录入方式：导入 Netscape 格式 cookies.txt 文件（已确认，仅此一种；粘贴字符串不做）。
- 文件复制到 `%LOCALAPPDATA%\GalleryGUI\accounts\<site>\<id>\cookies.txt`，**明文存放**。导入对话框显示提示："Cookie 将明文保存在本机，等同于浏览器登录态，请勿分享给他人"。
- 后续演进：DPAPI 按用户加密落盘、调用时解密到 temp、用后即删（见 §11）。

### 3.6 进程管理

- 取消 = 杀进程树（python 及其子进程）；已下文件由 download-archive + 数据库去重保证重下不重复。**V1 队列操作只有取消**，不做暂停/恢复。
- 并发默认 1（X 限流敏感），设置可调；并发数是全局队列属性。
- 子进程非零退出且未收到 `job-done` → 任务失败；`fatal` 事件 → 任务失败并取其消息。

## 4. 数据库设计

EF Core 8 + SQLite（WAL），`%LOCALAPPDATA%\GalleryGUI\data\gallery.db`，Migrations 管理演进。

### 4.1 表结构

**accounts（站点账号）**

| 列 | 说明 |
|---|---|
| Id PK | 自增 |
| SiteId | "twitter" |
| DisplayName / ScreenName | 登录账号昵称 / @handle |
| CookiePath | 相对 accounts 目录路径 |
| Status | Unverified / Ok / Invalid |
| IsActive | 每站点启用一个活动账号（V1 约束；表结构允许多账号） |
| AddedAt / VerifiedAt | |

**users（目标用户，核心表）**

| 列 | 说明 |
|---|---|
| Id PK | 自增 |
| SiteId + RestId | `UNIQUE(SiteId, RestId)`，站点内部用户 ID |
| ScreenName / DisplayName / AvatarUrl / ProfileUrl | 资料快照（导入时刷新） |
| Source | Following / Manual / Link |
| OwnerAccountId FK | 经哪个账号的 cookie 获取 |
| IsPinned | 置顶常用 |
| DownloadCount | 下载次数（冗余计数，任务完成事务内聚合更新）。口径：该用户 user_media 任务中 Status=Downloaded 的文件累计数；likes/bookmarks 文件不属于任何用户，不计入 |
| LastDownloadAt | 末次下载时间 |
| AddedAt / UpdatedAt | |

**download_jobs（下载任务）**

| 列 | 说明 |
|---|---|
| Id PK / AccountId FK | |
| TargetKind | user_media / account_likes / account_bookmarks |
| UserId FK nullable | likes、bookmarks 属于账号自身，此列为空 |
| Status | Pending / Running / Completed / Failed / Canceled |
| TotalFiles / DoneFiles / SkippedFiles / FailedFiles | 计数 |
| ErrorMessage | 失败摘要 |
| CreatedAt / StartedAt / FinishedAt | |

**download_files（文件记录 = 历史与溯源）**

| 列 | 说明 |
|---|---|
| Id PK / JobId FK / UserId FK nullable | |
| SourceItemId | 来源内容 ID（tweet_id）；列名通用化适配未来站点 |
| Url / FilePath / FileSize | FilePath 相对下载根目录 |
| Status | Downloaded / Skipped / Failed |
| CreatedAt | |

**settings**：Key（PK）- Value（可为 JSON 字符串）。

索引：`users(SiteId, RestId)` 唯一；`download_files(JobId)`、`download_files(UserId)`；`download_jobs(Status)`。

### 4.2 去重与统计策略

- **引擎层去重**：每账号一个 gallery-dl download-archive 文件，重下自动跳过——这是防重复的第一道闸。
- **应用层记录**：download_files 全部落库，支撑下载次数、末次下载时间与历史页。
- 聚合时机：job-done 后单事务更新 users.DownloadCount / LastDownloadAt，避免边下边写。
- 崩溃恢复：应用启动时把遗留 Running 任务标为 Failed（可重试）。

## 5. 站点扩展抽象

引擎（gallery-dl）站点无关；站点差异收敛在 `Core\Sites`：

```csharp
public interface ISiteProvider
{
    string SiteId { get; }
    string DisplayName { get; }
    IReadOnlyList<ContentKind> SupportedKinds;   // UserMedia / AccountLikes / AccountBookmarks
    UserInputParseResult ParseInput(string input);
    DownloadPlan BuildDownload(ContentKind kind, UserTarget target, SiteOptions options);
    SiteOptions DefaultOptions { get; }
    OptionSchema OptionsSchema { get; }
}
```

- `ParseInput` 接受：裸用户名、`x.com/用户名`、`twitter.com/...` 历史 URL、带查询串或路径后缀的链接；解析失败给出可读错误。
- `ContentKind` 为全局枚举，映射 UI 徽标与任务类型；`UserTarget` 区分目标用户（user_media）与登录账号（likes/bookmarks）。
- `DownloadPlan` = gallery-dl URL 列表 + 该次下载配置覆盖（§3.4）。
- `OptionSchema`：选项键、类型（bool/文本/枚举）、默认值、显示名——设置页据此**自动渲染控件**，新站点无需新 XAML。

runner 侧对应：`engine\sites\twitter.py` 实现 `list-following`、`user-info` 等需要站点知识的命令。**两侧之间协议保持站点无关**：`download` 命令只收 URL 与配置。

新增站点（如 Pixiv）的完整工作量：C# 一个 `ISiteProvider` 实现 + runner 一个站点模块 + DI 注册；导航、账号、队列、历史、设置页渲染全部复用。

明确不做：运行时插件动态加载。新站点 = 改代码发版，保持协议简单（YAGNI）。

## 6. UI 设计

NavigationView 左侧导航 + Mica 背景 + 跟随系统深浅色。界面语言 V1 为中文，字符串全部走 resw 资源，为本地化预留。

### 6.1 用户管理（主页）

- 顶部工具栏：添加用户（ContentDialog：用户名或链接）、导入关注列表（需有有效账号，导入中显示进度）、搜索框、排序（末次下载/下载次数/添加时间）。
- DataGrid：三态全选框、头像、用户名/显示名、来源徽标、下载次数、末次下载时间、行内操作（立即下载/打开主页/置顶/删除）。
- 底部随选择浮出操作条："已选 N 个 → 下载选中"。
- 空状态三步引导：导入 cookie → 验证账号 → 导入关注列表。

### 6.2 下载页

- 活动任务卡片：目标（头像+用户名，或"账号的喜欢/书签"）、类型徽标、进度条、文件计数（已完成/总数）、当前文件名、取消按钮；任务栏同步进度。
- 顶部入口"下载账号内容"：喜欢 / 书签。
- 完成任务自动转入历史页。

### 6.3 历史页

- 筛选：按用户、日期范围、任务类型。
- 文件列表：文件名、所属用户、SourceItemId（可点击打开 `x.com/i/status/<id>`）、大小、时间、状态。
- 操作：打开所在文件夹（explorer /select）、打开原文链接。

### 6.4 设置页（SettingsCard 风格）

- 通用：下载目录（选文件夹）、并发下载数。
- 账号：账号卡片（昵称、@handle、状态徽标、导入/更换 cookies.txt、重新验证、下载喜欢/书签快捷入口）；导入时明文提示。
- 站点 - X：按 OptionSchema 渲染（视频、转推/引用/回复过滤、命名模板带预览）。
- 引擎：gallery-dl 版本、检查/更新引擎按钮、打开日志文件夹。
- 关于：应用版本、检查更新（GitHub Releases）、开源地址、许可证信息。

## 7. 下载流程与错误处理

### 7.1 正常流

选中用户 → 任务落库（Pending）→ 调度器按并发设置取任务 → `BuildDownload` 生成 job.json → 启动 runner → 事件流实时更新任务行与 download_files 记录 → job-done 事务聚合更新用户统计 → 完成转入历史。

### 7.2 错误路径

| 场景 | 处理 |
|---|---|
| runner 崩溃 / 非零退出且无 job-done | 任务 Failed，stderr 尾部摘要入库，UI 提供"重试" |
| 认证失效（事件流识别 auth 错误） | 任务失败 + accounts.Status=Invalid，引导重新导入 |
| 限流 / 网络抖动 | 交给 gallery-dl 自带重试与 sleep；error 事件写任务日志 |
| 用户取消 | 杀进程树，任务 Canceled，archive 保证续传安全 |
| 断电 / 强退 | 启动时 Running → Failed（可重试） |
| 全局未处理异常 | 错误对话框 + Serilog 滚动日志 |

V1 明确不做：暂停/恢复、失败自动重排队。

## 8. 测试策略

- **单元测试**（xUnit）：`ParseInput` 各形态；`BuildDownload` 配置映射快照；JSONL 事件解析器（样例行流）；仓储与统计聚合（SQLite in-memory）；队列状态机（假 `IDownloadEngine` 推送事件序列）。
- **可选端到端冒烟**：本地 HttpListener 静态文件服务 + gallery-dl generic 提取器，验证"进程 → 事件流 → 落库"全链路，无外网依赖。
- **UI**：发布前手测清单。

## 9. 发布与更新

- **构建**：GitHub Actions（Windows runner）：打包引擎（Python embeddable + gallery-dl + runner）→ 测试 → 便携 zip + Inno 安装器 → 附 GitHub Release。
- **应用更新**：设置页"检查更新"查 GitHub Releases API，提示并下载新安装器运行（每用户安装无需管理员）。V1 不做静默自动更新。
- **引擎更新**：设置页从 PyPI 拉最新 gallery-dl wheel 覆盖 `%LOCALAPPDATA%` 引擎 site-packages——站点改版修复当天生效，不等应用发版。
- **版本策略**：SemVer + CHANGELOG。

## 10. GPL 合规（公开发布必须项）

- gallery-dl 为 GPL-3.0。随包分发其源码，须附 GPL-3.0 许可证全文与源码获取地址（未修改上游则链接上游仓库即可；若修改需提供修改版源码）。
- runner.py 直接 import gallery-dl 且随包分发，按保守处理与主程序一并采用 **GPL-3.0**；应用仓库公开开源。
- 子进程调用本身属聚合分发，但统一开源是最干净、对社区最友好的路线。
- "关于"页展示第三方组件与许可证清单。

## 11. 后续演进（V1 之外）

- Cookie DPAPI 加密存放；
- 暂停/恢复下载任务；
- 多账号管理（表结构已支持，UI 暂单账号）；
- 新站点接入（Pixiv 等，按 §5 清单）；
- 失败自动重排队、定时自动增量下载；
- 系统托盘与完成系统通知。

## 12. 已确认的需求决策记录

| 决策点 | 结论 |
|---|---|
| 使用场景 | 公开发布（影响 GPL、更新、异常兜底） |
| V1 下载范围 | 用户媒体 + 喜欢 + 书签 |
| Cookie 录入 | 仅 cookies.txt 文件导入 |
| 进度粒度 | 任务级（文件计数 + 当前文件名） |
| 集成方案 | 方案 B：嵌入式 Python + runner JSONL 协议 |
| 队列操作 | 仅取消；并发默认 1 可调 |
| 许可 | 应用整体 GPL-3.0 开源 |
