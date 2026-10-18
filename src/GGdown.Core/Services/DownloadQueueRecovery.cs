using System.Text.Json;
using GGdown.Data;
using GGdown.Sites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GGdown.Services;

public sealed record QueueRecoveryResult(int Resumed, int Unavailable);

public interface IQueueRecoveryService
{
    Task<int> GetInterruptedCountAsync(CancellationToken ct = default);
    Task<QueueRecoveryResult> ResumeInterruptedAsync(CancellationToken ct = default);
    Task DiscardInterruptedAsync(CancellationToken ct = default);
}

public sealed partial class DownloadQueueService : IQueueRecoveryService
{
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);

    private static IQueryable<DownloadJob> Interrupted(GGdownSiteDbContext db) =>
        db.Jobs.Where(j => j.Status == JobStatus.Failed && j.RecoveryData != null);

    public async Task<int> GetInterruptedCountAsync(CancellationToken ct = default)
    {
        var count = 0;
        foreach (var siteId in siteFactory.ExistingSites())
        {
            await using var db = await siteFactory.CreateAsync(siteId, ct);
            count += await Interrupted(db).CountAsync(ct);
        }
        return count;
    }

    public async Task<QueueRecoveryResult> ResumeInterruptedAsync(CancellationToken ct = default)
    {
        await _recoveryGate.WaitAsync(ct);
        try
        {
            int resumed = 0, unavailable = 0;
            foreach (var siteId in siteFactory.ExistingSites())
            {
                await using var db = await siteFactory.CreateAsync(siteId, ct);
                var jobs = await Interrupted(db).AsTracking().OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).ToListAsync(ct);
                foreach (var old in jobs)
                {
                    var account = await ResolveRecoveryAccountAsync(db, old.AccountId, ct);
                    if (account is null || account.Status != AccountStatus.Ok || !sites.IsRegistered(siteId)
                        || !File.Exists(AccountService.AbsoluteCookiePath(paths, account)))
                    {
                        unavailable++;
                        continue;
                    }
                    WorkItem request;
                    try
                    {
                        request = ReadRecoveryData(old.RecoveryData!);
                        if (request.SiteId != siteId || request.AccountId != old.AccountId
                            || request.Kind != old.TargetKind || request.UserId != old.UserId
                            || request.SiteOptions is null || string.IsNullOrWhiteSpace(request.BaseDirectory)
                            || string.IsNullOrWhiteSpace(request.ArchiveFile)
                            || string.IsNullOrWhiteSpace(request.TargetScreenName) || string.IsNullOrWhiteSpace(request.Title))
                            throw new JsonException("恢复参数与任务不一致");
                        var accountContent = request.Kind is TargetKind.AccountLikes or TargetKind.AccountBookmarks
                            or TargetKind.AccountNovelBookmarks;
                        request = request with
                        {
                            SiteOptions = request.SiteOptions.ToDictionary(p => p.Key, p => RestoreValue(p.Value)),
                            // 凭据可能已由用户重新导入，恢复时使用当前路径。
                            AccountId = account.Id,
                            CookieAbsolutePath = AccountService.AbsoluteCookiePath(paths, account),
                            TargetScreenName = accountContent ? account.ScreenName ?? request.TargetScreenName : request.TargetScreenName,
                            TargetRestId = accountContent ? account.RestId ?? request.TargetRestId : request.TargetRestId,
                            Title = accountContent
                                ? $"{sites.Get(siteId).KindLabel(ContentKindMapping.ToContentKind(request.Kind))}（@{account.ScreenName}）"
                                : request.Title,
                            // 重新导入会生成新的账号行；保留原归档，避免同一账号换凭据后重复下载。
                            ArchiveFile = Path.Combine(paths.ArchiveDir, Path.GetFileName(request.ArchiveFile)),
                        };
                    }
                    catch (NotSupportedException ex)
                    {
                        old.ErrorMessage = ex.Message;
                        await db.SaveChangesAsync(ct);
                        unavailable++;
                        continue;
                    }
                    catch (JsonException ex)
                    {
                        log.LogWarning(ex, "任务 {SiteId}/{JobId} 恢复参数损坏", siteId, old.Id);
                        old.ErrorMessage = "任务恢复参数损坏，请重新添加下载";
                        old.RecoveryData = null;
                        await db.SaveChangesAsync(ct);
                        unavailable++;
                        continue;
                    }

                    // 新任务和旧请求的消费在同一事务提交，恢复途中再次崩溃也不会重复建队列。
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    var job = NewJob(request);
                    old.RecoveryData = null;
                    db.Jobs.Add(job);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    QueuePersisted(request with { JobId = job.Id });
                    resumed++;
                }
            }
            return new QueueRecoveryResult(resumed, unavailable);
        }
        finally { _recoveryGate.Release(); }
    }

    private static async Task<Account?> ResolveRecoveryAccountAsync(GGdownSiteDbContext db, long accountId,
        CancellationToken ct)
    {
        var original = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct);
        if (original is null) return null;
        var candidates = db.Accounts.AsNoTracking()
            .Where(a => a.SiteId == original.SiteId && a.IsActive && a.Status == AccountStatus.Ok);
        // 只允许同一平台身份换凭据，不能把账号收藏等任务切换到另一个登录用户。
        if (!string.IsNullOrEmpty(original.RestId))
            candidates = candidates.Where(a => a.RestId == original.RestId);
        else if (!string.IsNullOrEmpty(original.ScreenName))
            candidates = candidates.Where(a => a.ScreenName == original.ScreenName);
        else
            return original;
        return await candidates.OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct) ?? original;
    }

    public async Task DiscardInterruptedAsync(CancellationToken ct = default)
    {
        await _recoveryGate.WaitAsync(ct);
        try
        {
            foreach (var siteId in siteFactory.ExistingSites())
            {
                await using var db = await siteFactory.CreateAsync(siteId, ct);
                await Interrupted(db).ExecuteUpdateAsync(s => s.SetProperty(j => j.RecoveryData, (string?)null), ct);
            }
        }
        finally { _recoveryGate.Release(); }
    }

    private static DownloadJob NewJob(WorkItem request) => new()
    {
        AccountId = request.AccountId, SiteId = request.SiteId, TargetKind = request.Kind, UserId = request.UserId,
        Status = JobStatus.Pending, CreatedAt = DateTime.UtcNow,
        RecoveryData = WriteRecoveryData(request),
    };

    private async Task<long> PersistAndQueueAsync(GGdownSiteDbContext db, WorkItem request, CancellationToken ct,
        bool startWorkers = true)
    {
        var job = NewJob(request);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        // 反序列化一份快照，避免入队后调用方修改选项导致实际下载与恢复请求不一致。
        request = ReadRecoveryData(job.RecoveryData!);
        QueuePersisted(request with { JobId = job.Id,
            SiteOptions = request.SiteOptions.ToDictionary(p => p.Key, p => RestoreValue(p.Value)) }, startWorkers);
        return job.Id;
    }

    private void QueuePersisted(WorkItem item, bool startWorkers = true)
    {
        JobSnapshot snapshot;
        lock (_gate)
        {
            _pending.AddLast(item);
            var label = sites.IsRegistered(item.SiteId)
                ? sites.Get(item.SiteId).KindLabel(ContentKindMapping.ToContentKind(item.Kind)) : item.Kind.ToString();
            snapshot = new(item.JobId, item.SiteId, item.Kind, item.Title, JobStatus.Pending,
                0, 0, 0, 0, null, null, label, item.UserId);
            _active[Key(item.SiteId, item.JobId)] = snapshot;
        }
        Publish(JobChanged, snapshot);
        if (startWorkers) EnsureWorkers();
    }

    private static object? RestoreValue(object? value) => value is JsonElement element ? element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var number) ? (object)number : element.GetDouble(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => RestoreValue(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(e => RestoreValue(e)).ToArray(),
        _ => null,
    } : value;

    private static string WriteRecoveryData(WorkItem r) => JsonSerializer.Serialize(new QueueRecoveryData(1,
        new(r.AccountId, r.SiteId, r.Kind.ToString(), r.UserId, r.TargetScreenName, r.TargetRestId, r.Title,
            r.BaseDirectory, r.SiteOptions, r.CookieAbsolutePath, r.ArchiveFile, r.DirectUrl,
            r.ContentSelection, r.DownloadSince)));

    private static WorkItem ReadRecoveryData(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("恢复参数须为对象");
        if (!document.RootElement.TryGetProperty("SchemaVersion", out var version))
        {
            var legacy = JsonSerializer.Deserialize<WorkItem>(json) ?? throw new JsonException("恢复参数为空");
            if (!Enum.IsDefined(legacy.Kind)) throw new NotSupportedException("旧任务的内容类型不受支持，请升级软件后再试");
            return legacy;
        }
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
            throw new NotSupportedException("任务恢复参数版本不受支持，请升级软件后再试");
        var envelope = JsonSerializer.Deserialize<QueueRecoveryData>(json);
        var r = envelope?.Request ?? throw new JsonException("恢复请求为空");
        if (!Enum.TryParse<TargetKind>(r.ContentType, out var kind) || !Enum.IsDefined(kind))
            throw new NotSupportedException("任务恢复内容类型不受支持，请升级软件后再试");
        return new(0, r.AccountId, r.SiteId, kind, r.UserId, r.TargetScreenName, r.TargetRestId, r.Title,
            r.BaseDirectory, r.SiteOptions, r.CookieAbsolutePath, r.ArchiveFile, r.DirectUrl,
            r.ContentSelection, r.DownloadSince);
    }
}
