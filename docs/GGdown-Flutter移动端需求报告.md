# GGdown 需求报告（Flutter 移动端）

- **文档性质**：从现有 Windows 桌面版反推产品需求，作为 Flutter 手机 App 的需求基线
- **来源仓库**：`gallery-gui`（WinUI 3 + .NET 8 + 嵌入式 Python/gallery-dl）
- **目标产品**：基于 gallery-dl 的移动端媒体下载器（Flutter）
- **日期**：2026-09-06
- **桌面版参考版本**：`dev` 分支，提交 `8e01d29`（Pixiv 接入 + X 体验增强）

本文描述的是**产品要做什么**，不是桌面代码怎么抄。桌面实现只作为「已经验证过的需求」和「不要丢掉的约束」。

---

## 1. 产品是什么

GGdown 是一个**按站点管理创作者、用登录态下载媒体、可增量续下**的桌面客户端。它不是 gallery-dl 的图形壳（不是把 CLI 参数铺成表单），而是：

1. 用户先导入 Cookie（登录态）
2. 维护一份「要下的人」名单
3. 按人 / 按账号内容 / 按链接入队
4. 后台跑 gallery-dl，进度、去重、历史都落在本地

移动端应沿用同一产品模型：**名单驱动 + 队列下载 + 本地归档**，而不是「贴一条 URL 按一次下载」的一次性工具。一次性贴链接可以作为入口，但不能替代名单。

### 1.1 一句话

给已经在用 X / Pixiv 的人，在手机上维护创作者名单、排队下载图片和视频，文件落到本机相册或指定目录，下次只下增量。

### 1.2 明确不做（相对 gallery-dl 全集）

gallery-dl 支持几百个站点。桌面版刻意做成**站点插件表 + 逐步开放**：

| 状态 | 站点 |
|---|---|
| 已开放 | X (Twitter)、Pixiv |
| 目录占位「即将支持」 | FANBOX、Danbooru、Gelbooru、Kemono、Coomer、Fantia、Instagram、Reddit、Tumblr、哔哩哔哩、微博 |

移动端第一期同样应**只做已开放站点**，用同一套站点抽象加新站，不要一上来对接 gallery-dl 全站。

---

## 2. 谁在用、解决什么问题

### 2.1 角色

只有一类用户：**收藏向下载者**。没有多用户、没有云账号、没有社交。

典型行为：

- 从浏览器导出已登录的 Cookie，导入 App
- 从关注列表勾一批画师，或手动加用户 / 粘贴主页
- 隔一段时间再下，期望「已经下过的跳过」
- 偶尔下自己的喜欢 / 书签 / 一条推文 / 一个搜索

### 2.2 核心痛点（桌面已验证）

| 痛点 | 桌面做法 | 移动端同样要解决 |
|---|---|---|
| CLI 难用 | GUI 名单 + 按钮 | 更短路径：分享菜单、剪贴板 |
| 每次全量重爬 | gallery-dl `archive` 文件按账号去重 | 必须保留 archive，否则流量和电量不可接受 |
| 登录态失效中途才发现 | `whoami` 验证 + 下载中 `auth` 致命错误把账号标失效 | 推送/横幅：「去重新登录」 |
| 同一人点两次下载出两个任务 | 队列里未结束则标「下载中」并拒绝再入队 | 更重要：误触多 |
| 下完看不到图 | 历史画廊 | 手机上这是主场景，应比桌面更靠前 |
| 国内访问 X 要代理 | 全局 HTTP/SOCKS 代理 | 系统 VPN / 应用内代理都要考虑 |

### 2.3 成功标准（产品）

用户完成一次「导入 Cookie → 加 3 个用户 → 下载 → 在画廊里看到新文件」，且第二次下载同一用户时，已有文件被跳过、几乎不耗流量。

---

## 3. 信息架构（页面）

桌面是四页 + 顶栏站点切换。移动端建议同一信息结构，改成底栏：

