using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TubaWinUi3.Services;

/// <summary>错误反馈仅生成由用户核对并发送的邮件草稿，不打包或收集日志附件。</summary>
public static class ErrorReportService
{
    internal const int MaxErrorSummaryLength = 600;
    private const int MaxReproStepsLength = 700;
    private const string Redacted = "[redacted]";

    public static Uri CreateFeedbackDraft(string? currentErrorText = null, string? reproSteps = null)
        => BuildFeedbackDraft(currentErrorText, reproSteps, GetAppVersion(),
            $"{Environment.OSVersion.VersionString} ({RuntimeInformation.OSArchitecture}); .NET {Environment.Version}");

    /// <summary>只接受传入的文本和版本信息；纯函数，不读取配置、日志、事件日志或硬件。</summary>
    internal static Uri BuildFeedbackDraft(string? currentErrorText, string? reproSteps, string appVersion, string environment)
    {
        var body = new StringBuilder();
        body.AppendLine(string.Format(LocalizationService.L("ErrorFeedback_AppVersion", "应用版本：{0}"), appVersion));
        body.AppendLine(string.Format(LocalizationService.L("ErrorFeedback_Environment", "环境：{0}"), environment));
        body.AppendLine();
        body.AppendLine(LocalizationService.L("ErrorFeedback_Summary", "错误摘要："));
        var summary = SummarizeError(currentErrorText);
        body.AppendLine(string.IsNullOrWhiteSpace(summary)
            ? LocalizationService.L("ErrorFeedback_NoError", "请描述遇到的问题。")
            : summary);
        body.AppendLine();
        body.AppendLine(LocalizationService.L("ErrorFeedback_Repro", "复现步骤："));
        body.AppendLine(TrimForDraft(SanitizeFeedbackText(reproSteps), MaxReproStepsLength));
        body.AppendLine();
        body.AppendLine(LocalizationService.L("ErrorFeedback_Review",
            "请补充问题现象和复现步骤，检查内容后在邮件应用中点击发送。"));
        return FeedbackContact.CreateDraft(
            LocalizationService.L("ErrorFeedback_Subject", "枕星AI助手错误反馈"), body.ToString());
    }

