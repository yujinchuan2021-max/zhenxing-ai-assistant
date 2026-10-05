using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Services;

/// <summary>
/// ZXAI 验收自检（诊断用，非产品功能）：命令行带 "--zxtest-send &lt;文本&gt;" 时，
/// 等待页面就绪后把文本注入 AI 助手输入框，并反射调用组件内部 TrySend 触发一次真实发送。
/// 用于无人值守验证 ChatPanel 初始化与发送链路（免鼠标键盘）。
/// </summary>
internal static class ZxSelfTest
{
    /// <summary>自检只执行一次（页面 Loaded 会多次触发，重复执行会造成重建竞态）。</summary>
    private static bool _fired;

    public static async Task MaybeRunAsync(UserControl page)
    {
        string[] args;
        try { args = Environment.GetCommandLineArgs(); }
        catch { return; }
        var refreshIdx = Array.FindIndex(args, a => string.Equals(a, "--zxtest-refresh", StringComparison.OrdinalIgnoreCase));
        var switchIdx = Array.FindIndex(args, a => string.Equals(a, "--zxtest-switch", StringComparison.OrdinalIgnoreCase));
        var idx = Array.FindIndex(args, a => string.Equals(a, "--zxtest-send", StringComparison.OrdinalIgnoreCase));
        var catIdx = Array.FindIndex(args, a => string.Equals(a, "--zxtest-cat", StringComparison.OrdinalIgnoreCase));
        if (refreshIdx < 0 && switchIdx < 0 && catIdx < 0 && (idx < 0 || idx + 1 >= args.Length)) return;
        if (_fired) return;
        _fired = true;
        var text = (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : null;
        var cat = (catIdx >= 0 && catIdx + 1 < args.Length) ? args[catIdx + 1] : null;

        try
        {
            await Task.Delay(4000); // 等页面与组件初始化完成

            // ZXAI 自检：直达分类页（--zxtest-cat 烤鸡）
            if (cat is not null && App.MainWindow is { } mw)
            {
                mw.ZxOpenCategory(cat);
                AgentDebugLog.Info($"[ZXTEST] open category='{cat}'");
                await Task.Delay(1500);
                AgentDebugLog.Info("[ZXTEST] category page loaded");
                return;
            }

            if (text is null) return;

            // ZXAI 适配（上游 v1.6.4 Agent 去 webview 化后）：直接用页面的输入框发送
            if (page is TubaWinUi3.Pages.AiAgentPage agentPage)
            {
                agentPage.ZxSendText(text);
                AgentDebugLog.Info($"[ZXTEST] send via ZxSendText text='{text}'");
            }
        }
        catch (Exception ex)
        {
            AgentDebugLog.Info($"[ZXTEST] EXCEPTION: {ex}");
        }
    }

}