| 桌面 | 建议 Flutter | 职责 |
|---|---|---|
| 用户管理 | **名单** | 创作者列表、多选、下载/暂停/删除、粘贴添加 |
| 下载 | **任务** | 进行中队列、账号内容入口、搜索/链接下载 |
| 历史 | **画廊** | 已下载文件浏览、筛选、打开/分享 |
| 设置 | **设置** | 账号、目录、代理、站点选项、关于 |
| 顶栏站点切换 | **顶栏或设置内站点** | 当前站点作用域：名单/账号/选项都按站点隔离 |

全局能力（桌面 InfoBar）：

- 登录态失效：错误通知，引导去设置
- 任务完成：短提示
- 移动端对应：SnackBar / 系统通知（后台下载完成必须走系统通知）

---

## 4. 功能需求

优先级：

- **P0**：没有就不是这个产品（MVP）
- **P1**：桌面已有、移动端应尽快跟上
- **P2**：桌面有或规划过、可后置
- **M**：仅移动端新增

### 4.1 站点与作用域

| ID | 需求 | 优先级 |
|---|---|---|
| S-01 | 应用有「当前站点」。名单、账号、下载选项、历史筛选都只看当前站点 | P0 |
| S-02 | 切换站点不串数据（用户、Cookie、任务标题可以 squashed 在同一队列 UI，但必须带站点标记） | P0 |
| S-03 | 未实现站点在目录里可见，进入后说明「即将支持」，禁止下载 | P1 |
| S-04 | 站点以插件描述：`siteId`、展示名、支持的内容类型、选项 Schema、输入解析、主页 URL 规则 | P0 |
| S-05 | 每站点最多 **一个活动账号** | P0 |

### 4.2 账号与登录态

| ID | 需求 | 优先级 |
|---|---|---|
| A-01 | 导入 Netscape `cookies.txt`，复制到应用私有目录，不改用户原文件 | P0 |
| A-02 | 导入后立刻 `whoami` 验证；失败则账号 `Invalid`，成功则记下 screen_name / display_name / rest_id | P0 |
| A-03 | 界面必须出现逐字警告：**「Cookie 将明文保存在本机，等同于浏览器登录态，请勿分享给他人」** | P0 |
| A-04 | 可重新验证、可更换 Cookie | P0 |
| A-05 | 下载过程认证失败：任务失败 + 账号标 Invalid + 通知去设置 | P0 |
| A-06 | Pixiv 额外要求 OAuth `refresh-token`（与 cookies 一起保存）。缺 token 不得导入 | P0（做 Pixiv 时） |
| A-07 | 启动不自动联网验证（桌面也是手动验证）；可在设置里点「重新验证」 | P1 |
| A-08 | M：系统分享 / 文件选择器导入 cookies.txt；Android 可从 Downloads 选文件 | M |
| A-09 | M：可选「应用内 WebView 登录后抽取 Cookie」（体验更好，安全与审核风险更高，P2） | M / P2 |

Cookie 路径约定（桌面）：`{appRoot}/accounts/{siteId}/{guid}/cookies.txt`，Pixiv 同目录 `refresh-token.txt`。移动端可沿用相对结构，root 换为应用沙箱。

### 4.3 创作者名单

| ID | 需求 | 优先级 |
|---|---|---|
| U-01 | 列表展示：头像、显示名、`@handle`、来源、下载次数、末次下载 | P0 |
| U-02 | 搜索过滤（用户名 / 昵称），防抖 | P0 |
| U-03 | 排序：末次下载 / 下载次数 / 添加时间；**置顶永远优先** | P0 |
| U-04 | 添加用户：解析输入 → 调引擎 `user-info` → upsert（同一站点 `rest_id` 唯一） | P0 |
| U-05 | 输入解析按站点：X 为 handle 或主页；Pixiv 为数字 ID 或 `/users/{id}` | P0 |
| U-06 | 从关注列表勾选添加（先拉关注，已添加的不可再选，支持搜索） | P0 |
| U-07 | 关注列表可缓存，避免每次全量请求 | P1 |
| U-08 | 置顶 / 取消置顶 | P1 |
| U-09 | 暂停（跳过）/ 恢复。暂停的人：不能点下载，批量下载时排除并提示 | P0 |
| U-10 | 删除名单记录（不删已下载文件） | P0 |
| U-11 | 打开主页（外部浏览器） | P1 |
| U-12 | 打开该用户已下载目录 / 系统文件应用 | P1 |
| U-13 | 多选：全选、取消全选、反选；有选中才出现批量操作 | P1 |
| U-14 | 批量：下载选中、暂停、恢复、删除 | P1 |
| U-15 | 下载选中成功或全部被跳过后，**自动取消选中**；硬失败（无 Cookie 等）保留选中 | P1 |
| U-16 | 队列中未结束的用户：行内按钮改为「下载中」并禁用；批量下载跳过并说明 | P0 |
| U-17 | 无用户且无账号：空状态三步引导（导入 Cookie → 等待验证 → 关注列表） | P1 |
| U-18 | X：粘贴推文 / 列表 / 搜索 / `#话题` / `from:` 查询 → **入队下载，不强制加名单** | P1 |
| U-19 | X：从剪贴板添加（能解析则直接执行，否则打开编辑框） | P1 |
| U-20 | X：行内「高光」单独入队 | P1 |
| U-21 | 刷新资料：更新头像、横幅、简介、关注者、媒体数 | P1 |
| U-22 | 新动态：`media_count - media_count_at_download > 0` 时显示「新 +N」 | P1 |
| U-23 | 用户行可用横幅作背景、显示关注者数（资料刷新后才有） | P2 |

