using Microsoft.UI.Xaml;

namespace TubaWinUi3.Services;

public sealed class HardwareSpooferTool : IBuiltinTool
{
    public string Id => "hardware-spoofer";
    public string Name => "配置修改器";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "修改注册表中的 CPU、GPU、系统等硬件信息显示，支持一键恢复原始配置。";
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
