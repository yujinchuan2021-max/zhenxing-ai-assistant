using System;
using System.IO;
using System.Linq;
using Microsoft.Web.WebView2.Core;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Community;
using Xunit;

namespace TubaWinUi3.Tests
{
    /// <summary>
    /// 枕星AI社区（WebView2 容器 + 自托管 Discourse）的边界回归：
    ///  ① 站点地址唯一入口的取值与校验（空 = 未配置；只认 https；不猜地址；
    ///     官方目标地址 `TargetOfficialUrl` 自 2026-09-27 已由入口启用（入口必须=该地址、不许指向别处），
    ///     且必须能通过本类校验）；
    ///  ② 导航决策：站内内嵌、跨站与第三方登录交出系统浏览器、危险协议与相对地址拦截、
    ///     未配置时任何地址都不放行；
    ///  ③ 加载失败文案与「是否提示检查网络」的映射；
    ///  ④ 页面状态机（未配置 / 加载中 / 可用 / 失败）与按钮可见性；
    ///  ⑤ WebView2 数据目录的隔离规则（测试模式落 ZXAI_DATA_ROOT；不与 AI 助手页目录重合）；
    ///  ⑥ 事件/动作层（<see cref="CommunityBrowserSession"/> + <see cref="CommunityNavigationPolicy"/>）：
    ///     站外链接/OAuth 真的交给系统浏览器、同源 target=_blank 在当前视图继续、
    ///     外开目标只认"已加载的可信同源页"、非 http(s)/带凭据地址无法绕过；
    ///     以及**事件顺序**：SourceChanged(新文档) 必须先挂起、等 NavigationCompleted(成功) 才提交
    ///     （失败/取消的新文档绝不能变成「在浏览器打开」的目标；**加载中的新文档**做同文档 URL 变化时
    ///     也只更新待提交值、仍不提交，成功后提交的是该最终地址而不是绑定的请求地址）；
    ///     不同 NavigationId 的导航事件交错时，只有**当前**导航的完成事件能结算
    ///     （旧导航迟到的成功/失败回调都不得改新导航的挂起状态），且**换导航时旧导航的待提交地址必须清空**
    ///     （不得借新导航的成功回调提交旧地址；同 id 再次进入则保留该导航自己的待提交地址）。
    /// 真机渲染与真实 OAuth 回跳仍需 GUI + 站点上线（见交付报告"未验项"）。
    /// </summary>
    public class CommunitySiteTests
    {
        private const string Community = "https://community.example.com";
        private static readonly Uri CommunityOrigin = new("https://community.example.com/");

        // ---------------------------------------------------------------- ① 地址入口

        [Theory]
        [InlineData(null, null)]                       // 都没配 → 未配置
        [InlineData("", "")]
        [InlineData("   ", "\t")]
        public void Resolve_EmptyEverything_IsUnconfigured(string? overrideUrl, string? officialUrl)
            => Assert.Null(CommunitySite.Resolve(overrideUrl, officialUrl));

        [Fact]
        public void Resolve_UsesOfficialUrl_WhenNoOverride()
            => Assert.Equal(Community, CommunitySite.Resolve(null, "https://community.example.com/"));

        [Fact]
        public void Resolve_OverrideWins_OverOfficialUrl()
            => Assert.Equal("https://staging.example.net",
                CommunitySite.Resolve("https://staging.example.net/", Community));

        [Fact]
        public void Resolve_BlankOverride_FallsBackToOfficial()
            => Assert.Equal(Community, CommunitySite.Resolve("   ", Community + "/"));

        [Fact]
        public void TargetOfficialUrl_IsTheAgreedDomain_AndPassesEntryValidation()
        {
            // 用户 2026-09-24 指定的官方目标地址：记在【唯一入口】旁边，
            // 并必须能通过本类自己的 https/主机名/无凭据校验（该核对先于启用存在——避免上线当天
            // 才发现填不进去；2026-09-27 已由入口启用）。
            Assert.Equal("https://community.zhenxingai.com", CommunitySite.TargetOfficialUrl);
            Assert.Equal(CommunitySite.TargetOfficialUrl, CommunitySite.Normalize(CommunitySite.TargetOfficialUrl));
            Assert.True(CommunitySite.TryResolveOrigin(CommunitySite.TargetOfficialUrl, out var origin));
            Assert.Equal("https://community.zhenxingai.com/", origin!.AbsoluteUri);

            // 2026-09-27 起入口已启用：必须就是官方目标地址（更严：既不许留空，也不许指向别处）
            var entry = CommunitySite.OfficialUrl;
            Assert.Equal(CommunitySite.TargetOfficialUrl, entry);
            Assert.Equal(
                CommunitySite.Normalize(CommunitySite.OfficialUrl),
                CommunitySite.Resolve(null, CommunitySite.OfficialUrl));

            // 启用后（把入口或设置键指向目标地址）能正常解析出同一个地址
            Assert.Equal(CommunitySite.TargetOfficialUrl, CommunitySite.Resolve(null, CommunitySite.TargetOfficialUrl));
            Assert.Equal(CommunitySite.TargetOfficialUrl, CommunitySite.Resolve(CommunitySite.TargetOfficialUrl, ""));
        }

        [Fact]
        public void TargetOfficialUrl_NavigatesInsideItsOwnOrigin_AndKeepsOutsidersExternal()
        {
            Assert.True(CommunitySite.TryResolveOrigin(CommunitySite.TargetOfficialUrl, out var origin));
            Assert.Equal(
                CommunityNavigationDecision.StayEmbedded,
                CommunitySite.Classify(CommunitySite.TargetOfficialUrl + "/t/welcome/1", origin));
            Assert.Equal(
                CommunityNavigationDecision.OpenExternal,
                CommunitySite.Classify("https://example.org/", origin));
            // 子域名算站外（与既有口径一致）
            Assert.Equal(
                CommunityNavigationDecision.OpenExternal,
                CommunitySite.Classify("https://cdn.community.zhenxingai.com/x", origin));
        }