添加来源枚举：`Following` / `Manual` / `Link`。同一 `rest_id` 再添加时更新资料，来源以最后一次为准。

### 4.4 下载队列

| ID | 需求 | 优先级 |
|---|---|---|
| D-01 | 任务模型：Pending → Running → Completed / Failed / Canceled | P0 |
| D-02 | **每个用户每种内容类型一条任务**（例：Alice 媒体一条，Alice 小说一条） | P0 |
| D-03 | 账号级内容（喜欢、书签）一条任务，不绑定名单用户 | P0 |
| D-04 | 并发数可配置，默认 1，最小 1 | P0 |
| D-05 | 进度：已完成 / 跳过 / 失败 / 总数；总数未知且 Running 时为不确定进度 | P0 |
| D-06 | 显示当前文件名 | P1 |
| D-07 | 可取消进行中任务 | P0 |
| D-08 | 应用被杀：启动时把遗留 Pending/Running 标 Failed（「应用异常退出，任务中断」） | P0 |
| D-09 | 下载目录可配置；入队时快照目录与站点选项，中途改设置不影响已入队任务 | P0 |
| D-10 | 使用 gallery-dl archive（按账号一个 archive 文件）做跨次去重 | P0 |
| D-11 | 任务完成回写该用户 `DownloadCount++`、`LastDownloadAt`、`MediaCountAtDownload` | P1 |
| D-12 | 未收到完成事件不得标 Completed | P0 |
| D-13 | M：离开前台仍继续下（Android 前台服务 + 通知；iOS 能力受限，见第 11 节） | M / P0 |
| D-14 | M：任务完成系统通知，点击进画廊或任务页 | M / P0 |
| D-15 | 当前文件若是图片，任务卡片可显示缩略图 | P2 |

内容类型（`TargetKind` / `ContentKind`，序号已在桌面库中固化，**不要重排**）：

| 值 | 含义 | X | Pixiv |
|---|---|---|---|
| UserMedia | 用户媒体 / 插画漫画 | ✓ `/media` | ✓ `/artworks` |
| AccountLikes | 账号喜欢 | ✓ | — |
| AccountBookmarks | 账号书签 / 收藏插画 | ✓ `/i/bookmarks` | ✓ |
| UserNovels | 用户小说 | — | ✓ |
| AccountNovelBookmarks | 收藏小说 | — | ✓ |
| UserHighlights | 高光 | ✓ | — |
| Permalink | 单条链接（推文、列表） | ✓ | — |
| Search | 搜索 | ✓ | — |

无 Cookie 时所有入队失败，提示先导入。Pixiv 若插画和小说都关掉，提示至少启用一种。

### 4.5 画廊 / 历史

