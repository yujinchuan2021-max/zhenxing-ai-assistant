namespace TubaWinUi3.Services;

public sealed class TimeSyncTool : IBuiltinTool
{
    public string Id => "time-sync";
    public string Name => "时间同步";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "一键切换系统 NTP 时间服务器（阿里云 / 腾讯云 / 国家授时中心…），支持服务器测速、偏差检测、立即校时、时间服务修复与恢复系统默认";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE823";
    public string Category => "系统工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.TimeSyncPage));
        return Task.CompletedTask;
    }
}
