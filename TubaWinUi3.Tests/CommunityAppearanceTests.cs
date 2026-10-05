using System.Text.Json;
using TubaWinUi3.Services.Community;

namespace TubaWinUi3.Tests;

public sealed class CommunityAppearanceTests
{
    [Theory]
    [InlineData("https://community.zhenxingai.com/", true)]
    [InlineData("https://community.zhenxingai.com/t/topic/1?x=1", true)]
    [InlineData("http://community.zhenxingai.com/", false)]
    [InlineData("https://community.zhenxingai.com:444/", false)]
    [InlineData("https://community.zhenxingai.com.evil.test/", false)]
    [InlineData("https://sub.community.zhenxingai.com/", false)]
    [InlineData("https://user:password@community.zhenxingai.com/", false)]
    [InlineData("about:blank", false)]
    public void AppearanceIsOnlyForTheOfficialHttpsOrigin(string address, bool official)
    {
        Assert.Equal(official, CommunityAppearanceBridge.IsOfficial(address));
        Assert.Equal(official, CommunityAppearanceBridge.IsReady(address,
            "{\"type\":\"zhenxing-community-ready\"}"));
    }

    [Theory]
    [InlineData("{invalid-json")]
    [InlineData("{\"type\":\"another-page\"}")]
    [InlineData("{\"type\":true}")]
    [InlineData("[\"zhenxing-community-ready\"]")]
    public void MalformedOrUnrelatedReadyMessagesAreIgnored(string message)
        => Assert.False(CommunityAppearanceBridge.IsReady("https://community.zhenxingai.com/", message));

    [Fact]
    public void TransparentNativeColorsAreCompositedAgainstTheResolvedPageCanvas()
    {
        Assert.Equal("#ffffff", CommunityAppearanceBridge.Flatten(0, 0, 0, 0, "#ffffff"));
        Assert.Equal("#000000", CommunityAppearanceBridge.Flatten(255, 0, 0, 0, "#ffffff"));
        Assert.Equal("#7f7f7f", CommunityAppearanceBridge.Flatten(128, 0, 0, 0, "#ffffff"));
        Assert.Equal("#818181", CommunityAppearanceBridge.Flatten(128, 255, 255, 255, "#020202"));
    }

    [Fact]
    public void NativePayloadContainsOnlyPublicAppearanceValues()
    {
        using var document = JsonDocument.Parse(CommunityAppearanceBridge.StateJson(false,
            "#ffffff", "#f8f8f8", "#222222", "#666666", "#dddddd", "#d85135", "#ffffff"));
        var state = document.RootElement;
        Assert.Equal("light", state.GetProperty("theme").GetString());
        Assert.Equal("zhenxing-community-appearance", state.GetProperty("type").GetString());
        Assert.Equal(new[] { "accent", "accentText", "background", "line", "muted", "surface", "text", "theme", "type" },
            state.EnumerateObject().Select(property => property.Name).OrderBy(value => value).ToArray());
    }
}