| ID | 需求 | 优先级 |
|---|---|---|
| H-01 | 记录每个落盘文件：路径、大小、状态（已下载/跳过/失败）、来源条目 ID、所属用户、时间 | P0 |
| H-02 | 默认以**缩略图网格**展示已下载图片；视频用占位 + 打开系统播放器 | P0（移动端主界面） |
| H-03 | 可切回文件列表 | P1 |
| H-04 | 筛选：用户、日期起止 | P1 |
| H-05 | 点按打开文件（系统查看器 / 相册） | P0 |
| H-06 | 打开所在文件夹 / 分享文件 | P1 |
| H-07 | 有 SourceItemId 时可「查看原文」（X：`https://x.com/i/status/{id}`） | P1 |
| H-08 | 文件已被删：提示，不崩溃 | P0 |
| H-09 | M：一键保存到系统相册（Android MediaStore / iOS Photo Library），需权限说明 | M / P1 |
| H-10 | M：系统分享入站（收到 `https://x.com/...` 或 pixiv 链接直接走解析入队） | M / P0 |

桌面历史默认已是画廊。手机上画廊应比名单更常打开，可考虑把底栏「画廊」放在「任务」旁边、默认进画廊或记住上次 Tab。

### 4.6 设置

| ID | 需求 | 优先级 |
|---|---|---|
| C-01 | 下载目录 | P0 |
| C-02 | 并发数 | P0 |
| C-03 | 全局代理：scheme（http / socks5 / socks5h）、主机、端口、可选用户名密码；主机空 = 直连 | P0 |
| C-04 | 代理作用于验证、拉关注、下载 | P0 |
| C-05 | 站点选项由 Schema 渲染（布尔 / 文本 / 枚举），保存后下次入队生效 | P0 |
| C-06 | 显示引擎版本（gallery-dl / runner） | P2 |
| C-07 | 关于：应用名、版本、开源许可与第三方声明 | P0 |
| C-08 | M：是否同时写入系统相册 | M / P1 |
| C-09 | M：是否仅在 WLAN 下载 | M / P0 |
| C-10 | M：通知权限引导 | M / P0 |

#### X 站点选项（桌面已有）

| 键 | 类型 | 默认 | 说明 |
|---|---|---|---|
| media | 枚举 all/images/videos | all | 下全部 / 仅图 / 仅视频（含动图） |
| size | 枚举 orig/large/medium/small | orig | 图片质量 |
| videos | 布尔 | true | media=all 时是否含视频 |
| conversations | 布尔 | true | 粘贴推文时下整串 |
| retweets | 布尔 | false | 含转推 |
| quoted | 布尔 | false | 含引用 |
| replies | 布尔 | false | 含回复 |
| filename | 文本 | `{tweet_id}_{author[name]}_{num}.{extension}` | 命名模板 |
| sleep | 文本 | 1 | 请求间隔秒 |

#### Pixiv 站点选项

| 键 | 类型 | 默认 | 说明 |
|---|---|---|---|
| download_artworks | 布尔 | true | 插画漫画 |
| download_novels | 布尔 | true | 小说 |
| ugoira | 布尔 | true | 动图 |
| novel_covers | 布尔 | false | 小说封面 |
| novel_embeds | 布尔 | false | 小说内嵌图 |
| filename | 文本 | `{id}_p{num}.{extension}` | 命名模板 |
| sleep | 文本 | 1 | 请求间隔秒 |

### 4.7 输入解析（粘贴 / 分享）

X（优先级从专到宽）：

1. 推文 `x.com/{user}/status/{id}` 或 `i/web/status/{id}` → 入队 Permalink
2. 列表 `x.com/i/lists/{id}` → 入队 Permalink
3. 搜索 URL 或 `from:` / `filter:` / `min_faves:` / `since:` / `until:` → 入队 Search
4. `#话题` 或 hashtag URL → 入队 Search
5. 用户主页或 handle → 加入名单
6. 保留路径（home、bookmarks 等）拒绝

Pixiv：仅用户数字 ID 或 `/users/{id}` / `member.php?id=`。作品页链接第一期可以拒绝或只提示「请加作者」——桌面目前不加作品页。

---

## 5. 领域模型

与桌面 SQLite 对齐，便于以后桌面/手机导出互换（非必须，但字段不要无故改名）。

