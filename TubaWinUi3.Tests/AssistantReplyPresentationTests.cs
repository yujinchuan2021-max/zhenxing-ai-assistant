using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class AssistantReplyPresentationTests
{
    [Fact]
    public void ShortQuestionRemainsFullyReadable()
    {
        const string text = "你想做手机 APP 还是电脑软件？";
        var view = AssistantReplyPresentation.Create(text);
        Assert.False(view.HasDetails);
        Assert.Equal(text, view.Preview);
        Assert.Equal(text, view.Body);
    }

    [Fact]
    public void LongReplyHasShortPreviewAndRetainsEntireExplanation()
    {
        var text = "先选适合你的路线。\n\n" + string.Join("\n", Enumerable.Repeat("这是需要展开阅读的完整说明。", 40));
        var view = AssistantReplyPresentation.Create(text);
        Assert.True(view.HasDetails);
        Assert.Equal("先选适合你的路线。", view.Preview);
        Assert.Equal(text, view.Body);
    }

    [Fact]
    public void UnfinishedMachineDataDoesNotFloodTheVisibleReplyOrMutateOriginal()
    {
        const string original = "先比较三种搭配。\n```toolflow-options\n{\"schema\":1,\"options\":[";
        var view = AssistantReplyPresentation.Create(original);
        Assert.Equal("先比较三种搭配。", view.Body);
        Assert.DoesNotContain("schema", view.Preview);
        Assert.EndsWith("[", original);
    }

    [Fact]
    public void NormalCodeIsAvailableInFullDetails()
    {
        var code = "说明\n\n```csharp\n" + new string('x', 800) + "\n```";
        var view = AssistantReplyPresentation.Create(code);
        Assert.True(view.HasDetails);
        Assert.Contains("```csharp", view.Body);
        Assert.DoesNotContain("```csharp", view.Preview);
    }

    [Fact]
    public void StreamingLongParagraphNeverFloodsThePreview()
    {
        var original = new string('文', 1800);
        var view = AssistantReplyPresentation.Create(original);
        Assert.True(view.HasDetails);
        Assert.True(view.Preview.Length <= 221);
        Assert.Equal(original, view.Body);
    }

    [Theory]
    [InlineData("[ACTION]\n```json\n{\"command\":\"dangerous\"}\n```\n可以继续核对。")]
    [InlineData("[ACTION]\n{\n\"command\":\"dangerous\"\n}\n可以继续核对。")]
    public void LegacyExecutionPayloadIsNotDisplayed(string payload)
    {
        var view = AssistantReplyPresentation.Create("先核对。\n" + payload);
        Assert.Contains("先核对。", view.Body);
        Assert.Contains("可以继续核对。", view.Body);
        Assert.DoesNotContain("dangerous", view.Body);
        Assert.DoesNotContain("[ACTION]", view.Body);
    }

    [Fact]
    public void ActionExampleInsideNormalCodeRemainsReadable()
    {
        const string example = "```text\n[ACTION]\n{\"command\":\"example\"}\n```";
        Assert.Equal(example, AssistantReplyPresentation.Create(example).Body);
    }

    [Fact]
    public void CopyIncludesProseFullRequirementsWarningsVersionsAndUrlsWithoutExecutionCodes()
    {
        var item = new ToolFlowItem { ItemId = "test", Kind = "software", Name = "开发软件", Version = "4.7", SourceUrl = "https://example.com/",
            DownloadUrl = "https://example.com/download", InstallTargetKey = "hidden-target" };
        var option = new ToolFlowRecommendationOption("light", "轻量方案", "摘要", "适用", "费用", "完整账号与网络要求",
            ["必须核对兼容性"], [item], true, "", "```toolflow-selected\n{}\n```");
        var set = new ToolFlowRecommendationSet("项目目标", "light", [option], []);
        var copied = AssistantReplyPresentation.CopyWithRecommendations("额外的正文说明", set);
        foreach (var expected in new[] { "额外的正文说明", "项目目标", "完整账号与网络要求", "必须核对兼容性",
            "4.7", item.SourceUrl, item.DownloadUrl }) Assert.Contains(expected, copied);
        Assert.DoesNotContain("hidden-target", copied);
        Assert.DoesNotContain("toolflow-selected", copied);
    }
}
