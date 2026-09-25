namespace GGdown.Threading;

/// <summary>把委托投递到 UI 线程；桌面宿主用 DispatcherQueue 实现，测试用同步实现。</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