```
Account        每站点一个活动账号；Cookie 相对路径；Status=Unverified/Ok/Invalid
User           站点内 RestId 唯一；置顶/暂停；资料与计数
DownloadJob    一次入队单元；Kind；可选 UserId；计数与错误
DownloadFile   任务下的文件行；SourceItemId 用于回链
SettingEntry   键值（目录、并发、代理、站点选项 JSON、当前站点）
FollowingCache 关注列表缓存
Archive        非表：每账号一个 gallery-dl archive 文本文件
```

关键不变量：

- `(SiteId, RestId)` 唯一
- 每站点至多一个 `IsActive` 账号
- archive 文件按 `account.Id` 分，换 Cookie 但同一账号行应继续去重
- 启动回收：未结束任务 → Failed

下载次数：按**完成或失败的任务次数**计，不是文件条数（桌面 StatsAggregator 语义）。

---

## 6. 引擎协议（建议 Flutter 原样复用）

桌面把 gallery-dl 藏在 `runner.py` 后面，stdout 一条一行 JSON（协议 v1）。Flutter 无论用进程、Chaquopy 还是 MethodChannel，**事件形状应保持兼容**，这样队列、去重、鉴权逻辑可以按同一套状态机写。

### 6.1 命令

| 命令 | 用途 | 关键参数 |
|---|---|---|
| `hello` | 握手 | 无 Cookie |
| `whoami` | 验证登录 | `--site` `--cookies` |
| `list-following` | 关注列表 | 同上 |
| `user-info` | 解析一个用户 | 另加 `--input` |
| `download` | 执行下载 | `--job` 指向 JSON 计划文件 |

非 `hello` 命令读取环境变量代理（`HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY`）。

退出码：`0` 成功；`2` 认证失败；其它失败。

### 6.2 事件（字段 `ev`）

| ev | 含义 | 主要字段 |
|---|---|---|
| hello | 协议握手 | protocol=1, runner, gallery_dl |
| account | whoami 结果 | screen_name, display_name, rest_id |
| user | 一个用户 | rest_id, screen_name, display_name, avatar_url, banner_url, bio, followers_count, media_count |
| end | 列表结束 | total |
| url-start | 开始一个 URL | url |
| file-start | 开始一个文件 | path, item_id |
| file-done | 成功 | path, size |
| file-skip | 跳过（archive 命中等） | path |
| job-done | 该次 download 结束 | total, skipped, failed |
| fatal | 致命 | msg, kind=`auth` 表示登录态失效 |

`hello` 的 `protocol` 必须为 `1`，否则拒绝运行。

### 6.3 下载计划 JSON

```json
{
  "urls": ["https://x.com/alice/media"],
  "options": {
    "extractor": { "twitter": { "cookies": "...", "archive": "...", "videos": true } },
    "base-directory": "/storage/emulated/0/Download/GGdown"
  }
}
```

由站点插件根据 Kind + 用户 + 选项生成 URL 和 gallery-dl 配置，**UI 不拼 gallery-dl 命令行**。

### 6.4 引擎版本钉扎

桌面钉死 **gallery-dl 1.32.10** + Python 3.13 embeddable。移动端必须同样钉版本并做回归，上游 API（尤其 X GraphQL、Pixiv AppAPI）经常变。

---

## 7. 非功能需求

| ID | 需求 | 说明 |
|---|---|---|
| N-01 | 离线可打开名单和画廊 | 只有验证/下载需要网 |
| N-02 | 默认并发 1，避免风控 | 可调高但要警告 |
| N-03 | 请求间隔可配（sleep） | 默认 1s |
| N-04 | 所有后台异常落到状态文案，不崩进程 | 桌面硬约束 |
| N-05 | 中文 UI | 与桌面一致 |
| N-06 | 日志可导出 | 设置里打开日志目录 / 分享日志 |
| N-07 | 存储：名单 SQLite；媒体文件用户可见目录 | 沙箱 + 用户选目录 |
| N-08 | 性能：关注列表可能上千，须虚拟列表 | 桌面 DataGrid，Flutter ListView.builder |
| N-09 | 安全：Cookie 明文是已知设计；至少不备份到公共云、不打日志 | 见合规 |
| N-10 | 代理本机口纠偏 | 桌面对 127.0.0.1:10808 误选 http 会改 socks5h；移动端更多走系统 VPN，应用内代理仍要能配 |

---

