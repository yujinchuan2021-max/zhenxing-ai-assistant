namespace TubaWinUi3.Services.Community;

/// <summary>
/// 社区 WebView 的导航会话：<b>事件 → 动作</b>的决策与状态都放在这里（纯 .NET、无 WinUI 依赖，
/// 可直接单测），页面只做转发（<c>args.Cancel</c> / <c>CoreWebView2.Navigate</c> / 系统浏览器打开）。
///
/// 这样那些"接线错了但纯函数测试全绿"的缺陷（外开被误判成 Blocked、同源 <c>target=_blank</c> 被丢弃、
/// <c>about:blank</c> 被当成可外开页面、失败导航抢走"当前页"、旧导航的完成回调结算新导航的挂起状态）
/// 都会在窄测试里被抓住——因为它们现在都必须走这一层。
///
/// 可信 origin <b>由本类自己从已配置地址推导</b>（不接受调用方传入），从根上排掉"传错 origin"导致的
/// 同源/外开误判。本类不持有任何凭据、不接触 cookie/token：只决定"这个地址该在哪打开"。
///
/// 关于"当前视图内的导航"（WebView2 允许不同 NavigationId 的导航事件交错）：
///   · 页面在 <see cref="CommunityNavigationAction.Embed"/> 分支调用 <see cref="BeginNavigation"/>，
///     把该导航的 <b>id 与地址</b>绑定进来；
///   · <see cref="SettleNavigation"/> 只结算<b>当前</b>导航的完成事件，其它 id 的完成事件一律忽略——
///     否则旧导航迟到的完成回调会清掉/提交新导航的挂起状态（"当前页"就会记错）；
///   · 因此"当前页"始终来自<b>与该完成事件同一个导航</b>的地址，而不是回调时"随手读到的 Web.Source"。
/// </summary>
public sealed class CommunityBrowserSession
{
    private readonly Func<string, bool> _openExternal;
    private readonly Action<string> _log;
    private readonly CommunityAuthenticationScope _authentication;
    private string? _currentTopLevelUrl;
    private bool _navigationInProgress;

    /// <param name="configuredUrl">已配置的社区地址（null/非法 = 未配置）。</param>
    /// <param name="openExternal">
    /// 实际"交系统浏览器"的动作（页面注入 <c>Process.Start</c>；测试注入记录器）。
    /// 返回是否真的打开了；异常由本类吞掉并记日志。
    /// </param>
    /// <param name="log">可选日志出口（默认 <c>Debug.WriteLine</c>）。</param>
    public CommunityBrowserSession(
        string? configuredUrl,
        Func<string, bool>? openExternal = null,
        Action<string>? log = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ConfiguredUrl = CommunitySite.Normalize(configuredUrl);
        CommunityOrigin = DeriveOrigin(ConfiguredUrl);
        _openExternal = openExternal ?? (_ => false);
        _log = log ?? (message => System.Diagnostics.Debug.WriteLine($"[CommunityBrowserSession] {message}"));
        _authentication = new(configuredUrl, utcNow);
    }

    /// <summary>已配置的社区地址（归一化后；未配置为 null）。</summary>
    public string? ConfiguredUrl { get; }

    /// <summary>可信社区 origin（由配置地址推导；未配置或地址不合法时为 null）。</summary>
    public Uri? CommunityOrigin { get; }

    /// <summary>是否已配置可用地址（未配置 → 页面显示「社区即将开放」，不存在任何可外开目标）。</summary>
    public bool IsConfigured => ConfiguredUrl is not null && CommunityOrigin is not null;

    public bool IsAuthenticationActive => _authentication.IsActive;
    public bool AuthenticationRestartRequired => _authentication.RestartRequired;
    public string? AuthenticationRestartUrl => _authentication.RestartUrl;
    public void CancelAuthentication() => _authentication.Cancel();

    /// <summary>最近一次"已加载且可信"的社区同源页面地址（用于「在浏览器打开」；未加载时为 null）。</summary>
    public string? LastLoadedPageUrl { get; private set; }

