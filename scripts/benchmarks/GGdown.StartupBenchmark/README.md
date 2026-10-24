# GGdown 启动统计基准

测量 `StatsAggregator.RepairOnStartupAsync` 在合成数据上的启动开销，用于验证「旧库一次性校正 + 脏标记增量修复」替代全量重算的效果。

## 运行

在仓库根目录执行（Release 构建，避免 Debug 影响数字）：

```powershell
dotnet run --project scripts/benchmarks/GGdown.StartupBenchmark -c Release
```

输出写入 `.cache/startup-benchmark/<UTC 时间戳>/`，其中 `results.json` 含逐轮数据与汇总中位数。每次运行都会新建目录，不会复用或覆盖历史结果。

只补测某一档规模时用 `--counts`：

```powershell
dotnet run --project scripts/benchmarks/GGdown.StartupBenchmark -c Release -- --counts 5000
```

## 场景

每作者 10 个已完成任务，作者数取 100 / 1,000 / 5,000，各三轮：

| Kind | 含义 |
| --- | --- |
| `FirstCorrection` | 旧库缺少版本标记时的首次启动校正；每轮使用独立目录 |
| `CleanRestart` | 已初始化后的干净重启，只应做脏查询 |
| `DirtyRepair` | 固定 5 个作者各留 1 个未投影任务，只重算这些作者 |
| `LegacyFullRebuildReference` | 旧的全量 `RecalculateDownloadCountsAsync`，仅作同机对照，**不是**新的启动入口 |

## 正确性校验

准备与结果校验不计入耗时：

- 首次校正后每个作者的下载数、最后下载时间、媒体快照都等于期望值，版本标记写为 `1`，无残留脏任务。
- 干净重启后所有作者 `UpdatedAt` 不变，统计未被破坏。
- 脏修复后只有那 5 个作者的 `UpdatedAt` 变化，统计被修正，脏标记清空。

任一项不满足即抛异常，基准不会输出「通过」的假结果。

## 约束

- 只写 `.cache` 下的合成数据，不读取真实账号、Cookie 或媒体。
- 不做任何联网操作，不使用真实站点。
- 数字是组件级测量（统计修复本身），不等于界面冷启动时间；不可把各组件耗时相加当作应用启动耗时。
