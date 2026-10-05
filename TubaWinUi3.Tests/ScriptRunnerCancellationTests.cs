using System.Diagnostics;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【A03 回归】ScriptRunnerService 取消/超时后必须真实终止其拥有的进程树。
/// 审计实测原实现：取消后 ChildAliveAfterCancellation=True（只结束 await，进程继续跑）。
/// 隔离原则（审计纠偏）：只使用并断言测试自己创建的进程 PID（由子进程自报，
/// 含父+子两级）；不枚举全机同名进程、不对全局进程执行 Kill。
/// finally 始终取消内部 CTS 并有界等待本次 task，不依赖"是否已收到 PID"。
/// </summary>
public class ScriptRunnerCancellationTests
{
    /// <summary>子进程脚本：自报父 PID + 派生一个子进程（ping 保活）并自报其 PID，然后保活 30 秒。</summary>
    // 注意：避免嵌套双引号（Windows 命令行解析会截断）；用字符串拼接输出
    private const string ParentChildScript =
        "$p = Start-Process ping -ArgumentList '-n','30','127.0.0.1' -PassThru -WindowStyle Hidden; " +
        "Write-Output ('ZXPID PARENT=' + $PID + ' CHILD=' + $p.Id); Start-Sleep -Seconds 30";

    [Fact]
    public async Task Cancel_TerminatesParentAndChildTree()
    {
        int? parentPid = null, childPid = null;
        var pidsSeen = new TaskCompletionSource<(int Parent, int Child)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        Task<ScriptRunResult>? task = null;

        try
        {
            task = ScriptRunnerService.RunAsync(new ScriptRunRequest
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{ParentChildScript}\"",
            }, onOutput: (line, _) => TryParsePids(line, pidsSeen), ct: cts.Token);

            (parentPid, childPid) = await pidsSeen.Task.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.True(IsAlive(parentPid.Value), "前提：父进程应已启动");
            Assert.True(IsAlive(childPid.Value), "前提：子进程应已启动");
            await Task.Delay(500);

            // 取消 → 等待 RunAsync 返回
            cts.Cancel();
            var canceled = false;
            try { await task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) { canceled = true; }
            Assert.True(canceled, "取消后 RunAsync 应抛 OperationCanceledException");

            // 断言：父与子（整个进程树）都已退出
            Assert.True(await WaitExitAsync(parentPid.Value, TimeSpan.FromSeconds(5)),
                $"【A03】取消后父进程 PID {parentPid} 应已退出");
            Assert.True(await WaitExitAsync(childPid.Value, TimeSpan.FromSeconds(5)),
                $"【A03】取消后子进程 PID {childPid} 应已退出（进程树必须被终止）");
        }
        finally
        {
            cts.Cancel(); // 【评审要求】finally 始终取消，不依赖是否收到 PID
            if (task is not null)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
            KillIfAlive(childPid);
            KillIfAlive(parentPid);
        }
    }

    [Fact]
    public async Task Timeout_TerminatesParentAndChildTree()
    {
        int? parentPid = null, childPid = null;
        var pidsSeen = new TaskCompletionSource<(int Parent, int Child)>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 用 CancelAfter 模拟"超时"（CommandAgentTool 的超时即 linked CTS 触发，路径等价）
        using var cts = new CancellationTokenSource();
        Task<ScriptRunResult>? task = null;

        try
        {
            task = ScriptRunnerService.RunAsync(new ScriptRunRequest
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{ParentChildScript}\"",
            }, onOutput: (line, _) => TryParsePids(line, pidsSeen), ct: cts.Token);

            (parentPid, childPid) = await pidsSeen.Task.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.True(IsAlive(parentPid.Value), "前提：父进程应已启动");
            Assert.True(IsAlive(childPid.Value), "前提：子进程应已启动");

            cts.CancelAfter(TimeSpan.FromSeconds(1)); // 「超时」触发（与用户取消同路径）
            var canceled = false;
            try { await task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) { canceled = true; }
            Assert.True(canceled, "超时后 RunAsync 应抛 OperationCanceledException");

            Assert.True(await WaitExitAsync(parentPid.Value, TimeSpan.FromSeconds(5)),
                $"【A03】超时后父进程 PID {parentPid} 应已退出");
            Assert.True(await WaitExitAsync(childPid.Value, TimeSpan.FromSeconds(5)),
                $"【A03】超时后子进程 PID {childPid} 应已退出");
        }
        finally
        {
            cts.Cancel();
            if (task is not null)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
            KillIfAlive(childPid);
            KillIfAlive(parentPid);
        }
    }

    [Fact]
    public async Task RunAsync_CancelDuringStdinWrite_TerminatesOwnProcess()
    {
        int? childPid = null;
        var pidFile = Path.Combine(Path.GetTempPath(), $"zxai_a03_{Guid.NewGuid():N}.pid");
        using var cts = new CancellationTokenSource();
        Task<ScriptRunResult>? task = null;

        try
        {
            // 子进程：先把 PID 写文件（本场景 stdout 泵的启动受 stdin 写入路径影响，不能依赖 stdout 自报），
            // 然后保活 30 秒、完全不读 stdin。4MB 输入远超管道缓冲，写入必然阻塞。
            var big = new string('x', 4 * 1024 * 1024);
            task = ScriptRunnerService.RunAsync(new ScriptRunRequest
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"$PID | Out-File -Encoding ascii '{pidFile}'; Start-Sleep -Seconds 30\"",
                InputText = big,
            }, ct: cts.Token);

            var t0 = DateTime.Now;
            while (!File.Exists(pidFile) && DateTime.Now - t0 < TimeSpan.FromSeconds(20))
                await Task.Delay(200);
            Assert.True(File.Exists(pidFile), "前提：子进程应写出 PID 文件");
            childPid = int.Parse((await ReadPidFileAsync(pidFile)).Trim());
            Assert.True(IsAlive(childPid.Value), "前提：子进程应已启动");

            await Task.Delay(300); // 让 stdin 写入进入阻塞（4MB >> 64KB 管道缓冲）
            cts.Cancel();
            var canceled = false;
            try { await task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) { canceled = true; }
            Assert.True(canceled, "stdin 阻塞期取消应抛 OperationCanceledException");

            var gone = await WaitExitAsync(childPid.Value, TimeSpan.FromSeconds(5));
            Assert.True(gone, $"【A03】stdin 阻塞期取消后 PID {childPid} 应已退出（不得泄漏）");
        }
        finally
        {
            cts.Cancel(); // 【评审要求】finally 始终取消
            if (task is not null)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
            KillIfAlive(childPid);
            try { File.Delete(pidFile); } catch { }
        }
    }

    // ---------- helpers（只操作测试自己的 PID） ----------

    /// <summary>
    /// 读取子进程写出的 PID 文件。文件"存在"≠句柄已关（Out-File 创建后仍在写）——
    /// File.Exists 一返回 true 就立刻读取会撞上"文件被占用"（TOCTOU 竞态，全量套件高负载下必现）。
    /// 对"被占用"做有界重试（~3s）；真正卡死/内容异常仍照常失败，不掩盖问题。
    /// </summary>
    private static async Task<string> ReadPidFileAsync(string path)
    {
        var t0 = DateTime.Now;
        while (true)
        {
            try { return await File.ReadAllTextAsync(path); }
            catch (IOException) when (DateTime.Now - t0 < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(50);
            }
        }
    }

    private static void TryParsePids(string line, TaskCompletionSource<(int, int)> tcs)
    {
        // 期望行：ZXPID PARENT=<pid> CHILD=<pid>
        var s = line;
        var pi = s.IndexOf("PARENT=", StringComparison.Ordinal);
        var ci = s.IndexOf("CHILD=", StringComparison.Ordinal);
        if (pi < 0 || ci < 0) return;
        var pStr = s[(pi + 7)..];
        var cStr = s[(ci + 6)..];
        var pEnd = pStr.IndexOf(' ');
        var cEnd = cStr.IndexOf(' ');
        var pVal = pEnd > 0 ? pStr[..pEnd] : pStr;
        var cVal = cEnd > 0 ? cStr[..cEnd] : cStr;
        if (int.TryParse(pVal.Trim(), out var p) && int.TryParse(cVal.Trim(), out var c))
            tcs.TrySetResult((p, c));
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private static async Task<bool> WaitExitAsync(int pid, TimeSpan timeout)
    {
        var t0 = DateTime.Now;
        while (DateTime.Now - t0 < timeout)
        {
            if (!IsAlive(pid)) return true;
            await Task.Delay(200);
        }
        return !IsAlive(pid);
    }

    private static void KillIfAlive(int? pid)
    {
        if (pid is not { } p) return;
        try
        {
            using var proc = Process.GetProcessById(p);
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