    /// <summary>本会话真正交出去给系统浏览器的次数（诊断用，也让测试能断言"一次都没打开"）。</summary>
    public int ExternalOpenCount { get; private set; }

    /// <summary>当前视图内导航的 id（由 <see cref="BeginNavigation"/> 绑定；尚无导航时为 null）。</summary>
    public ulong? ActiveNavigationId { get; private set; }

    /// <summary>与该导航绑定的地址（每次调用 <c>BeginNavigation</c> 时更新；重定向会带来新的导航）。</summary>
    public string? ActiveDocumentUrl { get; private set; }

    /// <summary>尚未提交的新文档地址（SourceChanged(新文档) 之后、该导航成功完成之前）。</summary>
    private string? _pendingDocumentUrl;

    /// <summary>上面这个"待提交地址"所属的导航 id（SourceChanged 事件本身不带 id，只能记下到达时的当前导航）。</summary>
    private ulong? _pendingOwnerNavigationId;

    // ---------------------------------------------------------------- 事件入口

    /// <summary>NavigationStarting：返回动作计划；站外目标会在本方法内直接交系统浏览器。</summary>
    public CommunityNavigationPlan OnNavigationStarting(string? targetUrl, string? sourcePageUrl = null)
    {
        if (_authentication.Enabled)
        {
            // The caller supplies the actual top-level WebView source, never a stale loaded-page fallback.
            if (_authentication.TryBegin(targetUrl, sourcePageUrl))
                return CommunityNavigationPlan.Embed(targetUrl!.Trim());

            if (_authentication.IsActive && CommunityAuthenticationScope.IsProviderPage(targetUrl))
                return CommunityNavigationPlan.Embed(targetUrl!.Trim());

            bool authContext = _authentication.ProtectsContinuation
                || CommunityAuthenticationScope.IsSensitivePage(sourcePageUrl)
                || CommunityAuthenticationScope.IsSensitivePage(_currentTopLevelUrl);
            if (CommunityAuthenticationScope.IsOfficialPage(targetUrl))
                return CommunityNavigationPlan.Embed(targetUrl!.Trim());

            if (authContext || CommunityAuthenticationScope.IsProviderPage(targetUrl))
                return RequireAuthenticationRestart();
        }

        return ApplyExternal(CommunityNavigationPolicy.ForNavigation(targetUrl, CommunityOrigin));
    }

    /// <summary>
    /// NewWindowRequested：同源 → <see cref="CommunityNavigationAction.Embed"/>（调用方在当前视图打开，
    /// 应用内永不开第二个窗口）；站外 → 交系统浏览器；其余 → 拦截。
    /// </summary>
    public CommunityNavigationPlan OnNewWindowRequested(string? targetUrl)
    {
        if (_authentication.Enabled && (_authentication.ProtectsContinuation
            || CommunityAuthenticationScope.IsSensitivePage(_currentTopLevelUrl)
            || IsPendingAuthenticationDocument()
            || CommunityAuthenticationScope.IsSensitivePage(targetUrl)))
            return RequireAuthenticationRestart();

        return ApplyExternal(CommunityNavigationPolicy.ForNewWindow(targetUrl, CommunityOrigin));
    }

    /// <summary>
    /// 绑定"当前视图内导航"：页面在 Embed 分支调用（<c>args.NavigationId</c> + <c>args.Uri</c>）。
    /// 之后只有这个 id 的完成事件才会被 <see cref="SettleNavigation"/> 结算。
    /// <b>换了一条导航（不同 id）就丢弃上一条留下的"待提交地址"</b>——否则旧导航的地址会借新导航的
    /// 成功回调被提交（`Begin(11,A) → SourceChanged(A,新文档) → Begin(12,B) → Settle(12,成功)` 会记成 A）。
    /// 同 id 再次进入（同一条导航的后续 NavigationStarting）按该导航自己的最终可信 URL 处理，不清。
    /// </summary>
    public void BeginNavigation(ulong navigationId, string? targetUrl)
    {
        if ((ActiveNavigationId is { } active && active != navigationId)
            || (_authentication.ProtectsContinuation && _pendingDocumentUrl is not null
                && !string.Equals(_pendingDocumentUrl, targetUrl, StringComparison.Ordinal)))
        {
            _pendingDocumentUrl = null;
            _pendingOwnerNavigationId = null;
        }

        ActiveNavigationId = navigationId;
        ActiveDocumentUrl = targetUrl?.Trim();
        _navigationInProgress = true;
    }

