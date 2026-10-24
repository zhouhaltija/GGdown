# GGdown 启动与发行体积优化 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 消除干净启动的全作者统计重算与引擎阻塞，精简发行依赖并控制旧引擎磁盘占用。

**Architecture:** 保留 WinUI/.NET/Python 自包含架构及完整性校验。统计采用一次性校正加脏标记修复；启动任务分别表达引擎、全局数据、完整数据就绪；运行引擎使用租约保护版本，维护任务只清理确认闲置的目录。

**Tech Stack:** C#/.NET 8、WinUI 3、EF Core/SQLite、xUnit、Python 3.13.7、uv、pytest、PowerShell、Inno Setup。

**Spec:** `docs/superpowers/specs/2026-10-24-startup-and-package-optimization-design.md`

## Global Constraints

- Python 3.13.7、gallery-dl 1.32.10、抖音提交 `473c90ff70c663cfb69310fff2b8d5192f200661` 保持不变。
- JSONL 协议保持 v1；不改变认证错误分类、数据库恢复顺序或 DI 生命周期。
- 首次统计校正成功后写 `SiteSettings[ggdown.stats-projection.version] = 1`；全量修复公共方法保留。
- 当前及一个回退版本保留；任何持有租约的版本额外保留；遗留 staging 清理阈值 24 小时。
- 路径删除前验证绝对边界并拒绝重解析点，不触碰账号、媒体、真实用户数据库。
- 新发行产物输出到 `.cache/performance-optimized-release`，不替换对照 `dist`。
- 每项先写有意义的失败测试，执行 red→green，再跑相关既有测试；最终运行指定 Debug x64 构建。

## Review Focus

- 老库缺少版本标记，失败重试不能误跳过校正：Task 1 测试校正失败/取消不写完成标记。
- 侧栏与数据页先可用时，用户立刻导入账号或下载：Task 2 测试 Python 操作先等待初始化，取消不影响其他等待者。
- 引擎失败、数据库成功或反向失败：Task 2 分别测试三种就绪状态及异常传播，不能永久等待或错误放行数据库。
- 更新期间已有 Python 进程使用旧版本，目录又被维护任务发现：Task 3 测试多租约、活跃旧版本及清理失败的原子保护。
- 开发虚拟环境已安装冗余包，导致精简后测试假通过：Task 4 用禁止候选导入的独立进程及新构建嵌入引擎复核依赖闭包。

## Task 1: 启动统计增量修复

**Files:**
- Modify: `src/GGdown.Core/Services/StatsAggregator.cs`
- Create: `src/GGdown.Tests/Services/StartupStatisticsTests.cs`
- Existing tests: `src/GGdown.Tests/Services/StatsAndRecoveryTests.cs`、`QueueReliabilityTests.cs`

**Interfaces:**
- Consumes: `ISiteDbContextFactory.CreateAsync`、`ExistingSites`；现有 `SiteSettings` 与 `StatisticsPending`。
- Produces: `public virtual Task RepairOnStartupAsync(CancellationToken ct = default)`；保留 `ApplyJobCompletionAsync` 与 `RecalculateDownloadCountsAsync` 签名及全量语义。

- [x] 写测试 `First_start_corrects_legacy_counts_before_marking_initialized`，断言错误计数和最后成功时间被校正、各平台分别写版本标记。
- [x] 写测试 `Clean_restart_does_not_rewrite_users`，先完成初始化再启动，断言所有作者 UpdatedAt、统计及媒体快照不变。
- [x] 写测试 `Restart_repairs_only_distinct_dirty_users`，混合多个脏任务、干净作者及相同 ID 的其他平台，断言只更新正确作者且清理对应脏标记。
- [x] 写测试 `Canceled_or_failed_initial_rebuild_can_retry`、`Full_rebuild_remains_available`，断言未完成不记成功、重试正确、全量方法仍校正无脏标记的错误值。
- [x] 执行 `dotnet test src/GGdown.Tests/GGdown.Tests.csproj --filter FullyQualifiedName~StartupStatisticsTests`，确认因缺少启动修复行为失败。
- [x] 实现按平台版本检查与修复；只在成功后写标记；每作者投影与脏标记清除维持事务一致性。
- [x] 运行新测试以及 `StatsAndRecoveryTests`、`QueueReliabilityTests`、`CrashRecoveryTests`，确认通过。
- [x] 复核差异并形成独立变更记录；提交前使用 verification-before-completion。

## Task 2: 页面启动与引擎准备分离

