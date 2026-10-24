using System.Text.RegularExpressions;

namespace GGdown.Engine;

/// <summary>仅清理版本根目录内可确认闲置的旧引擎，保留活动与最近回退版本。</summary>
public static class EngineVersionMaintenance
{
    private static readonly Regex VersionName = new("^[0-9a-f]{64}-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex StagingName = new("^\\.install-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    public static bool IsManagedVersion(string engineDirectory, string directory)
    {
        var versions = Path.GetFullPath(Path.Combine(engineDirectory, "versions"));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return string.Equals(Path.GetDirectoryName(candidate), versions, StringComparison.OrdinalIgnoreCase)
            && VersionName.IsMatch(Path.GetFileName(candidate)) && Directory.Exists(candidate)
            && IsRealDirectory(engineDirectory) && IsRealDirectory(versions) && IsRealDirectory(candidate);
    }

    public static int Cleanup(string engineDirectory, string activeDirectory, DateTime utcNow, Action<Exception>? onError = null)
    {
        try
        {
            if (!IsManagedVersion(engineDirectory, activeDirectory)) return 0;
            var active = Path.TrimEndingDirectorySeparator(Path.GetFullPath(activeDirectory));
            var versions = Path.GetDirectoryName(active)!;
            var directories = Directory.GetDirectories(versions).Where(IsRealDirectory).ToArray();
            var fallback = directories.Where(d => VersionName.IsMatch(Path.GetFileName(d))
                    && !string.Equals(d, active, StringComparison.OrdinalIgnoreCase) && IsComplete(d))
                .OrderByDescending(Directory.GetCreationTimeUtc).ThenBy(d => d, StringComparer.Ordinal).FirstOrDefault();
            var deleted = 0;
            foreach (var directory in directories)
            {
                if (string.Equals(directory, active, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(directory, fallback, StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(directory);
                if (!VersionName.IsMatch(name)
                    && !(StagingName.IsMatch(name) && Directory.GetCreationTimeUtc(directory) < utcNow.AddHours(-24))) continue;
                if (TryDelete(directory, onError)) deleted++;
            }
            return deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onError?.Invoke(ex);
            return 0;
        }
    }

    private static bool IsRealDirectory(string directory) => Directory.Exists(directory)
        && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0;

    private static bool IsComplete(string directory) => File.Exists(Path.Combine(directory, "runner.py"))
        && File.Exists(Path.Combine(directory, "engine.json"))
        && File.Exists(Path.Combine(directory, "python", "python.exe"))
        && Directory.Exists(Path.Combine(directory, "site-packages"));

    private static bool TryDelete(string directory, Action<Exception>? onError)
    {
        var handles = new List<FileStream>();
        try
        {
            using var lease = EngineVersionLease.Open(directory, exclusive: true);
            if (!LockTree(directory)) return false;
            // 所有文件先确认可删除且拒绝新读写，再开始删除，避免碰到旧进程后留下半个版本。
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onError?.Invoke(ex);
            return false;
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
        }

        bool LockTree(string path)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0) return false;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!LockTree(entry)) return false;
                }
                else
                    handles.Add(new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Delete));
            }
            return true;
        }
    }
}
