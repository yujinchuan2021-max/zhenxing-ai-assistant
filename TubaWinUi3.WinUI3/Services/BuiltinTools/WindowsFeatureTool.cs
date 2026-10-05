namespace TubaWinUi3.Services;

public sealed class WindowsFeatureTool : IBuiltinTool
{
    public string Id => "windows-feature";
    public string Name => "Windows 隐藏功能";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "查询、启用、禁用、重置 Windows 实验性功能开关（ViVe 引擎移植，ntdll 功能配置 API 直读，毫秒级响应）";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE950";
    public string Category => "系统工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.WindowsFeaturePage));
        return Task.CompletedTask;
    }
}