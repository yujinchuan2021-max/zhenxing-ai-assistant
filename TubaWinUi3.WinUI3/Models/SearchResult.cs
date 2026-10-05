namespace TubaWinUi3.Models;

using TubaWinUi3.Services;

public sealed class SearchResult
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Glyph { get; init; }
    public required SearchItemKind Kind { get; init; }
    public required string MatchKey { get; init; }
    public string? IconPath { get; init; }
    public string? Category { get; init; }
    public double Score { get; init; }

    public bool HasIconPath => !string.IsNullOrEmpty(IconPath);

    /// <summary>显示层翻译（标题精确、副标题兼容模板；匹配/跳转一律用原值）。</summary>
    public string TitleDisplay => MiscTexts.T(Title);
    public string SubtitleDisplay => MiscTexts.TSub(Subtitle);

    public string KindText => Kind switch
    {
        SearchItemKind.ExternalTool => MiscTexts.T("工具"),
        SearchItemKind.BuiltinTool => MiscTexts.T("内置"),
        SearchItemKind.Setting => MiscTexts.T("设置"),
        SearchItemKind.CustomTool => MiscTexts.T("自定义"),
        SearchItemKind.QuickAction => MiscTexts.T("快捷"),
        SearchItemKind.CommunityTool => MiscTexts.T("社区"),
        _ => ""
    };

    public override string ToString() => Title;
}

public enum SearchItemKind
{
    ExternalTool,
    BuiltinTool,
    Setting,
    CustomTool,
    QuickAction,
    CommunityTool,
    AiTool
}

public sealed class SearchNavigationTarget
{
    public string? HighlightToolPath { get; init; }
    public string? HighlightSettingKey { get; init; }
    public string? HighlightBuiltinId { get; init; }
}
