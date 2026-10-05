using Microsoft.UI.Xaml;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;

namespace TubaWinUi3.Services;

public sealed class StressTestTool : IBuiltinTool
{
    public string Id => "stress-test";
    public string Name => MiscTexts.T("一键三烤");
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => MiscTexts.T("CPU / GPU / 网卡压力测试工具，自由勾选烤机项目（CPU、GPU、网卡），网卡烤机支持自定义数据量与速率参考，实时监控温度、频率、功耗与网卡吞吐。");
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uECAD";
    public string Category => "硬件工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(StressTestPage));
        return Task.CompletedTask;
    }
}
