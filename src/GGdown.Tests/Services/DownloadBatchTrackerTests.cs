using System.Collections.Concurrent;
using GGdown.Data;
using GGdown.Services;

namespace GGdown.Tests.Services;

public sealed class DownloadBatchTrackerTests
{
    private sealed class QueueEvents
    {
        public IReadOnlyList<JobSnapshot> Active { get; set; } = [];
        public DownloadBatchTracker Tracker { get; }

        public QueueEvents() => Tracker = new(() => Active);

        public void Observe(params JobSnapshot[] snapshots)
        {
            Active = snapshots;
            foreach (var snapshot in snapshots) Tracker.ObserveChanged(snapshot);
            Active = [];
        }
    }

    private static JobSnapshot Snapshot(long id, JobStatus status, string site = "twitter") =>
        new(id, site, TargetKind.UserMedia, $"{site}-{id}", status, 0, 0, 0, 0, null, null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Removed_jobs_wait_for_all_known_terminal_events_when_active_is_already_empty(bool partialFirst)
    {
        var events = new QueueEvents();
        var tracker = events.Tracker;
        var partial = Snapshot(1, JobStatus.PartialFailed);
        var completed = Snapshot(2, JobStatus.Completed);
        events.Observe(partial with { Status = JobStatus.Running }, completed with { Status = JobStatus.Running });

        Assert.Null(tracker.ObserveRemoved(partialFirst ? partial : completed));
        var summary = Assert.IsType<DownloadBatchSummary>(tracker.ObserveRemoved(partialFirst ? completed : partial));

        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, summary.PartialFailed);
        Assert.Equal(0, summary.Failed);
        Assert.True(summary.HasFailures);
        Assert.True(summary.ShouldNotify);
    }

    [Fact]
    public async Task Concurrent_terminal_delivery_produces_one_combined_summary()
    {
        var events = new QueueEvents();
        var tracker = events.Tracker;
        var partial = Snapshot(1, JobStatus.PartialFailed);
        var completed = Snapshot(2, JobStatus.Completed);
        events.Observe(partial with { Status = JobStatus.Running }, completed with { Status = JobStatus.Running });
        using var barrier = new Barrier(2);
        var summaries = new ConcurrentBag<DownloadBatchSummary>();

        await Task.WhenAll(new[] { partial, completed }.Select(snapshot => Task.Run(() =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            if (tracker.ObserveRemoved(snapshot) is { } summary) summaries.Add(summary);
        })));

        var actual = Assert.Single(summaries);
        Assert.Equal(1, actual.Completed);
        Assert.Equal(1, actual.PartialFailed);
        Assert.True(actual.HasFailures);
    }

    [Fact]
    public void Active_tasks_are_included_even_before_their_changed_callback_arrives()
    {
        var first = Snapshot(1, JobStatus.Completed);
        var second = Snapshot(2, JobStatus.Running);
        IReadOnlyList<JobSnapshot> active = [first with { Status = JobStatus.Running }];
        var tracker = new DownloadBatchTracker(() => active);
        tracker.ObserveChanged(first with { Status = JobStatus.Running });

        active = [second];
        Assert.Null(tracker.ObserveRemoved(first));
        active = [];
        var summary = Assert.IsType<DownloadBatchSummary>(tracker.ObserveRemoved(second with { Status = JobStatus.Failed }));
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, summary.Failed);
    }

    [Fact]
    public void Same_job_id_on_different_sites_keeps_both_outcomes()
    {
        var events = new QueueEvents();
        var tracker = events.Tracker;
        var twitter = Snapshot(1, JobStatus.Completed);
        var pixiv = Snapshot(1, JobStatus.PartialFailed, "pixiv");
        events.Observe(twitter with { Status = JobStatus.Pending }, pixiv with { Status = JobStatus.Pending });

        Assert.Null(tracker.ObserveRemoved(twitter with { SiteId = "TWITTER" }));
        var summary = Assert.IsType<DownloadBatchSummary>(tracker.ObserveRemoved(pixiv));
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, summary.PartialFailed);
    }

    [Fact]
    public void Late_pending_and_duplicate_terminal_do_not_reopen_a_finished_batch()
    {
        var events = new QueueEvents();
        var tracker = events.Tracker;
        var previous = Snapshot(1, JobStatus.PartialFailed);
        events.Observe(previous with { Status = JobStatus.Pending });
        Assert.NotNull(tracker.ObserveRemoved(previous));

        tracker.ObserveChanged(previous with { Status = JobStatus.Pending, SiteId = "TWITTER" });
        tracker.ObserveChanged(previous with { Status = JobStatus.Running });
        tracker.ObserveChanged(previous);
        Assert.Null(tracker.ObserveRemoved(previous));

        var next = Snapshot(2, JobStatus.Completed);
        events.Observe(next with { Status = JobStatus.Pending });
        var summary = Assert.IsType<DownloadBatchSummary>(tracker.ObserveRemoved(next));
        Assert.Equal(1, summary.Completed);
        Assert.Equal(0, summary.PartialFailed);
        Assert.False(summary.HasFailures);
    }

    [Fact]
    public void Canceled_job_waits_for_terminal_delivery_but_does_not_turn_partial_batch_into_success()
    {
        var events = new QueueEvents();
        var tracker = events.Tracker;
        var partial = Snapshot(1, JobStatus.PartialFailed);
        var canceled = Snapshot(2, JobStatus.Canceled);
        events.Observe(partial with { Status = JobStatus.Running }, canceled with { Status = JobStatus.Pending });

        Assert.Null(tracker.ObserveRemoved(partial));
        var summary = Assert.IsType<DownloadBatchSummary>(tracker.ObserveRemoved(canceled));
        Assert.Equal(1, summary.PartialFailed);
        Assert.Equal(1, summary.Canceled);
        Assert.Equal(0, summary.Completed);
        Assert.True(summary.HasFailures);
    }

    [Fact]
    public void Canceled_only_batch_stays_quiet_and_does_not_pollute_the_next_batch()
    {
        var events = new QueueEvents();
        var tracker = events.Tracker;
        var canceled = Snapshot(1, JobStatus.Canceled);
        events.Observe(canceled with { Status = JobStatus.Pending });
        var quiet = Assert.IsType<DownloadBatchSummary>(tracker.ObserveRemoved(canceled));
        Assert.False(quiet.ShouldNotify);

        var next = Snapshot(2, JobStatus.Completed);
        events.Observe(next with { Status = JobStatus.Pending });
        var summary = Assert.IsType<DownloadBatchSummary>(tracker.ObserveRemoved(next));
        Assert.Equal(1, summary.Completed);
        Assert.Equal(0, summary.Canceled);
        Assert.Equal("twitter-2", summary.CompletedTitle);
    }

    [Fact]
    public void New_tasks_join_the_batch_while_previous_terminal_callbacks_are_outstanding()
    {
        var events = new QueueEvents();
        var tracker = events.Tracker;
        var partial = Snapshot(1, JobStatus.PartialFailed);
        var completed = Snapshot(2, JobStatus.Completed);
        events.Observe(partial with { Status = JobStatus.Running }, completed with { Status = JobStatus.Running });
        Assert.Null(tracker.ObserveRemoved(partial));

        var next = Snapshot(3, JobStatus.Completed);
        events.Observe(next with { Status = JobStatus.Pending });
        Assert.Null(tracker.ObserveRemoved(completed));
        var summary = Assert.IsType<DownloadBatchSummary>(tracker.ObserveRemoved(next));
        Assert.Equal(2, summary.Completed);
        Assert.Equal(1, summary.PartialFailed);
    }
}
