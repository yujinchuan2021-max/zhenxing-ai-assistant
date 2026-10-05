using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Services.AppManagement;

/// <summary>
/// MCP 确认流（主应用侧，ZXAI 2026-09-19）：
/// 外部 AI Agent（Claude Code 等）通过 MCP 请求安装/卸载软件时，MCP 进程写 pending 文件到
/// %LocalAppData%\TubaWinUi3\mcp-queue\{id}.request.json，本 watcher 轮询发现后**弹窗让用户确认**，
/// 允许 → 在本应用内执行（复用 SystemInstaller，与 AI 助手同路径）→ 结果写回 {id}.response.json。
/// 主应用未运行时 MCP 侧靠 .app-alive 心跳检测，直接提示"请先打开应用"。
/// </summary>
internal static class McpConfirmWatcher
{
    private static readonly string RealQueueDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TubaWinUi3", "mcp-queue");

    /// <summary>【GUI 隔离】测试模式走隔离根队列；正常为真实 mcp-queue。</summary>
    public static string QueueDir => DataRoots.TestRoot is { } tr ? Path.Combine(tr, "mcp-queue") : RealQueueDir;

    private static DispatcherQueueTimer? _timer;
    private static bool _busy;

    public static void Start(Window window)
    {
        try
        {
            Directory.CreateDirectory(QueueDir);
            TouchAlive();
            _timer = window.DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(3);
            _timer.Tick += async (_, _) => await ScanAsync(window);
            _timer.Start();
            AgentDebugLog.Info("[McpConfirm] watcher started");
        }
        catch (Exception ex)
        {
            AgentDebugLog.Info($"[McpConfirm] start failed: {ex.Message}");
        }
    }

    private static void TouchAlive()
    {
        try { File.WriteAllText(Path.Combine(QueueDir, ".app-alive"), DateTime.UtcNow.ToString("o")); } catch { }
    }

    private static async Task ScanAsync(Window window)
    {
        TouchAlive();
        if (_busy) return;
        try
        {
            var requests = Directory.GetFiles(QueueDir, "*.request.json");
            if (requests.Length == 0) return;
            var reqPath = requests.OrderBy(File.GetCreationTimeUtc).First();

            JsonNode? req = null;
            try { req = JsonNode.Parse(File.ReadAllText(reqPath, Encoding.UTF8)); } catch { }
            if (req is null) { try { File.Delete(reqPath); } catch { } return; }

            var id = req["id"]?.GetValue<string>() ?? "";
            var tool = req["tool"]?.GetValue<string>() ?? "";
            var args = req["args"] as JsonObject;

            // 过期（>3 分钟）→ 不再弹窗，直接回绝
            if (DateTime.UtcNow - File.GetCreationTimeUtc(reqPath) > TimeSpan.FromMinutes(3))
            {
                WriteResponse(id, false, MiscTexts.T("请求已过期（确认超时）。"));
                try { File.Delete(reqPath); } catch { }
                return;
            }

            string title, body, confirmKeyword;
            switch (tool)
            {
                case "install_software":
                    title = MiscTexts.T("外部 AI Agent 请求安装软件");
                    body = MiscTexts.TSub($"有外部 AI Agent 请求安装：{args?["target"]?.GetValue<string>()}\n\n") +
                           MiscTexts.T("将用 winget 装到系统（全局可用），并登记到应用中心。允许吗？");
                    confirmKeyword = args?["target"]?.GetValue<string>() ?? "";
                    break;
                case "uninstall_software":
                    title = MiscTexts.T("外部 AI Agent 请求卸载软件");
                    body = MiscTexts.TSub($"有外部 AI Agent 请求卸载：{args?["winget_id"]?.GetValue<string>()}\n\n允许吗？");
                    confirmKeyword = args?["winget_id"]?.GetValue<string>() ?? "";
                    break;
                default:
                    WriteResponse(id, false, MiscTexts.TSub($"未知请求类型：{tool}"));
                    try { File.Delete(reqPath); } catch { }
                    return;
            }
            _ = confirmKeyword;

            _busy = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = body,
                    PrimaryButtonText = MiscTexts.T("允许"),
                    CloseButtonText = MiscTexts.T("拒绝"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = (window.Content as FrameworkElement)?.XamlRoot,
                };
                var result = await dialog.ShowAsync();

                if (result != ContentDialogResult.Primary)
                {
                    WriteResponse(id, false, MiscTexts.T("用户拒绝了该请求。"));
                }
                else
                {
                    string report;
                    if (tool == "install_software")
                    {
                        var target = args?["target"]?.GetValue<string>() ?? "";
                        report = await SystemInstaller.InstallAsync(target, null, CancellationToken.None);
                    }
                    else
                    {
                        var wid = args?["winget_id"]?.GetValue<string>() ?? "";
                        report = await SystemInstaller.UninstallAsync(wid, CancellationToken.None);
                    }
                    Debug.WriteLine($"[McpConfirm] executed: {report}");
                    WriteResponse(id, true, report);
                }
                try { File.Delete(reqPath); } catch { }
            }
            finally
            {
                _busy = false;
            }
        }
        catch (Exception ex)
        {
            // 弹窗冲突等异常：request 保留，下一轮重试（过期检查兜底）
            AgentDebugLog.Info($"[McpConfirm] scan failed: {ex.Message}");
            _busy = false;
        }
    }

    private static void WriteResponse(string id, bool allowed, string message)
    {
        try
        {
            var resp = new JsonObject { ["id"] = id, ["allowed"] = allowed, ["message"] = message };
            File.WriteAllText(Path.Combine(QueueDir, $"{id}.response.json"),
                resp.ToJsonString(), new UTF8Encoding(false));
        }
        catch { }
    }
}
