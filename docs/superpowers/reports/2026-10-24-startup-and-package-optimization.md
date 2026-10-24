# GGdown 启动与发行体积优化 — 实施报告

日期：2026-10-24。范围：`docs/superpowers/specs/2026-10-24-startup-and-package-optimization-design.md` 的四项优化。
代码位于当前工作区（分支 `dev`，基线提交 `e132f2c`），**未提交、未推送、未发布**；`dist/` 保留为体积对照。

## 结论

| 目标 | 结果 |
| --- | --- |
| 干净启动不再全量重算统计 | 已初始化后启动只查脏标记，1,000 作者 **94.8 ms**、5,000 作者 **105.8 ms**（同机旧全量路径 49.3 s / 240.0 s） |
| 引擎准备不再阻塞页面 | 引擎、全局库、数据恢复三条任务分离；引擎失败只影响需要引擎的操作 |
| Python 依赖精简 | 发行引擎 site-packages 42.30 → **21.18 MiB**，整包 62.60 → **41.44 MiB** |
| 旧引擎磁盘占用受控 | 保留活动版本 + 最近完整回退，运行中版本由租约保护，其余按空闲条件清理 |

安装包：**62.89 → 58.10 MiB（−4.79 MiB，−7.6%）**，SHA-256 `4da2df6c96139be991600aad4ee55fc8d943ebfa2a62cf5f88db978a4748c4d7`。

## 改动内容

### 1. 启动统计增量修复
`StatsAggregator.RepairOnStartupAsync`：每个平台库用 `SiteSettings` 内部键 `ggdown.stats-projection.version`（值 `1`）记录是否已校正。缺标记则校正该库全部作者，**全部成功后**才写标记（取消/失败可重试）；已有标记时只重算 `StatisticsPending` 涉及的不同作者。每作者投影与脏标记清除仍在同一事务。`RecalculateDownloadCountsAsync` 保留原全量语义。

### 2. 引擎与页面就绪分离
新增 `StartupCoordinator`，把启动拆成 `EngineReady` / `GlobalReady` / `DataReady`：引擎与全局库并行，恢复等待全局库。`RunnerEngineOptions.Initialization` 在 `RunnerEngine` 读取运行路径前被 await，取消单个调用不会取消共享初始化。引擎故障只记录并传给需要引擎的操作，侧栏与已有数据仍可用。

### 3. 引擎版本租约与历史版本维护
`EngineVersionLease` 提供共享读租约与维护独占锁，租约文件放在 `versions/.leases/`（不进入版本 payload，因此不改变内容哈希）。`EngineVersionMaintenance.Cleanup` 保留活动版本与创建时间最近的完整回退目录；删除前先对候选树**每个文件**取「允许删除、拒绝新读写」句柄，全部成功才整树删除，避免留下半个版本。`.install-<guid>` 仅清理超过 24 小时且空闲的目录；未知目录、重解析点、越界路径一律跳过。

### 4. Python 依赖与打包清理
根依赖移除 `fastapi`、`uvicorn`、`pydantic`、`lxml`、`openpyxl`、`javascript`、`pyperclip` 及连带传递依赖，锁文件只删不升级（−18 个包，运行时闭包 17 个包）。新增 `engine/runtime_smoke.py`：导入三站适配器、gallery-dl Twitter/Pixiv 提取器、抖音 User/Account/Detail/API 与链接解析、curl_cffi，并构造 `DouyinClient`，全程不联网。`scripts/build-engine.ps1` 复制抖音源码时排除 `__pycache__`/`.pyc`，smoke 与 hello 使用 `-B`，并在结尾断言发行引擎不含字节码缓存。`THIRD-PARTY.md` 按锁文件重建，新增漂移守卫测试。

## 独立审阅发现与处理

审阅由独立 reviewer 完成，重点覆盖启动故障、迁移重试、版本使用保护、路径边界、依赖闭包与协议兼容。

**Critical（已修复）**：`AppSettings.SetSiteOptionsAsync` 原本整表替换平台库 `SiteSettings`，会连带删掉新增的统计版本标记——用户每次保存站点设置，下次启动就退回全量校正，等于抵消本次优化；`GetSiteOptionsAsync` 还会把内部键漏进下载参数。修复：新增 `SiteInternalSettings`（统一前缀 `ggdown.` 与键定义），读取与删除只作用于用户键，写入忽略内部键；`StatsAggregator` 改用同一常量。新增 3 个测试（含「保存设置后重启不重写作者」的端到端用例），修复前 3 个全部失败。

**Important（已修复）**：
- `RunnerEngine` 新增的租约代码在「引擎未安装」判定之前读取运行路径，未播种时 `RunnerScript` 为空，`Path.GetFullPath("")` 抛 `ArgumentException`，用户看不到「引擎未安装」。已把空配置与路径存在性判定移到路径/租约之前，并补测试（修复前实际抛 `ArgumentException`，修复后为 `EngineException`）。
- 引擎历史版本维护在引擎初始化 `try` 块内，`Cleanup` 抛出非 IO 异常会把整个引擎就绪判为失败。已改为独立 `try/catch`：维护失败只记警告，不影响就绪。
- 引擎初始化失败时 `EngineException` 不携带内部异常，诊断只剩一行 Message。已在包装前完整记录原始异常。

**Minor（评估后不改）**：维护删除改为「先改名 `.trash-*` 再删」。当前实现已先对整树取锁再整树删除，运行中版本不会被部分删除；删除中途遇到 IO 错误残留的目录 `IsComplete` 为假，不会被选为回退版本，且下次启动会重试，属自愈。改名方案还需为 `.trash-*` 增加新的清理生命周期，收益不足以抵消复杂度，故记录不实施。

