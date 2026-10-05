namespace TubaWinUi3.Services;

public sealed class GameTunnelTool : IBuiltinTool
{
    public string Id => "game-tunnel";
    public string Name => "游戏联机助手";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "虚拟局域网联机：我的世界、泰拉瑞亚、幻兽帕鲁等，把两台电脑接进同一个虚拟网络，朋友粘贴邀请码就能进，不需要公网 IP 也不用改路由器";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE774";
    public string Category => "游戏工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.GameTunnelPage));
        return Task.CompletedTask;
    }
}
