namespace GGdown.Engine;

/// <summary>锁文件位于版本内容之外，多操作共享读锁，维护任务持有独占锁。</summary>
public static class EngineVersionLease
{
    public static IDisposable Acquire(string versionDirectory) => Open(versionDirectory, exclusive: false);

    internal static FileStream Open(string versionDirectory, bool exclusive)
    {
        var directory = Path.GetFullPath(versionDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var locks = Path.Combine(Path.GetDirectoryName(directory)!, ".leases");
        Directory.CreateDirectory(locks);
        var file = Path.Combine(locks, Path.GetFileName(directory) + ".lock");
        if ((File.GetAttributes(locks) & FileAttributes.ReparsePoint) != 0
            || File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("引擎租约路径不能是链接");
        return new FileStream(file, FileMode.OpenOrCreate,
            exclusive ? FileAccess.ReadWrite : FileAccess.Read,
            exclusive ? FileShare.None : FileShare.Read);
    }
}
