using System.Diagnostics;
using System.IO;
using System.Text;

namespace TubaWinUi3.Services.AppManagement;

/// <summary>
/// 环境作用域隔离（v0.2 最小版）：把沙箱里安装的运行环境（Python / Node）
/// 以**进程级 PATH 注入**的方式提供给子进程，不写任何全局环境变量/注册表。
/// 用途：AI 助手与 MCP 在未来拉起的任务里使用沙箱环境；并可做"作用域内运行"验证。
/// </summary>
internal static class EnvironmentScopeManager
{
    /// <summary>沙箱内已知可执行目录（不存在的自动跳过）。</summary>
    public static List<string> GetSandboxBinPaths()
    {
        var list = new List<string>();
        try
        {
            var root = AppCenterService.EnvironmentsRoot;
            if (!Directory.Exists(root)) return list;

            // Python embeddable：python.exe 在包根
            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    if (Directory.EnumerateFiles(dir, "python.exe", SearchOption.TopDirectoryOnly).Any()
                        || Directory.EnumerateFiles(dir, "node.exe", SearchOption.TopDirectoryOnly).Any())
                    {
                        list.Add(dir);
                    }
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>拼一个"沙箱优先"的 PATH（沙箱目录在前，原 PATH 在后）。</summary>
    public static string BuildScopedPath(string? basePath = null)
    {
        var original = basePath ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        var sandbox = GetSandboxBinPaths();
        if (sandbox.Count == 0) return original;
        return string.Join(";", sandbox) + ";" + original;
    }

    /// <summary>
    /// 从注册表（Machine + User Environment）读**最新** PATH 并注入当前进程环境：
    /// winget 等安装会写注册表 PATH，但本进程的环境是启动时快照——刷新后，
    /// 之后拉起的子进程（run_command / 探测 / 验证）就能立即用上新装的软件。
    /// 用户级在前（winget --scope user 安装的优先），原进程 PATH 的自定义项保留在后。
    /// </summary>
    public static void RefreshProcessPath()
    {
        try
        {
            var machine = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment")
                ?.GetValue("Path") as string ?? "";
            var user = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey("Environment")
                ?.GetValue("Path") as string ?? "";

            var parts = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddAll(string value)
            {
                foreach (var p in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (seen.Add(p)) parts.Add(p);
            }

            AddAll(user);
            AddAll(machine);
            AddAll(Environment.GetEnvironmentVariable("PATH") ?? ""); // 保留宿主注入项
            AddAll(string.Join(";", GetSandboxBinPaths()));           // 沙箱环境也并入

            Environment.SetEnvironmentVariable("PATH", string.Join(";", parts), EnvironmentVariableTarget.Process);
        }
        catch { }
    }

    /// <summary>在"沙箱作用域"里运行一个命令（验证隔离环境可用的标准手段）。</summary>
    public static string RunInScope(string exePath, string arguments, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // 进程级 PATH 注入：不改主机环境
            psi.Environment["PATH"] = BuildScopedPath();

            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return MiscTexts.T("(超时)");
            }
            return string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n[stderr] {stderr}";
        }
        catch (Exception ex)
        {
            return MiscTexts.TSub($"(运行失败：{ex.Message})");
        }
    }
}
