using System.Diagnostics;
using System.Text.Json;
using GGdown.Data;
using GGdown.Paths;
using GGdown.Services;
using Microsoft.EntityFrameworkCore;

// 启动统计基准：只在本次新建的缓存目录写入合成数据，不读取真实账号或媒体。
// 用法（仓库根目录）：dotnet run --project scripts/benchmarks/GGdown.StartupBenchmark -c Release
const string Site = "twitter";
const int JobsPerAuthor = 10;
const int DirtyAuthors = 5;
var counts = new[] { 100, 1000, 5000 };
// 可选 --counts 1000,5000 只跑指定规模（排查或补测单档时用）。
var countsIndex = Array.IndexOf(args, "--counts");
if (countsIndex >= 0 && countsIndex + 1 < args.Length)
    counts = args[countsIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(int.Parse).ToArray();
var baseTime = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
var latestFinished = baseTime.AddDays(-1);
const int latestMediaSnapshot = 100 + JobsPerAuthor - 1;

var repo = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(repo, "src", "GGdown.Core", "GGdown.Core.csproj")))
{
    Console.Error.WriteLine("请在仓库根目录运行：dotnet run --project scripts/benchmarks/GGdown.StartupBenchmark -c Release");
    return 2;
}
var output = Path.Combine(repo, ".cache", "startup-benchmark", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(output);
Console.WriteLine($"OUTPUT {output}");

var measurements = new List<object>();

void Record(object item)
{
    measurements.Add(item);
    Console.WriteLine(JsonSerializer.Serialize(item));
}

foreach (var count in counts)
{
    var jobs = count * JobsPerAuthor;

    // 场景一：旧库首次启动校正（每个轮次用独立目录，因为成功后会写入版本标记）。
    for (var round = 1; round <= 3; round++)
    {
        var factory = await PrepareAsync(Path.Combine(output, $"first-{count}-{round}"), count);
        var sw = Stopwatch.StartNew();
        await new StatsAggregator(factory).RepairOnStartupAsync();
        sw.Stop();
        await VerifyFirstCorrectionAsync(factory, count);
        Record(new { Kind = "FirstCorrection", Users = count, Jobs = jobs, Round = round, Milliseconds = sw.Elapsed.TotalMilliseconds });
    }

    // 场景二：已初始化后的干净重启，只应做脏查询，不改写任何作者。
    var cleanFactory = await PrepareAsync(Path.Combine(output, $"clean-{count}"), count);
    var cleanStats = new StatsAggregator(cleanFactory);
    await cleanStats.RepairOnStartupAsync();
    for (var round = 1; round <= 3; round++)
    {
        var before = await SnapshotAsync(cleanFactory);
        var sw = Stopwatch.StartNew();
        await cleanStats.RepairOnStartupAsync();
        sw.Stop();
        await VerifyCleanRestartAsync(cleanFactory, count, before);
        Record(new { Kind = "CleanRestart", Users = count, Jobs = jobs, Round = round, Milliseconds = sw.Elapsed.TotalMilliseconds });
    }

    // 场景三：固定少量作者存在未投影的完成任务，只重算这些作者。
    var dirtyFactory = await PrepareAsync(Path.Combine(output, $"dirty-{count}"), count);
    var dirtyStats = new StatsAggregator(dirtyFactory);
    await dirtyStats.RepairOnStartupAsync();
    var dirtyIds = await DirtyUserIdsAsync(dirtyFactory, count);
    for (var round = 1; round <= 3; round++)
    {
        var before = await SnapshotAsync(dirtyFactory);
        await MarkDirtyAsync(dirtyFactory, dirtyIds);
        var sw = Stopwatch.StartNew();
        await dirtyStats.RepairOnStartupAsync();
        sw.Stop();
        await VerifyDirtyRepairAsync(dirtyFactory, dirtyIds, before);
        Record(new { Kind = "DirtyRepair", Users = count, Jobs = jobs, DirtyAuthors = dirtyIds.Count, Round = round, Milliseconds = sw.Elapsed.TotalMilliseconds });
    }

    // 参照项：旧的全量重算，仅用于对比，不是新的启动入口。
    var legacyFactory = await PrepareAsync(Path.Combine(output, $"legacy-{count}"), count);
    var legacySw = Stopwatch.StartNew();
    await new StatsAggregator(legacyFactory).RecalculateDownloadCountsAsync();
    legacySw.Stop();
    Record(new { Kind = "LegacyFullRebuildReference", Users = count, Jobs = jobs, Round = 1, Milliseconds = legacySw.Elapsed.TotalMilliseconds });
}

var summary = measurements
    .Select(item => JsonSerializer.SerializeToElement(item))
    .GroupBy(item => (Kind: item.GetProperty("Kind").GetString()!, Users: item.GetProperty("Users").GetInt32()))
    .Select(group => new
    {
        group.Key.Kind,
        group.Key.Users,
        Rounds = group.Count(),
        MedianMs = Math.Round(Median(group.Select(item => item.GetProperty("Milliseconds").GetDouble())), 2),
        MinMs = Math.Round(group.Min(item => item.GetProperty("Milliseconds").GetDouble()), 2),
        MaxMs = Math.Round(group.Max(item => item.GetProperty("Milliseconds").GetDouble()), 2),
    })
    .OrderBy(item => item.Users).ThenBy(item => item.Kind)
    .ToArray();

await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
{
    Timestamp = DateTime.UtcNow,
    Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    JobsPerAuthor,
    DirtyAuthors,
    Summary = summary,
    Measurements = measurements,
}, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine("SUMMARY");
foreach (var item in summary)
    Console.WriteLine($"{item.Kind,-28} users={item.Users,5} rounds={item.Rounds} median={item.MedianMs,9:F2} ms min={item.MinMs,9:F2} max={item.MaxMs,9:F2}");
Console.WriteLine($"RESULTS {Path.Combine(output, "results.json")}");
return 0;

static double Median(IEnumerable<double> values)
{
    var ordered = values.OrderBy(value => value).ToArray();
    return ordered.Length % 2 == 1
        ? ordered[ordered.Length / 2]
        : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
}

// 每作者 10 个已完成任务；作者统计先置为错误值，便于确认修复真的写入。
async Task<SiteDbContextFactory> PrepareAsync(string root, int count)
{
    var paths = new AppPaths(root);
    paths.EnsureCreated();
    var factory = new SiteDbContextFactory(paths);
    await using var db = await factory.CreateAsync(Site);
    var account = new Account { SiteId = Site, CookiePath = "synthetic.txt", AddedAt = baseTime };
    db.Accounts.Add(account);
    await db.SaveChangesAsync();

    var users = Enumerable.Range(1, count).Select(i => new User
    {
        SiteId = Site,
        RestId = $"synthetic-{i}",
        ScreenName = $"synthetic_{i}",
        AddedAt = baseTime,
        UpdatedAt = baseTime,
        DownloadCount = 0,
        MediaCountAtDownload = 0,
    }).ToArray();
    db.Users.AddRange(users);
    await db.SaveChangesAsync();

    var jobs = new List<DownloadJob>(2000);
    foreach (var user in users)
        for (var i = 0; i < JobsPerAuthor; i++)
        {
            jobs.Add(new DownloadJob
            {
                SiteId = Site,
                AccountId = account.Id,
                UserId = user.Id,
                Status = JobStatus.Completed,
                CreatedAt = baseTime.AddDays(-JobsPerAuthor + i),
                FinishedAt = baseTime.AddDays(-JobsPerAuthor + i),
                MediaCountSnapshot = 100 + i,
                StatisticsPending = false,
            });
            // 分批提交并清空跟踪器：5000 作者 × 10 任务一次性 AddRange 会让进程在准备阶段被内存压垮。
            if (jobs.Count < 2000) continue;
            db.Jobs.AddRange(jobs);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            jobs.Clear();
        }
    if (jobs.Count > 0)
    {
        db.Jobs.AddRange(jobs);
        await db.SaveChangesAsync();
    }
    db.ChangeTracker.Clear();
    return factory;
}

async Task<Dictionary<long, DateTime>> SnapshotAsync(SiteDbContextFactory factory)
{
    await using var db = await factory.CreateAsync(Site);
    return await db.Users.AsNoTracking().ToDictionaryAsync(user => user.Id, user => user.UpdatedAt);
}

static async Task<List<long>> DirtyUserIdsAsync(SiteDbContextFactory factory, int count)
{
    var step = Math.Max(1, count / DirtyAuthors);
    // 用 List 而非数组：EF Core 对 string[] 的 Contains 会绑定到 MemoryExtensions 重载而无法翻译。
    var restIds = Enumerable.Range(0, DirtyAuthors).Select(i => $"synthetic-{i * step + 1}").ToList();
    await using var db = await factory.CreateAsync(Site);
    return await db.Users.AsNoTracking().Where(user => restIds.Contains(user.RestId))
        .OrderBy(user => user.Id).Select(user => user.Id).ToListAsync();
}

// 准备阶段（不计时）：这些作者各留一个未投影的任务，并把统计改成错误值。
static async Task MarkDirtyAsync(SiteDbContextFactory factory, List<long> userIds)
{
    await using var db = await factory.CreateAsync(Site);
    var jobs = await db.Jobs.Where(job => job.UserId != null && userIds.Contains(job.UserId.Value))
        .OrderBy(job => job.Id).ToListAsync();
    foreach (var group in jobs.GroupBy(job => job.UserId))
        group.First().StatisticsPending = true;
    await db.Users.Where(user => userIds.Contains(user.Id))
        .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.DownloadCount, 0));
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
}

