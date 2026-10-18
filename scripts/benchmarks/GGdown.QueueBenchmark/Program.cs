using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using GGdown.Data;
using GGdown.Engine;
using GGdown.Paths;
using GGdown.Services;
using GGdown.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// 仅走公开队列接口，所有数据库及虚构账号均位于本次运行的新目录。
var options = Options.Parse(args);
var output = Path.Combine(FindRepository(), ".cache", "medium-priority", "job-file-writer",
    $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}");
Directory.CreateDirectory(output);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
var results = new List<ScenarioResult>();
Console.WriteLine($"输出目录：{output}");
if (options.Warmup > 0)
{
    Console.WriteLine($"预热：{options.Warmup} 文件事件 / 1 任务");
    var warmup = await RunAsync(options.Warmup, 1, 0, output, options.TimeoutSeconds);
    if (!warmup.Integrity.Passed)
        throw new InvalidOperationException($"预热完整性校验失败：{string.Join("; ", warmup.Integrity.Errors)}");
}
foreach (var events in options.Events)
foreach (var concurrency in options.Concurrency)
for (var repetition = 1; repetition <= options.Repeat; repetition++)
{
    Console.WriteLine($"开始：总事件 {events} / 同站并发 {concurrency} / 第 {repetition} 轮");
    var result = await RunAsync(events, concurrency, repetition, output, options.TimeoutSeconds);
    results.Add(result);
    await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
    {
        CreatedAtUtc = DateTime.UtcNow,
        Environment = new
        {
            Framework = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = System.Environment.ProcessorCount,
            Stopwatch.Frequency,
            Configuration =
#if DEBUG
                "Debug",
#else
                "Release",
#endif
            Options = options,
        },
        Scenarios = results,
    }, jsonOptions));
    Console.WriteLine($"完成：完整性={result.Integrity.Passed}，{result.EventsPerSecond:F0} 事件/秒，" +
        $"回调等待 p95={result.CallbackWait.P95Milliseconds:F3}ms，p99={result.CallbackWait.P99Milliseconds:F3}ms，" +
        $"工作集采样峰值={result.Memory.SampledPeakWorkingSetBytes / 1048576.0:F1}MiB");
}
return results.All(r => r.Integrity.Passed) ? 0 : 1;

static string FindRepository()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "src", "GGdown.Core", "GGdown.Core.csproj")))
            return directory.FullName;
    throw new InvalidOperationException("请从仓库目录执行基准。");
}