## 8. 许可与合规（移动端硬约束）

桌面：

- 应用本身 **GPL-3.0**
- 捆绑 **gallery-dl 1.32.10（GPL-2.0）** 与嵌入式 CPython
- 安装包带 `LICENSE`、`gallery-dl-LICENSE`、`THIRD-PARTY.md`

对 Flutter 商店发行的影响：

1. **捆绑 gallery-dl 的 App 必须按 GPL 提供对应源码**（含 Flutter 工程与 runner）。
2. **Apple App Store 与 GPL 长期不兼容**（GPL 的反向工程/再分发条款 vs 苹果协议）。iOS 上架若仍链接 gallery-dl，法律风险高，第一期建议 **只做 Android**，或 iOS 做成不内嵌 gallery-dl 的浏览端（那就是另一个产品）。
3. Cookie / refresh-token 是登录态。应用市场审核、隐私政策必须写清：数据只存本机、等于盗号风险、用户自愿导入。
4. 各站点 ToS：这是用户自己的登录态拉自己可见的内容，仍可能违反平台条款。需求上保持「个人归档工具」定位，不做账号共享、不做批量爬公共时间线作为卖点。

需求文档把这些写成 **发布门槛**，不是可选项。

---

## 9. 移动端相对桌面必须改的部分

这些不是「优化」，是换 Flutter 之后需求会变的地方。

### 9.1 gallery-dl 怎么跑

桌面：Inno 安装包带 Windows embeddable Python + site-packages + `runner.py`，C# 起进程读 JSONL。

手机上没有同等方案：

| 方案 | Android | iOS | 评价 |
|---|---|---|---|
| 嵌入 CPython（Chaquopy / python-for-android） | 可行 | 基本不可行 | 最贴近桌面；包体积大（几十 MB+） |
| 不嵌入，Dart 重写提取器 | 可行 | 可行 | 工作量等于再做一个 gallery-dl，否决 |
| 远程服务器跑 gallery-dl | 可行 | 可行 | 变成云盘/代理，Cookie 出设备，与「明文本机」承诺冲突，否决 |
| 仅 Android，Python 作为 AAR | 可行 | — | **推荐 MVP** |

**MVP 结论：Android First，进程内或独立进程跑钉扎版本的 gallery-dl，协议仍用第 6 节 JSONL。**

### 9.2 生命周期

桌面关窗口可以让队列继续（进程还在）。手机划掉就死。

- Android P0：前台服务 + 常驻通知（正在下载 N/M）
- 杀进程后再次打开：执行与桌面相同的任务回收（标 Failed），用户一键重试
- 不要假设后台能跑完一个大画师（可能几千文件）

### 9.3 存储与相册

- 默认目录：应用专属外部目录或用户选的文件夹（SAF）
- 画廊读取这些路径做缩略图（缓存）
- 可选：下载成功后插入 MediaStore，出现在系统相册
- iOS 若有一天做：几乎只能存 App 容器 + 用户导出

### 9.4 输入

桌面有文件对话框和剪贴板。手机更强的是：

- **分享菜单**：浏览器分享链接 → App 解析入队（P0）
- 剪贴板按钮（P1）
- cookies.txt 用文件选择器（P0），不要指望用户会把文件拷到应用目录

### 9.5 电量与网络

新增桌面没有的需求：

- 仅 WLAN（P0）
- 系统省电白名单引导（P1）
- 并发默认 1、sleep 默认 1s，避免在流量下把套餐打爆

---

## 10. 建议的 Flutter 模块切分

不必与 C# 项目一一对应，但边界应对齐，方便以后对桌面改动做移植。

```
app/                 UI（名单/任务/画廊/设置）
domain/              Account, User, Job, File, Site 插件接口
engine/              启动 Python、解析 JSONL、DownloadPlan
queue/               入队、并发、取消、启动回收
sites/               twitter.dart / pixiv.dart（ParseInput + BuildDownload + Schema）
data/                sqlite（drift 或 sqflite）+ archive 文件
platform/            前台服务、通知、SAF、分享 intent
```

站点插件接口应对齐桌面 `ISiteProvider`：`parseInput` / `buildDownload` / `optionsSchema` / `supportedKinds`。**新站点只加插件，不改队列。**

