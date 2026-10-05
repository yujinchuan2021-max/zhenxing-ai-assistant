using System.Text.Json;
using TubaWinUi3.Services.AiNews;

namespace TubaWinUi3.Tests;

public sealed class AiNewsPortalTests
{
    private static AiNewsViewState State => new(new(), [new("one", "Server update", "Source snippet", "Official",
        "https://example.org/article", null, "ai-models")]);
    [Theory]
    [InlineData("https://zhenxingai.com/ai-news/", true)]
    [InlineData("https://zhenxingai.com/ai-news/?lang=en#main", true)]
    [InlineData("https://zhenxingai.com/other/", false)]
    [InlineData("https://zhenxingai.com.evil.example/ai-news/", false)]
    [InlineData("http://zhenxingai.com/ai-news/", false)]
    [InlineData("https://user@zhenxingai.com/ai-news/", false)]
    [InlineData("https://zhenxingai.com:8769/ai-news/", false)]
    public void OnlyThePortalCanUseTheBridge(string url, bool expected)
        => Assert.Equal(expected, AiNewsPortalBridge.IsPortal(url));

    [Fact] public void QueriesUseOnlyKnownCategoryAndBoundedSearch()
    {
        var command = AiNewsPortalBridge.Read(AiNewsPortalBridge.OfficialUrl,
            "{\"type\":\"query\",\"category\":\"ai-models\",\"search\":\"  Qwen  \"}", State);
        Assert.Equal(new AiNewsQuery("ai-models", "Qwen"), command!.Query);
        foreach (string json in new[] {"[]", "{}", "{", "{\"type\":\"run\",\"command\":\"anything\"}",
            "{\"type\":\"query\",\"category\":\"unknown\"}", "{\"type\":\"query\",\"search\":1}",
            JsonSerializer.Serialize(new {type="query", search=new string('x',121)})})
            Assert.Null(AiNewsPortalBridge.Read(AiNewsPortalBridge.OfficialUrl, json, State));
        Assert.Null(AiNewsPortalBridge.Read("https://example.org/", "{\"type\":\"configure\"}", State));
    }
    [Fact] public void OriginalLinkComesFromTheCurrentItemRatherThanTheWebMessage()
    {
        var command = AiNewsPortalBridge.Read(AiNewsPortalBridge.OfficialUrl,
            "{\"type\":\"original\",\"id\":\"one\",\"url\":\"https://elsewhere.example/\"}", State);
        Assert.Equal("https://example.org/article", command!.Url);
        Assert.Null(AiNewsPortalBridge.Read(AiNewsPortalBridge.OfficialUrl,"{\"type\":\"original\",\"id\":\"unknown\"}",State));
    }
    [Fact] public void UnconfiguredStateStillContainsCompleteServerNews()
    {
        using var document = JsonDocument.Parse(AiNewsPortalBridge.StateJson(State,"en-US",true));
        var root=document.RootElement;
        Assert.Equal("unconfigured",root.GetProperty("aiState").GetString());
        Assert.Equal("Server update",root.GetProperty("items")[0].GetProperty("title").GetString());
        Assert.Equal("Source snippet",root.GetProperty("items")[0].GetProperty("summary").GetString());
        Assert.Equal("dark",root.GetProperty("theme").GetString());
        Assert.Equal(new[]{"aiState","failed","fromCache","items","language","loading","nextCursor","query","retrievedAt","theme","type"},
            root.EnumerateObject().Select(p=>p.Name).Order().ToArray());
    }
}
