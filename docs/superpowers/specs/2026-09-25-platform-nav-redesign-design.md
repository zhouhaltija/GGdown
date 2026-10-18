# GGdown 平台优先导航重构 + 每平台一库 设计文档

- 日期：2026-09-25
- 状态：已与用户逐节确认，待实现
- 分支：`dev`（沿用 GalleryGUI → GGdown 更名分支）

## 1. 背景与目标

当前主界面是单层左侧 `NavigationView`（用户管理/下载/历史/设置四个全局页），站点切换靠内容区顶部的标题式 Flyout（`SiteSwitcherViewModel` + `SiteCatalog`，13 个站点中 twitter/pixiv 可用，其余标「即将支持」）。四个页面共享同一套实现，靠 `ICurrentSite` 区分当前站点。

数据层是单一 SQLite 库（`%LOCALAPPDATA%\GGdown\data\ggdown.db`，`GGdownDbContext` 一个 context 承载全部实体）。

本次重构达成：

1. **平台优先的两级导航**：最左窄图标栏 = 平台；内容区顶部标签行 = 当前平台的功能页组。
2. **每平台独立的功能页组**：不同平台可拥有不同页集合（无账号概念的站点没有用户管理页）。
3. **界面设置**：控制图标栏显示哪些平台。
4. **每平台一个数据库**：用户、账号、任务、历史、站点选项全部隔离在各自平台库；全局库只存应用级设置。

### 非目标

- 不改动引擎层（`engine/`、JSONL 协议 v1）与 `ISiteProvider` 的下载计划逻辑。
- 不新增可用站点（Available 仍只有 twitter/pixiv；其余站点的页组仅作占位声明）。
- 不实现「删除某平台数据」的 UI（拆库后天然可行，留待后续）。

## 2. UI 设计（App 层）

### 2.1 布局与外壳

`MainWindow.xaml` 弃用 `NavigationView` 与站点切换 Flyout，改为自绘三段结构：

```
Grid（外层仍先 InfoBar 通知行 + 主体）
└─ Grid（2 列）
   ├─ PlatformRail（48px 竖栏）
   │   ├─ 平台图标 ListView：仅显示「界面设置」勾选且 Available=true 的平台
   │   │    · 选中项高亮，ToolTip 显示平台全名
   │   │    · 每平台一个 FontIcon glyph（SiteCatalog 补 Icon 字段）
   │   ├─ 栏底：全局下载指示器（进行中任务总数徽标，点击进入「全部平台」下载视图）
   │   └─ 栏底：⚙ 全局设置入口
   └─ 右侧内容区（2 行）
      ├─ Header：当前平台名（或「设置」）+ TabView（页组标签）
      └─ Frame（现有页面 + 新拆分页面，NavigationCacheMode=Enabled 语义不变）
```

- `SiteSwitcherViewModel` 与站点 Flyout 删除；`ICurrentSite` 保留，由图标栏驱动。
- 全局 `InfoBar` 通知行（B8）位置与行为不变，但「去设置」按钮改为导航到**当前平台的设置页**（登录态失效总是平台上下文内的）。

### 2.2 平台页组模型（Core，新文件 `Sites/SiteUiProfile.cs`）

```csharp
public enum SitePageKey { Users, Downloads, History, SiteSettings }

public sealed record SiteUiProfile(string SiteId, IReadOnlyList<SitePageKey> Pages);
```

- 静态目录 `SiteUiProfiles`：twitter、pixiv → 四页全有；其余「即将支持」站点按能力声明（如 danbooru/gelbooru/kemono/coomer → Downloads/History/SiteSettings，无 Users；fanbox/fantia/instagram/reddit/tumblr/bilibili/weibo → 四页）。仅 Available 站点的页组会被实际渲染。
- 页组目录与 `SiteCatalog` 并列（静态类，不进 DI）。

### 2.3 界面设置（可见平台）

