using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ModelChoicePresentationTests
{
    private const string Question = "你希望怎样使用模型？\n```model-preference\n{\"schema\":1}\n```";

    [Fact]
    public void RealReplyHidesQuestionProtocolAndRetainsTheReadableQuestion()
    {
        var presentation = AssistantReplyPresentation.Create(Question);
        Assert.Equal("你希望怎样使用模型？", presentation.Body);
        Assert.Equal(presentation.Body, presentation.Preview);
        Assert.True(ModelPreferenceQuestion.TryCapture(Question));
        var copied = ModelPreferenceQuestion.CopyWithChoices(presentation.Body);
        Assert.DoesNotContain("schema", copied);
        Assert.DoesNotContain("model-preference", copied);
        foreach (var choice in ModelPreferenceQuestion.Choices) Assert.Contains(choice.Label, copied);
    }

    [Fact]
    public void QuestionCannotAlsoOfferAnInstallPlan()
    {
        const string plan = "工具流名称：示例\n只准备所选工具。\n```toolflow-items\nDemo | software | | https://example.com/ | | demo\n```";
        var combined = Question + "\n" + plan;
        Assert.False(ModelPreferenceQuestion.TryCapture(combined));
        ToolFlowConversationMessage[] messages =
        [new() { Role = "user", Content = "准备工具" }, new() { Role = "assistant", Content = combined }];
        Assert.False(ToolFlowProposalParser.TryCapture(messages, 1, out _));
        messages[1] = messages[1] with { Content = plan + "\n```model-pref" };
        Assert.False(ToolFlowProposalParser.TryCapture(messages, 1, out _));
        Assert.False(ToolFlowProposalParser.TryCaptureRecommendations(messages, 1, out _));
        messages[1] = messages[1] with { Content = Question };
        Assert.False(ToolFlowProposalParser.TryCapture(messages, 1, out _));
    }

    [Theory]
    [InlineData("````text\n```model-preference\n{\"schema\":1}\n```\n````")]
    [InlineData("    ```model-preference\n    {\"schema\":1}\n    ```")]
    public void DisplayCompositionPreservesOrdinaryQuestionFormatExamples(string example)
    {
        Assert.Equal(example.Trim(), AssistantReplyPresentation.Create(example).Body);
        Assert.False(ModelPreferenceQuestion.TryCapture(example));
    }
}
