using TubaWinUi3.Services.Community;

namespace TubaWinUi3.Tests;

/// <summary>
/// Authorization navigation uses synthetic URLs, a fake clock and an external-open recorder.
/// No WebView, account, cookie store, HTTP request or browser process is created here.
/// </summary>
public sealed class CommunityAuthenticationTests
{
    private const string Community = CommunitySite.TargetOfficialUrl;
    private const string Login = Community + "/login";
    private const string Start = Community + "/auth/discourse_id";
    private const string Provider = "https://id.discourse.com";
    private const string StateMarker = "SYNTHETIC_STATE_MUST_NOT_LEAK";
    private const string CodeMarker = "SYNTHETIC_CODE_MUST_NOT_LEAK";
    private const string Authorize = Provider + "/oauth/authorize?client_id=synthetic&state=" + StateMarker;
    private const string Callback = Start + "/callback?code=" + CodeMarker + "&state=" + StateMarker;

    [Theory]
    [InlineData("")]
    [InlineData("?signup=true&email=synthetic%40example.invalid")]
    public void TrustedLoginAndRegistration_KeepOriginalNavigationWithoutReplay(string query)
    {
        var fixture = new Fixture();
        var target = Start + query;

        var plan = fixture.Session.OnNavigationStarting(target, Login);

        Assert.Equal(CommunityNavigationAction.Embed, plan.Action);
        Assert.False(plan.Cancels);
        Assert.Equal(target, plan.Target);
        Assert.True(fixture.Session.IsAuthenticationActive);
        Assert.Empty(fixture.Opened);
        Assert.Equal(0, fixture.Session.ExternalOpenCount);
        // The decision lets WebView finish its existing GET or POST. It does not ask
        // the page to reconstruct a request from this URL and lose the POST body.
    }

    [Theory]
    [InlineData(null)]
    [InlineData("about:blank")]
    [InlineData("https://example.invalid/login")]
    [InlineData("https://community.zhenxingai.com.evil.invalid/login")]
    [InlineData("https://community.zhenxingai.com:8443/login")]
    [InlineData("http://community.zhenxingai.com/login")]
    [InlineData("https://name:password@community.zhenxingai.com/login")]
    [InlineData(Callback)]
    public void AuthStart_WithoutTrustedSourceCannotGrantProviderEmbedding(string? source)
    {
        var fixture = new Fixture();

        fixture.Session.OnNavigationStarting(Start, source);

        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.NotEqual(CommunityNavigationAction.Embed,
            fixture.Session.OnNavigationStarting(Authorize, source).Action);
        Assert.False(fixture.Session.IsAuthenticationActive);
    }

    [Theory]
    [InlineData("https://community.zhenxingai.com.evil.invalid/auth/discourse_id")]
    [InlineData("https://sub.community.zhenxingai.com/auth/discourse_id")]
    [InlineData("https://community.zhenxingai.com:8443/auth/discourse_id")]
    [InlineData("http://community.zhenxingai.com/auth/discourse_id")]
    [InlineData("https://name:password@community.zhenxingai.com/auth/discourse_id")]
    [InlineData("https://community.zhenxingai.com/auth/discourse_id_other")]
    [InlineData("https://community.zhenxingai.com/t/auth/discourse_id")]
    public void AuthStart_RequiresExactCommunityOriginAndRoute(string target)
    {
        var fixture = new Fixture();

        fixture.Session.OnNavigationStarting(target, Login);

        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.NotEqual(CommunityNavigationAction.Embed,
            fixture.Session.OnNavigationStarting(Authorize, target).Action);
    }

    [Theory]
    [InlineData("https://id.discourse.com/oauth/authorize")]
    [InlineData("https://ID.DISCOURSE.COM:443/login")]
    [InlineData("https://id.discourse.com/signup")]
    public void ActiveScope_AllowsExactHttpsProviderInSameView(string target)
    {
        var fixture = new Fixture();
        fixture.StartAuthentication();

        var plan = fixture.Session.OnNavigationStarting(target, Start);

        Assert.Equal(CommunityNavigationAction.Embed, plan.Action);
        Assert.Equal(target, plan.Target);
        Assert.False(plan.Cancels);
        Assert.True(fixture.Session.IsAuthenticationActive);
        Assert.Empty(fixture.Opened);
        Assert.Equal(0, fixture.Session.ExternalOpenCount);
    }