- `IAppSettings` 新增 `GetVisibleSitesAsync` / `SetVisibleSitesAsync`，持久化键 `ui.visibleSites`（JSON 数组；默认 = 全部 Available 站点）。
- 「界面」页（全局设置页组之一）：勾选列表展示 `SiteCatalog.All` 13 个站点，不可用站点标「即将支持」；勾选即时生效。
- 规则：**Available=false 的站点即使勾选也不进栏**（栏内只放可导航的平台；勾选仅作为预告偏好被记住，待该站点接入后自动出现）。

### 2.4 全局设置拆分

点击栏底 ⚙：Header 显示「设置」，TabView 换为全局页组 **[通用] [网络] [引擎] [界面] [关于]**，从现有 `SettingsPage` 拆分为五个页面：

| 新页面 | 内容来源（现 SettingsPage 节） |
|---|---|
| 通用设置页 | 下载目录、并发下载数 |
| 网络设置页 | 代理 |
| 引擎设置页 | 引擎（原样搬移） |
| 界面设置页 | 2.3 的可见平台勾选 |
| 关于页 | 关于（原样搬移） |

**「账号」「站点选项」两节移出全局设置**，进入各平台的「设置」页：新 `SiteSettingsPage`（由 `ICurrentSite` 驱动，twitter/pixiv 各显示自己的账号卡与 OptionSchema 选项）。

### 2.5 下载 / 历史作用域

- `DownloadsViewModel` / `HistoryViewModel` 增加 `SiteId` 筛选参数：平台页组内只显示该平台的任务/历史；队列引擎行为不变（全局并发）。
- 栏底下载指示器 → 全局视角：聚合 `DownloadQueueService` 进行中任务总数；点击进入「全部平台」下载视图（同一 `DownloadsPage`，筛选为 null）。

### 2.6 导航状态持久化

- 当前平台：沿用 `ui.currentSite`。
- 每平台记住上次停留页：`ui.site.<id>.lastPage`（切回平台时恢复；默认 Users）。

### 2.7 共享页面的站点刷新

现有 UsersPage/DownloadsPage/HistoryPage 是 `NavigationCacheMode=Enabled` 的共享实例，跨平台切换复用同一实例。实现时必须核对/补齐：各 VM 订阅 `ICurrentSite.Changed` 并重新加载数据（UsersViewModel 已依赖 `ICurrentSite`，Downloads/History 需逐一检查）。

## 3. 数据库设计（Core 层，每平台一库）

### 3.1 文件布局

```
%LOCALAPPDATA%\GGdown\
├─ data\
│  ├─ ggdown.db          ← 全局库：仅应用级设置（AppSetting 实体）
│  │                        键：download.directory / download.concurrency / network.proxy /
│  │                        ui.currentSite / ui.visibleSites / ui.site.<id>.lastPage
│  └─ sites\
│     ├─ twitter.db      ← 用户、账号(Cookie 记录)、下载任务、文件、历史、关注缓存、站点选项
│     ├─ pixiv.db
│     └─ …（平台在「界面设置」勾选 Available 站点时惰性创建 + 迁移）
```

### 3.2 EF Core 拆分

单一 `GGdownDbContext` 拆成两个 context 类型（实体类复用，`Entities.cs` 不动）：

- **`GGdownGlobalDbContext`** —— 仅 `SettingEntry`；`DbInitializer` 启动时迁移全局库。
- **`GGdownSiteDbContext`** —— `Account` / `User` / `DownloadJob` / `DownloadFile` / `FollowingCacheEntry` / `SiteSettingEntry`（新增实体，承载从全局键 `site.<id>.options` 迁来的站点选项）；由新的 **`ISiteDbContextFactory.CreateAsync(siteId)`** 打开 `data\sites\<id>.db`，首次打开时惰性迁移。
- `DownloadJob` / `DownloadFile` **新增 `SiteId` 列**（现状无此列，任务仅经 `AccountId → Account.SiteId` 间接关联）——拆库后按库归属持久化必须显式携带；`UserId` 可空，不能作归属依据。

### 3.3 服务层改造

现状：一批服务直接注入 `IDbContextFactory<GGdownDbContext>`（`AppSettings`、`DownloadQueueService`、`AccountQueryService`、各 VM 等）。

改为：

