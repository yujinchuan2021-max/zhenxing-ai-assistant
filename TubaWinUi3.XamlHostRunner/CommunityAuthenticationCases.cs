using System.Reflection;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Community;
using Windows.Storage.Streams;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// The actual community page and WebView event wiring, with every request answered in memory.
/// Only an isolated test profile and synthetic cookies/forms are used; no login service is contacted.
/// </summary>
internal static class CommunityAuthenticationCases
{
    internal static (string, Func<Task>)[] All() =>
    [
        ("Community_Authentication_LoginAndSignup_PostCookieRoundTrip_Offline", PostCookieRoundTrip),
        ("Community_Authentication_UnsupportedProvider_RestartsCleanly_Offline", UnsupportedProvider),
    ];

    private static async Task PostCookieRoundTrip()
    {
        foreach (var signup in new[] { false, true })
        {
            await WithFixture(signup, async fixture =>
            {
                await fixture.SubmitAsync();
                Assert.True(fixture.Session.IsAuthenticationActive);
                Assert.Empty(fixture.External);
                var post = Assert.Single(fixture.Requests.Where(row => row.Path == "/auth/discourse_id"));
                Assert.Equal("POST", post.Method);
                Assert.Contains("authenticity_token=offline_form_only", post.Body);
                Assert.Contains("fixture_action=" + (signup ? "signup" : "login"), post.Body);
                Assert.Equal(signup, post.Query.Contains("signup=true", StringComparison.Ordinal));
                Assert.Contains(fixture.Cookie, post.Cookies);
                var provider = Assert.Single(fixture.Requests.Where(row => row.Host == "id.discourse.com"
                    && row.Path == "/__offline_authorize" && row.Context == CoreWebView2WebResourceContext.Document));
                Assert.Equal("GET", provider.Method);
                Assert.All(fixture.Requests.Where(row => row.Host == "id.discourse.com"),
                    row => Assert.DoesNotContain(fixture.Cookie, row.Cookies));

                await fixture.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('complete').click();");
                await fixture.WaitForDocumentAsync(fixture.CallbackUrl);
                var callback = Assert.Single(fixture.Requests.Where(row => row.Path == "/auth/discourse_id/callback"));
                Assert.Equal("GET", callback.Method);
                Assert.Contains(fixture.Cookie, callback.Cookies);
                Assert.Single(fixture.Requests.Where(row => row.Path == "/auth/discourse_id"));
                Assert.All(fixture.Requests.Where(row => row.Host == "id.discourse.com"),
                    row => Assert.DoesNotContain(fixture.Cookie, row.Cookies));
                Assert.Empty(fixture.External);
                Assert.False(fixture.Session.IsAuthenticationActive);
                Assert.False(fixture.Session.AuthenticationRestartRequired);
                Assert.Equal(Visibility.Visible, fixture.Web.Visibility);
                Assert.Equal(Visibility.Collapsed, fixture.Element<FrameworkElement>("StatePanel").Visibility);
                // Even after the callback has settled, the toolbar must not export its query.
                Assert.Equal(CommunitySite.OfficialUrl + "/login", fixture.Session.BrowserButtonTarget());
            });
        }
    }