---

## 11. 分期建议

### 11.1 MVP（能日常用）

1. Android 安装包，内嵌钉扎 gallery-dl
2. 仅 X
3. 导入 cookies.txt + 验证
4. 名单：添加、关注勾选、下载、暂停、删除
5. 队列：并发 1、进度、取消、杀进程回收
6. archive 增量
7. 画廊看已下载图
8. 分享链接入队（用户主页 / 推文）
9. 仅 WLAN、前台服务通知
10. GPL 源码发布方式（Git 仓库公开）

### 11.2 第二期（对齐当前桌面）

- Pixiv + refresh-token
- 全选/反选、下载中防重、下载后清选中
- 高光、搜索、列表链接
- 新动态红点、刷新资料、横幅
- 全局代理
- 站点选项 Schema
- 保存到系统相册

### 11.3 明确后置

- iOS 上架
- 目录里那些「即将支持」站点
- WebView 自动抽 Cookie
- 多账号（桌面也是每站一个活动号）
- 云同步

---

## 12. 验收清单（MVP）

- [ ] 未导入 Cookie 时点下载，提示去导入，不崩溃
- [ ] 导入无效 Cookie，账号显示失效，可更换
- [ ] 添加 `https://x.com/{user}` 后名单出现头像与名字
- [ ] 关注列表能勾选添加，已在名单中的人不能再勾
- [ ] 第一次下载该用户产生文件；第二次同一用户，任务以 skip 为主，磁盘占用几乎不增
- [ ] 划掉 App 再打开，未完成任务为失败，可重新入队
- [ ] 从 Chrome 分享一条推文链接，任务页出现对应任务
- [ ] 仅 WLAN 开启时，蜂窝网络不下载
- [ ] 下载中通知可点进 App
- [ ] 画廊能打开刚下的图
- [ ] 关于页能看到 GPL 与 gallery-dl 声明

---

## 13. 从桌面版应继承的产品决策（不要重新争论）

这些是桌面已经拍板、移动端应默认遵守的规则：

1. 每站点一个活动账号，不是账号管理器
2. 删除用户不删文件
3. 暂停 = 排除出下载，不是取消进行中任务
4. 置顶只影响排序
5. 下载次数按任务次数，不按文件数
6. 粘贴推文默认下整串对话
7. Cookie 明文 + 必须警告
8. 站点选项是 Schema 驱动，UI 不写死开关（方便加站）
9. 引擎协议 v1，不在 UI 里调 gallery-dl CLI
10. 未实现站点展示「即将支持」，而不是藏起来

---

## 14. 风险

| 风险 | 影响 | 缓解 |
|---|---|---|
| X / Pixiv API 变更 | 整站不可用 | 钉 gallery-dl 版本；引擎与 UI 分离，可热更新 runner（需另开需求） |
| 嵌入 Python 包体过大 | 安装转化差 | 只带需要的站点提取器；评估精简 site-packages |
| 后台被杀 | 大用户下不完 | 前台服务 + 可恢复队列 + archive 保证重跑安全 |
| 商店拒审（登录态/版权） | 发不出去 | 个人分发（Obtainium / GitHub Release）作为主渠道；Play 为次 |
| iOS + GPL | 无法上架 | Android First |
| 代理与系统 VPN 叠加 | 连不上 | 文档说明：开了系统 VPN 时应用内代理留空 |

---

## 15. 附录：桌面仓库地图（给移植时对照）

| 路径 | 内容 |
|---|---|
| `engine/runner.py` | JSONL 协议入口 |
| `engine/sites/twitter.py` / `pixiv.py` | 站点 whoami / following / user_info |
| `src/GGdown.Core/Sites/` | 输入解析、下载计划、选项 Schema |
| `src/GGdown.Core/Services/DownloadQueueService.cs` | 队列与任务状态机 |
| `src/GGdown.Core/Data/Entities.cs` | 表结构 |
| `src/GGdown.Core/ViewModels/` | 桌面交互语义（清选中、下载中、画廊） |
| `THIRD-PARTY.md` | 许可与钉扎版本 |

---

*本报告描述桌面版已实现能力及移动端落地约束，不构成站点 ToS 合规保证。*