    [Theory]
    [InlineData("http://id.discourse.com/login")]
    [InlineData("https://id.discourse.com:8443/login")]
    [InlineData("https://id.discourse.com.evil.invalid/login")]
    [InlineData("https://sub.id.discourse.com/login")]
    [InlineData("https://name:password@id.discourse.com/login")]
    [InlineData("https://id.discourse.com@evil.invalid/login")]
    public void ActiveScope_DoesNotAuthorizeProviderLookalikesOrCredentials(string target)
    {
        var fixture = new Fixture();
        fixture.StartAuthentication();

        var plan = fixture.Session.OnNavigationStarting(target, Authorize);

        Assert.NotEqual(CommunityNavigationAction.Embed, plan.Action);
        Assert.True(plan.Cancels);
        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.Empty(fixture.Opened);
        Assert.Equal(0, fixture.Session.ExternalOpenCount);
    }

    [Fact]
    public void ProviderNavigation_WithoutStartDoesNotGainTemporaryTrust()
    {
        var fixture = new Fixture();

        var plan = fixture.Session.OnNavigationStarting(Authorize, Login);

        Assert.NotEqual(CommunityNavigationAction.Embed, plan.Action);
        Assert.True(plan.Cancels);
        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.Empty(fixture.Opened);
    }

    [Theory]
    [InlineData("https://staging.example.invalid")]
    [InlineData("https://community.zhenxingai.com:8443")]
    [InlineData(null)]
    public void AlternateOrUnconfiguredCommunity_CannotBorrowOfficialIdScope(string? configured)
    {
        var fixture = new Fixture(configured);
        var source = configured is null ? Login : configured + "/login";
        var start = configured is null ? Start : configured + "/auth/discourse_id";

        fixture.Session.OnNavigationStarting(start, source);

        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.Null(fixture.Session.AuthenticationRestartUrl);
        Assert.NotEqual(CommunityNavigationAction.Embed,
            fixture.Session.OnNavigationStarting(Authorize, start).Action);
    }

    [Theory]
    [InlineData("/auth/discourse_id/callback?code=synthetic&state=synthetic")]
    [InlineData("/auth/failure?message=csrf_detected&strategy=discourse_id")]
    [InlineData("/latest")]
    public void ReturningToCommunity_ClosesScopeWithoutClaimingLogin(string path)
    {
        var fixture = new Fixture();
        fixture.ReachProvider();
        var target = Community + path;

        var plan = fixture.Session.OnNavigationStarting(target, Authorize);
        Assert.Equal(CommunityNavigationAction.Embed, plan.Action);
        Assert.False(plan.Cancels);
        fixture.Session.BeginNavigation(2, target);
        Assert.True(fixture.Session.SettleNavigation(2, isSuccess: true));

        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.Empty(fixture.Opened);
        Assert.NotEqual(CommunityNavigationAction.Embed,
            fixture.Session.OnNavigationStarting(Authorize, target).Action);
    }

    [Fact]
    public void FailedOrCancelledNavigation_RevokesProviderScope()
    {
        var fixture = new Fixture();
        fixture.StartAuthentication();
        fixture.Session.OnNavigationStarting(Authorize, Start);
        fixture.Session.BeginNavigation(1, Authorize);

        Assert.True(fixture.Session.SettleNavigation(1, isSuccess: false));

        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.NotEqual(CommunityNavigationAction.Embed,
            fixture.Session.OnNavigationStarting(Authorize, Start).Action);
        Assert.Empty(fixture.Opened);
    }

