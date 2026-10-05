namespace TubaWinUi3.Services;

public sealed class RuntimeRepairTool : IBuiltinTool
{
    public string Id => "runtime-repair";
    public string Name => "运行库修复";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "检测并修复缺失的 Visual C++ 2008-2026、.NET Framework 4.8.1、DirectX 旧版游戏组件，微软官方源下载 + 签名校验";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE90F";
    public string Category => "游戏工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.RuntimeRepairPage));
        return Task.CompletedTask;
    }
}