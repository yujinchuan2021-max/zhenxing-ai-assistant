using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

public sealed class PerformanceBenchmarkTool : IBuiltinTool
{
	public string Id => "performance-benchmark";
	public string Name => "性能测试";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

	public string Description => "全面测试 CPU/GPU/内存/硬盘/浏览器性能，按比例计算游戏与办公性能评分，导出专业 PDF 报告。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

	public string Glyph => "\ue9d9";
	public string Category => "硬件工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

	public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

	public Task ExecuteAsync(BuiltinToolContext context)
	{
		if (App.MainWindow is MainWindow mw)
			mw.NavigateToBenchmark();
		return Task.CompletedTask;
	}
}