    private static async Task UnsupportedProvider()
    {
        await WithFixture(false, async fixture =>
        {
            await fixture.SubmitAsync();
            Assert.True(fixture.Session.IsAuthenticationActive);
            await fixture.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('upstream').click();");
            await fixture.WaitUntilAsync(() => fixture.Session.AuthenticationRestartRequired
                && fixture.Element<FrameworkElement>("StatePanel").Visibility == Visibility.Visible);
            // Collect the corresponding cancellation/completion events before interpreting any
            // WebResourceRequested observation. Interception itself is not a server response.
            await Task.Delay(150);
            fixture.AssertUpstreamNavigationCanceled();
            Assert.Empty(fixture.External);
            Assert.False(fixture.Session.IsAuthenticationActive);
            Assert.Equal(CommunitySite.OfficialUrl + "/login", fixture.Session.AuthenticationRestartUrl);
            Assert.Equal(Visibility.Collapsed, fixture.Element<Button>("RetryButton").Visibility);
            var restart = fixture.Element<Button>("StateOpenInBrowserButton");
            var back = fixture.Element<Button>("ReturnCommunityButton");
            Assert.Equal(Visibility.Visible, restart.Visibility);
            Assert.Equal(Visibility.Visible, back.Visibility);
            Assert.True(restart.IsEnabled);

            InvokeButton(restart);
            await fixture.WaitUntilAsync(() => fixture.External.Count == 1);
            Assert.Equal(CommunitySite.OfficialUrl + "/login", Assert.Single(fixture.External));
            // The page must not infer a client login from an external-browser launch.
            Assert.Equal(Visibility.Visible, fixture.Element<FrameworkElement>("StatePanel").Visibility);
            InvokeButton(back);
            await fixture.WaitForDocumentAsync(CommunitySite.OfficialUrl + "/");
            Assert.False(fixture.Session.IsAuthenticationActive);
            Assert.False(fixture.Session.AuthenticationRestartRequired);
            Assert.Single(fixture.External);
            Assert.Single(fixture.Requests.Where(row => row.Path == "/auth/discourse_id"));
        });
    }