    [Fact]
    public void PreviousDocumentSoftNavigation_DoesNotFinishPendingAuthorization()
    {
        var fixture = new Fixture();
        fixture.Session.BeginNavigation(0, Login);
        fixture.Session.NotifySourceChanged(Login, isNewDocument: true);
        Assert.True(fixture.Session.SettleNavigation(0, isSuccess: true));
        fixture.StartAuthentication();
        fixture.Session.BeginNavigation(1, Start);

        // The original document can still replace its history while the login POST
        // is in flight and before the provider's new-document SourceChanged arrives.
        var oldDocument = Login + "#old-history";
        fixture.Session.NotifySourceChanged(oldDocument, isNewDocument: false);

        Assert.True(fixture.Session.IsAuthenticationActive);
        var provider = fixture.Session.OnNavigationStarting(Authorize, oldDocument);
        Assert.Equal(CommunityNavigationAction.Embed, provider.Action);
        Assert.False(provider.Cancels);
        fixture.Session.BeginNavigation(1, Authorize);
        fixture.Session.NotifySourceChanged(Authorize, isNewDocument: true);
        Assert.True(fixture.Session.SettleNavigation(1, isSuccess: true));
        Assert.True(fixture.Session.IsAuthenticationActive);

        var callback = fixture.Session.OnNavigationStarting(Callback, Authorize);
        Assert.Equal(CommunityNavigationAction.Embed, callback.Action);
        fixture.Session.BeginNavigation(2, Callback);
        fixture.Session.NotifySourceChanged(Callback, isNewDocument: true);
        Assert.True(fixture.Session.IsAuthenticationActive);
        Assert.True(fixture.Session.SettleNavigation(2, isSuccess: true));

        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.False(fixture.Session.AuthenticationRestartRequired);
        Assert.Empty(fixture.Opened);
        Assert.Equal(0, fixture.Session.ExternalOpenCount);
    }

    [Fact]
    public void StaleFailureCannotCancelNewerAuthenticationNavigation()
    {
        var fixture = new Fixture();
        fixture.StartAuthentication();
        fixture.Session.BeginNavigation(1, Start);
        fixture.Session.OnNavigationStarting(Authorize, Start);
        fixture.Session.BeginNavigation(2, Authorize);

        Assert.False(fixture.Session.SettleNavigation(1, isSuccess: false));

        Assert.True(fixture.Session.IsAuthenticationActive);
        Assert.Equal(CommunityNavigationAction.Embed,
            fixture.Session.OnNavigationStarting(Provider + "/login", Authorize).Action);
        Assert.Empty(fixture.Opened);
    }

    [Fact]
    public void LeavingOrCancellingPage_ClearsScopeAndPendingRestart()
    {
        var fixture = new Fixture();
        fixture.ReachProvider();

        fixture.Session.CancelAuthentication();

        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.False(fixture.Session.AuthenticationRestartRequired);
        Assert.NotEqual(CommunityNavigationAction.Embed,
            fixture.Session.OnNavigationStarting(Authorize, Start).Action);
        fixture.Session.CancelAuthentication();
        Assert.False(fixture.Session.AuthenticationRestartRequired);
        fixture.StartAuthentication();
        Assert.True(fixture.Session.IsAuthenticationActive);
    }

    [Fact]
    public void ScopeExpiresAtItsDeadline_ProviderStepsDoNotRenewIt()
    {
        var fixture = new Fixture();
        fixture.StartAuthentication();
        fixture.Now += CommunityAuthenticationScope.AuthenticationLifetime - TimeSpan.FromTicks(1);

        Assert.Equal(CommunityNavigationAction.Embed,
            fixture.Session.OnNavigationStarting(Provider + "/login", Authorize).Action);
        Assert.True(fixture.Session.IsAuthenticationActive);
        fixture.Now += TimeSpan.FromTicks(1);

        var plan = fixture.Session.OnNavigationStarting(Authorize, Provider + "/login");
        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.NotEqual(CommunityNavigationAction.Embed, plan.Action);
        Assert.True(plan.Cancels);
        Assert.Empty(fixture.Opened);
    }

    [Fact]
    public void OfficialAuthFailurePage_CanStartFreshAuthorizationOnRetry()
    {
        var fixture = new Fixture();
        fixture.ReachProvider();
        var failure = Community + "/auth/failure?message=csrf_detected&strategy=discourse_id";
        fixture.Session.OnNavigationStarting(failure, Authorize);
        fixture.Session.BeginNavigation(2, failure);
        fixture.Session.NotifySourceChanged(failure, isNewDocument: true);
        fixture.Session.SettleNavigation(2, isSuccess: true);
        Assert.False(fixture.Session.IsAuthenticationActive);

        var retry = fixture.Session.OnNavigationStarting(Start, failure);

        Assert.Equal(CommunityNavigationAction.Embed, retry.Action);
        Assert.False(retry.Cancels);
        Assert.True(fixture.Session.IsAuthenticationActive);
        Assert.False(fixture.Session.AuthenticationRestartRequired);
        Assert.Empty(fixture.Opened);
    }

