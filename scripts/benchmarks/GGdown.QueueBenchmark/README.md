# 真实 SQLite 队列基准

从仓库根目录运行（不需要 Python、网络、Cookie 或真实账号）：

```powershell
dotnet run --project scripts/benchmarks/GGdown.QueueBenchmark/GGdown.QueueBenchmark.csproj -c Release -- --events 10000,100000 --concurrency 1,4 --repeat 3 --warmup 1000 --timeout-seconds 300
```

快速检查可使用 `--events 100 --concurrency 1,4 --repeat 1 --warmup 0`。正式比较建议关闭 GGdown 及其他大量读写磁盘的程序，并保留相同 .NET、构建模式、硬件和电源计划。退出码为 0 表示所有正式场景的完整性检查通过；任一检查失败返回 1，未处理的异常也使进程失败。

每次执行创建独立的 `.cache/medium-priority/job-file-writer/<UTC时间>-<进程号>/`，不会读取 `%LOCALAPPDATA%/GGdown`。保留每轮 SQLite 数据库和 `results.json`，不自动删除历史结果。结果 JSON 包含运行环境、参数、逐轮数据和完整性错误。默认先单独预热 1000 个事件；预热数据库保留，但预热结果不纳入正式数据。

## 工作负载

- 10k / 100k 表示**每场景文件事件总数**。4 并发时分别为每任务 2500 / 25000 个事件；1 并发为每任务 10000 / 100000 个事件。
- 通过生产 `DownloadQueueService.EnqueueUserMediaAsync` 为 1 / 4 个不同的虚构用户排入同一 `twitter` 站点库，使用真实 `StatsAggregator`、`SiteDbContextFactory`、迁移和 `JobFileWriter`。
- fake engine 覆写 `DownloadWithEventsAsync`，直接 await 队列事件接收器。所有任务到达引擎后统一开始，结果记录实际最大引擎并发数，防止把排队执行误报为四并发。
- 90% `file-done`、10% `file-skip`，每个事件带确定的站点、用户、路径、来源 ID、URL 和 1024 字节大小字段。最后发送 `job-done`。不创建媒体文件，1024 仅为数据库元数据；不测下载文件写盘。
- 每轮独立建库、播种和迁移，准备结束后才启动计时。每轮启动测量前进行完整 GC，延迟样本数组已分配；不改生产连接池设置、事务大小、PRAGMA 或 writer 参数。

## 指标口径

| 字段 | 含义 |
| --- | --- |
| `ElapsedSeconds` | 从首次公开入队调用前，到全部 `JobRemoved` 到达；包含任务入库、事件生成/回调、文件提交、完成状态提交、用户统计更新 |
| `EventsPerSecond` | 成功返回的文件事件数 / 上述时长；只有 `Integrity.Passed=true` 时才代表完整落库吞吐 |
| `CallbackWait` | 从调用 `onEvent` 前到该 await 返回，逐文件记录；最近秩方法计算 p50/p95/p99，同时报告均值和最大值，单位毫秒 |
| `IncompleteCallbacks` | `onEvent` 返回时尚未成功完成的 `ValueTask` 次数；反映异步等待出现次数 |
| `Memory` | 测量区间起点及约每 25ms 采样的托管堆、进程工作集和私有内存；另含全进程累计托管分配增量、GC 次数增量和 CPU 时间增量 |
| `DatabaseAndSidecarBytes` | 完整性校验结束后站点数据目录中数据库及 WAL/SHM 等文件的总字节数 |

`CallbackWait` 是生产者可见等待，包含 `JobFileWriter.WriteAsync` 的 channel 背压、队列快照处理与线程调度；未插入内部探针，因此**不能解释成 SQLite 锁等待、单批事务耗时或事件到持久化提交的延迟**。立即返回的事件仍可能停留在 channel 中，持久化完成由任务终态后的数据库校验保证。`IncompleteCallbacks` 同样不能单独归因于 SQLite 锁竞争。

内存指标属于整个基准进程，包含 EF、SQLite、本基准的事件对象、延迟采样数组和采样器。每 100k 个事件的原始延迟数组约占 0.8MB，已计入基线；不据此声称 writer 自身的精确内存。采样峰值可能漏掉短于采样间隔的瞬时峰值，线程池繁忙也可能延迟采样，因此同时保留 `SampleCount`。工作集/私有内存受 OS 页缓存和分配器保留影响，连续轮次可能有升高的基线；应比较增量并保留绝对值，必要时每个场景单独启动新进程。分配与 CPU 时间包含采样器开销。

本基准排除了 JSONL 解析、Python 子进程、网络、真实下载、UI 和用户操作。新建数据库仍受 OS 缓存及本机存储策略影响；每轮数据库内容互相独立，但不声称冷磁盘或每轮冷 JIT。默认固定顺序运行并保留三轮原始值，没有自动给出显著性或推荐生产参数。

## 完整性校验

测量结束后逐行流式读取真实 SQLite 文件记录，检查每任务恰有预期行数、`EventSequence` 从 1 连续且无重复、所属站点和用户正确、来源 ID/路径/URL/大小/状态与输入一致；检查所有任务及终态快照均成功，任务汇总与真实行一致，统计脏标记已清理，每个用户下载次数为 1。警告/错误日志、超时、实际并发不足、丢失事件或计数不符均使完整性失败。

校验与分位数排序在吞吐及内存测量之后进行，不计入上述性能指标。保留失败轮的数据用于诊断，不能将完整性失败的数值用于 writer 性能结论。
