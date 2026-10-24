# GGdown 优化工作交接

更新时间：2026-10-24。用户因额度不足要求暂停，把已完成与待完成事项留档。**当前只交接，不继续实施。**

> 状态更新（2026-10-24 晚）：已续接并完成 Task 4、Task 5，结果见
> `docs/superpowers/reports/2026-10-24-startup-and-package-optimization.md`。
> 本文保留为当时的断点记录，其中「未修改 pyproject / 尚无 runtime_smoke」等描述已过时。

## 续接入口

仓库：`E:\Projects\gallery-gui`，分支 `dev`，基础提交 `e132f2c261429fcf2fc5bc13fd9df7785bb8ea1d`。没有创建 worktree，没有提交、推送、合并或发布；实现均在当前工作区，新增文件尚未跟踪。不要重置或清理这些改动。

用户已确认采用原生执行：当前会话自己按任务实现，最后一次独立审阅；不需要再问是否执行原计划。续接时先读：

1. `AGENTS.md`（本地文件，不提交）。
2. `docs/superpowers/specs/2026-10-24-startup-and-package-optimization-design.md`。
3. `docs/superpowers/plans/2026-10-24-startup-and-package-optimization.md`。
4. 本交接文件，以及 `.superpowers/sdd/2026-10-24-startup-and-package-optimization/progress.md`。

技能已用：using-superpowers、writing-plans、executing-plans、test-driven-development、systematic-debugging、verification-before-completion；requesting-code-review 已读取但尚未做最终审阅。按现有计划继续，不必重新设计一遍。计划中的复选框尚未逐项回填，以交接和 ledger 的实际结果为准。

## 已完成 Task 1：启动统计增量修复

修改 `src/GGdown.Core/Services/StatsAggregator.cs`，新增 `RepairOnStartupAsync(CancellationToken ct = default)`：

- 每个平台库读取 `SiteSettings` 内部键 `ggdown.stats-projection.version`，值为字符串 `1`。
- 没有标记或版本不同：校正该库所有作者，全部成功后才保存标记，取消/失败可重试。
- 已初始化：只重算 `StatisticsPending=true` 对应的不同作者；每作者投影和清脏仍在同一事务。
- 干净重启不改作者 UpdatedAt；原 `RecalculateDownloadCountsAsync` 保留全量修复语义。

新增 `src/GGdown.Tests/Services/StartupStatisticsTests.cs`，覆盖旧库初始化、干净重启、多个脏任务、跨库隔离、取消/写失败后重试、保留全量修复。

TDD：先观察缺失 API 的 CS1061，再实现。新测试连同 StatsAndRecoveryTests、QueueReliabilityTests、CrashRecoveryTests 共 **54/54 通过**。

## 已完成 Task 2：引擎与页面就绪分离

- 新增 `src/GGdown.Core/Services/StartupCoordinator.cs`：`StartupTasks(Task EngineReady, Task GlobalReady, Task DataReady)`，`Start(Func<Task> initializeEngine, Func<Task> initializeGlobalData, Func<Task> recoverData)`。引擎与全局库并行；恢复等待全局库。
- `RunnerEngineOptions.Initialization` 默认 `Task.CompletedTask`；RunnerEngine 的统一 `RunAsync` 先 `WaitAsync(ct)` 再读取运行路径、创建进程。取消一个调用不会取消共享初始化。
- `src/GGdown.App/App.xaml.cs` 使用协调器；侧栏仍走 GlobalReadiness，页面仍走 App.Readiness；恢复后调用新的统计修复入口。创建窗口前设置引擎初始化任务。
- 引擎失败由独立观察任务记录，不阻断已有数据浏览；初始化错误会传给实际引擎操作。已有引擎升级回退仍保留。
- 新增 StartupCoordinatorTests；RunnerEngineTests 增加等待、路径延迟配置、取消与初始化失败测试。

TDD：先观察缺少协调器/Initialization 编译失败，再实现。StartupCoordinatorTests、RunnerEngineTests、ProcessLifetimeTests、DependencyLifetimeTests 共 **34/34 通过**；接线后的 Debug x64 构建通过。

## 已完成 Task 3：引擎租约与旧版本维护

新增：

- `src/GGdown.Core/Engine/EngineVersionLease.cs`：共享读租约与维护独占锁。
- `src/GGdown.Core/Engine/EngineVersionMaintenance.cs`：`IsManagedVersion` 与 `Cleanup(engineDirectory, activeDirectory, utcNow, optional onError)`。
- `src/GGdown.Tests/Engine/EngineVersionMaintenanceTests.cs`。

