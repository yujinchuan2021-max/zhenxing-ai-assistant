using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

public sealed class WingetStoreTool : IBuiltinTool
{
    public string Id => "winget-store";
    public string Name => "正版软件商店";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "浏览并安装正版软件，基于 WinGet 软件源";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE719";
    public string Category => "实用工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(WingetStorePage));
        return Task.CompletedTask;
    }
}