        [Theory]
        [InlineData("http://community.example.com")]    // 只要 https：HTTP 站点不嵌
        [InlineData("community.example.com")]           // 相对地址不猜
        [InlineData("ftp://example.com")]               // 非 web 协议
        [InlineData("javascript:alert(1)")]             // 脚本协议
        [InlineData("file:///C:/Windows")]              // 本地文件
        [InlineData("https://")]                        // 没有主机名
        [InlineData("https://user:pw@community.example.com")]  // 地址里带凭据：拒绝
        public void Normalize_RejectsAnythingButPlainHttps(string candidate)
            => Assert.Null(CommunitySite.Normalize(candidate));

        [Theory]
        [InlineData("HTTPS://COMMUNITY.EXAMPLE.COM/", "https://community.example.com")]   // 大小写归一
        [InlineData("https://community.example.com", "https://community.example.com")]   // 末尾斜杠去掉
        [InlineData("https://community.example.com/", "https://community.example.com")]
        [InlineData("https://community.example.com/forum/", "https://community.example.com/forum")]  // 子路径保留
        [InlineData("https://community.example.com:8443", "https://community.example.com:8443")]     // 非默认端口保留
        public void Normalize_KeepsUsableHttpsUrls(string candidate, string expected)
            => Assert.Equal(expected, CommunitySite.Normalize(candidate));

        [Fact]
        public void TryResolveOrigin_DerivesSchemeHostPort()
        {
            Assert.True(CommunitySite.TryResolveOrigin("https://community.example.com/forum", out var origin));
            Assert.Equal("https://community.example.com/", origin!.ToString());

            Assert.True(CommunitySite.TryResolveOrigin("https://community.example.com:8443", out var withPort));
            Assert.Equal("https://community.example.com:8443/", withPort!.ToString());

            Assert.False(CommunitySite.TryResolveOrigin("", out var none));
            Assert.Null(none);
        }

        // ---------------------------------------------------------------- ② 导航决策

        [Theory]
        [InlineData("https://community.example.com/t/topic/1")]
        [InlineData("https://community.example.com/u/profile?tab=activity")]
        [InlineData("https://community.example.com/forum/assets/logo.png")]
        [InlineData("https://COMMUNITY.EXAMPLE.COM/latest")]        // 主机大小写不敏感
        public void Classify_SameOrigin_StaysEmbedded(string url)
            => Assert.Equal(CommunityNavigationDecision.StayEmbedded, CommunitySite.Classify(url, CommunityOrigin));

        [Theory]
        [InlineData("https://discourse.org/")]                      // 站外普通链接
        [InlineData("https://github.com/discourse/discourse")]
        [InlineData("https://accounts.google.com/o/oauth2/auth")]   // 第三方登录 → 系统浏览器
        [InlineData("https://community.example.com.evil.test/")]    // 相似域名不得当作同站
        [InlineData("https://sub.community.example.com/")]          // 子域名视为站外
        [InlineData("http://community.example.com/latest")]         // 协议不同 → 不内嵌
        [InlineData("https://community.example.com:8443/")]         // 端口不同 → 不内嵌
        public void Classify_OffSite_OpensInSystemBrowser(string url)
            => Assert.Equal(CommunityNavigationDecision.OpenExternal, CommunitySite.Classify(url, CommunityOrigin));

