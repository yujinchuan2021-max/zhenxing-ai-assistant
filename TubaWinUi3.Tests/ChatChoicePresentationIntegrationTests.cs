using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ChatChoicePresentationIntegrationTests
{
    private const string Payload = "{\"schema\":1,\"question\":\"你能正常使用这个服务吗？\",\"options\":[{\"id\":\"yes\",\"label\":\"能正常用\",\"answer\":\"我能正常使用这个服务。\"},{\"id\":\"no\",\"label\":\"打不开或没有账号\",\"answer\":\"我无法打开或没有可用账号。\"}]}";
    private const string Question = "先核对账号和网络。\n```choice-question\n" + Payload + "\n```";

    [Fact]
    public void CompletedQuestionHidesMachinePayloadAndCopiesReadableChoices()
    {
        var presentation = AssistantReplyPresentation.Create(Question);
        Assert.Equal("先核对账号和网络。", presentation.Body);
        var prompt = ChatChoiceQuestion.TryCapture(Question);
        Assert.NotNull(prompt);
        var copied = ChatChoiceQuestion.CopyWithChoices(Question, prompt);
        Assert.Contains("能正常用", copied);
        Assert.Contains("打不开或没有账号", copied);
        Assert.DoesNotContain("choice-question", copied);
        Assert.DoesNotContain("\"schema\"", copied);
    }

    [Theory]
    [InlineData("choice-")]
    [InlineData("choice-question")]
    public void AQuestionCannotAlsoActivateAnInstallPlan(string label)
    {
        var mixed = "工具流名称：测试\n项目目标：做游戏\n准备开发环境。\n```toolflow-items\nGodot | software | | https://godotengine.org/ | | godot\n```\n```" + label;
        ToolFlowConversationMessage[] messages = [new() { Role = "user", Content = "做游戏" }, new() { Role = "assistant", Content = mixed }];
        Assert.False(ToolFlowProposalParser.TryCapture(messages, 1, out _));
        Assert.False(ModelPreferenceQuestion.TryCapture("```model-preference\n{\"schema\":1}\n```\n```" + label));
    }

    [Fact]
    public void StreamingQuestionKeepsShortContextWithoutLeakingJson()
    {
        var streamed = "先核对网络。\n```choice-question\n{\"schema\":1,\"question\":";
        Assert.Equal("先核对网络。", AssistantReplyPresentation.Create(streamed).Body);
        Assert.Null(ChatChoiceQuestion.TryCapture(streamed));
    }

    [Fact]
    public void SystemQuestionContractCoversAllFiniteClarificationsAndExistingSoftware()
    {
        var instructions = ToolFlowProposalParser.BuildOutputInstructions(["godot", "7zip"]);
        Assert.Contains("所有选择式澄清都用按钮", instructions);
        Assert.Contains("choice-question", instructions);
        Assert.Contains("WinRAR", instructions);
        Assert.Contains("确认清单只列缺项", instructions);
    }
}
