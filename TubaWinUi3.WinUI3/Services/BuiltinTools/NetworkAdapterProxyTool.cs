namespace TubaWinUi3.Services;

public sealed class NetworkAdapterProxyTool : IBuiltinTool
{
    public string Id => "network-adapter-proxy";
    public string Name => "网络调度器";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "汇聚多网络适配器，智能分配流量，Wi-Fi 有线自动加速。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE774";
    public string Category => "网络工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.NetworkAdapterProxyPage));
        return Task.CompletedTask;
    }
}
