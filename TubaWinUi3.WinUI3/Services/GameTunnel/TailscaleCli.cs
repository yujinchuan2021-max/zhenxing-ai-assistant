using System.Diagnostics;
using System.Text;

namespace TubaWinUi3.Services;

/// <summary>一次 tailscale.exe 调用的结果。</summary>
public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    public string Combined => string.IsNullOrWhiteSpace(StdErr)
        ? StdOut
        : string.IsNullOrWhiteSpace(StdOut) ? StdErr : StdOut.TrimEnd() + "\n" + StdErr.TrimEnd();

    /// <summary>取第一行有内容的输出，用于错误提示（过长时截断）。</summary>
    public string FirstLine(string? fallback = null)
    {
        var text = !string.IsNullOrWhiteSpace(StdErr) ? StdErr : StdOut;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
        }
        return fallback is null ? GameTunnelTexts.T("（无输出）") : fallback;
    }
}

/// <summary>
/// tailscale.exe 的进程调用封装。<b>只负责跑命令</b>，语义解析在 <see cref="TailscaleService"/>。
/// 所有命令都是短命进程（不存在常驻子进程），因此没有退出清理负担。
/// </summary>
internal static class TailscaleCli
{
    private static string? _exePath;
    private static bool _resolved;
    private static string? _trayPath;
    private static bool _trayResolved;

    /// <summary>tailscale.exe 的完整路径；未安装时为 null。</summary>
    public static string? ExePath
    {
        get
        {
            if (!_resolved)
            {
                _exePath = Resolve("tailscale.exe");
                _resolved = true;
            }
            return _exePath;
        }
    }

    /// <summary>tailscale-ipn.exe（托盘 / 图形客户端）的完整路径；未安装时为 null。</summary>
    public static string? TrayPath
    {
        get
        {
            if (!_trayResolved)
            {
                _trayPath = Resolve("tailscale-ipn.exe");
                _trayResolved = true;
            }
            return _trayPath;
        }
    }

    public static bool IsInstalled => ExePath is not null;

    /// <summary>安装完成后调用，让下一次查询重新定位。</summary>
    public static void Invalidate()
    {
        _resolved = false;
        _exePath = null;
        _trayResolved = false;
        _trayPath = null;
    }

    private static string? Resolve(string fileName)
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", fileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", fileName),
        ];

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
            }
        }

        // 兜底走 PATH（某些用户把 Tailscale 装到了自定义目录并加进了 PATH）
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public static Task<CliResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
        => RunAsync(ExePath, args, timeout, ct);

    /// <summary>
    /// 运行 tailscale 命令。取消或超时会连同子进程树一起结束，不会遗留孤儿进程。
    /// </summary>
    public static async Task<CliResult> RunAsync(string? exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return new CliResult(-1, "", GameTunnelTexts.T("未安装 Tailscale"));
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start()) return new CliResult(-1, "", GameTunnelTexts.T("无法启动 tailscale.exe"));
        }
        catch (Exception ex)
        {
            return new CliResult(-1, "", ex.Message);
        }

        // 双流并发读取：只读一路会在另一路填满管道缓冲区时死锁
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            var partialOut = await SafeReadAsync(stdoutTask).ConfigureAwait(false);
            var partialErr = await SafeReadAsync(stderrTask).ConfigureAwait(false);
            var reason = ct.IsCancellationRequested ? GameTunnelTexts.T("命令已取消") : GameTunnelTexts.TSub($"{timeout.TotalSeconds:F0} 秒内没有响应");
            return new CliResult(-1, partialOut, string.IsNullOrWhiteSpace(partialErr) ? reason : partialErr);
        }

        var stdout = await SafeReadAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await SafeReadAsync(stderrTask).ConfigureAwait(false);
        return new CliResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return ""; }
    }

    /// <summary>把单个参数按 Windows 命令行规则转义成字符串（仅用于拼接用户可见的提示）。</summary>
    public static string Describe(IReadOnlyList<string> args)
        => "tailscale " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}