static async Task<ScenarioResult> RunAsync(int events, int concurrency, int repetition, string output, int timeoutSeconds)
{
    var directory = Path.Combine(output, $"events-{events}-jobs-{concurrency}-repeat-{repetition}");
    var paths = new AppPaths(directory);
    paths.EnsureCreated();
    var factory = new SiteDbContextFactory(paths);
    var account = new Account
    {
        SiteId = "twitter", CookiePath = "synthetic-not-a-cookie.txt", ScreenName = "benchmark",
        Status = AccountStatus.Ok, IsActive = true, AddedAt = DateTime.UtcNow,
    };
    var users = Enumerable.Range(0, concurrency).Select(i => new User
    {
        SiteId = "twitter", RestId = $"benchmark-{i}", ScreenName = $"bench_user_{i}",
        AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    }).ToArray();
    // 排除 EF 模型初始化、迁移、WAL 配置和种子数据；实际业务连接仍由生产工厂创建。
    await using (var db = await factory.CreateAsync("twitter"))
    {
        db.Accounts.Add(account);
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
    }
    var engine = new SyntheticEngine(events / concurrency, concurrency);
    var logger = new ErrorCollector();
    var queue = new DownloadQueueService(factory, engine, new StatsAggregator(factory), paths,
        new SiteRegistry([new TwitterSiteProvider()]), logger) { Concurrency = concurrency };
    var terminal = new ConcurrentBag<JobSnapshot>();
    var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var removed = 0;
    queue.JobRemoved += snapshot =>
    {
        terminal.Add(snapshot);
        if (Interlocked.Increment(ref removed) == concurrency) drained.TrySetResult();
    };
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    var cpuBefore = process.TotalProcessorTime;
    var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
    var collectionsBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
    using var samplingStop = new CancellationTokenSource();
    var memory = new MemorySamples();
    memory.Observe(process);
    var sampler = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        try
        {
            while (await timer.WaitForNextTickAsync(samplingStop.Token)) memory.Observe(process);
        }
        catch (OperationCanceledException) when (samplingStop.IsCancellationRequested) { }
    });
    var stopwatch = Stopwatch.StartNew();
    var timedOut = false;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
    try
    {
        await queue.EnqueueUserMediaAsync(account, users, paths.TempDir, new Dictionary<string, object?>(), timeout.Token);
        await drained.Task.WaitAsync(timeout.Token);
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        timedOut = true;
        // 取消生产后仍等待队列排干；超时会让本轮失败，并保留 SQLite 供检查。
        foreach (var active in queue.Active) await queue.CancelAsync(active.JobId, siteId: active.SiteId);
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }
    finally
    {
        stopwatch.Stop();
        await samplingStop.CancelAsync();
        await sampler;
    }
    memory.Observe(process);
    process.Refresh();
    var memoryResult = new MemoryResult(memory.FirstManaged, memory.PeakManaged,
        memory.FirstWorkingSet, memory.PeakWorkingSet, memory.FirstPrivate, memory.PeakPrivate,
        GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore,
        Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collectionsBefore[i]).ToArray(),
        memory.Count, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds);
    // 校验不计入吞吐及内存测量，逐行读取避免一次性加载全部实体。
    var integrity = await ValidateAsync(factory, events, concurrency, engine, terminal.ToArray(), logger.Errors, timedOut);
    var dbBytes = Directory.EnumerateFiles(paths.SitesDataDir).Sum(file => new FileInfo(file).Length);
    return new ScenarioResult(events, events / concurrency, concurrency, repetition,
        stopwatch.Elapsed.TotalSeconds, engine.Delivered / stopwatch.Elapsed.TotalSeconds,
        engine.Delivered, engine.MaxConcurrent, engine.IncompleteCallbacks, engine.GetWaits(), memoryResult,
        integrity, dbBytes, directory);
}

static async Task<IntegrityResult> ValidateAsync(ISiteDbContextFactory factory, int events, int concurrency,
    SyntheticEngine engine, JobSnapshot[] terminal, IReadOnlyCollection<string> logErrors, bool timedOut)
{
    var errors = new List<string>();
    void Check(bool valid, string error) { if (!valid && errors.Count < 30) errors.Add(error); }
    Check(!timedOut, "场景超时");
    Check(logErrors.Count == 0, $"队列日志出现警告或错误：{string.Join("; ", logErrors.Take(5))}");
    Check(engine.Delivered == events, $"引擎事件数 {engine.Delivered} != {events}");
    Check(engine.MaxConcurrent == concurrency, $"实际最大并发 {engine.MaxConcurrent} != {concurrency}");
    Check(terminal.Length == concurrency && terminal.All(s => s.Status == JobStatus.Completed), "终态快照数量或状态异常");
    await using var db = await factory.CreateAsync("twitter");
    var jobs = await db.Jobs.AsNoTracking().ToListAsync();
    Check(jobs.Count == concurrency, "任务行数异常");
    var byId = jobs.ToDictionary(j => j.Id);
    var userSlots = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.ScreenName);
    var counts = jobs.ToDictionary(j => j.Id, _ => 0L);
    var done = jobs.ToDictionary(j => j.Id, _ => 0L);
    var skipped = jobs.ToDictionary(j => j.Id, _ => 0L);
    var perJob = events / concurrency;
    long actual = 0;
    await foreach (var file in db.Files.AsNoTracking().OrderBy(f => f.JobId).ThenBy(f => f.EventSequence).AsAsyncEnumerable())
    {
        actual++;
        if (!byId.TryGetValue(file.JobId, out var job)) { Check(false, "出现未知任务文件"); continue; }
        var sequence = ++counts[file.JobId];
        Check(file.EventSequence == sequence, $"任务 {job.Id} 序号缺失或重复：{sequence}/{file.EventSequence}");
        Check(file.SiteId == "twitter" && file.UserId == job.UserId, "文件所属站点或用户不匹配");
        var expectedSlot = userSlots[job.UserId!.Value].Split('_')[^1];
        var index = sequence - 1;
        Check(file.SourceItemId == $"{expectedSlot}:{index}", "SourceItemId 内容不匹配");
        Check(Path.GetFileName(file.FilePath) == $"bench-{int.Parse(expectedSlot):D2}-{index:D8}.bin", "文件路径不匹配");
        Check(file.Url == $"https://benchmark.invalid/{expectedSlot}/{index}" && file.FileSize == 1024, "文件负载不匹配");
        var expectedStatus = index % 10 == 0 ? GGdown.Data.FileStatus.Skipped : GGdown.Data.FileStatus.Downloaded;
        Check(file.Status == expectedStatus, "文件状态不匹配");
        if (file.Status == GGdown.Data.FileStatus.Downloaded) done[file.JobId]++;
        if (file.Status == GGdown.Data.FileStatus.Skipped) skipped[file.JobId]++;
    }
    foreach (var job in jobs)
    {
        Check(counts[job.Id] == perJob, $"任务 {job.Id} 记录数不完整");
        Check(job.Status == JobStatus.Completed && job.ErrorMessage is null && job.RecoveryData is null, $"任务 {job.Id} 完成状态异常");
        Check(job.TotalFiles == perJob && job.DoneFiles == done[job.Id] && job.SkippedFiles == skipped[job.Id]
            && job.FailedFiles == 0 && !job.StatisticsPending, $"任务 {job.Id} 汇总或统计标记异常");
    }
    Check(actual == events, $"SQLite 实际行数 {actual} != {events}");
    Check(await db.Users.CountAsync(u => u.DownloadCount == 1) == concurrency, "用户下载次数投影异常");
    return new IntegrityResult(errors.Count == 0, actual, jobs.Count, errors.ToArray());
}