## 性能结果

基准：`scripts/benchmarks/GGdown.StartupBenchmark`，合成数据，每作者 10 个已完成任务，100 / 1,000 / 5,000 作者，每项三轮；准备与校验不计时。原始数据：`.cache/startup-benchmark/20261024-103332/results.json`。

| 场景 | 100 作者 | 1,000 作者 | 5,000 作者 |
| --- | --- | --- | --- |
| 首次校正（旧库一次性） | 4,890.6 ms | 45,199.6 ms | 239,268.0 ms |
| 干净重启（已初始化） | 98.0 ms | 94.8 ms | 105.8 ms |
| 少量脏作者修复（固定 5 个） | 334.7 ms | 307.1 ms | 296.7 ms |
| 旧全量重算（同机对照，非启动入口） | 5,037.2 ms | 49,275.1 ms | 240,029.9 ms |

- 干净重启与作者数基本无关（98 → 106 ms），相对同机旧全量路径快 **51× / 520× / 2,269×**，满足设计目标「1,000 / 5,000 作者小于 250 ms」。
- 首次校正与旧全量重算耗时相当（0.97× / 0.92× / 1.00×），符合设计：一次性校正做的是同样的工作，只是不再每次启动重复。
- 每轮都校验了下载数、最后下载时间、媒体快照、版本标记、脏标记清空，以及干净作者 `UpdatedAt` 未被改写；任一项不符基准即抛异常，本次运行退出码 0。

**测量口径说明**：本机当前状态下旧全量路径比 2026-10-24 审计基线（1,000 作者约 7.0 s、5,000 作者约 34 s）慢约 6.9×，而该路径本次只多了一条脏标记语句。同一批文件上的原始 SQL 只需 0.5 ms/作者，连接开闭约 3.4 ms/作者，回滚日志提交 22 ms 而 WAL 为 0.55 ms——差异来自机器与磁盘状态，不是本次改动。因此上表以**同一次运行内的旧全量路径**作为对照，不跨运行直接对比历史基线。

## 发行体积

| 产物 | 旧（`dist/`） | 新（`.cache/performance-optimized-release/`） | 变化 |
| --- | --- | --- | --- |
| 安装包 | 62.89 MiB | 58.10 MiB | −4.79 MiB（−7.6%） |
| app | 473 文件 / 169.52 MiB | 473 文件 / 169.53 MiB | 持平 |
| engine | 2,393 文件 / 62.60 MiB | 1,376 文件 / 41.44 MiB | −21.16 MiB（−33.8%） |
| licenses | 4 文件 / 0.09 MiB | 4 文件 / 0.09 MiB | 持平 |
| 引擎内 `.pyc` | 177 | 0 | 全部移除 |

引擎 `site-packages` 由 42.30 → 21.18 MiB，主要来自 `lxml`（8.51）、`pydantic_core`（5.12）、`pydantic`（1.69）等不再需要的包。

发行引擎独立自检：清空 `PYTHONHOME`、把 `PYTHONPATH` 指向仓库虚拟环境后，内置解释器仍只解析发行目录（`sys.prefix` = 发行 `python/`，`gallery_dl` 来自发行 `site-packages`），`runtime_smoke` 与 `runner.py hello`（protocol 1、gallery-dl 1.32.10）均通过，说明发行包不依赖仓库虚拟环境。

## 测试

| 检查 | 结果 |
| --- | --- |
| `dotnet test src/GGdown.sln` | **488 通过 / 1 跳过 / 0 失败**（退出码 0） |
| `uv run --project engine --locked python -m pytest engine/tests -q` | **123 通过**（基线 120 + 新增 3） |
| 统计与设置相关子集 | 18/18 通过 |
| 引擎相关子集（RunnerEngine / 维护 / 安装器 / 进程生命周期） | 47/47 通过，0 跳过 |
| `dotnet build src/GGdown.App -c Debug -p:Platform=x64` | **0 错误 0 警告**，22.29 s |

跳过的 1 项是既有的 `EngineSmokeTests.Engine_downloads_file_via_generic_extractor`（本机无 gallery-dl 源码检出的 E2E 用例）。

**已知偶发**：完整 C# 套件的前两次运行各有 1 项时序敏感用例失败（`QueueReliabilityTests.Lowering_concurrency_...`、`SiteSettingsViewModelTests.Changing_option_auto_saves_after_debounce`），两者单独运行分别 3/3、13/13 通过，第三次完整运行全绿。属既有的并发/防抖时序假设在高负载下不稳定，与本轮改动无关。

## 未验证项与限制

- 未使用真实账号、Cookie 或媒体数据，**没有验证真实在线下载**；引擎验证仅覆盖导入、对象构造、协议握手。
- 安装包只做了构建与 SHA-256 校验，未实际安装到干净系统验证安装/卸载流程。
- 性能数字是组件级统计修复耗时，不等于界面冷启动时间，不能把各项耗时相加当作应用启动耗时。
- 历史版本维护的「删除中途失败」路径没有构造真实 IO 故障测试，只覆盖了被占用、只读、非规范命名、重解析点与越界路径。
- `dist/` 未替换；新产物位于 `.cache/performance-optimized-release/`。

## 复现

```powershell
dotnet test src/GGdown.sln
uv run --project engine --locked python -m pytest engine/tests -q
dotnet run --project scripts/benchmarks/GGdown.StartupBenchmark -c Release
pwsh scripts/build.ps1 -AppVersion 0.1.0-rc.2 -OutputDirectory .cache/performance-optimized-release
dotnet build src/GGdown.App/GGdown.App.csproj -c Debug -p:Platform=x64
```