    [Theory]
    [InlineData("https://accounts.google.com/o/oauth2/auth?state=synthetic")]
    [InlineData("https://github.com/login/oauth/authorize?state=synthetic")]
    [InlineData("https://account.apple.com/auth/authorize?state=synthetic")]
    [InlineData("https://www.facebook.com/dialog/oauth?state=synthetic")]
    [InlineData("https://unknown-provider.example.invalid/login?state=synthetic")]
    public void UnverifiedUpstream_RestartsFromCleanLoginInsteadOfSplittingSession(string target)
    {
        var fixture = new Fixture();
        fixture.ReachProvider();

        AssertRestart(fixture, fixture.Session.OnNavigationStarting(target, Provider + "/login"));
    }

    [Theory]
    [InlineData(Start)]
    [InlineData(Authorize)]
    [InlineData(Callback)]
    [InlineData("https://unknown-provider.example.invalid/login?state=synthetic")]
    public void AuthenticationPopup_NeverReplaysUrlAsGetOrOpensExternal(string target)
    {
        var fixture = new Fixture();
        fixture.ReachProvider();

        AssertRestart(fixture, fixture.Session.OnNewWindowRequested(target));
    }

    [Fact]
    public void AuthStartPopupWithoutScope_RequiresCleanRestart()
    {
        var fixture = new Fixture();

        AssertRestart(fixture, fixture.Session.OnNewWindowRequested(Start + "?signup=true"));
    }

    [Theory]
    [InlineData("/auth/discourse_id/callback?code=SYNTHETIC_CODE_MUST_NOT_LEAK&state=SYNTHETIC_STATE_MUST_NOT_LEAK")]
    [InlineData("/auth/failure?message=csrf_detected&state=SYNTHETIC_STATE_MUST_NOT_LEAK")]
    [InlineData("/auth/other-provider/callback?code=SYNTHETIC_CODE_MUST_NOT_LEAK")]
    [InlineData("/%61uth/discourse_id/callback?code=SYNTHETIC_CODE_MUST_NOT_LEAK")]
    public void BrowserButtonAndFinalOpenBoundary_NeverExportCallbackParameters(string path)
    {
        var fixture = new Fixture();
        var target = Community + path;
        fixture.Session.BeginNavigation(9, target);
        fixture.Session.NotifySourceChanged(target, isNewDocument: true);
        fixture.Session.SettleNavigation(9, isSuccess: true);

        Assert.Equal(Login, fixture.Session.BrowserButtonTarget());
        Assert.True(fixture.Session.OpenInSystemBrowser(target));

        Assert.Equal(new[] { Login }, fixture.Opened);
        Assert.Equal(1, fixture.Session.ExternalOpenCount);
        AssertNoSensitiveLogs(fixture.Logs);
    }

    [Fact]
    public void ExplicitBrowserOpenDuringAuthorization_StartsAgainAndRevokesScope()
    {
        var fixture = new Fixture();
        fixture.ReachProvider();
        Assert.Equal(Login, fixture.Session.BrowserButtonTarget());

        Assert.True(fixture.Session.OpenInSystemBrowser(Authorize));

        Assert.Equal(new[] { Login }, fixture.Opened);
        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.Equal(1, fixture.Session.ExternalOpenCount);
        AssertNoSensitiveLogs(fixture.Logs);
    }

    [Fact]
    public void DeniedAndFailedAuthenticationPaths_DoNotWriteSensitiveUrlsToLogs()
    {
        var fixture = new Fixture();
        fixture.ReachProvider();
        fixture.Session.OnNavigationStarting("https://unknown.example.invalid/auth?state=" + StateMarker,
            Authorize);
        fixture.Session.OpenInSystemBrowser("https://name:" + CodeMarker + "@id.discourse.com/login?state=" + StateMarker);
        fixture.Session.BeginNavigation(11, Callback);
        fixture.Session.NotifySourceChanged(Callback, isNewDocument: true);
        fixture.Session.SettleNavigation(11, isSuccess: false);

        AssertNoSensitiveLogs(fixture.Logs);
    }