sealed record Options(int[] Events, int[] Concurrency, int Repeat, int Warmup, int TimeoutSeconds)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || args[i] is not ("--events" or "--concurrency" or "--repeat" or "--warmup" or "--timeout-seconds"))
                throw new ArgumentException("参数：--events 10000,100000 --concurrency 1,4 --repeat 3 --warmup 1000 --timeout-seconds 300");
            values.Add(args[i], args[i + 1]);
        }
        int Read(string name, int fallback) => values.TryGetValue(name, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
        int[] List(string name, string fallback) => values.GetValueOrDefault(name, fallback).Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var result = new Options(List("--events", "10000,100000"), List("--concurrency", "1,4"), Read("--repeat", 3), Read("--warmup", 1000), Read("--timeout-seconds", 300));
        if (result.Events.Any(n => n < 1) || result.Concurrency.Any(n => n < 1) || result.Repeat < 1 || result.Warmup < 0
            || result.TimeoutSeconds < 1 || result.Events.Any(n => result.Concurrency.Any(c => n % c != 0)))
            throw new ArgumentException("事件数必须大于零且能被各并发数整除；重复次数、超时和并发数必须为正，预热数不得为负。");
        return result;
    }
}

sealed class SyntheticEngine(int countPerJob, int concurrency) : IDownloadEngine
{
    private readonly long[][] _waits = Enumerable.Range(0, concurrency).Select(_ => new long[countPerJob]).ToArray();
    private readonly int[] _written = new int[concurrency];
    private readonly TaskCompletionSource _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;
    private int _running;
    private int _maxConcurrent;
    private long _delivered;
    private long _incomplete;
    public long Delivered => Interlocked.Read(ref _delivered);
    public long IncompleteCallbacks => Interlocked.Read(ref _incomplete);
    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    public async Task DownloadWithEventsAsync(DownloadPlan plan, string cookiesFile,
        Func<EngineEvent, CancellationToken, ValueTask> onEvent, CancellationToken ct = default)
    {
        var slot = int.Parse(new Uri(plan.Urls[0]).Segments[1].Trim('/').Split('_')[^1], CultureInfo.InvariantCulture);
        var running = Interlocked.Increment(ref _running);
        int previous;
        do { previous = Volatile.Read(ref _maxConcurrent); }
        while (previous < running && Interlocked.CompareExchange(ref _maxConcurrent, running, previous) != previous);
        if (Interlocked.Increment(ref _arrived) == concurrency) _start.TrySetResult();
        try
        {
            await _start.Task.WaitAsync(ct);
            for (var i = 0; i < countPerJob; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ev = new EngineEvent(i % 10 == 0 ? "file-skip" : "file-done",
                    Url: $"https://benchmark.invalid/{slot}/{i}",
                    Path: Path.Combine(plan.BaseDirectory, $"bench-{slot:D2}-{i:D8}.bin"),
                    ItemId: $"{slot}:{i}", Size: 1024);
                var before = Stopwatch.GetTimestamp();
                var pending = onEvent(ev, ct);
                if (!pending.IsCompletedSuccessfully) Interlocked.Increment(ref _incomplete);
                await pending;
                _waits[slot][i] = Stopwatch.GetTimestamp() - before;
                _written[slot] = i + 1;
                Interlocked.Increment(ref _delivered);
            }
            await onEvent(new EngineEvent("job-done", Total: countPerJob, Skipped: (countPerJob + 9) / 10, Failed: 0), ct);
        }
        finally { Interlocked.Decrement(ref _running); }
    }

