using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using TubaWinUi3.Services;
using TubaWinUi3.Services.AiNews;

namespace TubaWinUi3.Pages;

public sealed partial class AiNewsPage : Page, ILocalizablePage
{
    private readonly AiNewsFeedSession _session;
    private bool _visible;
    private bool _hooked;
    private bool _initializing;
    private bool _ready;
    private bool _needsRecreate;
    private ulong _navigationId;
    private int _webGeneration;
    public AiNewsPage() : this(new(AiNewsClient.CreateDefault(), new AiNewsEnricher(),
        AiNewsEnricher.CaptureSelectedModel, () => LocalizationService.CurrentLanguage)) { }
    internal AiNewsPage(AiNewsFeedSession session)
    {
        _session = session;
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
        Loaded += async (_, _) =>
        {
            _visible = true; _session.Enter();
            await EnsurePortalAsync();
            if (!_visible) return;
            PublishState();
            if (_ready) _ = _session.ResumeAsync();
        };
        Unloaded += (_, _) => { _visible = false; _session.Leave(); };
        ActualThemeChanged += (_, _) => PublishState();
        _session.Changed += state => DispatcherQueue.TryEnqueue(() =>
        {
            if (_visible && ReferenceEquals(_session.State, state)) PublishState();
        });
        ApplyLocalization();
    }

    private async Task EnsurePortalAsync()
    {
        if (_initializing) return;
        _initializing = true;
        try
        {
            if (_needsRecreate)
            {
                _webGeneration++;
                Root.Children.Remove(Web);
                Web.Close();
                Web = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                Root.Children.Insert(0, Web);
                _hooked = false; _needsRecreate = false;
            }
            if (!_hooked)
            {
                await Web.EnsureCoreWebView2Async(await WebView2EnvironmentService.GetAsync());
                var core = Web.CoreWebView2;
                int generation = ++_webGeneration;
                core.NavigationStarting += (sender, e) =>
                {
                    if (generation != _webGeneration) { e.Cancel = true; return; }
                    if (AiNewsPortalBridge.IsPortal(e.Uri)) { _navigationId = e.NavigationId; _ready = false; return; }
                    e.Cancel = true;
                    if (AiNewsDocument.IsSafeLink(e.Uri)) _ = OpenAsync(e.Uri);
                };
                core.NewWindowRequested += (sender, e) =>
                {
                    e.Handled = true;
                    if (generation != _webGeneration) return;
                    if (AiNewsDocument.IsSafeLink(e.Uri)) _ = OpenAsync(e.Uri);
                };
                core.NavigationCompleted += (_, e) =>
                {
                    if (generation != _webGeneration || e.NavigationId != _navigationId) return;
                    if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) { _ready = false; _session.Leave(); ShowFailure(); }
                    else if (e.IsSuccess) { ErrorPanel.Visibility = Visibility.Collapsed; Web.Visibility = Visibility.Visible; }
                };
                core.ProcessFailed += (_, e) =>
                {
                    if (generation != _webGeneration) return;
                    if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited
                        or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)) return;
                    _needsRecreate = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
                    _ready = false; _session.Leave(); ShowFailure();
                };
                core.WebMessageReceived += OnWebMessage;
                _hooked = true;
            }
            if (_visible && !_ready) Web.CoreWebView2.Navigate(AiNewsPortalBridge.OfficialUrl);
        }
        catch (Exception) { ShowFailure(); }
        finally { _initializing = false; }
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!_visible || !AiNewsPortalBridge.IsPortal(sender.Source)) return;
        var command = AiNewsPortalBridge.Read(e.Source, e.WebMessageAsJson, _session.State);
        switch (command?.Type)
        {
            case "ready": _ready = true; _session.Enter(); PublishState(); _ = _session.ResumeAsync(); break;
            case "query": case "refresh": _ = _session.LoadAsync(command.Query!); break;
            case "more": _ = _session.LoadAsync(_session.State.Query, more: true); break;
            case "configure": App.MainWindow?.NavigateToSettings("AiConfiguration"); break;
            case "original": _ = OpenAsync(command.Url!); break;
        }
    }

    private void PublishState()
    {
        if (!_visible || !_ready || Web.CoreWebView2 is not { } core || !AiNewsPortalBridge.IsPortal(core.Source)) return;
        try { core.PostWebMessageAsJson(AiNewsPortalBridge.StateJson(_session.State,
            LocalizationService.CurrentLanguage, ActualTheme == ElementTheme.Dark)); }
        catch (Exception) { _ready = false; ShowFailure(); }
    }
    private void ShowFailure() { Web.Visibility = Visibility.Collapsed; ErrorPanel.Visibility = Visibility.Visible; }
    private async void Retry_Click(object sender, RoutedEventArgs e) { _ready = false; await EnsurePortalAsync(); }
    private async void OpenBrowser_Click(object sender, RoutedEventArgs e) => await OpenAsync(AiNewsPortalBridge.OfficialUrl);
    private static async Task OpenAsync(string url)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(url)); } catch (Exception) { }
    }
    public void ApplyLocalization()
    {
        bool en = LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage;
        ErrorTitle.Text = en ? "News is temporarily unavailable" : "资讯页面暂时无法打开";
        ErrorText.Text = en ? "Retry or open the news portal in your browser. No AI account is required." : "可以重试，或在浏览器中打开资讯门户。浏览资讯无需配置 AI。";
        Retry.Content = en ? "Retry" : "重试";
        OpenBrowser.Content = en ? "Open in browser" : "在浏览器中打开";
        PublishState();
    }
}
