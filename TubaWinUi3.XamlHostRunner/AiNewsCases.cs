using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;
using TubaWinUi3.Services;
using TubaWinUi3.Services.AiNews;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

internal static class AiNewsCases
{
    internal static (string, Func<Task>)[] All() => [
        ("AiNews_FrameNavigationActivation", FrameNavigationActivation),
        ("AiNews_WebPortalWithoutModel", WebPortalWithoutModel),
        ("AiNews_BlankWebViewExitControl", BlankWebViewExitControl)
    ];
    private static Task FrameNavigationActivation()
    {
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Dark");
        var metadata = new TubaWinUi3.TubaWinUi3_XamlTypeInfo.XamlMetaDataProvider();
        Assert.NotNull(metadata.GetXamlType(typeof(TubaWinUi3.Pages.AiNewsPage)));
        var frame = new Frame();
        bool failed = false; frame.NavigationFailed += (_, e) => { failed = true; e.Handled = true; };
        Assert.True(frame.Navigate(typeof(TubaWinUi3.Pages.AiNewsPage)));
        var page = Assert.IsType<TubaWinUi3.Pages.AiNewsPage>(frame.Content);
        Assert.IsType<Grid>(page.Content); Assert.IsType<WebView2>(page.FindName("Web")); Assert.False(failed);
        Assert.True(frame.Navigate(typeof(TubaWinUi3.Pages.AiNewsPage), "return-check"));
        Assert.True(frame.CanGoBack); frame.GoBack(); Assert.IsType<TubaWinUi3.Pages.AiNewsPage>(frame.Content);
        page.ApplyLocalization();
        Assert.Equal(Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled, page.NavigationCacheMode);
        frame.Content = null;
        ((WebView2)page.FindName("Web")).Close();
        return Task.CompletedTask;
    }

    // The real hosted portal runs in an isolated WebView2 profile. News and model
    // selection are synthetic; no product App, user Key or model client is used.
    private static async Task WebPortalWithoutModel()
    {
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Dark");
        var reader = new Reader();
        var session = new AiNewsFeedSession(reader, new(), () => null, () => LocalizationService.CurrentLanguage);
        var page = new TubaWinUi3.Pages.AiNewsPage(session) { Width = 1050, Height = 760 };
        await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
        {
            root.Children.Add(page);
            var web = (WebView2)page.FindName("Web");
            async Task<JsonElement> Snapshot()
            {
                string output=await web.CoreWebView2.ExecuteScriptAsync("JSON.stringify({title:document.title,cards:document.querySelectorAll('.news-card').length,lead:document.querySelector('#lead')?.textContent,body:document.body.innerText,width:document.documentElement.clientWidth,overflow:document.documentElement.scrollWidth>document.documentElement.clientWidth})");
                using var encoded=JsonDocument.Parse(output);
                using var parsed=JsonDocument.Parse(encoded.RootElement.GetString()!);
                return parsed.RootElement.Clone();
            }
            for (int i=0;i<150 && (web.CoreWebView2 is null || reader.Calls==0);i++) await Task.Delay(100);
            Assert.NotNull(web.CoreWebView2); Assert.True(reader.Calls>0,"Portal ready must load server news without a model.");
            JsonElement snapshot=default;
            for (int i=0;i<50;i++)
            {
                snapshot=await Snapshot();
                if(snapshot.GetProperty("cards").GetInt32()>0)break;
                await Task.Delay(100);
            }
            Assert.Equal(2,snapshot.GetProperty("cards").GetInt32());
            Assert.Contains("Synthetic server update 0",snapshot.GetProperty("lead").GetString());
            Assert.False(snapshot.GetProperty("overflow").GetBoolean());
            Assert.Equal(AiNewsAiState.Unconfigured,session.State.AiState);
            await Capture(web,"portal-wide.png");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#search').value='Qwen'; document.querySelector('#search-form').requestSubmit();");
            for(int i=0;i<30 && session.State.Query.Search!="Qwen";i++)await Task.Delay(100);
            Assert.Equal("Qwen",session.State.Query.Search);
            page.Width=360; await Task.Delay(150);root.UpdateLayout();
            var narrow=await Snapshot();
            Assert.False(narrow.GetProperty("overflow").GetBoolean());
            await Capture(web,"portal-narrow.png");
            root.Children.Remove(page);await Task.Delay(80);int before=reader.Calls;
            root.Children.Add(page);await Task.Delay(150);
            Assert.Equal(before,reader.Calls); Assert.Equal("Qwen",session.State.Query.Search);
            root.Children.Remove(page);session.Leave();page.Content=null;
            var environment=await WebView2EnvironmentService.GetAsync();
            var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnExited(CoreWebView2Environment sender,CoreWebView2BrowserProcessExitedEventArgs args)
            { Console.WriteLine("WEB_BROWSER_EXIT|"+args.BrowserProcessExitKind);exited.TrySetResult(); }
            environment.BrowserProcessExited+=OnExited;
            web.Close();
            await Task.WhenAny(exited.Task,Task.Delay(5000));
            environment.BrowserProcessExited-=OnExited;
            Console.WriteLine("WEB_CLOSE_DONE|browserExited="+exited.Task.IsCompleted);
            await Task.Delay(150);
        });
    }
    private static async Task BlankWebViewExitControl()
    {
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        using var theme=new XamlThemeCases.ThemeTestEnvironment("Dark");
        await ChatScrollProbeCases.WithWindowAsync(async (_,root)=>
        {
            var web=new WebView2();root.Children.Add(web);
            var environment=await WebView2EnvironmentService.GetAsync();
            await web.EnsureCoreWebView2Async(environment);
            var loaded=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            web.CoreWebView2.NavigationCompleted+=(_,e)=>loaded.TrySetResult();
            web.CoreWebView2.NavigateToString("<!doctype html><title>Blank isolated control</title><p>Blank isolated control</p>");
            Assert.True(await Task.WhenAny(loaded.Task,Task.Delay(5000))==loaded.Task);
            var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnExited(CoreWebView2Environment sender,CoreWebView2BrowserProcessExitedEventArgs args)
            { Console.WriteLine("BLANK_BROWSER_EXIT|"+args.BrowserProcessExitKind);exited.TrySetResult(); }
            environment.BrowserProcessExited+=OnExited;
            root.Children.Remove(web);web.Close();
            await Task.WhenAny(exited.Task,Task.Delay(5000));
            environment.BrowserProcessExited-=OnExited;
            Console.WriteLine("BLANK_WEB_CLOSE_DONE|browserExited="+exited.Task.IsCompleted);
        });
    }
    private static async Task Capture(WebView2 web,string filename)
    {
        using var stream=new InMemoryRandomAccessStream();
        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);
        using var reader=new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes=new byte[(int)stream.Size];reader.ReadBytes(bytes);
        File.WriteAllBytes(Path.Combine(DataRoots.EffectiveTestRoot!,filename),bytes);
    }
    private sealed class Reader : IAiNewsReader
    {
        internal int Calls;
        public Task<AiNewsBatch> ReadAsync(AiNewsQuery query,bool force,CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new AiNewsBatch(Enumerable.Range(0,3).Select(i=>new AiNewsItem(i.ToString(),
                "Synthetic server update "+i,"Official supplied summary. No model setup is required.","Synthetic official source",
                "https://example.org/"+i,DateTimeOffset.UtcNow,"ai-models")).ToArray(),"",DateTimeOffset.UtcNow));
        }
    }
}
