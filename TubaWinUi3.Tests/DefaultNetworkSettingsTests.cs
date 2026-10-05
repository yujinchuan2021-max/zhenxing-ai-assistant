using System.Net.Http;
using System.Text.Json;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

public class DefaultNetworkSettingsTests
{
    [Fact]
    public async Task UapisSearchRequest_UsesExistingFreeEndpointWithoutAuthorization()
    {
        const string query = "工具流 \"2D 游戏\" + C#";
        using var request = WebSearchService.CreateUapisSearchRequest(query);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://uapis.cn/api/v1/search/aggregate", request.RequestUri?.AbsoluteUri);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        Assert.Equal(query, body.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public void SharedHttpFactory_PreservesSystemProxyDefaultsWithoutCustomCredentials()
    {
        using var handler = ProxyService.CreateHandler();

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.Null(handler.Credentials);
    }

    [Fact]
    public void DownloadHandler_PreservesSystemProxyDefaultsAndIpv4ConnectionPolicy()
    {
        using var handler = HttpClientFactory.CreateIpv4PreferredHandler();

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.NotNull(handler.ConnectCallback);
    }
}
