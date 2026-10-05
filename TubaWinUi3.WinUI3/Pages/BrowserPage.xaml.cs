using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TubaWinUi3.Services;

namespace TubaWinUi3.Pages;

public sealed class BrowserPageParam
{
    public required string Url { get; init; }
    public string? Title { get; init; }
}

public sealed partial class BrowserPage : Page
{
    private string _url;

    public BrowserPage()
    {
        InitializeComponent();

        if (Content is FrameworkElement root)
            root.RequestedTheme = ThemeService.CurrentElementTheme;

        PageHeader.Title = "浏览器";
        _url = "about:blank";
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is BrowserPageParam param)
        {
            _url = param.Url;
            PageHeader.Title = param.Title ?? "浏览器";
            _ = InitWebViewAsync();
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            await WebView.EnsureCoreWebView2Async(await WebView2EnvironmentService.GetAsync());

            // ZXAI 字体统一：把应用 Assets 目录映射为虚拟主机，供应用自带 HTML 页面（如毒蘑菇测试）
            // 通过 https://zxassets.local/Fonts/... 与页面同源加载打包字体（WebView2 实测：file:// 页面拿不到打包字体）
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "zxassets.local",
                Path.Combine(AppContext.BaseDirectory, "Assets"),
                CoreWebView2HostResourceAccessKind.Allow);

            // ZXAI 字体统一：文档创建时注入 --app-font 覆盖（按已保存选择；选择在下次启动生效）。
            await WebFontBridge.AttachAsync(WebView.CoreWebView2);

            WebView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            WebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

            WebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            WebView.CoreWebView2.DocumentTitleChanged += OnDocumentTitleChanged;

            WebView.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = MiscTexts.T("WebView2 初始化失败"),
                Content = MiscTexts.TSub($"请确保已安装 WebView2 Runtime。\n\n{ex.Message}"),
                CloseButtonText = MiscTexts.T("确定"),
                XamlRoot = XamlRoot,
                RequestedTheme = ThemeService.CurrentElementTheme
            };
            await dialog.ShowAsync();
            App.MainWindow?.NavigateBack();
        }
    }

    private ulong _currentNavigationId;

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (InternalBrowserLink.TryGetWebUri(args.Uri, out var uri)) sender.Navigate(uri.AbsoluteUri);
    }

    private void OnNavigationStarting(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs args)
    {
        _currentNavigationId = args.NavigationId;
        LoadingRing.IsActive = true;
    }

    private void OnNavigationCompleted(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
    {
        // 忽略已被新导航顶掉的旧导航：重定向/JS 跳转会使上一次导航以
        // IsSuccess=false、WebErrorStatus=Unknown 结束，但页面实际已由新导航加载成功
        if (args.NavigationId != _currentNavigationId)
            return;

        LoadingRing.IsActive = false;

        // 加载失败时由 WebView2 原生错误页展示，不再使用自定义错误面板
    }

    private void OnDocumentTitleChanged(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        object args)
    {
        var docTitle = sender.DocumentTitle;
        if (!string.IsNullOrEmpty(docTitle))
        {
            PageHeader.Title = docTitle;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2?.CanGoBack == true)
            WebView.CoreWebView2.GoBack();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2 is not null)
            WebView.CoreWebView2.Reload();
    }

    private void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        var url = WebView.CoreWebView2?.Source?.ToString() ?? _url;
        url = TranslateForExternalBrowser(url);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    // ZXAI 字体统一（R3）：zxassets.local / bench.local 只是本应用 WebView 内的虚拟主机映射，
    // 系统浏览器无法解析。交给外部浏览器前还原为对应的本地文件（file://）地址，
    // 保持「在浏览器中打开」原有功能；普通网络网址原样返回。
    private static string TranslateForExternalBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        if (!uri.Host.Equals("zxassets.local", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("bench.local", StringComparison.OrdinalIgnoreCase))
            return url;

        try
        {
            var assetsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets"));
            var rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(assetsRoot, rel));

            // 边界检查：解析结果必须仍在 Assets 根目录内（防路径穿越）
            if ((full.Equals(assetsRoot, StringComparison.OrdinalIgnoreCase) ||
                 full.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) &&
                File.Exists(full))
            {
                return new Uri(full).AbsoluteUri;
            }
        }
        catch { }

        return url;
    }

    public static void Open(string url, string? title = null)
    {
        App.MainWindow?.NavigateToToolPage(typeof(BrowserPage), new BrowserPageParam
        {
            Url = url,
            Title = title
        });
    }
}
