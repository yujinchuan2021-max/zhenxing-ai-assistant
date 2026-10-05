namespace TubaWinUi3.Services;

public sealed class PortViewerTool : IBuiltinTool
{
    public string Id => "port-viewer";
    public string Name => "端口占用";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "查看系统所有 TCP/UDP 端口占用情况，定位占用进程。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE774";
    public string Category => "网络工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.PortViewerPage));
        return Task.CompletedTask;
    }
}