**Files:**
- Create: `src/GGdown.Core/Services/StartupCoordinator.cs`
- Modify: `src/GGdown.Core/Engine/RunnerEngine.cs`、`src/GGdown.App/App.xaml.cs`
- Create: `src/GGdown.Tests/Services/StartupCoordinatorTests.cs`
- Modify: `src/GGdown.Tests/Engine/RunnerEngineTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `StatsAggregator.RepairOnStartupAsync`；现有数据库迁移、队列恢复及 EngineInstaller。
- Produces: `StartupTasks(Task EngineReady, Task GlobalReady, Task DataReady)`；`StartupCoordinator.Start(Func<Task> initializeEngine, Func<Task> initializeGlobalData, Func<Task> recoverData)` 返回上述三个任务。
- Produces: `RunnerEngineOptions.Initialization`，类型 `Task`，默认 `Task.CompletedTask`；`RunAsync` 在读取任何运行路径及创建进程前 await 此任务，使用调用者 ct 取消等待。

- [x] 写测试 `Data_readiness_does_not_wait_for_engine`，阻塞引擎委托，断言 GlobalReady 与 DataReady 能先完成。
- [x] 写测试 `Recovery_waits_for_global_database`，断言 global 未完成时 recoverData 未调用。
- [x] 写测试 `Engine_failure_does_not_fault_data_readiness` 及 `Global_failure_faults_data_readiness`，断言故障边界与有限等待。
- [x] 为 RunnerEngine 写测试 `Operation_waits_before_reading_runtime_paths`、`Canceling_wait_does_not_cancel_shared_initialization`、`Initialization_failure_is_reported_without_starting_python`。使用现有 stub runner；初始化完成前文件路径不可用，完成后指定正确路径。
- [x] 运行上述新测试，记录预期失败；不得以仅比较源码文本替代行为测试。
- [x] 实现协调器与统一引擎等待入口；App 在创建窗口前设置初始化任务；全局初始化完成后恢复队列并调用启动统计修复。
- [x] 调整引擎准备失败处理：记录并传播到 EngineReady，数据页独立可用；回退成功仍作为引擎成功。观察后台异常，避免未观察故障与悬挂 TaskCompletionSource。
- [x] 运行 `StartupCoordinatorTests`、`RunnerEngineTests`、`ProcessLifetimeTests`、`DependencyLifetimeTests`；构建 WinUI 应用验证接线。
- [x] 复核差异并形成独立变更记录。

## Task 3: 有运行保护的历史版本维护

**Files:**
- Create: `src/GGdown.Core/Engine/EngineVersionLease.cs`
- Create: `src/GGdown.Core/Engine/EngineVersionMaintenance.cs`
- Modify: `src/GGdown.Core/Engine/EngineInstaller.cs`、`RunnerEngine.cs`
- Modify: `src/GGdown.App/App.xaml.cs`
- Create: `src/GGdown.Tests/Engine/EngineVersionMaintenanceTests.cs`
- Existing tests: `src/GGdown.Tests/Engine/EngineInstallerTests.cs`

**Interfaces:**
- Consumes: `EngineInstallation.Directory`、`RunnerEngineOptions.Initialization`、`IAppPaths.EngineDir`。
- Produces: `EngineVersionLease.Acquire(string versionDirectory)` 返回 `IDisposable`，多个操作可共享读租约，清理需独占权限。
- Produces: `EngineVersionMaintenance.Cleanup(string engineDirectory, string activeDirectory, DateTime utcNow)` 返回已删除目录数；只处理版本目录，不传播普通 IO/权限清理失败。

- [x] 写测试 `Keeps_active_and_latest_complete_fallback`，多版本中只清理更早闲置目录。
- [x] 写测试 `Active_old_version_is_untouched_until_all_leases_release`，同时持有两个租约，任何一个未释放都不能部分删除。
- [x] 写测试 `Recent_staging_and_unknown_directories_are_preserved`、`Only_stale_idle_staging_is_removed`，覆盖 24 小时边界。
- [x] 写测试 `Rejects_paths_outside_versions_and_does_not_follow_links`，外部哨兵文件必须保留；平台不允许创建链接时明确跳过该子场景。
- [x] 写测试 `Maintenance_failure_does_not_break_installation`、`Lease_does_not_change_content_version`，断言被占用目录完整保留、同一引擎不重复安装。
- [x] 运行新测试，确认针对尚未实现的维护及保护行为失败。
- [x] 实现租约、规范命名/边界检查与清理；哈希明确排除维护元数据，不能让租约使下次启动判定版本变化。
- [x] RunnerEngine 只对受管理的版本目录持有租约，覆盖进程创建至完全退出；Debug 源码路径与旧式引擎目录保持兼容。
- [x] 引擎准备成功后执行维护，保留活动路径及回退候选；失败回退时不破坏旧版本。维护日志不阻断就绪。
- [x] 运行全部 Engine 测试及 `ProcessLifetimeTests`，确认升级、取消、损坏修复和原有回退行为不变。
- [x] 复核差异并形成独立变更记录。

## Task 4: Python 依赖与打包清理

**Files:**
- Modify: `engine/pyproject.toml`、`engine/uv.lock`
- Modify: `scripts/build-engine.ps1`、`THIRD-PARTY.md`
- Create: `engine/tests/test_runtime_dependencies.py`

**Interfaces:**
- Consumes: 现有三站适配器及固定第三方源码。
- Produces: 只包含实际依赖闭包的锁文件；构建脚本不传播开发缓存，smoke 使用 `-B`；许可证清单与锁定发行包一致。

- [x] 写测试 `test_runtime_dependencies_exclude_unused_application_features`，检查根依赖没有评估列出的候选包；在当前状态应失败。
- [x] 写独立进程 smoke 测试，禁止导入候选包及其已删除传递依赖，然后导入三站适配器、抖音 User/Account/Detail/API/链接解析/提取模块，并构造 DouyinClient。断言退出码 0，防止开发虚拟环境掩盖缺失依赖。
- [x] 运行新测试确认预期失败，再精简根依赖；通过 `uv lock --project engine --offline` 优先使用本地缓存，保持仍需依赖版本。缓存不足时使用正常锁文件更新并检查版本差异。
- [x] 修改打包脚本：复制第三方源码时排除 __pycache__/.pyc，smoke 禁止生成缓存；所有删除限定已验证的发行引擎输出目录，不清理源码或用户环境。
- [x] 更新 THIRD-PARTY 清单，核对每个保留 wheel 的许可证仍随包存在。
- [x] 执行 `uv run --project engine --locked python -m pytest engine/tests -q`，确认全部适配器测试通过。
- [x] 在新的输出目录组装发行引擎，使用嵌入 Python 执行同等导入、构造、限速与 hello smoke，确认不依赖仓库虚拟环境。
- [x] 复核差异并形成独立变更记录。

## Task 5: 性能、安装包和最终验收

**Files:**
- Create: `scripts/benchmarks/GGdown.StartupBenchmark/GGdown.StartupBenchmark.csproj`、`Program.cs`、`README.md`
- Create: `docs/superpowers/reports/2026-10-24-startup-and-package-optimization.md`
- Local output: `.cache/performance-optimized-release` 与独立基准数据目录。

**Interfaces:**
- Consumes: `RepairOnStartupAsync`、EngineInstaller、生产 SQLite 工厂及新发行产物。
- Produces: 可复现的初始化/干净重启/少量脏作者修复基准、完整性结果、新旧体积差值及验证限制。

- [x] 将上一轮隔离基准整理为可复现脚本，每作者 10 条完成任务，100/1,000/5,000 作者，每项三轮；不接触真实数据，准备及结果校验不计时。
- [x] 分开记录首次版本校正、初始化后的干净修复、固定少量脏作者修复；检查次数、最后时间、脏标记及干净作者未改写。
- [x] 对照基线 1,000 作者约 7 秒、5,000 作者约 34 秒，报告中位数和逐轮数据。若干净修复仍慢，定位真实剩余 SQL，不通过放宽就绪安全边界绕过。
- [x] 执行 `pwsh scripts/build.ps1 -AppVersion 0.1.0-rc.2 -OutputDirectory .cache/performance-optimized-release`，确认新安装包和 SHA-256 生成，旧 dist 保留。
- [x] 统计新旧 app、engine、安装器文件数及 MiB，明确压缩前后差别；若构建工具或网络受阻，报告实际阻塞及已验证范围。
- [x] 执行 `dotnet test src/GGdown.sln` 和 `uv run --project engine --locked python -m pytest engine/tests -q`，读取退出码与测试汇总。
- [x] 用独立审阅检查启动故障、迁移重试、版本使用保护、路径边界、依赖闭包及协议兼容性，修复实际问题后重跑受影响检查。
- [x] 确认 GGdown 未运行；最终执行 `dotnet build src/GGdown.App/GGdown.App.csproj -c Debug -p:Platform=x64` 并读取成功结果。
- [x] 完成报告，列出改动、性能和体积证据、测试数、跳过项及未做真实在线下载的限制。

## 计划自检

- [x] 四项实施范围均有对应任务；共享运行库、NativeAOT 和常驻进程仅为未采用方案。
- [x] 启动统计与完整重建分开，已有全量测试不必改变语义；版本标记按平台保存。
- [x] 引擎等待入口统一，任务取消与共享初始化生命周期分开。
- [x] 运行目录、回退目录、目录链接和 staging 均有明确保留/删除规则及测试。
- [x] 精简依赖同时验证独立发行解释器，避免只验证富依赖开发环境。
- [x] 性能检查包含正确性与测量口径，不把单次组件数字当作界面冷启动。
- [x] 用户审阅设计与计划，并选择原生执行或子代理执行后开始实现。

## 执行方式建议

推荐原生执行：由当前会话按以上五项逐项完成 red→green，最后做一次独立审阅。各项接口和顺序已写清，避免每个小步骤重新载入完整项目。若用户更看重逐项独立审阅，可选择子代理执行。