    internal static string SummarizeError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (Regex.IsMatch(trimmed, @"^(?:堆栈|Stack(?: trace)?)[：:]|^at\s+", RegexOptions.IgnoreCase)) break;
            lines.Add(trimmed);
            if (lines.Count == 3) break;
        }
        return TrimForDraft(SanitizeFeedbackText(string.Join("\n", lines)), MaxErrorSummaryLength);
    }

    /// <summary>对明显凭据、网址、邮箱和绝对路径做保守过滤；用户仍需核对草稿。</summary>
    internal static string SanitizeFeedbackText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var result = text.Replace("\0", "");
        result = Regex.Replace(result, @"(?i)\bBearer\s+\S+", "Bearer " + Redacted);
        result = Regex.Replace(result, @"sk-[A-Za-z0-9_\-]{8,}|\bgh[pousr]_[A-Za-z0-9]{20,}", Redacted);
        result = Regex.Replace(result,
            """(?i)(["']?(?:api[ _\-]?key|access[ _\-]?token|refresh[ _\-]?token|token|secret|password|passwd|密码|密钥|口令)["']?\s*[:=：＝]\s*)(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s,;}\]]+)""",
            match => match.Groups[1].Value + Redacted);
        result = Regex.Replace(result, """(?i)\b(?:https?|ftp)://[^\s<>"']+""", Redacted);
        result = Regex.Replace(result, @"(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", Redacted);
        result = Regex.Replace(result, """(?i)(?:[A-Z]:[\\/]|\\\\)[^\r\n"'<>|]+""", Redacted);
        result = Regex.Replace(result, """(?<![\w:])/[^ \t\r\n"'<>]+""", Redacted);
        return result.Trim();
    }

    private static string TrimForDraft(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var marker = LocalizationService.L("ErrorFeedback_Truncated", "（摘要已截断）");
        var take = Math.Max(0, maxLength - marker.Length);
        if (take > 0 && char.IsHighSurrogate(text[take - 1])) take--;
        return text[..take] + marker;
    }

    /// <summary>收集运行环境与硬件信息，仅供错误窗口查看和复制，不加入反馈草稿。</summary>
    public static string CollectSystemInfo()
    {
        var sb = new StringBuilder();

        sb.AppendLine(MiscTexts.TSub($"应用版本：{GetAppVersion()}"));
        sb.AppendLine(MiscTexts.TSub($"运行模式：{(RuntimeHelper.IsMsixPackaged ? MiscTexts.T("MSIX 打包版") : MiscTexts.T("便携版"))}"));
        sb.AppendLine(MiscTexts.TSub($"程序路径：{Environment.ProcessPath}"));
        sb.AppendLine(MiscTexts.TSub($"数据目录：{ConfigManager.GetDataDir()}"));
        sb.AppendLine(MiscTexts.TSub($"操作系统：{GetWindowsVersion()}"));
        sb.AppendLine(MiscTexts.TSub($"系统架构：{Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "Unknown"}"));
        sb.AppendLine(MiscTexts.TSub($".NET 版本：{Environment.Version}"));
        sb.AppendLine(MiscTexts.TSub($"管理员权限：{(IsRunningAsAdmin() ? MiscTexts.T("是") : MiscTexts.T("否"))}"));

        try
        {
            sb.AppendLine(MiscTexts.TSub($"处理器：{WmiQuery("Win32_Processor", "Name")}"));
            sb.AppendLine(MiscTexts.TSub($"内存：{GetTotalMemory()}"));
            sb.AppendLine(MiscTexts.TSub($"显卡：{WmiQuery("Win32_VideoController", "Name")}"));
            sb.AppendLine(MiscTexts.TSub($"主板：{WmiQuery("Win32_BaseBoard", "Product")}"));
        }
        catch { }

        if (HardwareInfoService.HasCache)
            sb.AppendLine(MiscTexts.T("硬件缓存：已加载"));

        return sb.ToString().TrimEnd();
    }

    private static string GetWindowsVersion()
    {
        try
        {
            var version = Environment.OSVersion.Version;
            var build = version.Build;
            var releaseId = "";

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key?.GetValue("DisplayVersion") is string dv)
                    releaseId = dv;
                else if (key?.GetValue("ReleaseId") is string ri)
                    releaseId = ri;
            }
            catch { }

            var name = build >= 26100 ? "Windows 11 24H2"
                     : build >= 22631 ? "Windows 11 23H2"
                     : build >= 22621 ? "Windows 11 22H2"
                     : build >= 22000 ? "Windows 11 21H2"
                     : build >= 19045 ? "Windows 10 22H2"
                     : build >= 19044 ? "Windows 10 21H2"
                     : build >= 19043 ? "Windows 10 21H1"
                     : "Windows";

            if (!string.IsNullOrEmpty(releaseId))
                return $"{name} (Build {build}, {releaseId})";
            return $"{name} (Build {build})";
        }
        catch
        {
            return MiscTexts.T("Windows (版本未知)");
        }
    }

    private static string GetTotalMemory()
    {
        try
        {
            var gcMem = GC.GetGCMemoryInfo();
            var totalMem = gcMem.TotalAvailableMemoryBytes;
            return $"{totalMem / (1024.0 * 1024.0 * 1024.0):F1} GB";
        }
        catch
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (var obj in searcher.Get())
                {
                    var val = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
                    return $"{val / (1024.0 * 1024.0 * 1024.0):F1} GB";
                }
            }
            catch { }
        }
        return MiscTexts.T("未知");
    }

    private static string WmiQuery(string className, string propertyName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {propertyName} FROM {className}");
            foreach (var obj in searcher.Get())
            {
                var val = obj[propertyName]?.ToString();
                if (!string.IsNullOrEmpty(val)) return val;
            }
        }
        catch { }
        return MiscTexts.T("未知");
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string GetAppVersion()
    {
        return UpdateService.CurrentVersion.ToString(3);
    }
}
