using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

public sealed class DigitalLiteracyTestTool : IBuiltinTool
{
    public string Id => "digital-literacy-test";
    public string Name => "电子文盲测试";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "测试你的电脑基础知识水平，看看你是不是「电子文盲」！共 25 道选择题，满分 100 分，答对得分。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE9CE";
    public string Category => "实用工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(DigitalLiteracyTestPage));
        return Task.CompletedTask;
    }
}
