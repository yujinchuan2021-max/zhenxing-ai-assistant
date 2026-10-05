using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

public sealed class CpuRankingTool : IBuiltinTool
{
    public string Id => "cpu-ranking";
    public string Name => "CPU 天梯图";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "查看桌面/笔记本 CPU 性能天梯图，支持品牌筛选与排序。数据来自 NanoReview";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uEEA1";
    public string Category => "硬件工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(CpuRankingPage));
        return Task.CompletedTask;
    }
}
