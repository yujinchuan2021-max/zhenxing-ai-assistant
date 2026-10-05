using Microsoft.Web.WebView2.Core;
using TubaWinUi3.Services;

namespace TubaWinUi3.Services.Community;

/// <summary>
/// 枕星AI社区站点配置与导航边界（单一入口 + 纯函数，便于单测）。
///
/// 社区形态 = 自托管 <b>Discourse</b>（开源 GPL-2.0，官方支持 Docker 部署）站点，
/// 应用内用 WebView2 直接打开该 HTTPS 站点；本类<b>不</b>实现帖子/账号/审核/举报逻辑，
/// 也不持有任何凭据、不抓取 cookie 或 token。
///
/// 未配置（地址为空）时：页面显示「社区即将开放」，<b>不初始化 WebView2、不导航到任何地址</b>
/// （不会退回 example.com、上游论坛或任何临时站点）。
/// </summary>
public static class CommunitySite
{
    /// <summary>
    /// 【唯一入口】枕星AI社区官方站点地址（自托管 Discourse 的 HTTPS 地址）。
    /// 2026-09-27 起启用：指向已部署的公开社区站点（原「站点尚未部署、先保持即将开放」占位说明作废）。
    /// 留空或取值不合法 = 未配置，页面显示「社区即将开放」，不做任何降级猜测。
    /// 临时指向自建测试站时可用设置键 <see cref="SettingKey"/> 覆盖，无需改代码。
    /// </summary>
    public const string OfficialUrl = "https://community.zhenxingai.com";

    /// <summary>
    /// 【官方目标地址】枕星AI社区的既定域名（2026-09-27 已由 <see cref="OfficialUrl"/> 启用；
    /// 原「站点尚未部署，故不在此处启用」说明作废）。保留为单一事实来源：入口与校验都以此值为准；
    /// 不改代码时也可写设置 <c>CommunitySiteUrl = https://community.zhenxingai.com</c>（<see cref="SettingKey"/>）覆盖。
    /// 该值已由单测核对：能通过本类的 https/主机名/无凭据校验，并生成同源判定用的 origin。
    /// </summary>
    public const string TargetOfficialUrl = "https://community.zhenxingai.com";

    /// <summary>可选覆盖键（AppSettings 键值存储，值形如 https://community.example.com）。</summary>
    public const string SettingKey = "CommunitySiteUrl";

    /// <summary>当前生效的社区地址（设置覆盖优先，回落 <see cref="OfficialUrl"/>）；未配置或不合法返回 null。</summary>
    public static string? ResolveConfiguredUrl()
    {
        string? overrideUrl = null;
        try { overrideUrl = AppSettings.Get(SettingKey); }
        catch { /* 设置不可用（如首次启动/文件被占用）时按"未配置"处理，绝不猜地址 */ }
        return Resolve(overrideUrl, OfficialUrl);
    }

    /// <summary>地址取值（纯函数）：覆盖值优先；两者都空 → null。</summary>
    internal static string? Resolve(string? overrideUrl, string? officialUrl)
        => Normalize(string.IsNullOrWhiteSpace(overrideUrl) ? officialUrl : overrideUrl);

    /// <summary>
    /// 归一化与校验（纯函数）：只接受 <b>https</b> 绝对地址、必须有主机名、不得带账号密码；
    /// 返回去掉末尾斜杠的形式；不满足则 null（调用方据此进入"未配置"状态，不做任何降级猜测）。
    /// </summary>
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;

