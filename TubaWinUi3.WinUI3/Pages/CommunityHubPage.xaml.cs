using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Community;

namespace TubaWinUi3.Pages;

/// <summary>
/// 枕星AI社区（左导航顶级入口，硬件信息下方）。
///
/// 形态：应用内 WebView2 容器，打开自托管 <b>Discourse</b>（开源 GPL-2.0）的 HTTPS 站点。
/// 帖子/账号/审核/举报全部由 Discourse 负责——本页不实现这些逻辑，也不持有凭据、
/// 不抓 cookie/token、不跳过证书校验。
///
/// 本页只做转发：所有"事件 → 动作"的判断都在 <see cref="CommunityBrowserSession"/> /
/// <see cref="CommunityNavigationPolicy"/>（纯 .NET，可单测）里；本页负责把结果落到
/// <c>args.Cancel</c> / <c>CoreWebView2.Navigate</c> / 系统浏览器打开上。
///
/// 边界：
///   · 地址唯一入口 = <see cref="CommunitySite.OfficialUrl"/>（可被设置键覆盖）；为空 → 「社区即将开放」，
///     <b>不初始化 WebView2、不导航到任何地址</b>；
///   · 普通跨站链接交给系统浏览器；官方社区授权仅在短期受信任范围内保留于同一 WebView；
///   · 同源 <c>target=_blank</c> → 在当前视图继续（应用内永不开第二个窗口）；
///   · 非 http(s) 协议、相对地址、带账号密码的地址一律拦截；
///   · HTTPS 证书不通过 → 停止加载（不接管 ServerCertificateErrorDetected）。
///
/// 数据目录：复用共享的 <see cref="WebView2EnvironmentService"/>（已支持 ZXAI_DATA_ROOT 隔离），
/// 与 AI 助手页（组件库自建环境、目录在 exe 旁）不争用同一目录。
/// </summary>
public sealed partial class CommunityHubPage : Page, ILocalizablePage
{
    private CommunityBrowserSession _session = new(null);
    private bool _webViewHooked;
    private CommunityViewState _state = CommunityViewState.Unconfigured;
    private Func<string>? _failureTextFactory;
    private bool _visible;
    private bool _appearanceUpdateQueued;
    private bool _authenticationFallbackVisible;

    public CommunityHubPage()
    {
        InitializeComponent();
        ApplyToolbarAccessibility();
        ActualThemeChanged += (_, _) => QueueCommunityAppearance();
        Loaded += (_, _) => QueueCommunityAppearance();
        Unloaded += (_, _) =>
        {
            _visible = false;
            _session.CancelAuthentication();
        };
        ApplyState(CommunityViewState.Unconfigured);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _visible = true;
        _ = EnsureCommunityAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _visible = false;
        _session.CancelAuthentication();
        base.OnNavigatedFrom(e);
    }

    // ---------------------------------------------------------------- 初始化与状态

