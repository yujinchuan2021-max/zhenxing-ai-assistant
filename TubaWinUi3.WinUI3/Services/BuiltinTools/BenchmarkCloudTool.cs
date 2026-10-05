using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

public sealed class BenchmarkCloudTool : IBuiltinTool
{
	public string Id => "benchmark-cloud";
	public string Name => "跑分排行";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

	public string Description => "上传测试报告到社区，查看全球排行榜，与同硬件用户对比性能。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

	public string Glyph => "\ue9d5";
	public string Category => "硬件工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

	public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

	public Task ExecuteAsync(BuiltinToolContext context)
	{
		App.MainWindow?.NavigateToToolPage(typeof(BenchmarkCloudPage));
		return Task.CompletedTask;
	}
}
