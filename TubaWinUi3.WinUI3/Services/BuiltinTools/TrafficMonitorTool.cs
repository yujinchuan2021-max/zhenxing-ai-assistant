using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

public sealed class TrafficMonitorTool : IBuiltinTool
{
    public string Id => "traffic-monitor";
    public string Name => "流量监控器";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "选择网卡实时查看各连接的流量、速度与延迟，整卡吞吐折线统计，支持快照录制与滑条回放。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE774";
    public string Category => "网络工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TrafficMonitorPage));
        return Task.CompletedTask;
    }
}