    /// <summary>
    /// SourceChanged：
    ///   · <b>新文档</b>（<paramref name="isNewDocument"/> = true）→ 挂起，等该导航成功完成再提交；
    ///   · <b>同文档软导航</b>（pushState/replaceState）且已有挂起项 → 那是**加载中的新文档**在自己改 URL
    ///     （WebView2 的 DOMContentLoaded 早于 NavigationCompleted，页面脚本可在加载期间改同文档 URL）
    ///     → 更新挂起值（这才是该文档的最终地址），**仍不提交**；
    ///   · 同文档软导航且无挂起项 → 当前文档早已加载完成 → 立即提交。
    /// 只接受可信同源地址：不可信的一律忽略（既不改当前页，也不动挂起项）。
    /// </summary>
    public void NotifySourceChanged(string? pageUrl, bool isNewDocument)
    {
        bool communityPage = CommunityNavigationPolicy.IsTrustedSameOriginPage(pageUrl, CommunityOrigin);
        bool providerPage = _authentication.Enabled && _authentication.ProtectsContinuation
            && CommunityAuthenticationScope.IsProviderPage(pageUrl);
        if (!communityPage && !providerPage) return;

        var url = pageUrl!.Trim();
        _currentTopLevelUrl = url;

        if (isNewDocument || _pendingDocumentUrl is not null)
        {
            _pendingDocumentUrl = url;                          // 挂起/更新待提交地址：等该导航的完成结果
            _pendingOwnerNavigationId = ActiveNavigationId;      // 记下它属于哪条导航（SourceChanged 无 id）
            return;
        }

        if (communityPage)
        {
            LastLoadedPageUrl = url;      // 已加载完成文档上的软导航
            // The previous document can still change its history after the login POST starts.
            // Until that navigation settles, its soft URL change is not a return from the provider.
            if (!_navigationInProgress) _authentication.ReturnedToCommunity();
        }
    }

    /// <summary>
    /// NavigationCompleted 的结算（<b>先按 id 过滤，再结算</b>）：
    ///   · 返回 false = 该完成事件不属于当前导航（交错/迟到的旧回调）→ 调用方连 UI 状态都不该改；
    ///   · 成功 → 提交"当前页"，地址优先级：**属于本导航的待提交最终地址**（加载期间的同文档变化会更新它，
    ///     那才是文档真正的最终 URL）→ 与该导航绑定的请求地址（没有 SourceChanged 的兜底）；
    ///   · 失败/取消 → 丢弃待提交地址，<b>保留</b>上一张成功加载的页面（"当前页" = 最近一次成功加载）。
    /// 待提交地址必须<b>属于正在结算的这条导航</b>（`_pendingOwnerNavigationId`）才可用：换导航时已清空，
    /// 这里再兜一层，避免旧导航的地址被新导航的成功回调提交。
    /// 未绑定过导航时（理论上前端一定会先 BeginNavigation）按"宽松"处理，不丢合法完成；一旦绑定过就只认该 id。
    /// </summary>
    public bool SettleNavigation(ulong navigationId, bool isSuccess)
    {
        if (ActiveNavigationId is { } active && navigationId != active)
        {
            _log($"忽略非当前导航 (#{navigationId}，当前 #{active}) 的完成事件");
            return false;
        }

        var ownsPending = _pendingDocumentUrl is not null && _pendingOwnerNavigationId == ActiveNavigationId;
        var candidate = (ownsPending ? _pendingDocumentUrl : null) ?? ActiveDocumentUrl;
        _navigationInProgress = false;

        if (!isSuccess)
        {
            _pendingDocumentUrl = null;
            _pendingOwnerNavigationId = null;
            if (_authentication.ProtectsContinuation) _authentication.RequireRestart();
            _log("本次导航未成功，不更新当前页");
            return true;
        }

        if (CommunityNavigationPolicy.IsTrustedSameOriginPage(candidate, CommunityOrigin))
        {
            LastLoadedPageUrl = candidate!.Trim();
            _currentTopLevelUrl = LastLoadedPageUrl;
            _authentication.ReturnedToCommunity(); // Navigation only; this is not evidence of a signed-in account.
        }
        else if (_authentication.Enabled && CommunityAuthenticationScope.IsProviderPage(candidate))
            _currentTopLevelUrl = candidate;

        _pendingDocumentUrl = null;
        _pendingOwnerNavigationId = null;
        return true;
    }