        var text = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri;
        return text.Length > 1 && text.EndsWith('/') ? text[..^1] : text;
    }

    /// <summary>由配置地址解析出"社区站 origin"（scheme://host:port/），用于同源判定。</summary>
    internal static bool TryResolveOrigin(string? configuredUrl, out Uri? origin)
    {
        origin = null;
        var normalized = Normalize(configuredUrl);
        if (normalized is null) return false;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return false;
        origin = new Uri($"{uri.Scheme}://{uri.Authority}/", UriKind.Absolute);
        return true;
    }

    /// <summary>
    /// 导航决策（纯函数）：社区站 origin 内继续内嵌；其它 http(s) 目标交给系统浏览器；
    /// 非 http(s) 协议与相对地址一律拦截；未配置时任何地址都不放行。
    /// </summary>
    public static CommunityNavigationDecision Classify(string? targetUrl, Uri? communityOrigin)
    {
        if (string.IsNullOrWhiteSpace(targetUrl)) return CommunityNavigationDecision.None;

        var text = targetUrl.Trim();
        if (string.Equals(text, "about:blank", StringComparison.OrdinalIgnoreCase))
            return CommunityNavigationDecision.StayEmbedded;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var target))
            return CommunityNavigationDecision.Blocked;

        var isHttp = string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var isHttps = string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!isHttp && !isHttps)
            return CommunityNavigationDecision.Blocked;

        // 带账号密码的地址一律拦截（不内嵌、也不外开）：同源判定不能把它当成"自己人"，
        // 否则 https://user:pw@<社区域名>/ 会留在 WebView 里，与文档口径不符。
        if (!string.IsNullOrEmpty(target.UserInfo))
            return CommunityNavigationDecision.Blocked;

        if (communityOrigin is null)
            return CommunityNavigationDecision.Blocked;

        return IsSameOrigin(target, communityOrigin)
            ? CommunityNavigationDecision.StayEmbedded
            : CommunityNavigationDecision.OpenExternal;
    }

    /// <summary>同源 = 协议 + 主机 + 端口完全一致（子域名视为站外，交给系统浏览器）。</summary>
    internal static bool IsSameOrigin(Uri target, Uri origin)
        => string.Equals(target.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(target.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
           && target.Port == origin.Port;

    /// <summary>加载失败的面向用户说明（webview 的 WebErrorStatus → 中文）。</summary>
    public static string Describe(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved
            => LocalizationService.L("Community_FailHostNameNotResolved", MiscTexts.T("找不到社区服务器地址：域名可能还没解析好，或当前网络无法解析域名。")),
        CoreWebView2WebErrorStatus.ServerUnreachable
            => LocalizationService.L("Community_FailServerUnreachable", MiscTexts.T("连不上社区服务器：服务可能还没启动，或被网络/防火墙拦住了。")),
        CoreWebView2WebErrorStatus.CannotConnect
            => LocalizationService.L("Community_FailCannotConnect", MiscTexts.T("无法建立连接：地址可能不正确，或被本机网络/代理挡住了。")),
        CoreWebView2WebErrorStatus.Timeout
            => LocalizationService.L("Community_FailTimeout", MiscTexts.T("访问社区超时：网络较慢或代理不通，可以稍后重试。")),
        CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset
            => LocalizationService.L("Community_FailConnectionAborted", MiscTexts.T("连接被中断：请检查代理设置或稍后重试。")),
        CoreWebView2WebErrorStatus.Disconnected
            => LocalizationService.L("Community_FailDisconnected", MiscTexts.T("网络已断开：恢复联网后重试。")),
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
            or CoreWebView2WebErrorStatus.CertificateExpired
            or CoreWebView2WebErrorStatus.CertificateIsInvalid
            => LocalizationService.L("Community_FailCertificate", MiscTexts.T("HTTPS 证书未通过校验：证书可能已过期、域名不匹配或不受信任，已停止加载（不会跳过证书检查）。")),
        CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired
            or CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired
            => LocalizationService.L("Community_FailAuthRequired", MiscTexts.T("站点或代理要求身份验证：请先在系统里完成代理认证，再回来重试。")),
        CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse
            => LocalizationService.L("Community_FailBadResponse", MiscTexts.T("服务器返回了异常响应：社区服务可能正在重启。")),
        CoreWebView2WebErrorStatus.RedirectFailed
            => LocalizationService.L("Community_FailRedirectFailed", MiscTexts.T("页面跳转失败：站点可能配置了异常的跳转规则。")),
        CoreWebView2WebErrorStatus.OperationCanceled
            => LocalizationService.L("Community_FailOperationCanceled", MiscTexts.T("本次加载已取消（多半是被新的导航取代）。")),
        _ => LocalizationService.L("Community_FailGeneric", MiscTexts.T("社区页面加载失败。")),
    };

    /// <summary>该失败是否更像"本机网络/代理"问题（用于提示"检查网络"）。</summary>
    public static bool SuggestsNetworkCheck(CoreWebView2WebErrorStatus status) => status
        is CoreWebView2WebErrorStatus.HostNameNotResolved
        or CoreWebView2WebErrorStatus.ServerUnreachable
        or CoreWebView2WebErrorStatus.CannotConnect
        or CoreWebView2WebErrorStatus.Timeout
        or CoreWebView2WebErrorStatus.ConnectionAborted
        or CoreWebView2WebErrorStatus.ConnectionReset
        or CoreWebView2WebErrorStatus.Disconnected;
}

/// <summary>WebView 导航去向。</summary>
public enum CommunityNavigationDecision
{
    /// <summary>没有可处理的目标（空地址）。</summary>
    None,

    /// <summary>留在应用内嵌视图里加载。</summary>
    StayEmbedded,

    /// <summary>交给系统浏览器打开（跨站链接、第三方登录等）。</summary>
    OpenExternal,

    /// <summary>拦截：非 http(s) 协议、相对地址，或社区地址未配置。</summary>
    Blocked,
}
