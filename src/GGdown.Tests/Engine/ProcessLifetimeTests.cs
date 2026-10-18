using System.Diagnostics;
using System.Text;
using GGdown.Engine;

namespace GGdown.Tests.Engine;

public class ProcessLifetimeTests
{
    [SkippableFact]
    public async Task Abrupt_owner_exit_terminates_its_runner()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows 进程作业保护");
        var assembly = typeof(RunnerProcessLifetime).Assembly.Location.Replace("'", "''");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Add-Type -Path '{{assembly}}'
            $child = [System.Diagnostics.Process]::Start([System.Diagnostics.ProcessStartInfo]@{
                FileName = 'ping.exe'
                Arguments = '-n 60 127.0.0.1'
                UseShellExecute = $false
                CreateNoWindow = $true
                RedirectStandardOutput = $true
            })
            $lifetime = [GGdown.Engine.RunnerProcessLifetime]::Attach($child)
            [Console]::WriteLine($child.Id)
            [Console]::Out.Flush()
            Start-Sleep -Seconds 60
            """;
        using var owner = Process.Start(new ProcessStartInfo("pwsh")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        Process? child = null;
        try
        {
            var line = await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(int.TryParse(line, out var pid), "测试宿主未能启动引擎：" + line);
            child = Process.GetProcessById(pid);
            _ = child.Handle;
            owner.Kill(); // 只终止宿主，子进程由 Windows 作业保护终止。
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync();
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                child.Dispose();
            }
        }
    }

    [SkippableFact]
    public async Task Closing_owner_handle_terminates_the_runner()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows 进程作业保护");
        using var process = Process.Start(new ProcessStartInfo("ping.exe")
        {
            ArgumentList = { "-n", "60", "127.0.0.1" },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        try
        {
            var lifetime = RunnerProcessLifetime.Attach(process);
            lifetime.Dispose(); // 崩溃时 Windows 同样会关闭应用持有的最后一个作业句柄。
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