        [Theory]
        [InlineData("javascript:void(0)")]
        [InlineData("data:text/html,<h1>x</h1>")]
        [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
        [InlineData("ms-appx:///Assets/Fonts/app-font.json")]
        [InlineData("/relative/path")]                              // 相对地址：不猜
        public void Classify_NonHttpTargets_AreBlocked(string url)
            => Assert.Equal(CommunityNavigationDecision.Blocked, CommunitySite.Classify(url, CommunityOrigin));

        [Theory]
        [InlineData("https://community.example.com/latest")]
        [InlineData("https://accounts.google.com/o/oauth2/auth")]
        [InlineData("javascript:void(0)")]
        [InlineData("/relative/path")]
        public void Classify_WhenCommunityUnconfigured_BlocksEverything(string url)
            => Assert.Equal(CommunityNavigationDecision.Blocked, CommunitySite.Classify(url, null));

        [Fact]
        public void Classify_BlankOrAboutBlank()
        {
            Assert.Equal(CommunityNavigationDecision.None, CommunitySite.Classify(null, CommunityOrigin));
            Assert.Equal(CommunityNavigationDecision.None, CommunitySite.Classify("  ", CommunityOrigin));
            // 初始化时的空白页允许内嵌（否则 WebView 永远起不来）
            Assert.Equal(CommunityNavigationDecision.StayEmbedded, CommunitySite.Classify("about:blank", null));
        }

        // ---------------------------------------------------------------- ③ 失败文案

        [Theory]
        [InlineData(CoreWebView2WebErrorStatus.HostNameNotResolved)]
        [InlineData(CoreWebView2WebErrorStatus.ServerUnreachable)]
        [InlineData(CoreWebView2WebErrorStatus.CannotConnect)]
        [InlineData(CoreWebView2WebErrorStatus.Timeout)]
        [InlineData(CoreWebView2WebErrorStatus.ConnectionAborted)]
        [InlineData(CoreWebView2WebErrorStatus.ConnectionReset)]
        [InlineData(CoreWebView2WebErrorStatus.Disconnected)]
        [InlineData(CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect)]
        [InlineData(CoreWebView2WebErrorStatus.CertificateExpired)]
        [InlineData(CoreWebView2WebErrorStatus.CertificateIsInvalid)]
        [InlineData(CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired)]
        [InlineData(CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired)]
        [InlineData(CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse)]
        [InlineData(CoreWebView2WebErrorStatus.RedirectFailed)]
        [InlineData(CoreWebView2WebErrorStatus.OperationCanceled)]
        [InlineData(CoreWebView2WebErrorStatus.Unknown)]
        public void DescribeFailure_AlwaysHasUserFacingText(CoreWebView2WebErrorStatus status)
        {
            var text = CommunitySite.Describe(status);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.EndsWith("。", text);          // 面向用户的一句话，不是异常串
        }

        [Fact]
        public void DescribeFailure_CertificateVsNetwork()
        {
            // 证书问题必须说明"已停止加载、不跳过校验"，且不提示"检查网络"（免得排查跑偏）
            var cert = CommunitySite.Describe(CoreWebView2WebErrorStatus.CertificateIsInvalid);
            Assert.Contains("证书", cert);
            Assert.False(CommunitySite.SuggestsNetworkCheck(CoreWebView2WebErrorStatus.CertificateIsInvalid));
            Assert.False(CommunitySite.SuggestsNetworkCheck(CoreWebView2WebErrorStatus.CertificateExpired));

            Assert.Contains("解析", CommunitySite.Describe(CoreWebView2WebErrorStatus.HostNameNotResolved));
            Assert.True(CommunitySite.SuggestsNetworkCheck(CoreWebView2WebErrorStatus.HostNameNotResolved));
            Assert.True(CommunitySite.SuggestsNetworkCheck(CoreWebView2WebErrorStatus.ServerUnreachable));
            Assert.True(CommunitySite.SuggestsNetworkCheck(CoreWebView2WebErrorStatus.CannotConnect));
            Assert.False(CommunitySite.SuggestsNetworkCheck(CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse));
            // 代理要求认证：属于"要先认证"，不算网络不通（提示口径不同）
            Assert.False(CommunitySite.SuggestsNetworkCheck(CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired));
        }

        // ---------------------------------------------------------------- ④ 页面状态机

        [Theory]
        [InlineData(false, false, false, CommunityViewState.Unconfigured)]   // 没配置 → 永远是"即将开放"
        [InlineData(false, true, true, CommunityViewState.Unconfigured)]
        [InlineData(true, false, true, CommunityViewState.Failed)]
        [InlineData(true, true, false, CommunityViewState.Loading)]
        [InlineData(true, false, false, CommunityViewState.Ready)]
        public void State_Matrix(bool configured, bool loading, bool failed, CommunityViewState expected)
            => Assert.Equal(expected, CommunityPageState.For(configured, loading, failed));

        [Fact]
        public void State_CopyAndButtons()
        {
            Assert.Equal("社区即将开放", CommunityPageState.TitleFor(CommunityViewState.Unconfigured));
            Assert.Equal("社区页面加载失败", CommunityPageState.TitleFor(CommunityViewState.Failed));

            // 只有需要状态面板的两个状态才有标题；加载中/可用态不占屏
            Assert.False(string.IsNullOrWhiteSpace(CommunityPageState.TitleFor(CommunityViewState.Unconfigured)));
            Assert.False(string.IsNullOrWhiteSpace(CommunityPageState.TitleFor(CommunityViewState.Failed)));
            Assert.True(string.IsNullOrEmpty(CommunityPageState.TitleFor(CommunityViewState.Ready)));
            Assert.True(string.IsNullOrEmpty(CommunityPageState.TitleFor(CommunityViewState.Loading)));

            // 未配置态：说明必须存在；不承诺开放时间，也不出现"接口/后端"这类内部说法
            var unconfigured = CommunityPageState.MessageFor(CommunityViewState.Unconfigured);
            Assert.False(string.IsNullOrWhiteSpace(unconfigured));
            Assert.DoesNotContain("即将上线", unconfigured);
            Assert.DoesNotContain("接口", unconfigured);
            Assert.DoesNotContain("后端", unconfigured);

            Assert.True(CommunityPageState.ShowsRetry(CommunityViewState.Failed));
            Assert.False(CommunityPageState.ShowsRetry(CommunityViewState.Unconfigured));
            Assert.False(CommunityPageState.ShowsRetry(CommunityViewState.Ready));
            Assert.True(CommunityPageState.ShowsOpenInBrowser(CommunityViewState.Failed));
            Assert.True(CommunityPageState.ShowsOpenInBrowser(CommunityViewState.Ready));
            Assert.False(CommunityPageState.ShowsOpenInBrowser(CommunityViewState.Unconfigured));

            Assert.Equal("具体原因", CommunityPageState.FailureMessageFor("  具体原因  "));
            Assert.False(string.IsNullOrWhiteSpace(CommunityPageState.FailureMessageFor(null)));
        }

        // ---------------------------------------------------------------- ⑤ 数据目录隔离

        [Fact]
        public void WebViewDataFolder_TestMode_UsesIsolatedRoot()
        {
            var isolated = Path.Combine(Path.GetTempPath(), "zxai-community-test-root");
            Assert.Equal(Path.Combine(isolated, "WebView2"), WebView2EnvironmentService.ResolveUserDataFolder(isolated));
        }

        [Fact]
        public void WebViewDataFolder_Production_DoesNotCollideWithAiPageDefault()
        {
            var folder = WebView2EnvironmentService.ResolveUserDataFolder(null);

            // 生产目录固定落在 %LocalAppData%\TubaWinUi3\WebView2
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.StartsWith(Path.GetFullPath(localAppData), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.Combine("TubaWinUi3", "WebView2"), folder);

            // 且不等于 exe 旁的组件默认目录（AI 助手页的 WebView 用的是后者）——两者不争用同一目录
            var exeAdjacent = Path.Combine(AppContext.BaseDirectory, "TubaWinUi3.exe.WebView2");
            Assert.NotEqual(Path.GetFullPath(exeAdjacent).TrimEnd('\\'),
                Path.GetFullPath(folder).TrimEnd('\\'));
        }

        [Fact]
        public void CommunityPage_UsesSharedEnvironmentService_NotItsOwnDataFolder()
        {
            // 社区页必须复用共享环境（这样才继承上面的隔离规则）；
            // 一旦有人改成自己 CreateWithOptionsAsync(userDataFolder:...) 就会脱离隔离，这里直接钉住。
            var source = File.ReadAllText(FindPageCodeBehindPath());
            Assert.Contains("WebView2EnvironmentService.GetAsync()", source);
            Assert.DoesNotContain("CreateWithOptionsAsync", source);
        }

        // ---------------------------------------------------------------- ⑥ 事件/动作层（会话 + 策略）
        //
        // 这一组覆盖的正是"纯函数全绿、接线却坏掉"的那类缺陷：
        //   · 站外链接/OAuth/「在浏览器打开」按钮必须真的把地址交给系统浏览器（不是被当 Blocked 丢掉）；
        //   · 同源 target=_blank 必须在当前视图继续（不是被 Handled 后静默丢弃）；
        //   · about:blank / 跨站 / 失败页绝不能成为"可外开目标"。

        /// <summary>假的系统浏览器：记录被打开的地址，可模拟打开失败。</summary>
        private sealed class FakeBrowser
        {
            public List<string> Opened { get; } = new();
            public bool Succeeds { get; set; } = true;

            public bool Open(string url)
            {
                if (!Succeeds) return false;
                Opened.Add(url);
                return true;
            }
        }

        private static CommunityBrowserSession SessionWith(FakeBrowser browser, string? configuredUrl = Community)
            => new(configuredUrl, browser.Open, _ => { });

        [Fact]
        public void Session_OffSiteLink_OpensInSystemBrowser_AndCancelsEmbedding()
        {
            // 回归：曾经这里用 Classify(url, null) 复核，null origin 让所有 http(s) 都成 Blocked，
            // 结果站外链接永远打不开（Codex 复核阻断项 1）。
            var browser = new FakeBrowser();
            var session = SessionWith(browser);

            var plan = session.OnNavigationStarting("https://github.com/discourse/discourse");

            Assert.Equal(CommunityNavigationAction.OpenExternal, plan.Action);
            Assert.True(plan.Cancels);
            Assert.Equal(new[] { "https://github.com/discourse/discourse" }, browser.Opened);
            Assert.Equal(1, session.ExternalOpenCount);
        }

        [Theory]
        [InlineData("https://accounts.google.com/o/oauth2/auth?client_id=x")]   // 第三方登录
        [InlineData("https://community.example.com.evil.test/")]               // 相似域名
        [InlineData("http://community.example.com/latest")]                    // 异协议
        [InlineData("https://sub.community.example.com/")]                     // 子域名
        public void Session_OffSiteOrUntrusted_OpensInSystemBrowser(string url)
        {
            var browser = new FakeBrowser();
            var session = SessionWith(browser);

            var plan = session.OnNavigationStarting(url);

            Assert.Equal(CommunityNavigationAction.OpenExternal, plan.Action);
            Assert.Equal(new[] { url }, browser.Opened);
        }

        [Fact]
        public void Session_SameOriginNavigation_EmbedsAndNeverOpensBrowser()
        {
            var browser = new FakeBrowser();
            var session = SessionWith(browser);

            var plan = session.OnNavigationStarting("https://community.example.com/t/topic/1");

            Assert.Equal(CommunityNavigationAction.Embed, plan.Action);
            Assert.False(plan.Cancels);
            Assert.Empty(browser.Opened);
            Assert.Equal(0, session.ExternalOpenCount);
        }

        [Fact]
        public void Session_AboutBlank_EmbedsWithoutExternalOpen()
        {
            // 初始化时的空白页必须能在视图内存在，否则 WebView 起不来；但它不是"可外开目标"（见下一组）
            var browser = new FakeBrowser();
            var session = SessionWith(browser);

            var plan = session.OnNavigationStarting("about:blank");

            Assert.Equal(CommunityNavigationAction.Embed, plan.Action);
            Assert.Empty(browser.Opened);
        }

        [Theory]
        [InlineData("javascript:void(0)")]
        [InlineData("data:text/html,<h1>x</h1>")]
        [InlineData("file:///C:/Windows/win.ini")]
        [InlineData("ms-appx:///Assets/x.json")]
        [InlineData("/relative/path")]
        public void Session_DangerousTargets_AreBlockedAndNeverOpened(string url)
        {
            var browser = new FakeBrowser();
            var session = SessionWith(browser);

            var plan = session.OnNavigationStarting(url);

            Assert.Equal(CommunityNavigationAction.Block, plan.Action);
            Assert.True(plan.Cancels);
            Assert.Empty(browser.Opened);
        }

        [Theory]
        [InlineData("https://community.example.com/latest")]
        [InlineData("https://github.com/discourse/discourse")]
        [InlineData("javascript:void(0)")]
        [InlineData("/relative/path")]
        public void Session_Unconfigured_BlocksEverythingAndNeverOpens(string url)
        {
            // 「社区即将开放」态绝不能打开任何东西——包括看起来"站外合法"的地址
            var browser = new FakeBrowser();
            var session = SessionWith(browser, null);

            var plan = session.OnNavigationStarting(url);

            Assert.Equal(CommunityNavigationAction.Block, plan.Action);
            Assert.Empty(browser.Opened);
            Assert.Equal(0, session.ExternalOpenCount);
            Assert.False(session.IsConfigured);
        }

        [Fact]
        public void Session_NewWindow_SameOrigin_EmbedsIntoCurrentView()
        {
            // 回归：曾经同源 target=_blank 被 Handled 后静默丢弃（Codex 复核阻断项 2）
            var browser = new FakeBrowser();
            var session = SessionWith(browser);

            var plan = session.OnNewWindowRequested("https://community.example.com/t/new/9");

            Assert.Equal(CommunityNavigationAction.Embed, plan.Action);
            Assert.Equal("https://community.example.com/t/new/9", plan.Target);
            Assert.Empty(browser.Opened);
        }

        [Fact]
        public void Session_NewWindow_OffSite_OpensInSystemBrowser()
        {
            var browser = new FakeBrowser();
            var session = SessionWith(browser);

            var plan = session.OnNewWindowRequested("https://example.org/interesting");

            Assert.Equal(CommunityNavigationAction.OpenExternal, plan.Action);
            Assert.Equal(new[] { "https://example.org/interesting" }, browser.Opened);
        }

        [Fact]
        public void Session_NewWindow_Unconfigured_BlocksWithoutOpening()
        {
            var browser = new FakeBrowser();
            var session = SessionWith(browser, null);

            var plan = session.OnNewWindowRequested("https://community.example.com/t/new/9");

            Assert.Equal(CommunityNavigationAction.Block, plan.Action);
            Assert.Empty(browser.Opened);
        }

        [Fact]
        public void Session_Origin_IsDerivedFromConfiguredUrl()
        {
            var configured = SessionWith(new FakeBrowser());
            Assert.True(configured.IsConfigured);
            Assert.Equal("https://community.example.com/", configured.CommunityOrigin!.ToString());
            Assert.Equal(Community, configured.ConfiguredUrl);

            var insecure = SessionWith(new FakeBrowser(), "http://insecure.test");   // 非 https：视为未配置
            Assert.False(insecure.IsConfigured);
            Assert.Null(insecure.CommunityOrigin);
            Assert.Null(insecure.ConfiguredUrl);
        }

        [Fact]
        public void BrowserButton_BeforeAnyLoad_FallsBackToConfiguredUrl()
        {
            // 回归：曾经把 about:blank 当"当前页"（Codex 复核阻断项 3）
            var session = SessionWith(new FakeBrowser());

            Assert.Null(session.LastLoadedPageUrl);
            Assert.Equal(Community, session.BrowserButtonTarget());
        }

        [Fact]
        public void BrowserButton_AfterLoad_PrefersCurrentTrustedSameOriginPage()
        {
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(1, "https://community.example.com/latest");
            session.SettleNavigation(1, isSuccess: true);
            Assert.Equal("https://community.example.com/latest", session.BrowserButtonTarget());

            // 站内软导航（pushState，同文档）：SourceChanged 可即时更新
            session.NotifySourceChanged("https://community.example.com/t/topic/42", isNewDocument: false);
            Assert.Equal("https://community.example.com/t/topic/42", session.BrowserButtonTarget());
        }

        [Fact]
        public void BrowserButton_IgnoresAboutBlankCrossOriginAndFailedLoads()
        {
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(1, "about:blank");
            session.SettleNavigation(1, isSuccess: true);
            session.NotifySourceChanged("about:blank", isNewDocument: false);
            Assert.Equal(Community, session.BrowserButtonTarget());

            session.NotifySourceChanged("https://evil.test/phish", isNewDocument: true);
            Assert.Equal(Community, session.BrowserButtonTarget());

            session.NotifySourceChanged("javascript:alert(1)", isNewDocument: false);
            Assert.Equal(Community, session.BrowserButtonTarget());

            // 加载失败的同源页也不记（避免把错误页当当前页）
            session.BeginNavigation(2, "https://community.example.com/t/broken");
            session.SettleNavigation(2, isSuccess: false);
            Assert.Equal(Community, session.BrowserButtonTarget());
        }

        [Fact]
        public void BrowserButton_Unconfigured_IsNullEvenIfWebViewHoldsSomePage()
        {
            var session = SessionWith(new FakeBrowser(), null);

            session.NotifySourceChanged("about:blank", isNewDocument: false);
            session.NotifySourceChanged("https://anywhere.test/x", isNewDocument: true);

            Assert.Null(session.BrowserButtonTarget());
        }

        // ---- 事件顺序（Codex 二次复核项 A）：SourceChanged 先于 NavigationCompleted，失败导航也会触发

        [Fact]
        public void Session_NewDocumentSourceChange_IsNotCommittedBeforeCompletion()
        {
            var session = SessionWith(new FakeBrowser());

            // 新文档只是"挂起"：还没拿到完成结果之前，不能成为外开目标
            session.NotifySourceChanged("https://community.example.com/latest", isNewDocument: true);
            Assert.Null(session.LastLoadedPageUrl);
            Assert.Equal(Community, session.BrowserButtonTarget());   // 回落配置地址
        }

        [Fact]
        public void Session_NewDocumentSourceChange_CommitsOnSuccessfulCompletion()
        {
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(1, "https://community.example.com/latest");
            session.NotifySourceChanged("https://community.example.com/latest", isNewDocument: true);
            Assert.True(session.SettleNavigation(1, isSuccess: true));

            Assert.Equal("https://community.example.com/latest", session.LastLoadedPageUrl);
            Assert.Equal("https://community.example.com/latest", session.BrowserButtonTarget());
        }

        [Fact]
        public void Session_FailedNewDocument_NeverBecomesBrowserTarget()
        {
            // 回归（Codex 二次复核项 A）：SourceChanged(新文档) → NavigationCompleted(false)
            // 曾经会把失败页当成"当前页"，与"只记录成功加载页"的承诺相反。
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(1, "https://community.example.com/latest");
            session.SettleNavigation(1, isSuccess: true);                                      // 先有一张好页
            session.BeginNavigation(2, "https://community.example.com/t/broken");
            session.NotifySourceChanged("https://community.example.com/t/broken", isNewDocument: true);
            session.SettleNavigation(2, isSuccess: false);                                     // 失败

            Assert.Equal("https://community.example.com/latest", session.LastLoadedPageUrl);
            Assert.Equal("https://community.example.com/latest", session.BrowserButtonTarget());
        }

        [Fact]
        public void Session_FailedFirstLoad_KeepsNoCurrentPage()
        {
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(1, "https://community.example.com/down");
            session.NotifySourceChanged("https://community.example.com/down", isNewDocument: true);
            session.SettleNavigation(1, isSuccess: false);

            Assert.Null(session.LastLoadedPageUrl);
            Assert.Equal(Community, session.BrowserButtonTarget());   // 只有配置地址可开
        }

        [Fact]
        public void Session_CanceledNavigation_DiscardsPendingButKeepsCurrentPage()
        {
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(1, "https://community.example.com/latest");
            session.SettleNavigation(1, isSuccess: true);

            // 取消 = 未成功：丢弃挂起项、保留上一张成功页（页面在取消分支直接返回，不额外调用任何清理 API）
            session.BeginNavigation(2, "https://community.example.com/t/never");
            session.NotifySourceChanged("https://community.example.com/t/never", isNewDocument: true);
            session.SettleNavigation(2, isSuccess: false);

            Assert.Equal("https://community.example.com/latest", session.LastLoadedPageUrl);
            Assert.Equal("https://community.example.com/latest", session.BrowserButtonTarget());
        }

        // ---- NavigationId 交错（Codex 三次复核项 C）：旧导航的完成回调不得结算新导航的挂起状态

        [Fact]
        public void Session_InterleavedStaleFailure_DoesNotClearNewNavigationPending()
        {
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(11, "https://community.example.com/a");
            session.NotifySourceChanged("https://community.example.com/a", isNewDocument: true);

            // 新导航开始（用户点了别处）
            session.BeginNavigation(12, "https://community.example.com/b");
            session.NotifySourceChanged("https://community.example.com/b", isNewDocument: true);

            // 旧导航 #11 的完成事件迟到（失败）：必须被忽略，不能清掉 #12 的挂起项
            Assert.False(session.SettleNavigation(11, isSuccess: false));
            Assert.Equal(Community, session.BrowserButtonTarget());   // 尚无成功页 → 回落配置地址

            // 新导航 #12 成功：正常提交
            Assert.True(session.SettleNavigation(12, isSuccess: true));
            Assert.Equal("https://community.example.com/b", session.BrowserButtonTarget());
        }

        [Fact]
        public void Session_InterleavedStaleSuccess_DoesNotCommitNewNavigationEarly()
        {
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(21, "https://community.example.com/a");
            session.NotifySourceChanged("https://community.example.com/a", isNewDocument: true);

            session.BeginNavigation(22, "https://community.example.com/b");
            session.NotifySourceChanged("https://community.example.com/b", isNewDocument: true);

            // 旧导航 #21 的成功回调迟到：不得把 B 提前提交（B 可能还没加载出来）
            Assert.False(session.SettleNavigation(21, isSuccess: true));
            Assert.Null(session.LastLoadedPageUrl);
            Assert.Equal(Community, session.BrowserButtonTarget());

            Assert.True(session.SettleNavigation(22, isSuccess: true));
            Assert.Equal("https://community.example.com/b", session.LastLoadedPageUrl);
        }

        [Fact]
        public void Session_StaleCompletion_DoesNotDisturbCommittedPage()
        {
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(31, "https://community.example.com/keep");
            session.SettleNavigation(31, isSuccess: true);

            // 另一条导航（#32）的完成/取消事件：既不改当前页，也不动挂起项
            // （反绕过负控：只要绑定过一次导航，就只有该 id 能结算——"未绑定"的宽松分支到此不可达）
            Assert.False(session.SettleNavigation(32, isSuccess: false));
            Assert.False(session.SettleNavigation(32, isSuccess: true));
            Assert.Equal("https://community.example.com/keep", session.BrowserButtonTarget());

            // 即便此刻存在挂起项，陈旧 id 也不能把它提交或清掉
            session.BeginNavigation(33, "https://community.example.com/pending");
            session.NotifySourceChanged("https://community.example.com/pending", isNewDocument: true);
            Assert.False(session.SettleNavigation(34, isSuccess: true));
            Assert.False(session.SettleNavigation(34, isSuccess: false));
            Assert.Equal("https://community.example.com/keep", session.LastLoadedPageUrl);

            session.SettleNavigation(33, isSuccess: true);
            Assert.Equal("https://community.example.com/pending", session.LastLoadedPageUrl);
        }

        [Fact]
        public void Session_CommitPrefersFinalDocumentUrlOverBoundUrl()
        {
            // 结算不读回调时的 Web.Source；地址优先级 = 挂起的最终文档地址 > 与该导航绑定的请求地址
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(41, "https://community.example.com/bound");
            session.NotifySourceChanged("https://community.example.com/final", isNewDocument: true);
            session.SettleNavigation(41, isSuccess: true);

            Assert.Equal("https://community.example.com/final", session.LastLoadedPageUrl);
            Assert.Equal("https://community.example.com/final", session.BrowserButtonTarget());
        }

        [Fact]
        public void Session_CommitFallsBackToBoundUrl_WhenNoSourceChangeSeen()
        {
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(42, "https://community.example.com/bound");
            session.SettleNavigation(42, isSuccess: true);

            Assert.Equal("https://community.example.com/bound", session.LastLoadedPageUrl);
        }

        // ---- 新导航不得继承旧导航的待提交地址（Codex 五次复核项 E）

        [Fact]
        public void Session_NewNavigation_DoesNotInheritPreviousPendingAddress()
        {
            // 事件顺序：Begin(11, A) → SourceChanged(A, 新文档) → Begin(12, B) → Settle(12, 成功)
            // 旧 pending A 不得借新导航的成功回调被提交；B 没有 SourceChanged 时用绑定地址 B 提交。
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(11, "https://community.example.com/a");
            session.NotifySourceChanged("https://community.example.com/a", isNewDocument: true);

            session.BeginNavigation(12, "https://community.example.com/b");

            // 11 的迟到完成事件（成功或失败）都不能改任何状态
            Assert.False(session.SettleNavigation(11, isSuccess: true));
            Assert.False(session.SettleNavigation(11, isSuccess: false));
            Assert.Null(session.LastLoadedPageUrl);

            // 12 成功 → 提交 B（绝不是 A）
            Assert.True(session.SettleNavigation(12, isSuccess: true));
            Assert.Equal("https://community.example.com/b", session.LastLoadedPageUrl);
            Assert.Equal("https://community.example.com/b", session.BrowserButtonTarget());
        }

        [Fact]
        public void Session_NewNavigationFailure_AfterPreviousPending_KeepsLastSuccessfulPage()
        {
            // P 成功 → Begin(11, A) → SourceChanged(A, 新文档) → Begin(12, B) → Settle(12, 失败) → 仍保留 P
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(1, CommittedPage);
            session.SettleNavigation(1, isSuccess: true);

            session.BeginNavigation(11, "https://community.example.com/a");
            session.NotifySourceChanged("https://community.example.com/a", isNewDocument: true);

            session.BeginNavigation(12, "https://community.example.com/b");
            session.SettleNavigation(12, isSuccess: false);

            Assert.Equal(CommittedPage, session.LastLoadedPageUrl);
            Assert.Equal(CommittedPage, session.BrowserButtonTarget());
        }

        [Fact]
        public void Session_SameNavigationId_KeepsItsOwnPending()
        {
            // 同 id 再次进入（同一条导航的后续 NavigationStarting）：该导航自己的待提交地址保留
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(21, "https://community.example.com/start");
            session.NotifySourceChanged("https://community.example.com/final", isNewDocument: true);
            session.BeginNavigation(21, "https://community.example.com/start");

            session.SettleNavigation(21, isSuccess: true);
            Assert.Equal("https://community.example.com/final", session.LastLoadedPageUrl);
        }

        // ---- 加载期间的同文档 URL 变化（Codex 四次复核项 D）：不得提前提交，成功后提交最终地址

        /// <summary>一张"已成功加载"的同源页（新文档加载期间应保持的当前页）。</summary>
        private const string CommittedPage = "https://community.example.com/latest";

        [Fact]
        public void Session_SoftNavigationWhileNewDocumentLoads_DoesNotCommit_AndFailureKeepsPreviousPage()
        {
            // 真实事件顺序：Begin(A) → SourceChanged(A, 新文档) → SourceChanged(B, 同文档, 加载期间脚本改 URL)
            //            → NavigationCompleted(失败，例如 window.stop())
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(1, CommittedPage);
            session.SettleNavigation(1, isSuccess: true);
            Assert.Equal(CommittedPage, session.LastLoadedPageUrl);

            session.BeginNavigation(2, "https://community.example.com/a");
            session.NotifySourceChanged("https://community.example.com/a", isNewDocument: true);
            session.NotifySourceChanged("https://community.example.com/b", isNewDocument: false);

            // 加载期间：仍是已提交的 P，绝不提前变成 B
            Assert.Equal(CommittedPage, session.LastLoadedPageUrl);
            Assert.Equal(CommittedPage, session.BrowserButtonTarget());

            session.SettleNavigation(2, isSuccess: false);

            // 失败之后：仍是 P；挂起项已丢弃（随后的普通软导航可立即提交）
            Assert.Equal(CommittedPage, session.LastLoadedPageUrl);
            Assert.Equal(CommittedPage, session.BrowserButtonTarget());

            session.NotifySourceChanged("https://community.example.com/c", isNewDocument: false);
            Assert.Equal("https://community.example.com/c", session.LastLoadedPageUrl);
        }

        [Fact]
        public void Session_SoftNavigationWhileNewDocumentLoads_CommitsFinalUrlOnSuccess()
        {
            var session = SessionWith(new FakeBrowser());

            session.BeginNavigation(1, CommittedPage);
            session.SettleNavigation(1, isSuccess: true);

            session.BeginNavigation(2, "https://community.example.com/a");
            session.NotifySourceChanged("https://community.example.com/a", isNewDocument: true);
            session.NotifySourceChanged("https://community.example.com/b", isNewDocument: false);

            // 完成前不得提交 B
            Assert.Equal(CommittedPage, session.LastLoadedPageUrl);
            Assert.Equal(CommittedPage, session.BrowserButtonTarget());

            session.SettleNavigation(2, isSuccess: true);

            // 完成后 = 最终可信同源地址 B（不能被绑定的 A 覆盖回去）
            Assert.Equal("https://community.example.com/b", session.LastLoadedPageUrl);
            Assert.Equal("https://community.example.com/b", session.BrowserButtonTarget());
        }

        [Fact]
        public void Session_SettleWithoutBoundNavigation_FallsBackToPending()
        {
            // 宽松路径：还没绑定过导航（理论上前端一定会先 BeginNavigation）时不丢合法完成
            var session = SessionWith(new FakeBrowser());

            session.NotifySourceChanged("https://community.example.com/lazy", isNewDocument: true);
            Assert.True(session.SettleNavigation(1, isSuccess: true));
            Assert.Equal("https://community.example.com/lazy", session.LastLoadedPageUrl);
        }

        // ---- 带账号密码的地址（Codex 二次复核项 B）

        [Theory]
        [InlineData("https://user:pw@community.example.com/latest")]
        [InlineData("https://user@community.example.com/")]
        [InlineData("https://u:p@community.example.com:443/t/topic/1")]
        public void Classify_UserInfoInTarget_IsBlocked(string url)
            => Assert.Equal(CommunityNavigationDecision.Blocked, CommunitySite.Classify(url, CommunityOrigin));

        [Fact]
        public void Session_UserInfoTarget_IsBlockedAndNeverOpened()
        {
            // 同源判定不得把 https://user:pw@<社区域名>/ 当"自己人"（否则它会留在 WebView 里）
            var browser = new FakeBrowser();
            var session = SessionWith(browser);

            var plan = session.OnNavigationStarting("https://user:pw@community.example.com/latest");

            Assert.Equal(CommunityNavigationAction.Block, plan.Action);
            Assert.True(plan.Cancels);
            Assert.Empty(browser.Opened);
        }

        [Fact]
        public void UserInfoUrl_IsNotATrustedPageNorABrowserTarget()
        {
            var session = SessionWith(new FakeBrowser());

            Assert.False(CommunityNavigationPolicy.IsTrustedSameOriginPage(
                "https://user:pw@community.example.com/latest", CommunityOrigin));

            session.NotifySourceChanged("https://user:pw@community.example.com/latest", isNewDocument: false);
            Assert.Null(session.LastLoadedPageUrl);
            Assert.Equal(Community, session.BrowserButtonTarget());
        }

        [Theory]
        [InlineData("javascript:void(0)")]
        [InlineData("data:text/html,x")]
        [InlineData("file:///C:/Windows/win.ini")]
        [InlineData("https://user:pw@community.example.com/latest")]
        [InlineData("not a url")]
        [InlineData("")]
        [InlineData(null)]
        public void OpenInSystemBrowser_RejectsAnythingButPlainHttp(string? url)
        {
            // 最终校验无法绕过：非 http(s)/带账号密码的地址即使被传进来也不会打开
            var browser = new FakeBrowser();
            var session = SessionWith(browser);

            Assert.False(session.OpenInSystemBrowser(url));
            Assert.Empty(browser.Opened);
            Assert.Equal(0, session.ExternalOpenCount);
        }

        [Fact]
        public void OpenInSystemBrowser_Failure_DoesNotThrowOrCount()
        {
            var browser = new FakeBrowser { Succeeds = false };
            var session = SessionWith(browser);

            Assert.False(session.OpenInSystemBrowser("https://example.org/x"));
            Assert.Equal(0, session.ExternalOpenCount);
        }

        // ---- 策略层纯函数（会话背后的决策）

        [Theory]
        [InlineData("https://example.com/x", true)]
        [InlineData("http://example.com/x", true)]
        [InlineData("ftp://example.com/x", false)]
        [InlineData("javascript:void(0)", false)]
        [InlineData("data:text/html,x", false)]
        [InlineData("file:///C:/x", false)]
        [InlineData("ms-appx:///Assets/x", false)]
        [InlineData("/relative", false)]
        [InlineData("https://user:pw@example.com/", false)]
        [InlineData("", false)]
        public void Policy_TryGetBrowserUrl_Matrix(string url, bool expected)
            => Assert.Equal(expected, CommunityNavigationPolicy.TryGetBrowserUrl(url, out _, out _));

        [Fact]
        public void Policy_TryGetBrowserUrl_ReportsScheme()
        {
            Assert.True(CommunityNavigationPolicy.TryGetBrowserUrl("https://example.com/", out var https, out var isHttps));
            Assert.Equal("https://example.com/", https);
            Assert.True(isHttps);

            Assert.True(CommunityNavigationPolicy.TryGetBrowserUrl("http://example.com/", out var http, out var httpIsHttps));
            Assert.Equal("http://example.com/", http);
            Assert.False(httpIsHttps);
        }

        [Fact]
        public void Policy_BrowserButtonTarget_PrefersTrustedCurrentThenConfigured()
        {
            Assert.Equal("https://community.example.com/latest",
                CommunityNavigationPolicy.BrowserButtonTarget("https://community.example.com/latest", Community, CommunityOrigin));

            // about:blank / 跨站 / 非 http 的"当前页"一律不采信 → 回落配置地址
            foreach (var current in new[] { "about:blank", "", "  ", "https://evil.test/x", "javascript:void(0)", null })
                Assert.Equal(Community, CommunityNavigationPolicy.BrowserButtonTarget(current, Community, CommunityOrigin));

            // 未配置：当前页再"正常"也不能外开
            Assert.Null(CommunityNavigationPolicy.BrowserButtonTarget("https://community.example.com/latest", null, null));
            Assert.Null(CommunityNavigationPolicy.BrowserButtonTarget(null, null, null));
        }

        [Fact]
        public void Policy_IsTrustedSameOriginPage_RequiresOrigin()
        {
            Assert.True(CommunityNavigationPolicy.IsTrustedSameOriginPage(Community + "/latest", CommunityOrigin));
            Assert.False(CommunityNavigationPolicy.IsTrustedSameOriginPage(Community + "/latest", null));
            Assert.False(CommunityNavigationPolicy.IsTrustedSameOriginPage("about:blank", CommunityOrigin));
            Assert.False(CommunityNavigationPolicy.IsTrustedSameOriginPage("https://evil.test/", CommunityOrigin));
        }

        [Fact]
        public void Page_ForwardsEventDecisionsToTheSession()
        {
            // 唯一一条"钉实现"断言（已知成本，Codex 复核时请裁决是否保留）：
            // 两个事件的转发是 WebView2 行为，单测跑不了；这条只能保证"事件进来后确实问过会话、且按动作分流"，
            // 防止有人把决定权搬回页面里（那正是这三个缺陷的成因）。
            var source = File.ReadAllText(FindPageCodeBehindPath());

            Assert.Contains("_session.OnNavigationStarting(args.Uri, sender.Source)", source);
            Assert.Contains("_session.OnNewWindowRequested(args.Uri)", source);
            Assert.Contains("CommunityNavigationAction.Embed", source);
            Assert.Contains("args.Cancel = true", source);
            Assert.Contains("Web.CoreWebView2?.Navigate(target)", source);
            Assert.Contains("_session.NotifySourceChanged(", source);
            // 事件顺序依赖的入参必须真的传下去（新文档才需要等 NavigationCompleted 结算）
            Assert.Contains("args.IsNewDocument", source);
            // 导航 id 必须绑定/结算到会话（交错防护：旧导航的完成回调不得结算新导航的挂起状态）
            Assert.Contains("_session.BeginNavigation(args.NavigationId", source);
            Assert.Contains("_session.SettleNavigation(args.NavigationId", source);
            // 页面不得再自持一份导航 id（结算口径只在会话里）
            Assert.DoesNotContain("_currentNavigationId", source);
            // 不得再出现把 origin 作用域的判定当作"外开许可"的调用
            Assert.DoesNotContain("Classify(", source);

            // Authorization POSTs must be allowed to finish their original navigation;
            // calling Navigate here would replay only the URL and discard the form body.
            var navigationStart = source.IndexOf("private void OnNavigationStarting(", StringComparison.Ordinal);
            Assert.True(navigationStart >= 0);
            var navigationEnd = source.IndexOf("private void OnNavigationCompleted(", navigationStart, StringComparison.Ordinal);
            Assert.True(navigationEnd > navigationStart);
            Assert.DoesNotContain(".Navigate(", source[navigationStart..navigationEnd]);

            // Losing the page must also revoke the temporary provider allowance.
            var leavingStart = source.IndexOf("protected override void OnNavigatedFrom(", StringComparison.Ordinal);
            Assert.True(leavingStart >= 0);
            var leavingEnd = source.IndexOf("\n    }", leavingStart, StringComparison.Ordinal);
            Assert.True(leavingEnd > leavingStart);
            Assert.Contains("_session.CancelAuthentication()", source[leavingStart..leavingEnd]);
        }

        private static string FindPageCodeBehindPath()
            => Path.Combine(FindRepoRoot(), "TubaWinUi3.WinUI3", "Pages", "CommunityHubPage.xaml.cs");

        private static string FindRepoRoot(
            [System.Runtime.CompilerServices.CallerFilePath] string? sourceFile = null)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TubaWinUi3.WinUI3", "TubaWinUi3.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            // 隔离构建（--artifacts-path 到仓库外）时用编译期源文件路径回溯
            if (!string.IsNullOrEmpty(sourceFile))
            {
                var parent = new FileInfo(sourceFile).Directory?.Parent;
                while (parent is not null)
                {
                    if (File.Exists(Path.Combine(parent.FullName, "TubaWinUi3.WinUI3", "TubaWinUi3.csproj")))
                        return parent.FullName;
                    parent = parent.Parent;
                }
            }

            throw new InvalidOperationException("未找到仓库根（缺少 TubaWinUi3.WinUI3/TubaWinUi3.csproj）");
        }
    }
}
