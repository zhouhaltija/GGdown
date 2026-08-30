using GalleryGUI.Threading;
using Microsoft.UI.Dispatching;

namespace GalleryGUI.App.Infrastructure;

/// <summary>桌面宿主的 <see cref="IUiDispatcher"/>：把委托投递到 UI 线程的 DispatcherQueue。</summary>
public sealed class UiDispatcher(DispatcherQueue dispatcherQueue) : IUiDispatcher
{
    public void Post(Action action) => dispatcherQueue.TryEnqueue(() => action());
}
