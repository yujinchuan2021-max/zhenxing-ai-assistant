using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

public sealed class SpeedTestTool : IBuiltinTool
{
    public string Id => "speed-test";
    public string Name => "网速测试";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "原生测试网络延迟、下载与上传速度，支持浙大 / Ookla / Cloudflare 多测速节点切换";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE86F";
    public string Category => "网络工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(SpeedTestPage));
        return Task.CompletedTask;
    }
}
