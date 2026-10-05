using System.Diagnostics;
using System.Text;

namespace TubaWinUi3.Services;

/// <summary>
/// 【A03 终态诚实性】取消/超时后进程树未能在期限内确认退出（Kill 已请求）。
/// 调用方据此上报"已请求终止但未确认退出"，不得宣称"已强制终止"。
/// </summary>
public sealed class ScriptTerminationUnconfirmedException : Exception
{
    public int ProcessId { get; }
    public ScriptTerminationUnconfirmedException(int processId)
        : base(MiscTexts.TSub($"已请求终止进程树，但 PID {processId} 未在期限内确认退出"))
        => ProcessId = processId;
}

public sealed class ScriptRunResult
{
    public int ExitCode { get; init; }
    public bool Success => ExitCode == 0;
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public TimeSpan Duration { get; init; }
}

public sealed class ScriptRunRequest
{
    public string FileName { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string? WorkingDirectory { get; init; }
    public bool RunAsAdmin { get; init; }
    public Dictionary<string, string>? EnvironmentVariables { get; init; }
    public Encoding? OutputEncoding { get; init; }
    public string? InputText { get; init; }
}

public enum ScriptOutputKind
{
    Normal,
    Error,
    ProgressUpdate
}

public static class ScriptRunnerService
{
    public static Task<ScriptRunResult> RunAsync(
        ScriptRunRequest request,
        Action<string, ScriptOutputKind>? onOutput = null,
        CancellationToken ct = default)
    {
        return Task.Run(() => RunCoreAsync(request, onOutput, ct), ct);
    }

    private static async Task<ScriptRunResult> RunCoreAsync(
        ScriptRunRequest request,
        Action<string, ScriptOutputKind>? onOutput,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = request.OutputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = request.OutputEncoding ?? Encoding.UTF8
        };

        if (request.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in request.EnvironmentVariables)
                psi.Environment[key] = value;
        }

        if (request.RunAsAdmin)
        {
            psi.Verb = "runas";
            psi.UseShellExecute = true;
            psi.CreateNoWindow = false;
            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
            psi.RedirectStandardInput = false;
        }

        using var process = Process.Start(psi);
        if (process is null)
            return new ScriptRunResult { ExitCode = -1, Error = MiscTexts.T("无法启动进程"), Duration = sw.Elapsed };

