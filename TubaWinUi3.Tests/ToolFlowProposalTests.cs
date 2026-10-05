using TubaWinUi3.Services.ToolFlows;
using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowProposalTests
{
    private static ToolFlowConversationMessage Message(string role, string content) =>
        new() { Role = role, Content = content };

    private static string Plan(string name = "写作工具流", string header = "工具流名称") =>
        $"{header}：{name}\n先整理项目素材，再按以下清单准备工具。账号登录需要你协助。\n"
        + "```toolflow-items\nWriting app | software | 1.x | https://example.com/app/ | | writing-target\n"
        + "Account | service | | https://example.com/signup/ | |\n```";

    [Theory]
    [InlineData("工具流名称")]
    [InlineData("Workflow name")]
    [InlineData("Tool flow name")]
    public void CapturesCompleteChineseAndEnglishProposals(string header)
    {
        var text = Plan(header: header);
        Assert.True(ToolFlowProposalParser.TryCapture(
            [Message("user", "帮我整理写作项目"), Message("assistant", text)], 1, out var proposal));
        Assert.Equal("写作工具流", proposal!.Name);
        Assert.Equal(text, proposal.Text); // Original visible body, including structured rows.
        Assert.Equal("帮我整理写作项目", proposal.SuggestedProjectGoal);
        Assert.Equal(2, proposal.Items.Count);
        Assert.Equal("writing-target", proposal.Items[0].InstallTargetKey);
        Assert.Null(proposal.Items[1].InstallTargetKey);
        Assert.All(proposal.Items, item => Assert.True(Guid.TryParse(item.ItemId, out _)));
    }

    [Fact]
    public void ClickingEarlierProposalBindsThatMessageAndItsConversationPrefix()
    {
        var earlier = Plan("Earlier plan");
        var later = Plan("Different later plan");
        var messages = new List<ToolFlowConversationMessage>
        {
            Message("user", "Earlier goal"), Message("assistant", earlier),
            Message("user", "Another project"), Message("assistant", later),
        };
        Assert.True(ToolFlowProposalParser.TryCapture(messages, 1, out var proposal));
        messages.Add(Message("user", "A third request"));
        Assert.Equal("Earlier plan", proposal!.Name);
        Assert.Equal(earlier, proposal.Text);
        Assert.Equal("Earlier goal", proposal.SuggestedProjectGoal);
        Assert.Equal(2, proposal.Conversation.Count);
        Assert.Equal(earlier, proposal.Conversation[^1].Content);
        Assert.DoesNotContain(proposal.Conversation, m => m.Content == later || m.Content == "Another project");
    }

    [Theory]
    [InlineData("今天有什么计划？")]
    [InlineData("工具流名称：只是一个标题")]
    [InlineData("工具流名称：尚未完成\n先确认需求\n```toolflow-items\nApp | software | | | | app")]
    [InlineData("工具流名称：空清单\n这里是方案说明\n```toolflow-items\n\n```")]
    [InlineData("工具流名称：没有说明\n```toolflow-items\nApp | software | | | | app\n```")]
    [InlineData("仅解释格式：\n```text\n工具流名称：格式示例\n```\n```toolflow-items\nApp | software | | | | app\n```")]
    public void OrdinaryAnswersTitlesExamplesAndIncompleteBlocksAreNotPlans(string text)
    {
        Assert.False(ToolFlowProposalParser.TryCapture(
            [Message("user", "Help"), Message("assistant", text)], 1, out _));
    }

    [Theory]
    [InlineData("App | software | | http://example.com/ | | app")]
    [InlineData("App | software | | https://example.local/ | | app")]
    [InlineData("App | software | | https://user:password@example.com/ | | app")]
    [InlineData("App | software | | https://example.com/ | | app | ignored command")]
    [InlineData("App")]
    public void InvalidRowsDoNotOfferAnInstallAction(string row)
    {
        var text = "工具流名称：方案\n完整说明\n```toolflow-items\n" + row + "\n```";
        Assert.False(ToolFlowProposalParser.TryCapture(
            [Message("user", "Help"), Message("assistant", text)], 1, out _));
    }

    [Fact]
    public void MissingAndAmbiguousMessageOwnershipAreRejected()
    {
        Assert.False(ToolFlowProposalParser.TryCapture([Message("assistant", Plan())], 0, out _));
        Assert.False(ToolFlowProposalParser.TryCapture([Message("user", Plan())], 0, out _));
        Assert.False(ToolFlowProposalParser.TryCapture([Message("user", "Help")], 2, out _));
        Assert.False(ToolFlowProposalParser.TryCapture(
            [Message("user", "Help"), Message("assistant", Plan() + "\n" + Plan("Second"))], 1, out _));
    }

    [Fact]
    public void SourceTextAndUnknownTargetsArePreservedWithoutNameInference()
    {
        var text = "Workflow name: My plan\r\nProject goal: Build my project\r\nUse suitable tools.\r\n"
            + "```toolflow-items\r\nGodot | software | | https://godotengine.org/ | |\r\n"
            + "Custom tool | software | | | | unknown-custom-target\r\n```\r\n";
        Assert.True(ToolFlowProposalParser.TryCapture(
            [Message("user", "First rough idea"), Message("assistant", text)], 1, out var proposal));
        Assert.Equal("Build my project", proposal!.SuggestedProjectGoal);
        Assert.Equal(text, proposal.Text);
        Assert.Null(proposal.Items[0].InstallTargetKey); // Never infer godot from the name.
        Assert.Equal("unknown-custom-target", proposal.Items[1].InstallTargetKey); // Runner treats it as manual.
    }

    [Fact]
    public void ClosedBlockOnlyBecomesEligibleAfterCompleteMessageArrives()
    {
        var complete = Plan();
        var partial = complete[..complete.LastIndexOf("```", StringComparison.Ordinal)];
        Assert.False(ToolFlowProposalParser.TryCapture(
            [Message("user", "Help"), Message("assistant", partial)], 1, out _));
        Assert.True(ToolFlowProposalParser.TryCapture(
            [Message("user", "Help"), Message("assistant", complete)], 1, out _));
    }

    [Fact]
    public void SharedOutputContractUsesOnlyHostSuppliedTargets()
    {
        var instructions = ToolFlowProposalParser.BuildOutputInstructions(["z-target", "a-target"]);
        Assert.Contains("toolflow-items", instructions);
        Assert.Contains("Workflow name", instructions);
        Assert.Contains("普通问答", instructions);
        Assert.Contains("a-target, z-target", instructions);
        Assert.DoesNotContain("godot", instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostCatalogDistinguishesClientVariantsWithoutChangingInstallIdentity()
    {
        var descriptions = SystemInstaller.GetTargetDescriptions();
        Assert.Equal(SystemInstaller.KnownTargets.Order(), descriptions.Keys.Order());
        Assert.Contains("Codex CLI；非桌面安装项", descriptions["codex"]);
        Assert.Contains("OpenCode CLI；非桌面安装项", descriptions["opencode"]);
        Assert.Contains("Claude Code CLI；非桌面安装项", descriptions["claude-code"]);
        Assert.Contains("桌面入口", descriptions["cursor"]);
        Assert.Contains("桌面入口", descriptions["claude-desktop"]);
        Assert.DoesNotContain("winrar", descriptions.Keys); // Access-only reuse is not new install authority.
        Assert.True(SystemInstaller.TryGetToolAccessMetadata("opencode", out var opencode));
        Assert.Equal("OpenCode", opencode.Name); // Product/registry evidence still uses its real name.
        Assert.False(opencode.IsGui);
        Assert.True(SystemInstaller.TryGetToolAccessMetadata("claude-code", out var claudeCode));
        Assert.Equal("Claude Code", claudeCode.Name);
        Assert.False(claudeCode.IsGui);
    }

    [Fact]
    public void VariantDescriptionsAnnotateOnlyTheExplicitHostAllowList()
    {
        var descriptions = new Dictionary<string, string>(SystemInstaller.GetTargetDescriptions())
        {
            ["unavailable-desktop-install"] = "Must not become an install target"
        };
        var instructions = ToolFlowProposalParser.BuildOutputInstructions(["cursor", "codex"], descriptions);
        Assert.Contains("codex（Codex CLI；非桌面安装项）, cursor（Cursor；桌面入口）", instructions);
        Assert.DoesNotContain("unavailable-desktop-install", instructions);
        Assert.DoesNotContain("Must not become an install target", instructions);
        Assert.DoesNotContain("claude-code（", instructions);
    }

    [Fact]
    public void TraeCatalogAndOutputRulesSeparateInstallationFromRegionalLoginReadiness()
    {
        var descriptions = SystemInstaller.GetTargetDescriptions();
        var trae = descriptions["trae"];
        Assert.Contains("国际版", trae);
        Assert.Contains("ByteDance.Trae", trae);
        Assert.Contains("首次登录需要海外网络", trae);
        Assert.Contains("不是国内版", trae);
        Assert.True(SystemInstaller.TryGetToolAccessMetadata("trae", out var metadata));
        Assert.Equal("Trae", metadata.Name); // Preserve the real product alias used by existing software detection.
        Assert.True(metadata.IsGui);
        Assert.Contains("Trae.exe", metadata.ExecutableNames);

        var instructions = ToolFlowProposalParser.BuildOutputInstructions(["trae"], descriptions);
        Assert.Contains("官网可达或软件已安装不证明登录与AI可用", instructions);
        Assert.Contains("国内API不解除Agent自己的登录门槛", instructions);
        Assert.Contains("宿主trae固定指向国际版ByteDance.Trae", instructions);
        Assert.Contains("首次登录需海外网络", instructions);
        Assert.Contains("国内版ByteDance.Trae.CN不能用trae代码安装", instructions);
        Assert.Contains("未提供匹配代码就留空", instructions);
        Assert.Contains("已确认方案不静默换装", instructions);
        Assert.Contains("trae（" + trae + "）", instructions);
    }
}