- 站点相关服务注入 `ISiteDbContextFactory`，每次操作显式携带 siteId；UI 侧由 `ICurrentSite` 提供当前站点。
- `IAppSettings` 改走全局库（`GGdownSettingsStore` 挂 `GGdownGlobalDbContext`）；`GetSiteOptionsAsync`/`SetSiteOptionsAsync` 改读写各平台库的 `SiteSettingEntry`。
- `DownloadQueueService`（singleton）保持全局并发；任务持久化按其 `SiteId` 分发到对应平台库；**启动恢复改为遍历 `data\sites\*.db`**（Running/Pending → Failed），不再有跨库外键问题（Job→Account 同库）。
- DI 约定沿用 `DependencyInjection.AddGGdownCore` 注释：factory 注册先于 scoped 注册、Singleton 不捕获 Scoped（`AppSettings` 的 captive-dependency 修复模式同样适用于两个新 context）。
- `App.Readiness` 门禁不变；启动序列变为：目录 → 全局库迁移 → **旧库拆分迁移（若需要）** → 队列恢复扫描全部平台库。

### 3.4 存量数据迁移（一次性）

启动时检测：`data\ggdown.db` 存在旧全量数据（含 Accounts 等站点表）且 `data\sites\` 尚未生成 → 执行拆分：

1. 旧库改名留档为 `data\ggdown.db.pre-split.bak`，在其副本上读取。
2. 按行拆分：`Account`/`User`/`FollowingCacheEntry` 按 `SiteId` 列；`DownloadJob` 经 `AccountId → Account.SiteId` 回填新 `SiteId` 列（`DownloadFile` 经 `JobId`）；`SettingEntry` 中 `site.<id>.options` 键迁入对应平台库，其余留在全局库。
3. 每平台库写入在一个批次内完成，任一步失败整体回滚该库、下次启动重试（幂等：以 `sites\` 是否已生成为完成标志）。
4. 迁移完成前 `App.Readiness` 不放行（沿用现有门禁机制）。

行为对齐现有 `DataRootMigration` 的套路（检测旧状态 → 搬移 → 留档）。

## 4. 实现顺序

1. **DB 拆分**（3.2–3.4：双 context、`ISiteDbContextFactory`、服务改造、存量迁移）——先让数据层站稳。
2. **导航壳层**（2.1、2.2、2.6：PlatformRail + TabView + Frame，替换 NavigationView）。
3. **全局设置拆分 + 界面设置**（2.3、2.4）。
4. **下载/历史筛选 + 全局指示器**（2.5）与**共享页刷新核对**（2.7）。

## 5. 测试

- Core（xunit）：
  - `ui.visibleSites` round-trip 与默认值（全部 Available 站点）；
  - `SiteUiProfiles` 页组断言（twitter/pixiv 四页；占位站点页组）；
  - `ISiteDbContextFactory` 惰性建库/迁移；
  - 存量拆分迁移：构造含 twitter+pixiv 混合数据的旧库 → 断言各平台库行数/归属、全局设置键保留、幂等重入；
  - `DownloadQueueService` 跨库恢复（两库各留 Running 任务 → 启动后均置 Failed）；
  - Downloads/History ViewModel 的 SiteId 筛选。
- UI 手动验证：平台切换、页组恢复、界面设置即时生效、栏底指示器计数、「去设置」通知落地到平台设置页。
- 现有测试 fixture（单库）改为双库 fixture。

## 6. 风险与缓解

- **迁移丢数据**：拆分全程从留档副本读取，失败回滚、幂等重试；旧库保留 `.bak` 供人工恢复。
- **页面缓存跨平台脏状态**（2.7）：实现时逐 VM 核对 `ICurrentSite.Changed` 刷新，漏刷即「X 的用户列表出现在 Pixiv」——列为验收项。
- **双 context 的 DI 生命周期回归**：沿用现有注册顺序注释与 captive-dependency 修复模式，注册处补注释。
- **任务无 SiteId 的历史数据**：以 Account 归属回填；AccountId 悬空的孤儿任务归入 twitter（当前唯一历史主站点）并记录日志。
