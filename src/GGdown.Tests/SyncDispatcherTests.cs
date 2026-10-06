using System.Collections.Concurrent;

namespace GGdown.Tests;

public class SyncDispatcherTests
{
    [Fact]
    public void Concurrent_posts_are_serialized_like_the_ui_thread()
    {
        var dispatcher = new SyncDispatcher();
        using var start = new Barrier(4);
        var active = 0;
        var observed = new ConcurrentBag<int>();
        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            dispatcher.Post(() =>
            {
                observed.Add(Interlocked.Increment(ref active));
                Thread.Sleep(20);
                Interlocked.Decrement(ref active);
            });
        }) { IsBackground = true }).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(4, observed.Count);
        Assert.All(observed, count => Assert.Equal(1, count));
    }
}