    // ---------------------------------------------------------------- 动作入口

    /// <summary>「在浏览器打开」按钮的目标：当前可信同源页优先，否则配置地址；都没有 → null。</summary>
    public string? BrowserButtonTarget()
        => MustRestartExternalAuthentication(null) ? AuthenticationRestartUrl
            : CommunityNavigationPolicy.BrowserButtonTarget(LastLoadedPageUrl, ConfiguredUrl, CommunityOrigin);

    /// <summary>
    /// 交系统浏览器（<b>最终校验在这里，无法绕过</b>）：只放行绝对 http(s) 且不带账号密码的地址；
    /// 打不开只记日志/计数，不抛出、不打断页面。
    /// </summary>
    public bool OpenInSystemBrowser(string? url)
    {
        // Check the original argument first: malformed schemes/userinfo remain rejected, even during login.
        if (!CommunityNavigationPolicy.TryGetBrowserUrl(url, out var safeUrl, out _))
        {
            _log("拒绝交给系统浏览器（非 http(s) 或含凭据）");
            return false;
        }

        if (MustRestartExternalAuthentication(safeUrl))
        {
            safeUrl = AuthenticationRestartUrl;
            _authentication.Cancel();
        }

        try
        {
            if (_openExternal(safeUrl!))
            {
                ExternalOpenCount++;
                return true;
            }

            _log("系统浏览器未能打开");
            return false;
        }
        catch (Exception)
        {
            _log("打开系统浏览器异常");
            return false;
        }
    }

    // ---------------------------------------------------------------- 内部

    private CommunityNavigationPlan ApplyExternal(CommunityNavigationPlan plan)
    {
        if (plan.Action is CommunityNavigationAction.OpenExternal && plan.Target is { } target)
            OpenInSystemBrowser(target);
        else if (plan.Action is CommunityNavigationAction.Block)
            _log("已拦截导航");

        return plan;
    }

    private CommunityNavigationPlan RequireAuthenticationRestart()
    {
        _authentication.RequireRestart();
        _log("登录需要从社区登录页重新开始，未外送中间授权地址");
        return CommunityNavigationPlan.RestartAuthentication(AuthenticationRestartUrl!);
    }

    private bool MustRestartExternalAuthentication(string? requestedUrl)
        => _authentication.Enabled && (_authentication.ProtectsContinuation
            || CommunityAuthenticationScope.IsSensitivePage(requestedUrl)
            || CommunityAuthenticationScope.IsSensitivePage(_currentTopLevelUrl)
            || IsPendingAuthenticationDocument()
            || CommunityAuthenticationScope.IsSensitivePage(LastLoadedPageUrl)
            || CommunityAuthenticationScope.IsSensitivePage(ConfiguredUrl));

    private bool IsPendingAuthenticationDocument()
        => (_navigationInProgress || _currentTopLevelUrl is null)
           && CommunityAuthenticationScope.IsSensitivePage(ActiveDocumentUrl);

    private static Uri? DeriveOrigin(string? normalizedConfiguredUrl)
        => normalizedConfiguredUrl is not null
           && CommunitySite.TryResolveOrigin(normalizedConfiguredUrl, out var origin)
            ? origin
            : null;
}
