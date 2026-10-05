using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Community;
using Windows.Storage.Streams;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>Real WebView2 CSS evaluation against memory-only responses. No website, account, API or product navigation.</summary>
internal static class CommunityAppearanceCases
{
    internal static (string, Func<Task>)[] All() =>
    [
        ("Community_Appearance_RoundTrip_Offline", RoundTrip),
        ("Community_Appearance_OriginAndValidation_Offline", OriginAndValidation),
        ("Community_NativePalette_PageOverridesApp_Offline", NativePalette),
    ];

    // Derived from the current public Discourse palette/token names. Deliberately starts with a dark
    // palette and dark !important canvas plus black text, independent of the requested client theme.
    private const string Fixture = """
        <!doctype html><html><head><meta charset="utf-8"><title>Offline community appearance fixture</title>
        <style>
        :root { --primary:#ededf0 !important;--secondary:#212123 !important;--primary-50:#27272f;
          --primary-100:#2b2b2e;--primary-600:#9797a4;--primary-900:#ededf0;
          --token-color-text-default:var(--primary-900);--token-color-text-subtlest:var(--primary-600);
          --token-color-background-input:#212123;--d-button-primary-text-color:#ffffff;--tertiary:#ff8567; }
        body { background:#212123 !important;color:#2b2b30 !important;margin:0;min-height:1800px; }
        #main-outlet { background:var(--primary-50);padding:24px; }
        .cooked { color:var(--token-color-text-default);background:var(--primary-50);padding:16px; }
        .metadata { color:var(--token-color-text-subtlest);background:var(--secondary); }
        textarea { color:var(--primary);background:var(--token-color-background-input); }
        .btn-primary { color:var(--d-button-primary-text-color);background:var(--tertiary); }
        .hljs { color:var(--primary);background:var(--hljs-bg,#111111); }
        .hljs-keyword { color:var(--hljs-keyword,#88aece); }
        </style></head><body><main id="main-outlet"><article id="post" class="cooked">论坛帖子正文</article>
        <p id="metadata" class="metadata">最新更新 · 已登录状态不会被主题切换重建</p>
        <textarea id="editor">用户正在编辑的草稿</textarea><button id="primary" class="btn-primary">发布</button>
        <pre id="code" class="hljs"><span id="keyword" class="hljs-keyword">return</span> fixture;</pre>
        <iframe id="subframe" srcdoc="<!doctype html><html><body>Isolated child document</body></html>"></iframe>
        </main></body></html>
        """;

