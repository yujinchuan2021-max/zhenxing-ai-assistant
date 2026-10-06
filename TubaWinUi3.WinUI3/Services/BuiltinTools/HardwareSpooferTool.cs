using Microsoft.UI.Xaml;

namespace TubaWinUi3.Services;

public sealed class HardwareSpooferTool : IBuiltinTool
{
    public string Id => "hardware-spoofer";
    public string Name => "配置修改器";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "编辑 CPU、主板、显卡、内存、显示器和硬盘的展示名称，内置主流型号、修改预览和原值恢复。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "";
    public string Category => "系统工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.HardwareSpooferPage));
        return Task.CompletedTask;
    }
}