        if (request.RunAsAdmin)
        {
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // 【A03 审计修复】取消/超时也必须终止进程树（与普通分支同样的诚实终态处理：
                // 仅确认退出才重抛 OCE；等待超时抛 Unconfirmed，不假报"已终止"）
                var runasExited = await KillProcessTreeAsync(process).ConfigureAwait(false);
                if (!runasExited)
                    throw new ScriptTerminationUnconfirmedException(process.Id);
                throw;
            }
            sw.Stop();
            return new ScriptRunResult
            {
                ExitCode = process.ExitCode,
                Duration = sw.Elapsed,
                Output = MiscTexts.T("(以管理员身份运行，无法捕获输出)")
            };
        }

        try
        {
            // 【A03 审计修复】stdin 写入后台化：4MB 级写到满管道缓冲时 WriteAsync 会长时间阻塞，
            // 若主流程 await 它，取消将永远走不到进程回收路径（审计纠偏确认的泄漏点）。
            // 后台任务不被等待；进程终止时管道断开，任务自行结束（写任务与回收不互相等待）。
            if (!string.IsNullOrEmpty(request.InputText))
                _ = WriteStdinAsync(process, request.InputText, ct);

            var outputTask = ReadStreamAsync(process.StandardOutput, outputBuilder, onOutput, ScriptOutputKind.Normal, ct);
            var errorTask = ReadStreamAsync(process.StandardError, errorBuilder, onOutput, ScriptOutputKind.Error, ct);

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(ct);
            sw.Stop();

            return new ScriptRunResult
            {
                ExitCode = process.ExitCode,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString(),
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            // 【A03 审计修复】取消/超时路径必须真正终止子进程树并等待退出后才上报。
            // 此前只结束输出读取/WaitForExit 的 await 并 Dispose Process——Dispose 不会停止进程，
            // 界面却报告"已强制终止"（审计实测 ChildAliveAfterCancellation=True）。
            // 终态诚实性：仅当确认退出才重抛 OCE（调用方报"已终止"）；等待超时抛
            // ScriptTerminationUnconfirmedException（调用方报"已请求终止但未确认"）。
            var exited = await KillProcessTreeAsync(process).ConfigureAwait(false);
            if (!exited)
                throw new ScriptTerminationUnconfirmedException(process.Id);
            throw;
        }
    }

    /// <summary>
    /// 【A03】stdin 写入后台任务：不阻塞主流程；取消/管道断开时静默结束
    /// （进程回收由主路径的取消处理负责，两者不互相等待）。
    /// </summary>
    private static async Task WriteStdinAsync(Process process, string text, CancellationToken ct)
    {
        try
        {
            await process.StandardInput.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch { /* 取消 / 管道断开（进程已终止）：吞掉，由主路径决定终态 */ }
    }

    /// <summary>
    /// 【A03 审计修复】终止进程树并等待退出。
    /// 首选 Process.Kill(entireProcessTree:true)；失败（权限/竞态）退回 taskkill /T /F。
    /// 返回 true = 已确认进程退出；false = Kill 已发出但 10 秒内未确认（调用方须如实上报）。
    /// </summary>
    private static async Task<bool> KillProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            try
            {
                using var tk = Process.Start(new ProcessStartInfo("taskkill",
                    $"/PID {process.Id} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (tk is not null)
                    await tk.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch { /* 进程可能已退出/无权限——下方等待会按实际状态收敛 */ }
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // 【A03】Kill 已发出但接近超时未确认——返回 false，调用方不得宣称"已强制终止"
            return false;
        }
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        StringBuilder builder,
        Action<string, ScriptOutputKind>? callback,
        ScriptOutputKind defaultKind,
        CancellationToken ct)
    {
        var partialLine = new StringBuilder();
        var pendingCR = false;
        const char CR = (char)13;
        const char LF = (char)10;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var buf = new char[1];
                var read = await reader.ReadAsync(buf.AsMemory(), ct);
                if (read == 0) break;

                var ch = buf[0];

                if (pendingCR)
                {
                    pendingCR = false;
                    if (ch == LF)
                    {
                        // ZXAI 修复：CRLF 行尾应算“正常行结束”而非进度刷新——
                        // 此前一遇到 CR 就清空 partialLine，导致 Windows 程序（cmd / PowerShell）
                        // 以 CRLF 输出的整行内容被误当进度流丢弃（run_command/run_powershell 结果为空）。
                        var crlfLine = partialLine.ToString();
                        partialLine.Clear();
                        if (!string.IsNullOrEmpty(crlfLine))
                        {
                            builder.AppendLine(crlfLine);
                            callback?.Invoke(crlfLine, defaultKind);
                        }
                        else
                        {
                            builder.AppendLine();
                            callback?.Invoke("", defaultKind);
                        }
                        continue;
                    }

                    // 单独的 CR：真正的进度刷新（如进度条）→ 只上报、不进内容
                    var progress = partialLine.ToString();
                    partialLine.Clear();
                    if (!string.IsNullOrEmpty(progress))
                        callback?.Invoke(progress, ScriptOutputKind.ProgressUpdate);
                    // 当前字符继续按普通字符处理（落到下方分支）
                }

                if (ch == CR)
                {
                    pendingCR = true;
                }
                else if (ch == LF)
                {
                    var lineSoFar = partialLine.ToString();
                    partialLine.Clear();
                    if (!string.IsNullOrEmpty(lineSoFar))
                    {
                        builder.AppendLine(lineSoFar);
                        callback?.Invoke(lineSoFar, defaultKind);
                    }
                    else
                    {
                        builder.AppendLine();
                        callback?.Invoke("", defaultKind);
                    }
                }
                else
                {
                    partialLine.Append(ch);
                }
            }
            catch
            {
                break;
            }
        }

        if (partialLine.Length > 0)
        {
            var remaining = partialLine.ToString();
            builder.AppendLine(remaining);
            callback?.Invoke(remaining, defaultKind);
        }
    }
}
