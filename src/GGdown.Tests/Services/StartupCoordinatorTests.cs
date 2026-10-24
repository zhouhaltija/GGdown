using GGdown.Services;

namespace GGdown.Tests.Services;

public sealed class StartupCoordinatorTests
{
    [Fact]
    public async Task Data_readiness_does_not_wait_for_engine()
    {
        var engine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = StartupCoordinator.Start(() => engine.Task, () => Task.CompletedTask, () => Task.CompletedTask);
        await startup.DataReady.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(startup.GlobalReady.IsCompletedSuccessfully);
        Assert.False(startup.EngineReady.IsCompleted);
        engine.SetResult();
        await startup.EngineReady;
    }

    [Fact]
    public async Task Recovery_waits_for_global_database()
    {
        var global = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = false;
        var startup = StartupCoordinator.Start(() => Task.CompletedTask, () => global.Task,
            () => { recovered = true; return Task.CompletedTask; });
        await startup.EngineReady;
        Assert.False(recovered);
        Assert.False(startup.DataReady.IsCompleted);
        global.SetResult();
        await startup.DataReady.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(recovered);
    }

    [Fact]
    public async Task Engine_failure_does_not_fault_data_readiness()
    {
        var startup = StartupCoordinator.Start(() => throw new InvalidOperationException("引擎损坏"),
            () => Task.CompletedTask, () => Task.CompletedTask);
        await startup.DataReady.WaitAsync(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => startup.EngineReady);
        Assert.Equal("引擎损坏", error.Message);
    }

    [Fact]
    public async Task Global_failure_faults_data_readiness()
    {
        var recovered = false;
        var startup = StartupCoordinator.Start(() => Task.CompletedTask,
            () => throw new InvalidOperationException("数据库损坏"),
            () => { recovered = true; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => startup.GlobalReady);
        await Assert.ThrowsAsync<InvalidOperationException>(() => startup.DataReady);
        Assert.False(recovered);
        await startup.EngineReady;
    }

    [Fact]
    public async Task Recovery_failure_does_not_fault_global_or_engine_readiness()
    {
        var startup = StartupCoordinator.Start(() => Task.CompletedTask, () => Task.CompletedTask,
            () => throw new InvalidOperationException("恢复失败"));
        await startup.GlobalReady;
        await startup.EngineReady;
        await Assert.ThrowsAsync<InvalidOperationException>(() => startup.DataReady);
    }
}
