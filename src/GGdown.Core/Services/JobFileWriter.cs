using System.Threading.Channels;
using GGdown.Data;
using GGdown.Engine;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Services;

/// <summary>任务文件事件有界排队、批量提交；取消下载后仍排干已接收记录。</summary>
internal sealed class JobFileWriter : IAsyncDisposable
{
    private readonly Channel<DownloadFile> _channel = Channel.CreateBounded<DownloadFile>(
        new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _consumer;
    private readonly string _siteId;
    private readonly long _jobId;
    private readonly long? _userId;
    private long _sequence;

    public JobFileWriter(ISiteDbContextFactory factory, string siteId, long jobId, long? userId)
    {
        (_siteId, _jobId, _userId) = (siteId, jobId, userId);
        _consumer = Task.Run(async () =>
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(_shutdown.Token))
                {
                    var batch = new List<DownloadFile>(100);
                    while (batch.Count < 100 && _channel.Reader.TryRead(out var row)) batch.Add(row);
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await using var db = await factory.CreateAsync(siteId, _shutdown.Token);
                            await using var transaction = await db.Database.BeginTransactionAsync(_shutdown.Token);
                            foreach (var row in batch)
                            {
                                await db.Database.ExecuteSqlInterpolatedAsync($"""
                                    INSERT INTO Files (JobId, SiteId, UserId, SourceItemId, Url, FilePath, FileSize, Status, CreatedAt, EventSequence)
                                    VALUES ({row.JobId}, {row.SiteId}, {row.UserId}, {row.SourceItemId}, {row.Url}, {row.FilePath}, {row.FileSize}, {(int)row.Status}, {row.CreatedAt}, {row.EventSequence})
                                    ON CONFLICT(JobId, EventSequence) WHERE EventSequence IS NOT NULL DO NOTHING
                                    """, _shutdown.Token);
                            }
                            await transaction.CommitAsync(_shutdown.Token);
                            break;
                        }
                        catch (SqliteException ex) when (attempt < 2 && ex.SqliteErrorCode is 5 or 6)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), _shutdown.Token);
                        }
                    }
                }
            }
            catch (Exception ex) { _channel.Writer.TryComplete(ex); throw; }
        });
    }

    public ValueTask WriteAsync(EngineEvent ev, FileStatus status, string? itemId, CancellationToken ct) =>
        _channel.Writer.WriteAsync(new DownloadFile
        {
            JobId = _jobId, SiteId = _siteId, UserId = _userId, SourceItemId = itemId,
            Url = ev.Url ?? "", FilePath = ev.Path ?? "", FileSize = ev.Size, Status = status,
            CreatedAt = DateTime.UtcNow, EventSequence = ++_sequence,
        }, ct);

    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        await _consumer.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _shutdown.CancelAsync();
        try { await _consumer; } catch { /* 异常已由 CompleteAsync 交给任务收尾。 */ }
        _shutdown.Dispose();
    }
}
