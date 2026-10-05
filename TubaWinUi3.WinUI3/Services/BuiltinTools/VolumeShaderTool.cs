using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TubaWinUi3.Pages;
using Windows.Graphics;

namespace TubaWinUi3.Services;

public sealed class VolumeShaderTool : IBuiltinTool
{
    public string Id => "volume-shader-test";
    public string Name => MiscTexts.T("毒蘑菇测试");
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => MiscTexts.T("GPU 分形压力测试：轻松 / 中等 / 变态三档压力，超分辨率渲染突破屏幕，实时帧率监控。");
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE950"; // 显卡图标
    public string Category => "硬件工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        try
        {
            // 构建本地HTML文件路径
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "VolumeShader", "index.html");
            
            if (!File.Exists(htmlPath))
            {
                ShowErrorDialog(context, new FileNotFoundException(MiscTexts.TSub($"找不到毒蘑菇测试页面: {htmlPath}")));
                return Task.CompletedTask;
            }

            // ZXAI 字体统一：经 BrowserPage 的 zxassets.local 虚拟主机打开页面，
            // 与 /Fonts 打包字体同源（WebView2 实测：file:// 页面拿不到打包字体）。
            // 外部浏览器场景由 BrowserPage.TranslateForExternalBrowser 还原为本地 file:// 页面。
            BrowserPage.Open("https://zxassets.local/VolumeShader/index.html", MiscTexts.T("毒蘑菇显卡测试"));

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VolumeShaderTool] ExecuteAsync FAILED: {ex.Message}\n{ex.StackTrace}");
            ShowErrorDialog(context, ex);
            return Task.CompletedTask;
        }
    }

    private static async void ShowErrorDialog(BuiltinToolContext context, Exception ex)
    {
        try
        {
            var dialog = context.CreateDialog(MiscTexts.T("毒蘑菇测试 - 启动失败"));
            dialog.Content = new Microsoft.UI.Xaml.Controls.ScrollViewer
            {
                Content = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = $"{ex.Message}\n\n{ex.StackTrace}",
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            dialog.CloseButtonText = MiscTexts.T("确定");
            await dialog.ShowAsync();
        }
        catch { }
    }
}
