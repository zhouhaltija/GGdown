using GGdown.Data;

namespace GGdown.Services;

public sealed record DownloadBatchSummary(
    int Completed, int PartialFailed, int Failed, int Canceled, string? CompletedTitle)
{
    public bool HasFailures => PartialFailed > 0 || Failed > 0;
    public bool ShouldNotify => Completed > 0 || HasFailures;
}

public sealed class DownloadBatchTracker(Func<IReadOnlyList<JobSnapshot>> getActive)
{
    private readonly object _gate = new();
    private readonly HashSet<(string SiteId, long JobId)> _waiting = [];
    private int _completed;
    private int _partialFailed;
    private int _failed;
    private int _canceled;
    private string? _completedTitle;

    private static (string SiteId, long JobId) Key(JobSnapshot snapshot) =>
        (snapshot.SiteId.ToLowerInvariant(), snapshot.JobId);

    public void ObserveChanged(JobSnapshot snapshot)
    {
        if (snapshot.Status is not (JobStatus.Pending or JobStatus.Running)) return;
        lock (_gate)
        {
            var key = Key(snapshot);
            if (_waiting.Contains(key)) return;
            // 锁外发布的旧 Pending/进度可能晚于终态；只登记当前仍在队列中的任务。
            if (getActive().Any(s => Key(s) == key)) _waiting.Add(key);
        }
    }

    public DownloadBatchSummary? ObserveRemoved(JobSnapshot snapshot)
    {
        lock (_gate)
        {
            // 队列事件均在队列锁外发布；在这里同步读取 Active 不会形成反向锁顺序。
            var active = getActive();
            foreach (var current in active) _waiting.Add(Key(current));
            // 移除一次即计数一次；迟到、重复或订阅前结束的未知任务无需保留历史 ID。
            if (!_waiting.Remove(Key(snapshot))) return null;
            switch (snapshot.Status)
            {
                case JobStatus.Completed:
                    _completed++;
                    _completedTitle = snapshot.Title;
                    break;
                case JobStatus.PartialFailed: _partialFailed++; break;
                case JobStatus.Failed: _failed++; break;
                case JobStatus.Canceled: _canceled++; break;
            }
            // Active 先于终态事件移除，因此还必须等所有已知任务的 Removed 送达。
            if (_waiting.Count > 0 || active.Count > 0) return null;
            var summary = new DownloadBatchSummary(_completed, _partialFailed, _failed, _canceled, _completedTitle);
            _completed = _partialFailed = _failed = _canceled = 0;
            _completedTitle = null;
            return summary;
        }
    }
}
