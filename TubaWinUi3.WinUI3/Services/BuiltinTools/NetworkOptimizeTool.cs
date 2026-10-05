namespace TubaWinUi3.Services;

public sealed class NetworkOptimizeTool : IBuiltinTool
{
    public string Id => "network-optimize";
    public string Name => "网络优化";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "TCP 参数优化（拥塞控制 / Chimney / Nagle / 网卡节能）、DNS 延迟测速与配置、公网 IP 查询、网络重置与 DHCP 修复";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE968";
    public string Category => "网络工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.NetworkOptimizePage));
        return Task.CompletedTask;
    }
}