    [Fact]
    public void BrowserLauncherFailure_DoesNotLogExceptionCredentialsOrClaimOpened()
    {
        var logs = new List<string>();
        var session = new CommunityBrowserSession(Community,
            _ => throw new InvalidOperationException(Callback), logs.Add);

        Assert.False(session.OpenInSystemBrowser(Callback));

        Assert.Equal(0, session.ExternalOpenCount);
        Assert.NotEmpty(logs);
        AssertNoSensitiveLogs(logs);
    }

    [Fact]
    public void OrdinaryLinksAndSameOriginPopups_RetainExistingBehavior()
    {
        var fixture = new Fixture();
        const string external = "https://example.invalid/article?topic=community";
        var article = Community + "/t/welcome/1";

        var embedded = fixture.Session.OnNavigationStarting(article, Login);
        Assert.Equal(CommunityNavigationAction.Embed, embedded.Action);
        Assert.False(embedded.Cancels);
        Assert.Equal(CommunityNavigationAction.Embed, fixture.Session.OnNewWindowRequested(article).Action);
        Assert.False(fixture.Session.IsAuthenticationActive);
        var outside = fixture.Session.OnNavigationStarting(external, article);
        Assert.Equal(CommunityNavigationAction.OpenExternal, outside.Action);
        Assert.True(outside.Cancels);
        Assert.Equal(new[] { external }, fixture.Opened);
        Assert.Equal(1, fixture.Session.ExternalOpenCount);
    }

    [Fact]
    public void CompletedReturnToOrdinaryCommunityPage_RestoresOrdinaryLinkBehavior()
    {
        var fixture = new Fixture();
        fixture.ReachProvider();
        var latest = Community + "/latest";
        fixture.Session.OnNavigationStarting(latest, Authorize);
        fixture.Session.BeginNavigation(2, latest);
        fixture.Session.NotifySourceChanged(latest, isNewDocument: true);
        fixture.Session.SettleNavigation(2, isSuccess: true);

        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.Equal(latest, fixture.Session.BrowserButtonTarget());
        const string external = "https://example.invalid/article";
        Assert.Equal(CommunityNavigationAction.OpenExternal,
            fixture.Session.OnNavigationStarting(external, latest).Action);
        Assert.Equal(new[] { external }, fixture.Opened);
    }

    private static void AssertRestart(Fixture fixture, CommunityNavigationPlan plan)
    {
        Assert.Equal(CommunityNavigationAction.RestartAuthentication, plan.Action);
        Assert.True(plan.Cancels);
        Assert.Equal(Login, plan.Target);
        Assert.Equal(Login, fixture.Session.AuthenticationRestartUrl);
        Assert.True(fixture.Session.AuthenticationRestartRequired);
        Assert.False(fixture.Session.IsAuthenticationActive);
        Assert.Empty(fixture.Opened);
        Assert.Equal(0, fixture.Session.ExternalOpenCount);
        AssertNoSensitiveLogs(fixture.Logs);
    }

    private static void AssertNoSensitiveLogs(IEnumerable<string> logs)
    {
        var text = string.Join("\n", logs);
        Assert.DoesNotContain(StateMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeMarker, text, StringComparison.Ordinal);
    }

    private sealed class Fixture
    {
        internal DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        internal List<string> Opened { get; } = [];
        internal List<string> Logs { get; } = [];
        internal CommunityBrowserSession Session { get; }

        internal Fixture(string? configured = Community)
            => Session = new(configured, url => { Opened.Add(url); return true; }, Logs.Add, () => Now);

        internal void StartAuthentication()
        {
            var plan = Session.OnNavigationStarting(Start, Login);
            Assert.Equal(CommunityNavigationAction.Embed, plan.Action);
            Assert.False(plan.Cancels);
            Assert.True(Session.IsAuthenticationActive);
        }

        internal void ReachProvider()
        {
            StartAuthentication();
            Session.BeginNavigation(1, Start);
            Assert.Equal(CommunityNavigationAction.Embed, Session.OnNavigationStarting(Authorize, Start).Action);
            Session.BeginNavigation(1, Authorize);
            Session.NotifySourceChanged(Authorize, isNewDocument: true);
            Assert.True(Session.SettleNavigation(1, isSuccess: true));
            Assert.True(Session.IsAuthenticationActive);
        }
    }
}