    private static async Task WithFixture(bool signup, Func<Fixture, Task> body)
    {
        Assert.NotNull(DataRoots.TestRoot);
        Assert.Equal(Path.GetFullPath(Path.Combine(DataRoots.TestRoot!, "WebView2")),
            Path.GetFullPath(WebView2EnvironmentService.UserDataFolder));
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
        {
            // No Frame navigation: OnNavigatedTo/EnsureCommunityAsync never reads application settings.
            var page = new CommunityHubPage { Width = 900, Height = 650, RequestedTheme = ElementTheme.Light };
            root.Children.Add(page);
            var fixture = new Fixture(page, signup);
            try
            {
                await fixture.InitializeAsync();
                await body(fixture);
                fixture.ThrowIfRequestFailed();
            }
            finally
            {
                fixture.Session.CancelAuthentication();
                Set(page, "_visible", false);
                root.Children.Remove(page);
                fixture.Web.Close();
                fixture.DisposeResponses();
            }
        });
    }

    private sealed record Request(string Host, string Path, string Query, string Method, string Body, string Cookies,
        CoreWebView2WebResourceContext Context);

    private sealed class Fixture
    {
        private readonly CommunityHubPage _page;
        private readonly bool _signup;
        private readonly List<IRandomAccessStream> _responses = [];
        private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
        private readonly List<(string Url, bool Canceled)> _navigationStarts = [];
        private readonly List<string> _sourceChanges = [];
        private Exception? _requestFailure;
        private CoreWebView2Environment? _environment;
        private readonly string _home;
        private readonly string _provider;
        private int _eventSequence;
        internal readonly List<Request> Requests = [];
        internal readonly List<string> External = [];
        internal readonly CommunityBrowserSession Session;
        internal readonly WebView2 Web;
        internal readonly string Cookie = "zxai_offline_auth_" + Guid.NewGuid().ToString("N") + "=synthetic_only";
        internal string CallbackUrl { get; }

        internal Fixture(CommunityHubPage page, bool signup)
        {
            _page = page;
            _signup = signup;
            var scenario = signup ? "signup" : "login";
            _home = CommunitySite.OfficialUrl + "/__offline_auth_" + scenario;
            _provider = "https://id.discourse.com/__offline_authorize?fixture=" + scenario;
            CallbackUrl = CommunitySite.OfficialUrl + "/auth/discourse_id/callback?state=offline_only&code=synthetic_only";
            Web = Element<WebView2>("Web");
            Session = new CommunityBrowserSession(CommunitySite.OfficialUrl, url => { External.Add(url); return true; }, _ => { });
        }

        internal T Element<T>(string name) where T : class => Assert.IsAssignableFrom<T>(_page.FindName(name));

        internal async Task InitializeAsync()
        {
            _environment = await WebView2EnvironmentService.GetAsync();
            await Web.EnsureCoreWebView2Async(_environment);
            var core = Web.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnRequest;
            core.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess) _loaded.Add(core.Source);
            };
            Set(_page, "_session", Session);
            Set(_page, "_visible", true);
            Set(_page, "_webViewHooked", true);
            Invoke(_page, "HookWebViewEvents");
            // Subscribe after the real page so Cancel reflects its final navigation decision.
            core.NavigationStarting += (_, args) =>
            {
                _navigationStarts.Add((args.Uri, args.Cancel));
                Trace("NavigationStarting(afterPage)", args.Uri,
                    $"id={args.NavigationId};cancel={args.Cancel};redirect={args.IsRedirected};source={SafeAddress(core.Source)}");
            };
            core.NavigationCompleted += (_, args) => Trace("NavigationCompleted(afterPage)", core.Source,
                $"id={args.NavigationId};success={args.IsSuccess};error={args.WebErrorStatus}");
            core.SourceChanged += (_, args) =>
            {
                _sourceChanges.Add(core.Source);
                Trace("SourceChanged(afterPage)", core.Source, $"newDocument={args.IsNewDocument}");
            };
            core.Navigate(_home);
            await WaitForDocumentAsync(_home);
            Assert.Empty(External);
            // HttpOnly cookie cannot be read by document script; request observations below are server-side.
            var scriptCookie = await core.ExecuteScriptAsync("document.cookie");
            Assert.DoesNotContain(Cookie, scriptCookie);
        }

        internal async Task SubmitAsync()
        {
            await Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('auth').submit();");
            await WaitForDocumentAsync(_provider);
        }

        private async void OnRequest(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                var request = args.Request;
                var uri = new Uri(request.Uri);
                string Header(string name) => request.Headers.Contains(name) ? request.Headers.GetHeader(name) : "";
                Trace("WebResourceRequested", request.Uri,
                    $"method={request.Method};context={args.ResourceContext};purpose={Header("Purpose")};secPurpose={Header("Sec-Purpose")};fetchDest={Header("Sec-Fetch-Dest")};source={SafeAddress(sender.Source)}");
                var body = "";
                if (request.Content is { } content && content.Size > 0)
                {
                    using var reader = new DataReader(content.GetInputStreamAt(0));
                    await reader.LoadAsync(checked((uint)content.Size));
                    var bytes = new byte[checked((int)content.Size)];
                    reader.ReadBytes(bytes);
                    body = Encoding.UTF8.GetString(bytes);
                }
                Requests.Add(new(uri.Host, uri.AbsolutePath, uri.Query, request.Method, body,
                    request.Headers.Contains("Cookie") ? request.Headers.GetHeader("Cookie") : "", args.ResourceContext));
                if (uri.Host == "community.zhenxingai.com" && (uri.AbsolutePath.StartsWith("/__offline_auth_", StringComparison.Ordinal)
                    || uri.AbsolutePath == "/"))
                {
                    Respond(args, 200, "OK", HomeHtml(), "Set-Cookie: " + Cookie + "; Path=/; HttpOnly; Secure; SameSite=Lax\r\n");
                }
                else if (uri.Host == "community.zhenxingai.com" && uri.AbsolutePath == "/auth/discourse_id")
                    Respond(args, 302, "Found", "", "Location: " + _provider + "\r\n");
                else if (uri.Host == "id.discourse.com" && uri.AbsolutePath == "/__offline_authorize")
                    Respond(args, 200, "OK", "<!doctype html><html><body>Offline identity provider<a id='complete' href='" + CallbackUrl
                        + "'>Complete</a><a id='upstream' href='https://accounts.google.com/__offline_authorize?state=synthetic_only'>Other provider</a></body></html>");
                else if (uri.Host == "community.zhenxingai.com" && uri.AbsolutePath == "/auth/discourse_id/callback")
                    Respond(args, 200, "OK", "<!doctype html><html><body>Offline callback received</body></html>");
                else
                    Respond(args, 404, "Offline", "");
            }
            catch (Exception ex)
            {
                _requestFailure ??= ex;
                // Fail closed even if fixture processing failed: no intercepted request reaches the network.
                Respond(args, 500, "Offline fixture failed", "");
            }
            finally { deferral.Complete(); }
        }

        private string HomeHtml() => "<!doctype html><html><body><form id='auth' method='POST' action='/auth/discourse_id"
            + (_signup ? "?signup=true" : "")
            + "'><input type='hidden' name='authenticity_token' value='offline_form_only'>"
            + "<input type='hidden' name='fixture_action' value='" + (_signup ? "signup" : "login")
            + "'></form></body></html>";

        private void Respond(CoreWebView2WebResourceRequestedEventArgs args, int status, string reason, string html, string headers = "")
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(html)).AsRandomAccessStream();
            _responses.Add(stream);
            args.Response = _environment!.CreateWebResourceResponse(stream, status, reason,
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n" + headers);
            Trace("MemoryResponse", args.Request.Uri, $"status={status};context={args.ResourceContext}");
        }

        private void Trace(string kind, string? url, string details) => Console.WriteLine(
            $"AUTH_EVENT|{(_signup ? "signup" : "login")}|{++_eventSequence}|{kind}|{SafeAddress(url)}|{details}");

        private static string SafeAddress(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Scheme + "://" + uri.Host + uri.AbsolutePath : "(no-document)";

        internal Task WaitForDocumentAsync(string url) => WaitUntilAsync(() => _loaded.Contains(url)
            && Web.CoreWebView2.Source == url);

        internal void AssertUpstreamNavigationCanceled()
        {
            // WebView2 documents that optimized GET requests can occur while the host handles
            // NavigationStarting, even when Cancel=true. Thus WebResourceRequested alone cannot
            // prove a committed navigation or zero network GETs in production. This fixture still
            // answers every such request in memory; the production contract is cancellation,
            // unchanged source/no committed upstream document and no external-browser continuation.
            // https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2navigationstartingeventargs
            static bool IsUpstream(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Host == "accounts.google.com" && uri.AbsolutePath == "/__offline_authorize";
            var start = Assert.Single(_navigationStarts.Where(item => IsUpstream(item.Url)));
            Assert.True(start.Canceled, "The actual page must cancel the unsupported provider navigation.");
            Assert.Equal(_provider, Web.CoreWebView2.Source);
            Assert.DoesNotContain(_loaded, IsUpstream);
            Assert.DoesNotContain(_sourceChanges, IsUpstream);
        }

        internal async Task WaitUntilAsync(Func<bool> condition)
        {
            for (var i = 0; i < 150; i++)
            {
                ThrowIfRequestFailed();
                if (condition()) return;
                await Task.Delay(50);
            }
            throw new TimeoutException("Offline community authentication fixture did not reach the expected state.");
        }

        internal void ThrowIfRequestFailed()
        {
            if (_requestFailure is not null) throw new InvalidOperationException("Offline response fixture failed.", _requestFailure);
        }

        internal void DisposeResponses()
        {
            foreach (var response in _responses) response.Dispose();
        }
    }

    private static void InvokeButton(Button button)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(button) ?? FrameworkElementAutomationPeer.CreatePeerForElement(button);
        Assert.IsAssignableFrom<IInvokeProvider>(peer!.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static void Set(CommunityHubPage page, string name, object? value) =>
        (typeof(CommunityHubPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing page field: " + name)).SetValue(page, value);

    private static void Invoke(CommunityHubPage page, string name) =>
        (typeof(CommunityHubPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing page method: " + name)).Invoke(page, null);
}
