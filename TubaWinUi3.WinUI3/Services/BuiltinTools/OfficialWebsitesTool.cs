namespace TubaWinUi3.Services;

public sealed class OfficialWebsitesTool : IBuiltinTool
{
    public string Id => "official-websites";
    public string Name => "常用官网";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "收录 Steam、Epic、UU 加速器等常用软件官方网站，一键直达。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE8F1";
    public string Category => "实用工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.OfficialWebsitesPage));
        return Task.CompletedTask;
    }
}
