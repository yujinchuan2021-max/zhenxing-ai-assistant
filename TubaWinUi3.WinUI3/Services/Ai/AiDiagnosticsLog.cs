using System.Diagnostics;
using FieldCure.AssistStudio.Core.Helpers;

namespace TubaWinUi3.Services.Ai;

/// <summary>
/// FieldCure AssistStudio 组件库 → 工具箱日志的桥。
/// 组件作者留了 OnInfo / OnWarning / OnException 三个静态回调，默认无人接听——于是组件内部的
/// 失败（WebView2 环境拿不到、渲染被 _isInitialized 守卫拦下、脚本异常等）完全不发声，用户只
/// 看到"字没了、什么都没发生"。App 启动时 <see cref="Initialize"/> 一次，全部落到
/// &lt;DataDir&gt;\AiAssistant\diag.log（超过 1 MB 截断保留尾部），并同步到 Debug 输出。
/// </summary>
public static class AiDiagnosticsLog
{
    private const long MaxBytes = 1024 * 1024;

    private static readonly object _lock = new();
    private static bool _enabled;

    // 路径实时解析（不缓存）：配置位置可能在运行中切换
    private static string LogPath => Path.Combine(ConfigManager.GetDataDir(), "AiAssistant", "diag.log");

    /// <summary>
    /// 接线组件诊断回调（幂等）。未接线时 <see cref="Write"/> 不落盘——测试进程不应污染真实数据目录。
    /// </summary>
    public static void Initialize()
    {
        _enabled = true;
        DiagnosticLogger.OnInfo = message => Write("INFO ", message);
        DiagnosticLogger.OnWarning = message => Write("WARN ", message);
        DiagnosticLogger.OnException = ex => Write("ERROR", ex?.ToString());
    }

    /// <summary>写一行诊断日志；任何失败都吞掉（日志本身不能影响功能）。</summary>
    public static void Write(string level, string? message)
    {
        if (!_enabled || string.IsNullOrEmpty(message)) return;
        Debug.WriteLine($"[AI][{level.Trim()}] {message}");
        try
        {
            lock (_lock)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                TrimIfNeeded(path);
                File.AppendAllText(path, $"[{DateTime.Now:MM-dd HH:mm:ss.fff}] {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    private static void TrimIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes) return;
        File.WriteAllLines(path, File.ReadAllLines(path).TakeLast(2000));
    }
}
