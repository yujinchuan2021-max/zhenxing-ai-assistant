using Microsoft.Web.WebView2.Core;

namespace TubaWinUi3.Services;

/// <summary>
/// 应用自有 HTML/WebView 的界面字体桥：文档创建时以脚本覆盖 CSS 变量 --app-font
/// （app-font.css 已按目录为全部候选字体提供 @font-face；选择保存后重启全局生效）。
/// 仅覆盖页面 CSS 变量——不触碰 XAML 资源字典，与 font-maintenance.md §9 的运行期替换无关。
/// 调用点：应用内加载自有页面的 WebView2（zxassets.local / bench.local 虚拟主机），
/// 须在首次 Navigate 之前 AttachAsync（文档创建脚本对之后加载的文档生效）。
/// </summary>
internal static class WebFontBridge
{
    /// <summary>失败静默（页面回落到 css 里构建期默认字体），绝不因字体桥失败影响 WebView 功能。</summary>
    public static async System.Threading.Tasks.Task AttachAsync(CoreWebView2 core)
    {
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(AppFonts.WebFontOverrideScript());
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebFontBridge] 注入失败：{ex.Message}");
        }
    }
}
