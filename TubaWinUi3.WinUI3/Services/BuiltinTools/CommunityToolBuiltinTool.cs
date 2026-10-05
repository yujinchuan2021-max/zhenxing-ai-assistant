using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

public sealed class CommunityToolBuiltinTool : IBuiltinTool
{
	public string Id => "community-tools";
	public string Name => "社区工具";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

	public string Description => "来自社区贡献的工具插件，下载安装即可使用。支持提交和删除工具。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

	public string Glyph => "\ue774";
	public string Category => "实用工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

	public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

	public Task ExecuteAsync(BuiltinToolContext context)
	{
		App.MainWindow?.NavigateToToolPage(typeof(CommunityToolsPage));
		return Task.CompletedTask;
	}
}
