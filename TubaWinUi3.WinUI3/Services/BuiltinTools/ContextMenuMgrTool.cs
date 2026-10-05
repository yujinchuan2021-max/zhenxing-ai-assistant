using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

/// <summary>
/// 右键菜单管理：原为下载外部 ContextMenuMgr 程序；现由内置工具
/// 「流氓软件的克星」提供更完整的内置右键菜单管理（基础/专用模块/高级兼容），
/// 此工具直接跳转过去。
/// </summary>
public sealed class ContextMenuMgrTool : IBuiltinTool
{
    public string Id => "context-menu-mgr";
    public string Name => "右键菜单管理";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "管理 Windows 右键菜单项，支持启用/禁用/编辑/添加/删除、新建/发送到/打开方式及 WinX/现代/IE 菜单（内置）";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE74C";
    public string Category => "系统工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        // 跳转「流氓软件的克星」并定位到右键菜单管理分区。
        App.MainWindow?.NavigateToToolPage(typeof(RogueCleanerPage), "contextmenu");
        return Task.CompletedTask;
    }
}