行为：保留活动版本和创建时间最近的完整回退目录；更旧目录只有在无租约且所有文件均可锁定删除时才清理。未知目录不删；不跟随目录树中的链接；`.install-<guid>` 需超过 24 小时且空闲才清理。失败通过回调记诊断并跳过。

接线：EngineInstaller 给 staging 持租约；RunnerEngine 对受管理的版本目录持租约直至进程清理结束；App 仅在安装/复用成功且没有 UpdateError 时调用维护。

实施取舍（已记 ledger）：

- **租约文件放在 `versions/.leases/`，不在版本 payload 内**，因此无需改动内容哈希或复制过滤规则；小锁文件作为维护元数据保留。
- 删除任何内容前，先给候选树中所有文件取得允许删除但拒绝新读写的句柄，保护没有租约的旧进程，避免遇到占用后只剩半个版本。忙碌或只读版本留待后续启动。
- 额外可选 onError 参数用于 App 的 Serilog 诊断，不影响计划中的三参数调用。

TDD：缺失 API 的 RED 已观察。一次失败来自测试 fixture 的 site-packages 为空，生产安装器只复制文件，因此补入 synthetic dependency.py；不是修改安装器绕过失败。之后所有 Engine + ProcessLifetime 测试 **58/58 通过，0 跳过**。

最后日志：`.superpowers/sdd/2026-10-24-startup-and-package-optimization/task3-green.log`。

## 当前断点：Task 4 仅写了失败测试

新增 `engine/tests/test_runtime_dependencies.py`，尚未实现依赖精简。

刚运行两项测试，**2 个预期失败**：

1. `test_runtime_dependencies_exclude_unused_application_features`：根依赖仍含 fastapi 等候选项。
2. `test_adapters_work_without_optional_application_dependencies`：独立进程内禁止候选及传递依赖后导入 `runtime_smoke.verify_runtime`，当前缺少该模块，报 ModuleNotFoundError。

**这是已记录的 TDD RED 断点，不是意外回归。** 当前 Python 全套会因这两项新增测试而失败，不能声称全套通过。

下一步直接实现：

1. 新增 `engine/runtime_smoke.py` 的 `verify_runtime()`，导入并验证三站适配器、gallery-dl Twitter/Pixiv 提取器、抖音 User/Account/Detail/API、链接解析/提取模块、curl_cffi，并用假 session/cookie 构造 DouyinClient；不做真实联网。测试的独立进程已设置必要 sys.path。
2. 精简 `engine/pyproject.toml`：移除 `lxml`、`javascript`、`fastapi`、`uvicorn`、`openpyxl`、`pydantic`、`pyperclip`。锁文件连带移除 pydantic-core 等不再需要的包。保留仍有需要的依赖，先不额外扩大范围。
3. 用 `uv lock --project engine --offline` 优先从缓存更新锁文件，检查保留依赖没有被意外升级；缓存不足再正常解析。
4. `scripts/build-engine.ps1` 复制第三方源码时排除 __pycache__/.pyc；smoke 和 hello 使用 `-B`；验证独立嵌入解释器，不只验证富依赖开发 venv。
5. 同步 `THIRD-PARTY.md` 中包和许可证清单；全部保留 wheel 的许可证必须随包。
6. 跑新增测试及完整 Python 测试，确认 GREEN。

当前未修改 pyproject、uv.lock、build-engine 或 THIRD-PARTY，也还没有 runtime_smoke.py。

## Task 5 待做：基准、安装包、全套验证与审阅

- 新建计划中的 `scripts/benchmarks/GGdown.StartupBenchmark/`。旧隔离基准可参考 `.cache/performance-audit/PerformanceAudit.csproj` 与 `Program.cs`。
- 每作者 10 个完成任务，100/1,000/5,000 作者，区分首次校正、已初始化干净修复、固定少量脏作者修复，各三轮；校验统计、时间、脏标记和干净作者未改写。
- 不能直接拿旧的全量 `RecalculateDownloadCountsAsync` 当新启动入口测量；该方法按设计保留原语义。
- 保留现有 `dist`，新构建使用 `.cache/performance-optimized-release`：

```powershell
pwsh scripts/build.ps1 -AppVersion 0.1.0-rc.2 -OutputDirectory .cache/performance-optimized-release
```

