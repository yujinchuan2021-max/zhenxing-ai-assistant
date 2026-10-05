namespace TubaWinUi3.Services;

public sealed class DiskHealthTool : IBuiltinTool
{
    public string Id => "disk-health";
    public string Name => "磁盘健康";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "SMART 健康度检测（CrystalDiskInfo 方案）：温度/通电/寿命/读写量，支持 SSD TRIM 与机械盘碎片整理";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uEDA8";
    public string Category => "硬件工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.DiskHealthPage));
        return Task.CompletedTask;
    }
}