    public WaitResult GetWaits()
    {
        // 只纳入已成功返回的文件事件；完整性失败的场景不可用于性能比较。
        var sorted = _waits.SelectMany((waits, slot) => waits.Take(_written[slot])).ToArray();
        if (sorted.Length == 0) return new WaitResult(0, 0, 0, 0, 0, 0);
        Array.Sort(sorted);
        double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        double Percentile(double fraction) => Milliseconds(sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * fraction) - 1)]);
        return new WaitResult(sorted.Length, Percentile(.5), Percentile(.95), Percentile(.99), Milliseconds(sorted[^1]),
            sorted.Average(t => Milliseconds(t)));
    }

    public Task DownloadAsync(DownloadPlan plan, string cookiesFile, IProgress<EngineEvent> progress, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<EngineHello> HelloAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AccountInfo> WhoAmIAsync(string siteId, string cookiesFile, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<SiteUserInfo>> ListFollowingAsync(string siteId, string cookiesFile, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SiteUserInfo> GetUserInfoAsync(string siteId, string cookiesFile, string input, CancellationToken ct = default) => throw new NotSupportedException();
}

sealed class ErrorCollector : ILogger<DownloadQueueService>
{
    private readonly ConcurrentQueue<string> _errors = new();
    public IReadOnlyCollection<string> Errors => _errors.ToArray();
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel)) _errors.Enqueue(formatter(state, exception));
    }
}

sealed class MemorySamples
{
    public long FirstManaged { get; private set; }
    public long PeakManaged { get; private set; }
    public long FirstWorkingSet { get; private set; }
    public long PeakWorkingSet { get; private set; }
    public long FirstPrivate { get; private set; }
    public long PeakPrivate { get; private set; }
    public int Count { get; private set; }
    public void Observe(Process process)
    {
        process.Refresh();
        var managed = GC.GetTotalMemory(forceFullCollection: false);
        var workingSet = process.WorkingSet64;
        var privateBytes = process.PrivateMemorySize64;
        if (Count++ == 0) (FirstManaged, FirstWorkingSet, FirstPrivate) = (managed, workingSet, privateBytes);
        PeakManaged = Math.Max(PeakManaged, managed);
        PeakWorkingSet = Math.Max(PeakWorkingSet, workingSet);
        PeakPrivate = Math.Max(PeakPrivate, privateBytes);
    }
}

sealed record WaitResult(int Samples, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds,
    double MaxMilliseconds, double MeanMilliseconds);
sealed record MemoryResult(long BaselineManagedBytes, long SampledPeakManagedBytes, long BaselineWorkingSetBytes,
    long SampledPeakWorkingSetBytes, long BaselinePrivateBytes, long SampledPeakPrivateBytes,
    long ProcessAllocatedBytes, int[] GenerationCollections, int SampleCount, double ProcessCpuMilliseconds);
sealed record IntegrityResult(bool Passed, long ActualFileRows, int ActualJobRows, string[] Errors);
sealed record ScenarioResult(int TotalFileEvents, int EventsPerJob, int Concurrency, int Repetition,
    double ElapsedSeconds, double EventsPerSecond, long DeliveredEvents, int ObservedEngineConcurrency,
    long IncompleteCallbacks, WaitResult CallbackWait, MemoryResult Memory, IntegrityResult Integrity,
    long DatabaseAndSidecarBytes, string ArtifactDirectory);