- 对照新旧 app/engine/安装器文件数及 MiB，报告实际压缩收益及 SHA-256；未真实打包前不能承诺安装包缩减量。
- 执行完整 C#、Python 测试；当前只完成相关 C# 子集，**尚未做实现后的 C# 全套 GREEN**。
- 按 executing-plans/requesting-code-review 做一次独立最终审阅（只派 reviewer，实施仍为原生）；修复重要发现，按 TDD 验证。不跳过审阅。
- 审阅重点还包括 RunnerEngine 新增路径/租约代码是否改变空配置、非法路径的错误语义；维护 API 对根路径、重解析点、并发租约与回退候选是否足够保守；初始化/修复是否完整观察异常。
- 最终再运行仓库要求的 Debug x64 构建；完成 `docs/superpowers/reports/2026-10-24-startup-and-package-optimization.md`，更新计划进度。
- 不假报真实账号在线下载成功；本轮未使用真实 Cookie、账号或媒体数据。

## 已知环境与测试命令

Windows PowerShell，.NET 8，Python 为 `engine/.venv/Scripts/python.exe`。依赖缓存 `.cache/uv-managed` 等已有；uv 位于用户本地 bin。

```powershell
dotnet test src/GGdown.Tests/GGdown.Tests.csproj --no-restore --filter 'FullyQualifiedName~StartupStatisticsTests|FullyQualifiedName~StatsAndRecoveryTests|FullyQualifiedName~QueueReliabilityTests|FullyQualifiedName~CrashRecoveryTests' --logger 'console;verbosity=minimal'
dotnet test src/GGdown.Tests/GGdown.Tests.csproj --no-restore --filter 'FullyQualifiedName~StartupCoordinatorTests|FullyQualifiedName~RunnerEngineTests|FullyQualifiedName~ProcessLifetimeTests|FullyQualifiedName~DependencyLifetimeTests' --logger 'console;verbosity=minimal'
dotnet test src/GGdown.Tests/GGdown.Tests.csproj --no-restore --filter 'FullyQualifiedName~GGdown.Tests.Engine|FullyQualifiedName~ProcessLifetimeTests' --logger 'console;verbosity=minimal'
& ./engine/.venv/Scripts/python.exe -m pytest engine/tests/test_runtime_dependencies.py -q -p no:cacheprovider
dotnet test src/GGdown.sln
uv run --project engine --locked python -m pytest engine/tests -q
dotnet build src/GGdown.App/GGdown.App.csproj -c Debug -p:Platform=x64
```

- 沙箱内 testhost 在 90 秒后无法连接；沙箱内 Python 套件也无法完成。相同命令以 exec_command `sandbox_permissions=require_escalated` 执行后正常通过，自动审批未拒绝。继续用这一路径执行测试，避免重复等待。
- 初始 Python 基线 **120/120 通过**，只有 pytest cache 写入权限警告；用 `-p no:cacheprovider` 或缓存改到 `.cache` 可避免该警告。
- 另有正常构建警告 NU1900：无法访问 NuGet 漏洞数据源，不是编译错误。
- Git Bash 技能记账脚本启动时报 MSYS NtCreateDirectoryObject access denied。已经改用 PowerShell 和 apply_patch 写同等 ledger；不要反复重试该脚本。
- 测试 RED 命令有的末尾接 Get-Content，外层 PowerShell 退出码为 0；真正 RED 证据是日志内 CS1061/缺少 API，不能只看 shell 退出码。
- 初次沙箱调用的旧会话 5577（Python）、67814（xUnit）可能已超时或被中断；续接先确认状态，勿据旧会话声称结果。

## 基线数字与交付约束

现有安装包 `dist/GGdown-Setup-0.1.0-rc.2.exe`：62.89 MiB；app 169.52 MiB；engine 62.60 MiB；许可证 0.09 MiB。引擎首次复制约 5.758 秒，未更新校验三轮约 1.248/1.081/1.023 秒。

全量统计基线：1,000 作者约 7.204/7.000/6.992 秒；5,000 作者约 33.115/33.942/34.725 秒。首屏 200 作者查询约 5–8 ms。原始数据 `.cache/performance-audit/20261024-092102/results.json`；原始报告 `.cache/performance-audit/优化评估.md`。

保留自包含离线安装；不做 NativeAOT、Trim、共享运行库改造或常驻 Python 协议。Python 固定 3.13.7、gallery-dl 1.32.10、抖音源码固定提交不变。JSONL v1、认证错误分类、DI 生命周期不变。AGENTS.md/CLAUDE.md、dist、Cookie 不入 Git。

## 交接时构建

本交接回合已重新执行指定 Debug x64 构建，退出码 0，0 错误，4 条 NU1900 警告，耗时 29.40 秒。后续继续修改后仍需重新执行该命令，不能沿用本次结果。
