using System.Diagnostics;
using System.Net;
using GalleryGUI.Engine;
using GalleryGUI.Paths;
using GalleryGUI.Sites;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalleryGUI.Tests.E2E;

[Trait("Category", "E2E")]
public class EngineSmokeTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Create();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    private static string? LocatePython()
    {
        foreach (var exe in new[] { "python", "python3", "py" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--version")
                { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
                p.WaitForExit(5000);
                if (p is { HasExited: true, ExitCode: 0 }) return exe;
            }
            catch { }
        }
        return null;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "gallery-dl", "setup.py")))
            dir = dir.Parent!;
        return dir?.FullName ?? "";
    }

    [SkippableFact]
    public async Task Engine_downloads_file_via_generic_extractor()
    {
        var python = LocatePython();
        Skip.If(python is null, "本机无 Python");
        var repo = RepoRoot();
        Skip.If(repo.Length == 0, "未找到仓库内 gallery-dl 源码");

        // 1x1 像素 JPEG（最小合法负载）
        var jpg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0a" +
            "HBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAA" +
            "AAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AVN//2Q==");
        var port = 18000 + Random.Shared.Next(2000);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    ctx.Response.ContentType = "image/jpeg";
                    await ctx.Response.OutputStream.WriteAsync(jpg);
                    ctx.Response.Close();
                }
                catch { break; }
            }
        });

        try
        {
            var engine = new RunnerEngine(_paths,
                new RunnerEngineOptions
                {
                    PythonExe = python!,
                    RunnerScript = Path.Combine(repo, "engine", "runner.py"),
                    GalleryDlPath = Path.Combine(repo, "gallery-dl"),
                },
                NullLogger<RunnerEngine>.Instance);

            // 直接构造 generic 提取器可处理的计划（不经过 TwitterSiteProvider 的 URL 构造）
            var nested = new Dictionary<string, object?>
            {
                ["extractor"] = new Dictionary<string, object?>
                { ["generic"] = new Dictionary<string, object?> { ["enabled"] = true } },
                ["base-directory"] = _paths.Root,
                ["download-archive"] = Path.Combine(_paths.ArchiveDir, "e2e.txt"),
            };
            var plan = new DownloadPlan("twitter", [$"http://127.0.0.1:{port}/file.jpg"], _paths.Root, nested);

            var events = new List<EngineEvent>();
            await engine.DownloadAsync(plan, "unused", new Progress<EngineEvent>(events.Add));

            Assert.Contains(events, e => e.Event == "file-done");
            Assert.Contains(events, e => e.Event == "job-done" && e.Total >= 1);
            var downloaded = Directory.GetFiles(_paths.Root, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}temp{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}logs{Path.DirectorySeparatorChar}"))
                .ToList();
            Assert.NotEmpty(downloaded);
        }
        finally
        {
            listener.Stop();
        }
    }
}
