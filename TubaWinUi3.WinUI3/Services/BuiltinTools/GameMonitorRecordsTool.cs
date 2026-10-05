using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

/// <summary>
/// 「记录查看」内置工具：独立入口，等价于在「游戏监控」页点「查看记录图表」。
/// 解析输出目录下的 JSON / CSV 记录，用工程内原生的 LiveCharts2 图表回放历史数据。
/// </summary>
public sealed class GameMonitorRecordsTool : IBuiltinTool
{
    public string Id => "game-monitor-records";
    public string Name => "记录查看";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "解析游戏监控导出的 JSON / CSV 记录，回放 FPS、温度、负载等历史数据：每个指标一张独立图表、各自使用自己的纵轴刻度尺，支持分组切换、区间裁剪与 P1/P99 统计";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE9D9";
    public string Category => "游戏工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        try
        {
            App.MainWindow?.NavigateToToolPage(typeof(GameMonitorRecordsPage));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameMonitorRecords] ExecuteAsync FAILED: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
