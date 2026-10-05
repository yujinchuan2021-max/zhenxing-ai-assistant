// EnergyStarTool — built-in tool entry that opens the EnergyStarWindow.
// Underlying engine ported from EnergyStarX (https://github.com/JasonWei512/EnergyStarX)
// Copyright 2022 Bingxing Wang — MIT licensed (see Services/EnergyStar/LICENSE.txt).

namespace TubaWinUi3.Services;

public sealed class EnergyStarTool : IBuiltinTool
{
    public string Id => "energy-star";
    public string Name => "后台节流省电神器";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "通过 Windows 11 效率模式 (EcoQoS) 节流后台进程以省电降温, 前台应用保持流畅 (基于 Energy Star X 内核)。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE83F";
    public string Category => "系统工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(TubaWinUi3.Pages.EnergyStarPage));
        return Task.CompletedTask;
    }
}
