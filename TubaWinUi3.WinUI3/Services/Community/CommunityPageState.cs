using TubaWinUi3.Services;

namespace TubaWinUi3.Services.Community;

/// <summary>社区页的四种可见状态（纯数据，页面据此切换显示块）。</summary>
public enum CommunityViewState
{
    /// <summary>社区地址未配置：显示「社区即将开放」，不初始化 WebView。</summary>
    Unconfigured,

    /// <summary>正在加载社区页面。</summary>
    Loading,

    /// <summary>已配置且页面可交互。</summary>
    Ready,

    /// <summary>加载失败（网络/证书/渲染进程等），可重试或改用系统浏览器。</summary>
    Failed,
}

/// <summary>
/// 状态取值（纯函数，便于单测）：把"是否配置 / 是否加载中 / 是否失败"映射成一个可见状态，
/// 并给出对应的标题与说明文案。文案面向普通用户，不出现内部接口名。
/// </summary>
public static class CommunityPageState
{
    public static CommunityViewState For(bool configured, bool loading, bool failed)
        => !configured ? CommunityViewState.Unconfigured
           : failed ? CommunityViewState.Failed
           : loading ? CommunityViewState.Loading
           : CommunityViewState.Ready;

    public static string TitleFor(CommunityViewState state) => state switch
    {
        CommunityViewState.Unconfigured => LocalizationService.L("Community_StateUnconfiguredTitle", MiscTexts.T("社区即将开放")),
        CommunityViewState.Failed => LocalizationService.L("Community_StateFailedTitle", MiscTexts.T("社区页面加载失败")),
        _ => string.Empty,
    };

    /// <summary>未配置时的说明：如实描述当前状态，不给开放时间承诺、不显示任何示例内容。</summary>
    public static string MessageFor(CommunityViewState state) => state switch
    {
        CommunityViewState.Unconfigured =>
            LocalizationService.L(
                "Community_StateUnconfiguredMessage",
                MiscTexts.T("社区官方站点还在准备中。上线后这里会直接显示社区页面，不用另外安装任何东西；")
                + MiscTexts.T("在那之前，工具箱的其它功能都可以正常使用。")),
        _ => string.Empty,
    };

    /// <summary>失败时的说明：数据源提供的具体原因优先，其余用通用文案。</summary>
    public static string FailureMessageFor(string? detail)
        => string.IsNullOrWhiteSpace(detail) ? LocalizationService.L("Community_StateFailedMessage", MiscTexts.T("社区页面加载失败。")) : detail!.Trim();

    /// <summary>该状态下是否展示主操作按钮（重试）。</summary>
    public static bool ShowsRetry(CommunityViewState state) => state == CommunityViewState.Failed;

    /// <summary>该状态下是否展示"在浏览器中打开"。</summary>
    public static bool ShowsOpenInBrowser(CommunityViewState state)
        => state is CommunityViewState.Failed or CommunityViewState.Ready;
}
