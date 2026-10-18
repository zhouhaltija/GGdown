using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GGdown.Engine;

public sealed record EngineInstallation(string Directory, bool Updated, Exception? UpdateError = null);

public sealed class EngineInstaller(Func<string, CancellationToken, Task>? validate = null)
{
    private sealed record ActiveVersion(string Directory, string Version);

    public async Task<EngineInstallation> InstallAsync(string bundledDirectory, string engineDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var active = ReadActive(engineDirectory);
        var previous = active is not null ? Path.Combine(engineDirectory, "versions", active.Directory)
            : IsComplete(engineDirectory) ? engineDirectory : null;
        string? previousVersion = null;
        if (previous is not null)
        {
            try
            {
                previousVersion = await ContentVersionAsync(previous, ct);
                if (active is not null && previousVersion != active.Version) previous = null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                // 已损坏的版本不是回退候选，仍须尝试从完整发行目录重新安装。
                previous = null;
            }
        }
        string? staging = null;
        try
        {
            if (!Directory.Exists(bundledDirectory))
            {
                if (previous is not null) return new(previous, false);
                throw new DirectoryNotFoundException("没有可用的内置引擎或已安装引擎");
            }
            RequireComplete(bundledDirectory);
            var version = await ContentVersionAsync(bundledDirectory, ct);
            // 实际内容也要一致，避免损坏或缺失依赖的安装被当作同版本跳过。
            if (previous is not null && previousVersion == version)
                return new(previous, false);

            var versions = Path.Combine(engineDirectory, "versions");
            Directory.CreateDirectory(versions);
            staging = Path.Combine(versions, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            foreach (var file in Directory.EnumerateFiles(bundledDirectory, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(staging, Path.GetRelativePath(bundledDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = File.OpenRead(file);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, ct);
            }
            RequireComplete(staging);
            if (await ContentVersionAsync(staging, ct) != version)
                throw new InvalidDataException("复制过程中引擎内容发生变化");
            await (validate ?? ValidateHelloAsync)(staging, ct);
            ct.ThrowIfCancellationRequested();

            // 不覆盖任何旧目录；运行中的进程始终保留自己的绝对路径快照。
            var name = version + "-" + Guid.NewGuid().ToString("N");
            var destination = Path.Combine(versions, name);
            Directory.Move(staging, destination);
            staging = null;
            var pointer = Path.Combine(engineDirectory, "active.json");
            var pendingPointer = pointer + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(pendingPointer, JsonSerializer.Serialize(new ActiveVersion(name, version)), ct);
                ct.ThrowIfCancellationRequested();
                File.Move(pendingPointer, pointer, overwrite: true);
            }
            finally
            {
                File.Delete(pendingPointer);
            }
            return new(destination, true);
        }
        catch (Exception ex) when (previous is not null && !ct.IsCancellationRequested)
        {
            return new(previous, false, ex);
        }
        finally
        {
            if (staging is not null)
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { /* 未激活的临时目录留待后续维护，不触碰运行中的版本。 */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static ActiveVersion? ReadActive(string engineDirectory)
    {
        try
        {
            var active = JsonSerializer.Deserialize<ActiveVersion>(File.ReadAllText(Path.Combine(engineDirectory, "active.json")));
            if (active is null || string.IsNullOrWhiteSpace(active.Directory)
                || active.Directory != Path.GetFileName(active.Directory)) return null;
            return IsComplete(Path.Combine(engineDirectory, "versions", active.Directory)) ? active : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsComplete(string directory) =>
        Directory.Exists(directory)
        && File.Exists(Path.Combine(directory, "python", "python.exe"))
        && File.Exists(Path.Combine(directory, "runner.py"))
        && File.Exists(Path.Combine(directory, "engine.json"))
        && Directory.Exists(Path.Combine(directory, "site-packages"));

    private static void RequireComplete(string directory)
    {
        if (!IsComplete(directory)) throw new InvalidDataException("引擎目录缺少 Python、runner、清单或依赖目录");
    }

    private static async Task<string> ContentVersionAsync(string directory, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            // Python 在 hello/运行时生成的缓存不是发行内容。
            if (relative.Split('/').Contains("__pycache__") || relative.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)) continue;
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            byte[] digest;
            if (relative == "engine.json")
            {
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(file, ct)) as JsonObject
                    ?? throw new InvalidDataException("引擎清单必须是 JSON 对象");
                manifest.Remove("built_at");
                var normalized = new JsonObject(manifest.OrderBy(property => property.Key, StringComparer.Ordinal)
                    .Select(property => KeyValuePair.Create(property.Key, property.Value?.DeepClone())));
                digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToJsonString()));
            }
            else
            {
                await using var stream = File.OpenRead(file);
                digest = await SHA256.HashDataAsync(stream, ct);
            }
            hash.AppendData(digest);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task ValidateHelloAsync(string directory, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(Path.Combine(directory, "python", "python.exe"))
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(Path.Combine(directory, "runner.py"));
        start.ArgumentList.Add("hello");
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidDataException("无法启动引擎验证");
        using var lifetime = RunnerProcessLifetime.Attach(process);
        var stdout = ReadTailAsync(process.StandardOutput, 65536, timeout.Token);
        var stderr = ReadTailAsync(process.StandardError, 4000, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var diagnostic = await stderr;
            if (process.ExitCode != 0 || !output.Split('\n').Any(line => JsonlParser.Parse(line) is { Event: "hello", Protocol: 1 }))
                throw new InvalidDataException($"引擎 hello 验证失败（退出码 {process.ExitCode}）：{diagnostic}");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) { }
            throw;
        }
    }

    private static async Task<string> ReadTailAsync(StreamReader reader, int capacity, CancellationToken ct)
    {
        var tail = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > capacity) tail.Remove(0, tail.Length - capacity);
        }
        return tail.ToString();
    }
}
