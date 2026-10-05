namespace TubaWinUi3.Services;

public sealed class FormatConvertTool : IBuiltinTool
{
    public string Id => "format-converter";
    public string Name => "格式转换";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "图片/音视频/Word/Excel/PPT/PDF/文本互转，OCR 识别、PDF 合并拆分、任意文件打包 ZIP，批量队列、拖入即用";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE8B2";
    public string Category => "实用工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.FormatConverterPage));
        return Task.CompletedTask;
    }
}