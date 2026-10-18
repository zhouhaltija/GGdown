using System.Security.Cryptography;
using System.Text;

namespace GGdown.Services;

/// <summary>避免第二个进程把仍在下载的任务误判为上次崩溃；进程退出后由系统释放。</summary>
public sealed class AppInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private AppInstanceLock(Mutex mutex) => _mutex = mutex;

    public static AppInstanceLock? TryAcquire(string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
        var mutex = new Mutex(false, @"Local\GGdown-" + key, out var created);
        if (created) return new AppInstanceLock(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();
}