    private static async Task RoundTrip()
    {
        await WithFixture("https://community.zhenxingai.com/__offline_appearance_fixture", async (web, loaded, ready) =>
        {
            Assert.True(ready() > 0, "The official fixture must complete the production appearance handshake.");
            web.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#editor').focus();document.querySelector('#editor').setSelectionRange(1,4);window.scrollTo(0,140);");
            var original = await Snapshot(web);
            var beforeNavigations = loaded();
            foreach (var dark in new[] { false, true, false })
            {
                var state = State(dark);
                web.CoreWebView2.PostWebMessageAsJson(state);
                var snapshot = await WaitForTheme(web, dark ? "dark" : "light");
                Assert.Equal(dark ? "rgb(33, 33, 35)" : "rgb(252, 251, 249)", snapshot.GetProperty("bodyBackground").GetString());
                Assert.Equal(dark ? "dark" : "light", snapshot.GetProperty("scheme").GetString());
                Assert.True(snapshot.GetProperty("styleCount").GetInt32() == 1, "Theme updates must reuse one scoped stylesheet.");
                Assert.True(snapshot.GetProperty("primaryImportant").GetBoolean());
                foreach (var row in snapshot.GetProperty("readability").EnumerateArray())
                    Assert.True(Contrast(row.GetProperty("foreground").GetString()!, row.GetProperty("background").GetString()!) >= 4.5,
                        "Unreadable community control: " + row.GetProperty("id").GetString() + " " + row);
                Assert.Equal(original.GetProperty("draft").GetString(), snapshot.GetProperty("draft").GetString());
                Assert.Equal(original.GetProperty("selectionStart").GetInt32(), snapshot.GetProperty("selectionStart").GetInt32());
                Assert.Equal(original.GetProperty("selectionEnd").GetInt32(), snapshot.GetProperty("selectionEnd").GetInt32());
                Assert.Equal("editor", snapshot.GetProperty("focused").GetString());
                Assert.Equal(original.GetProperty("scrollY").GetDouble(), snapshot.GetProperty("scrollY").GetDouble());
                Assert.Equal(beforeNavigations, loaded());
                Assert.False(snapshot.GetProperty("subframeStyled").GetBoolean());
                await Capture(web, dark ? "community-offline-dark.png" : "community-offline-light.png");
            }
            // Late-loaded palette CSS and a dynamically appended post must not resurrect the previous scheme.
            await web.CoreWebView2.ExecuteScriptAsync("const s=document.createElement('style');s.textContent=':root{--primary-900:#ffffff !important;--token-color-background-input:#212123 !important} body{background:#212123 !important}';document.head.appendChild(s);const p=document.createElement('article');p.id='dynamic';p.className='cooked';p.textContent='新增帖子';document.querySelector('#main-outlet').appendChild(p);");
            var dynamic = await Snapshot(web);
            var dynamicRow = dynamic.GetProperty("readability").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "dynamic");
            Assert.True(Contrast(dynamicRow.GetProperty("foreground").GetString()!, dynamicRow.GetProperty("background").GetString()!) >= 4.5);
            Assert.Equal("rgb(252, 251, 249)", dynamic.GetProperty("bodyBackground").GetString());
            Assert.Equal(beforeNavigations, loaded());
        });
    }

    private static async Task OriginAndValidation()
    {
        await WithFixture("https://example.invalid/__offline_appearance_fixture", async (web, _, ready) =>
        {
            web.CoreWebView2.PostWebMessageAsJson(State(false));
            await Task.Delay(100);
            var snapshot = await Snapshot(web);
            Assert.Equal(0, ready());
            Assert.Null(snapshot.GetProperty("theme").GetString());
            Assert.Equal(0, snapshot.GetProperty("styleCount").GetInt32());
            Assert.Equal("rgb(33, 33, 35)", snapshot.GetProperty("bodyBackground").GetString());
        });
        await WithFixture("https://community.zhenxingai.com/__offline_appearance_validation", async (web, _, _) =>
        {
            web.CoreWebView2.PostWebMessageAsJson(State(false));
            var before = await WaitForTheme(web, "light");
            web.CoreWebView2.PostWebMessageAsJson(State(true).Replace("#212123", "url(https://evil.invalid/image)", StringComparison.Ordinal));
            await Task.Delay(100);
            var after = await Snapshot(web);
            Assert.Equal(before.GetProperty("theme").GetString(), after.GetProperty("theme").GetString());
            Assert.Equal(before.GetProperty("bodyBackground").GetString(), after.GetProperty("bodyBackground").GetString());
            Assert.Equal(1, after.GetProperty("styleCount").GetInt32());
        });
    }

    private static string State(bool dark) => dark
        ? CommunityAppearanceBridge.StateJson(true, "#212123", "#2b2b2e", "#ededf0", "#b0afb7", "#3d3b3b", "#ff8567", "#ffffff")
        : CommunityAppearanceBridge.StateJson(false, "#fcfbf9", "#ffffff", "#2b2b30", "#62646d", "#ddd9d4", "#d85135", "#ffffff");

    private static async Task NativePalette()
    {
        Assert.Equal(ApplicationTheme.Dark, Application.Current.RequestedTheme);
        var resources = new XamlControlsResources();
        var fonts = new ResourceDictionary { ["AppFontFamily"] = AppFonts.WinUI };
        Application.Current.Resources.MergedDictionaries.Add(resources);
        Application.Current.Resources.MergedDictionaries.Add(fonts);
        try
        {
            await WithFixture("https://community.zhenxingai.com/__offline_native_palette", async (web, _, _) =>
            {
                var root = Assert.IsType<Grid>(web.Parent);
                var previousTheme = root.RequestedTheme;
                // Attach the production page directly, without Frame navigation. Its WebView stays
                // uninitialized and OnNavigatedTo never reads the configured community address.
                var page = new CommunityHubPage { Width = 320, Height = 420, Opacity = 0,
                    IsHitTestVisible = false, RequestedTheme = ElementTheme.Light };
                try
                {
                    root.RequestedTheme = ElementTheme.Dark;
                    root.Children.Add(page);
                    foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
                    {
                        page.RequestedTheme = theme;
                        await Task.Delay(120);
                        root.UpdateLayout();
                        Assert.Equal(theme, page.ActualTheme);
                        var json = page.BuildCommunityAppearanceJson();
                        using var document = JsonDocument.Parse(json);
                        var native = document.RootElement;
                        var canvas = native.GetProperty("background").GetString()!;
                        var text = native.GetProperty("text").GetString()!;
                        Assert.Equal(theme == ElementTheme.Dark ? "dark" : "light", native.GetProperty("theme").GetString());
                        // These bounds catch the actual failure: dark application canvas combined
                        // with the Light page's black text, before anything reaches JavaScript.
                        Assert.True(theme == ElementTheme.Light ? Luminance(CssRgb(canvas)) > .75 : Luminance(CssRgb(canvas)) < .15,
                            "The native community canvas must follow its page, not Application.RequestedTheme: " + json);
                        Assert.True(Contrast(CssRgb(text), CssRgb(canvas)) >= 4.5,
                            "The production native payload already contains unreadable mixed-theme colors: " + json);
                        web.CoreWebView2.PostWebMessageAsJson(json);
                        var snapshot = await WaitForTheme(web, theme == ElementTheme.Dark ? "dark" : "light");
                        Assert.Equal(CssRgb(canvas), snapshot.GetProperty("bodyBackground").GetString());
                        foreach (var colorRow in snapshot.GetProperty("readability").EnumerateArray())
                            Assert.True(Contrast(colorRow.GetProperty("foreground").GetString()!, colorRow.GetProperty("background").GetString()!) >= 4.5,
                                "The real native payload produced unreadable community content: " + colorRow);
                        Assert.Null(((WebView2)page.FindName("Web")).CoreWebView2);
                    }
                }
                finally
                {
                    root.Children.Remove(page);
                    root.RequestedTheme = previousTheme;
                }
            });
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(fonts);
            Application.Current.Resources.MergedDictionaries.Remove(resources);
        }
    }

    private static string CssRgb(string hex) => $"rgb({Convert.ToByte(hex.Substring(1, 2), 16)}, {Convert.ToByte(hex.Substring(3, 2), 16)}, {Convert.ToByte(hex.Substring(5, 2), 16)})";

    private static async Task WithFixture(string url, Func<WebView2, Func<int>, Func<int>, Task> body)
    {
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
        {
            var web = new WebView2 { Width = 800, Height = 620 };
            root.Children.Add(web);
            var streams = new List<IRandomAccessStream>();
            try
            {
                var environment = await WebView2EnvironmentService.GetAsync();
                await web.EnsureCoreWebView2Async(environment);
                var core = web.CoreWebView2;
                // All document/subresource requests are fulfilled in memory. The official URL exercises
                // the production origin guard, without any connection to the actual community server.
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, args) =>
                {
                    var document = args.Request.Uri.Equals(url, StringComparison.Ordinal);
                    var stream = new MemoryStream(Encoding.UTF8.GetBytes(document ? Fixture : "")).AsRandomAccessStream();
                    streams.Add(stream);
                    args.Response = environment.CreateWebResourceResponse(stream, document ? 200 : 404,
                        document ? "OK" : "Offline", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                };
                var readyCount = 0;
                core.WebMessageReceived += (_, args) =>
                { if (CommunityAppearanceBridge.IsReady(args.Source, args.WebMessageAsJson)) readyCount++; };
                await core.AddScriptToExecuteOnDocumentCreatedAsync(CommunityAppearanceBridge.DocumentScript);
                var navigationCount = 0;
                var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationCompleted += (_, args) =>
                {
                    navigationCount++;
                    if (args.IsSuccess) navigated.TrySetResult();
                    else navigated.TrySetException(new InvalidOperationException("Offline fixture navigation failed: " + args.WebErrorStatus));
                };
                core.Navigate(url);
                await navigated.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(100);
                await body(web, () => navigationCount, () => readyCount);
            }
            finally
            {
                root.Children.Remove(web);
                web.Close();
                foreach (var stream in streams) stream.Dispose();
            }
        });
    }

    private static async Task<JsonElement> WaitForTheme(WebView2 web, string theme)
    {
        for (var i = 0; i < 50; i++)
        {
            var state = await Snapshot(web);
            if (state.GetProperty("theme").GetString() == theme) return state;
            await Task.Delay(50);
        }
        throw new TimeoutException("The offline forum did not apply the client theme.");
    }

    private static async Task<JsonElement> Snapshot(WebView2 web)
    {
        var encoded = await web.CoreWebView2.ExecuteScriptAsync("""
            JSON.stringify({theme:document.documentElement.dataset.zxaiTheme ?? null,
              scheme:getComputedStyle(document.documentElement).colorScheme,
              bodyBackground:getComputedStyle(document.body).backgroundColor,
              styleCount:document.querySelectorAll('#zxai-client-appearance').length,
              primaryImportant:document.documentElement.style.getPropertyPriority('--primary')==='important',
              subframeStyled:!!document.querySelector('#subframe')?.contentDocument?.documentElement?.dataset.zxaiClient,
              draft:document.querySelector('#editor').value,selectionStart:document.querySelector('#editor').selectionStart,
              selectionEnd:document.querySelector('#editor').selectionEnd,focused:document.activeElement.id,scrollY:window.scrollY,
              readability:['post','metadata','editor','primary','keyword','dynamic'].map(id=>document.getElementById(id)).filter(Boolean).map(node=>{
                let canvas=node;while(canvas&&getComputedStyle(canvas).backgroundColor==='rgba(0, 0, 0, 0)')canvas=canvas.parentElement;
                return {id:node.id,foreground:getComputedStyle(node).color,background:getComputedStyle(canvas||document.body).backgroundColor};
              })})
            """);
        using var stringDocument = JsonDocument.Parse(encoded);
        using var stateDocument = JsonDocument.Parse(stringDocument.RootElement.GetString()!);
        return stateDocument.RootElement.Clone();
    }

    private static double Contrast(string first, string second)
    {
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static double Luminance(string css)
    {
        var components = css[(css.IndexOf('(') + 1)..css.IndexOf(')')].Split(',').Take(3)
            .Select(value => double.Parse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture) / 255d)
            .Select(value => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4)).ToArray();
        return .2126 * components[0] + .7152 * components[1] + .0722 * components[2];
    }

    private static async Task Capture(WebView2 web, string filename)
    {
        using var stream = new InMemoryRandomAccessStream();
        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
        File.WriteAllBytes(Path.Combine(DataRoots.EffectiveTestRoot!, filename), bytes);
    }
}