    /// <summary>
    /// 解析社区地址并按需初始化 WebView2。
    /// 未配置（或地址不合法）时直接进入「社区即将开放」，一次导航都不发起。
    /// </summary>
    private async Task EnsureCommunityAsync()
    {
        // 会话自己从配置地址推导可信 origin（不接受外部传入，避免"传错 origin"这类接线错误）
        _session.CancelAuthentication();
        _session = new CommunityBrowserSession(CommunitySite.ResolveConfiguredUrl(), OpenExternalInSystemBrowser);

        if (!_session.IsConfigured)
        {
            ApplyState(CommunityViewState.Unconfigured);
            return;
        }

        ApplyState(CommunityViewState.Loading);

        try
        {
            if (!_webViewHooked)
            {
                await Web.EnsureCoreWebView2Async(await WebView2EnvironmentService.GetAsync());
                await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(CommunityAppearanceBridge.DocumentScript);
                HookWebViewEvents();
                _webViewHooked = true;
            }

            ApplyCommunityAppearance();
            Web.CoreWebView2.Navigate(_session.ConfiguredUrl!);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommunityHubPage] WebView2 初始化失败: {ex.Message}");
            SetFailure(() => LocalizationService.L("Community_FailWebViewInit", MiscTexts.T("WebView2 初始化失败：这台机器上可能缺少 WebView2 运行时，或嵌入组件不可用。")));
        }
    }

    private void HookWebViewEvents()
    {
        var core = Web.CoreWebView2;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.SourceChanged += OnSourceChanged;
        core.HistoryChanged += (_, _) => UpdateHeaderButtons();
        core.ProcessFailed += OnProcessFailed;
        core.WebMessageReceived += OnAppearanceMessage;
    }

    private void OnAppearanceMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (CommunityAppearanceBridge.IsReady(args.Source, args.WebMessageAsJson)) ApplyCommunityAppearance();
    }

    private void ApplyCommunityAppearance()
    {
        if (!_visible || Web.CoreWebView2 is not { } core) return;
        bool dark = ActualTheme == ElementTheme.Dark;
        try
        {
            core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            if (!CommunityAppearanceBridge.IsOfficial(core.Source)) return;
            core.PostWebMessageAsJson(BuildCommunityAppearanceJson());
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CommunityHubPage] appearance sync: {ex.Message}"); }
    }

    private void QueueCommunityAppearance()
    {
        if (_appearanceUpdateQueued) return;
        _appearanceUpdateQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _appearanceUpdateQueued = false;
            // ThemeResource expressions finish resolving before the native colors are sent.
            ApplyCommunityAppearance();
        });
    }

    // Shared with the isolated native fixture; reading the connected XAML probes never initializes
    // the browser, reads settings or navigates. Every brush comes from the same page theme.
    internal string BuildCommunityAppearanceJson()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        static string Read(Microsoft.UI.Xaml.Media.Brush source, string background, string fallback)
        {
            if (source is not Microsoft.UI.Xaml.Media.SolidColorBrush brush) return fallback;
            var color = brush.Color;
            return CommunityAppearanceBridge.Flatten(color.A, color.R, color.G, color.B, background);
        }
        string fallbackCanvas = dark ? "#212123" : "#fcfbf9";
        string canvas = Read(Root.Background, fallbackCanvas, fallbackCanvas);
        return CommunityAppearanceBridge.StateJson(dark, canvas,
            Read(AppearanceSurfaceProbe.Background, canvas, dark ? "#2b2b2e" : "#ffffff"),
            Read(AppearanceTextProbe.Foreground, canvas, dark ? "#ededf0" : "#2b2b30"),
            Read(AppearanceMutedProbe.Foreground, canvas, dark ? "#b0afb7" : "#62646d"),
            Read(AppearanceLineProbe.BorderBrush, canvas, dark ? "#3d3b3b" : "#ddd9d4"),
            Read(AppearanceAccentProbe.Background, canvas, dark ? "#ff8567" : "#d85135"),
            Read(AppearanceAccentTextProbe.Foreground, canvas, "#ffffff"));
    }

    /// <summary>单一状态出口：显示哪一块、按钮可不可点，全部由这里决定。</summary>
    private void ApplyState(CommunityViewState state)
    {
        _state = state;
        if (state != CommunityViewState.Failed) _authenticationFallbackVisible = false;

        var showWeb = state is CommunityViewState.Ready or CommunityViewState.Loading;
        Web.Visibility = showWeb && _webViewHooked ? Visibility.Visible : Visibility.Collapsed;
        StatePanel.Visibility = showWeb ? Visibility.Collapsed : Visibility.Visible;

        LoadingRing.IsActive = state == CommunityViewState.Loading;

        if (state == CommunityViewState.Unconfigured)
        {
            StateGlyph.Glyph = "\uE774";   // 地球：站点尚未开放
            StateTitle.Text = CommunityPageState.TitleFor(state);
            StateMessage.Text = CommunityPageState.MessageFor(state);
        }
        else if (state == CommunityViewState.Failed)
        {
            StateGlyph.Glyph = "\uE783";   // 感叹号圆环
            StateTitle.Text = CommunityPageState.TitleFor(state);
            // 失败原因由 SetFailure 写入（网络/证书/进程等各自不同）
        }

        RetryButton.Visibility = !_authenticationFallbackVisible && CommunityPageState.ShowsRetry(state)
            ? Visibility.Visible : Visibility.Collapsed;
        StateOpenInBrowserButton.Visibility = CommunityPageState.ShowsOpenInBrowser(state) ? Visibility.Visible : Visibility.Collapsed;
        ReturnCommunityButton.Visibility = _authenticationFallbackVisible ? Visibility.Visible : Visibility.Collapsed;
        ApplyAuthenticationLabels();

        UpdateHeaderButtons();
    }

    public void ApplyLocalization()
    {
        ApplyToolbarAccessibility();
        ApplyAuthenticationLabels();
        if (_authenticationFallbackVisible)
        {
            return;
        }
        if (_state is not (CommunityViewState.Unconfigured or CommunityViewState.Failed)) return;

        StateTitle.Text = CommunityPageState.TitleFor(_state);
        StateMessage.Text = _state == CommunityViewState.Failed
            ? CommunityPageState.FailureMessageFor(_failureTextFactory?.Invoke())
            : CommunityPageState.MessageFor(_state);
    }

    private void ApplyToolbarAccessibility()
    {
        var english = LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BackButton,
            english ? "Back within the community" : "社区网页后退");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ForwardButton,
            english ? "Forward within the community" : "社区网页前进");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RefreshButton,
            english ? "Refresh the community page" : "刷新社区网页");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OpenInBrowserButton,
            LocalizationService.L("Community_OpenInBrowser.Text", english ? "Open in browser" : "在浏览器中打开"));
    }

    private void SetFailure(Func<string> factory)
    {
        _authenticationFallbackVisible = false;
        _failureTextFactory = factory;
        StateMessage.Text = CommunityPageState.FailureMessageFor(factory());
        ApplyState(CommunityViewState.Failed);
    }

    private void ShowAuthenticationRestart()
    {
        _authenticationFallbackVisible = true;
        ApplyState(CommunityViewState.Failed);
    }

    private void ApplyAuthenticationLabels()
    {
        bool english = LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage;
        StateOpenInBrowserLabel.Text = _authenticationFallbackVisible
            ? (english ? "Restart login in browser" : "在浏览器重新登录")
            : LocalizationService.L("Community_OpenInBrowser.Text", english ? "Open in browser" : "在浏览器中打开");
        ReturnCommunityLabel.Text = english ? "Return to community" : "返回社区";
        if (!_authenticationFallbackVisible) return;
        StateTitle.Text = english ? "Continue login in your browser" : "请在浏览器中继续登录";
        StateMessage.Text = english
            ? "This sign-in cannot continue in the app. Restart from the community login page in your browser. Login there applies only to that browser."
            : "这次登录无法在客户端内继续。请在浏览器从社区登录页重新开始；登录完成后，仅在该浏览器中生效。";
    }

    private void UpdateHeaderButtons()
    {
        var core = Web.CoreWebView2;
        var ready = core is not null && _state is CommunityViewState.Ready or CommunityViewState.Loading;

        BackButton.IsEnabled = ready && core!.CanGoBack;
        ForwardButton.IsEnabled = ready && core!.CanGoForward;

        // 目标由会话决定：已加载的可信同源页优先，否则配置地址；都没有 → 按钮不可点
        var hasExternalTarget = _session.BrowserButtonTarget() is not null;
        OpenInBrowserButton.IsEnabled = hasExternalTarget;
        StateOpenInBrowserButton.IsEnabled = hasExternalTarget;
    }

    // ---------------------------------------------------------------- WebView 事件（只转发）

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!_visible)
        {
            _session.CancelAuthentication();
            args.Cancel = true;
            return;
        }
        var plan = _session.OnNavigationStarting(args.Uri, sender.Source);

        switch (plan.Action)
        {
            case CommunityNavigationAction.Embed:
                // Do not replay this URL: the original navigation can be the login form's POST.
                // 把这次导航的 id 与地址绑定进会话：之后只有这个 id 的完成事件才参与结算
                _session.BeginNavigation(args.NavigationId, args.Uri);
                ApplyState(CommunityViewState.Loading);
                break;

            case CommunityNavigationAction.RestartAuthentication:
                args.Cancel = true;
                ShowAuthenticationRestart();
                break;

            case CommunityNavigationAction.OpenExternal:
            case CommunityNavigationAction.Block:
                args.Cancel = true;   // 不在应用内加载（外开/拦截都必须取消）
                break;

            default:
                break;                // Ignore：空地址，什么都不做
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        // 交错防护：WebView2 允许不同 NavigationId 的导航事件交错，旧导航迟到的完成回调
        // 不得结算（清除/提交）新导航的挂起状态，也不该改 UI 状态 —— 会话按 id 过滤，
        // 返回 false 表示"这不是当前导航的完成事件"。
        var isCurrentNavigation = _session.SettleNavigation(args.NavigationId, args.IsSuccess);
        if (!isCurrentNavigation) return;
        if (_authenticationFallbackVisible || _session.AuthenticationRestartRequired)
        {
            ShowAuthenticationRestart();
            return;
        }

        if (args.IsSuccess)
        {
            ApplyCommunityAppearance();
            ApplyState(CommunityViewState.Ready);
            return;
        }

        // 主动取消（例如上面拦截外链）不算失败；会话已按"未成功"结算：丢弃挂起项、保留上一张成功页
        if (args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;

        SetFailure(() => CommunitySite.Describe(args.WebErrorStatus));
    }

    private void OnSourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
    {
        // 同文档软导航（pushState）可即时更新"当前页"；新文档先挂起，等 NavigationCompleted 报成功
        // 才提交（WebView2 事件顺序：SourceChanged 先于 NavigationCompleted，失败导航也会触发它）。
        _session.NotifySourceChanged(Web.CoreWebView2?.Source?.ToString(), args.IsNewDocument);
        UpdateHeaderButtons();
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        if (!_visible)
        {
            _session.CancelAuthentication();
            args.Handled = true;
            return;
        }
        var plan = _session.OnNewWindowRequested(args.Uri);   // 站外目标在这一步就已交给系统浏览器

        args.Handled = true;   // 应用内永不开第二个窗口

        if (plan.Action is CommunityNavigationAction.RestartAuthentication)
        {
            ShowAuthenticationRestart();
            return;
        }

        if (plan.Action is CommunityNavigationAction.Embed && plan.Target is { } target)
        {
            // 同源 target=_blank：在当前视图继续（Discourse 站内链接常见写法）
            Web.CoreWebView2?.Navigate(target);
            return;
        }

        if (plan.Action is CommunityNavigationAction.Block)
            System.Diagnostics.Debug.WriteLine("[CommunityHubPage] 已拦截新窗口请求");
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine($"[CommunityHubPage] WebView2 进程异常: {args.ProcessFailedKind}");
        _session.CancelAuthentication();
        SetFailure(() => LocalizationService.L("Community_FailProcessFailed", MiscTexts.T("社区页面渲染进程异常退出，点「重试」可以重新加载。")));
    }

    // ---------------------------------------------------------------- 页头动作

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2?.CanGoBack == true) Web.CoreWebView2.GoBack();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2?.CanGoForward == true) Web.CoreWebView2.GoForward();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_authenticationFallbackVisible)
        {
            ReturnCommunityButton_Click(sender, e);
            return;
        }
        if (Web.CoreWebView2 is not null && _session.IsConfigured)
        {
            Web.CoreWebView2.Reload();
            return;
        }

        _ = EnsureCommunityAsync();   // 还没初始化（或之前初始化失败）时，重走一次初始化
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = EnsureCommunityAsync();

    private void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        var target = _authenticationFallbackVisible ? _session.AuthenticationRestartUrl : _session.BrowserButtonTarget();
        bool restarting = target is not null && target == _session.AuthenticationRestartUrl;
        if (_session.OpenInSystemBrowser(target) && restarting) ShowAuthenticationRestart();
    }

    private void ReturnCommunityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.AuthenticationRestartUrl is null) return;
        _session.CancelAuthentication();
        // /login immediately redirects back to the identity provider; return really means the home page.
        Web.CoreWebView2?.Navigate(CommunitySite.TargetOfficialUrl);
    }

    /// <summary>
    /// 真正的"交系统浏览器"动作（注入给会话；最终校验在会话里，这里只负责启动进程）。
    /// 失败只记日志，不打断页面。
    /// </summary>
    private static bool OpenExternalInSystemBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception)
        {
            System.Diagnostics.Debug.WriteLine("[CommunityHubPage] 打开外部浏览器失败");
            return false;
        }
    }
}
