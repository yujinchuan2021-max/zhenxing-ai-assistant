namespace TubaWinUi3.Services.Community;

/// <summary>WebView 事件 → 应用动作。</summary>
public enum CommunityNavigationAction
{
    /// <summary>没有可处理的目标（空地址）：什么都不做。</summary>
    Ignore,

    /// <summary>留在当前 WebView 视图内加载。</summary>
    Embed,

    /// <summary>交给系统浏览器打开（跨站链接、第三方登录等）。</summary>
    OpenExternal,

    /// <summary>拦截：非 http(s) 协议、相对地址、未配置，或过不了外开校验的目标。</summary>
    Block,

    /// <summary>Cancel an authentication continuation; offer a clean login in the system browser.</summary>
    RestartAuthentication,
}

/// <summary>一次导航动作的计划：动作 + 目标地址（Embed / OpenExternal 时有值）。</summary>
public readonly record struct CommunityNavigationPlan(CommunityNavigationAction Action, string? Target)
{
    /// <summary>什么都不做（空地址）。</summary>
    public static CommunityNavigationPlan Ignore { get; } = new(CommunityNavigationAction.Ignore, null);

    /// <summary>拦截。</summary>
    public static CommunityNavigationPlan Block { get; } = new(CommunityNavigationAction.Block, null);

    /// <summary>在当前视图内加载。</summary>
    public static CommunityNavigationPlan Embed(string target) => new(CommunityNavigationAction.Embed, target);

    /// <summary>交系统浏览器。</summary>
    public static CommunityNavigationPlan OpenExternal(string target) => new(CommunityNavigationAction.OpenExternal, target);

    public static CommunityNavigationPlan RestartAuthentication(string cleanLoginUrl)
        => new(CommunityNavigationAction.RestartAuthentication, cleanLoginUrl);

    /// <summary>调用方是否应取消本次导航（外开与拦截都要取消，否则会在应用内加载）。</summary>
    public bool Cancels => Action is CommunityNavigationAction.OpenExternal or CommunityNavigationAction.Block
        or CommunityNavigationAction.RestartAuthentication;
}

/// <summary>
/// 「WebView 事件 → 动作」的决策层（<b>纯函数</b>，与 UI 无关，全部可单测）。
///
/// 两个决策入口都用<b>调用方传入的可信 origin</b>（= 已配置社区地址解析出的 scheme://host:port/）：
///   · <see cref="ForNavigation"/>：NavigationStarting —— 同源内嵌 / 站外外开 / 其余拦截；
///   · <see cref="ForNewWindow"/>：NewWindowRequested —— 同源 → <see cref="CommunityNavigationAction.Embed"/>
///     （由调用方在当前视图打开，应用内永不开第二个窗口）/ 站外外开 / 其余拦截。
///
/// ⚠️ 关键教训（真实缺陷）：<b>「交系统浏览器」是否放行，不能再用 origin 作用域的
/// <see cref="CommunitySite.Classify"/> 判断</b>——origin 为 null 时它会把一切 http(s) 判成
/// <see cref="CommunityNavigationDecision.Blocked"/>，导致站外链接、OAuth、以及两个「在浏览器打开」
/// 按钮全部失效。外开校验是与 origin 无关的独立规则，见 <see cref="TryGetBrowserUrl"/>：
/// 只接受绝对 http(s)、必须有主机名、不得带账号密码；其余（javascript:/data:/file:/blob:/ms-*:、
/// 相对地址、带凭据地址）一律拒绝，绝不"为了能用"而放宽。
/// </summary>
public static class CommunityNavigationPolicy
{
    /// <summary>NavigationStarting 的决策。</summary>
    public static CommunityNavigationPlan ForNavigation(string? targetUrl, Uri? communityOrigin)
        => Plan(targetUrl, communityOrigin);

    /// <summary>NewWindowRequested 的决策（与导航同口径；同源返回 Embed，由调用方在当前视图打开）。</summary>
    public static CommunityNavigationPlan ForNewWindow(string? targetUrl, Uri? communityOrigin)
        => Plan(targetUrl, communityOrigin);

    private static CommunityNavigationPlan Plan(string? targetUrl, Uri? communityOrigin)
    {
        switch (CommunitySite.Classify(targetUrl, communityOrigin))
        {
            case CommunityNavigationDecision.StayEmbedded:
                return CommunityNavigationPlan.Embed(targetUrl!.Trim());

            case CommunityNavigationDecision.OpenExternal:
                // 站外 http(s)：先过"能交给系统浏览器"的独立校验；过不了就当拦截，绝不放行
                return TryGetBrowserUrl(targetUrl, out var safe, out _)
                    ? CommunityNavigationPlan.OpenExternal(safe!)
                    : CommunityNavigationPlan.Block;

            case CommunityNavigationDecision.None:
                return CommunityNavigationPlan.Ignore;

            default:
                return CommunityNavigationPlan.Block;
        }
    }

    /// <summary>
    /// 能否把这个地址交给系统浏览器（<b>与 origin 无关</b>的独立校验，可单测）：
    /// 只接受绝对 <c>http(s)</c> 地址、必须有主机名、不得带账号密码；
    /// 其余一律 false（调用方据此不打开）。<paramref name="isHttps"/> 便于调用方记录/提示。
    /// </summary>
    public static bool TryGetBrowserUrl(string? url, out string? safeUrl, out bool isHttps)
    {
        safeUrl = null;
        isHttps = false;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var text = url.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;

        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!isHttp && !https) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;   // 不把带账号密码的地址丢给浏览器

        safeUrl = text;
        isHttps = https;
        return true;
    }

    /// <summary>
    /// 「在浏览器打开」按钮的目标地址：
    /// 优先"当前<b>已加载</b>且可信"的社区同源页面；否则回落<b>已配置的官方地址</b>；
    /// 两者都不成立 → null（按钮不可点。绝不打开 about:blank、空白页或任何未经验证的目标）。
    /// </summary>
    public static string? BrowserButtonTarget(string? currentPageUrl, string? configuredUrl, Uri? communityOrigin)
    {
        if (IsTrustedSameOriginPage(currentPageUrl, communityOrigin)
            && TryGetBrowserUrl(currentPageUrl, out var current, out _))
            return current;

        return TryGetBrowserUrl(configuredUrl, out var configured, out _) ? configured : null;
    }

    /// <summary>
    /// 当前地址是不是"已加载、可信、可外开"的社区同源页面：
    /// 非空、不是 <c>about:blank</c>、且 <see cref="CommunitySite.Classify"/> 判为 StayEmbedded。
    /// （未配置时 origin 为 null → 一律不是，所以「社区即将开放」态下不会有可外开的目标。）
    /// </summary>
    public static bool IsTrustedSameOriginPage(string? currentUrl, Uri? communityOrigin)
        => !string.IsNullOrWhiteSpace(currentUrl)
           && !string.Equals(currentUrl.Trim(), "about:blank", StringComparison.OrdinalIgnoreCase)
           && CommunitySite.Classify(currentUrl, communityOrigin) is CommunityNavigationDecision.StayEmbedded;
}