async Task VerifyFirstCorrectionAsync(SiteDbContextFactory factory, int count)
{
    await using var db = await factory.CreateAsync(Site);
    var wrong = await db.Users.CountAsync(user => user.DownloadCount != JobsPerAuthor
        || user.LastDownloadAt != latestFinished || user.MediaCountAtDownload != latestMediaSnapshot);
    if (wrong != 0) throw new InvalidOperationException($"首次校正后仍有 {wrong} 个作者统计不正确（期望 {count} 个作者）");
    var version = await db.SiteSettings.SingleOrDefaultAsync(setting => setting.Key == "ggdown.stats-projection.version");
    if (version?.Value != "1") throw new InvalidOperationException("首次校正未写入版本标记");
    if (await db.Jobs.AnyAsync(job => job.StatisticsPending)) throw new InvalidOperationException("首次校正后仍存在脏任务");
}

async Task VerifyCleanRestartAsync(SiteDbContextFactory factory, int count, Dictionary<long, DateTime> before)
{
    var after = await SnapshotAsync(factory);
    if (after.Count != count) throw new InvalidOperationException($"作者数量异常：{after.Count}");
    var rewritten = after.Count(pair => before[pair.Key] != pair.Value);
    if (rewritten != 0) throw new InvalidOperationException($"干净重启改写了 {rewritten} 个作者");
    await using var db = await factory.CreateAsync(Site);
    if (await db.Users.CountAsync(user => user.DownloadCount != JobsPerAuthor) != 0)
        throw new InvalidOperationException("干净重启后统计被破坏");
    if (await db.Jobs.AnyAsync(job => job.StatisticsPending)) throw new InvalidOperationException("干净重启后出现脏任务");
}

async Task VerifyDirtyRepairAsync(SiteDbContextFactory factory, List<long> userIds, Dictionary<long, DateTime> before)
{
    await using var db = await factory.CreateAsync(Site);
    var repaired = await db.Users.Where(user => userIds.Contains(user.Id)).ToListAsync();
    if (repaired.Count != userIds.Count) throw new InvalidOperationException("脏作者数量异常");
    if (repaired.Any(user => user.DownloadCount != JobsPerAuthor)) throw new InvalidOperationException("脏作者统计未修复");
    if (repaired.Any(user => before[user.Id] == user.UpdatedAt)) throw new InvalidOperationException("脏作者未被重算");
    var dirtyLeft = await db.Jobs.CountAsync(job => job.StatisticsPending);
    if (dirtyLeft != 0) throw new InvalidOperationException($"仍有 {dirtyLeft} 个脏任务未清理");
    var after = await db.Users.AsNoTracking().ToDictionaryAsync(user => user.Id, user => user.UpdatedAt);
    var touched = after.Count(pair => before[pair.Key] != pair.Value);
    if (touched != userIds.Count) throw new InvalidOperationException($"改写了 {touched} 个作者，期望只改 {userIds.Count} 个");
}
