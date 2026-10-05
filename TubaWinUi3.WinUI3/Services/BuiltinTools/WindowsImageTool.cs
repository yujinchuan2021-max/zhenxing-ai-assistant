using Microsoft.UI.Xaml;

namespace TubaWinUi3.Services;

public sealed class WindowsImageTool : IBuiltinTool
{
    public string Id => "windows-image";
    public string Name => "Windows 镜像";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "下载 Windows 原版系统镜像（ISO/ESD），支持 ESD 转 ISO。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE896";
    public string Category => "系统工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.WindowsImagePage));
        return Task.CompletedTask;
    }
}
