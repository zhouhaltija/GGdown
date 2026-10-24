namespace GGdown.Services;

public sealed record StartupTasks(Task EngineReady, Task GlobalReady, Task DataReady);

/// <summary>引擎准备不阻塞浏览；数据库恢复始终在全局库初始化之后执行。</summary>
public static class StartupCoordinator
{
    public static StartupTasks Start(Func<Task> initializeEngine, Func<Task> initializeGlobalData, Func<Task> recoverData)
    {
        var engine = Task.Run(initializeEngine);
        var global = Task.Run(initializeGlobalData);
        return new(engine, global, RecoverAsync());

        async Task RecoverAsync()
        {
            await global.ConfigureAwait(false);
            await recoverData().ConfigureAwait(false);
        }
    }
}
