using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Models;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.CloudTools;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

internal static class CommunityManagedSourceCases
{
    internal static (string, Func<Task>)[] All() =>
        [("CommunityTools_DefaultOwnedCatalog_Offline", DefaultOwnedCatalog)];

    private static async Task DefaultOwnedCatalog()
    {
        var dataRoot = DataRoots.EffectiveTestRoot;
        Assert.NotNull(dataRoot);
        var fixture = Path.Combine(dataRoot!, "owned-community-fixture");
        Directory.CreateDirectory(fixture);
        var seed = Path.Combine(fixture, "seed.json");
        var tool = new CloudToolDefinition
        {
            Id = "fixture-tool", Name = "离线验证工具", Category = "检测工具", Version = "1",
            Description = "此条目是界面验证数据，不会访问网络或运行程序。",
            Packages = [new() { Architecture = UpdateService.CurrentArchitecture,
                Url = "https://zhenxingai.com/downloads/tools/fixture.zip", SizeBytes = 100,
                Sha256 = new string('a', 64), EntryPoint = "app.exe" }]
        };
        var catalog = new CloudToolCatalog { Revision = 1, PublishedAt = "2026-10-07T00:00:00Z",
            Tools = [tool, tool with { Id = "manual-fixture", Name = "无自动包的条目", Packages = [] }] };
        File.WriteAllText(seed, JsonSerializer.Serialize(catalog));
        var source = CommunityToolService.CurrentSource;
        var manager = CloudToolService.OverrideForTests;
        using var transport = new RejectTransport();
        using var http = new HttpClient(transport);
        try
        {
            CloudToolService.OverrideForTests = new CloudToolManager(fixture, http, CloudToolService.OwnEndpoint,
                UpdateService.CurrentArchitecture, new Version(0, 1, 1), seed, Path.Combine(fixture, "Tools"), allowNetwork: false);
            CommunityToolService.CurrentSource = CommunityDataSource.Zhenxing;
            await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
            {
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1150, 840));
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    var page = new CommunityToolsPage { RequestedTheme = theme };
                    root.Children.Add(page);
                    for (var i = 0; i < 30 && !page.IsLoaded; i++) await Task.Delay(30);
                    root.UpdateLayout();
                    Assert.True(page.IsLoaded);
                    var selector = Field<ComboBox>(page, "SourceSelector");
                    Assert.Equal(0, selector.SelectedIndex);
                    Assert.Equal(3, selector.Items.Count);
                    var cards = Assert.IsAssignableFrom<IEnumerable<CommunityTool>>(Field<GridView>(page, "ToolsGrid").ItemsSource);
                    var visible = Assert.Single(cards);
                    Assert.Equal("fixture-tool", visible.CloudToolId);
                    Assert.Equal("下载", visible.LaunchButtonText);
                    Assert.Equal(CommunityToolInstallStatus.NotInstalled, visible.InstallStatus);
                    Assert.Contains("1", Field<TextBlock>(page, "StatusText").Text);
                    Assert.Equal(Visibility.Visible, Field<GridView>(page, "ToolsGrid").Visibility);
                    Assert.Equal(Visibility.Collapsed, Field<ProgressBar>(page, "LoadingProgress").Visibility);
                    root.Children.Remove(page);
                }
            });
            Assert.Equal(0, transport.Requests);
        }
        finally
        {
            CloudToolService.OverrideForTests = manager;
            CommunityToolService.CurrentSource = source;
            CommunityToolService.InvalidateCache();
        }
    }

    private static T Field<T>(CommunityToolsPage page, string name) where T : class => Assert.IsType<T>(
        typeof(CommunityToolsPage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));

    private sealed class RejectTransport : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; throw new InvalidOperationException("Native fixture cannot access download sources."); }
    }
}
