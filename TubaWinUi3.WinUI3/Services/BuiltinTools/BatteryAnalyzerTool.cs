using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

public sealed class BatteryAnalyzerTool : IBuiltinTool
{
    public string Id => "battery-analyzer";
    public string Name => "电池消耗分析";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "分析电池消耗趋势、应用耗电排行，比 Windows 设置更强大的电池分析工具";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE85E";
    public string Category => "硬件工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(BatteryAnalyzerPage));
        return Task.CompletedTask;
    }
}
