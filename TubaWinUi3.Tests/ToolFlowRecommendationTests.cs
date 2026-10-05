using System.Text.Json;
using System.Text.Json.Nodes;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>Offline contract tests only: no installation, configuration, files or network.</summary>
public sealed class ToolFlowRecommendationTests
{
    private static ToolFlowConversationMessage Message(string role, string content) => new() { Role = role, Content = content };

    private static JsonObject Item(string name, string type = "software", string target = "") => new()
    {
        ["name"] = name, ["type"] = type, ["version"] = "", ["sourceUrl"] = "https://example.com/tools/",
        ["downloadUrl"] = "", ["installTargetKey"] = target,
    };

    private static JsonObject Option(string id) => new()
    {
        ["id"] = id, ["name"] = id + " project plan", ["summary"] = id + " complete workflow",
        ["fit"] = id + " configuration and automation", ["cost"] = id + " budget",
        ["requirements"] = "Agent, compatible model access and project development environment",
        ["warnings"] = new JsonArray("Version and account setup need verification"),
        ["items"] = new JsonArray(Item(id + " Agent", "agent"), Item(id + " model access", "service"), Item(id + " development environment")),
    };

    private static JsonObject Contract() => new()
    {
        ["schema"] = 1, ["goal"] = "Create my project with the constraints we discussed", ["recommended"] = "medium",
        ["options"] = new JsonArray(Option("light"), Option("medium"), Option("heavy")),
    };

    private static JsonObject FirstOption(JsonObject value) => value["options"]!.AsArray()[0]!.AsObject();
    private static JsonObject FirstItem(JsonObject value) => FirstOption(value)["items"]!.AsArray()[0]!.AsObject();
    private static string Plan(JsonObject? value = null) => "建议按配置成本选择开发模式。未知项需要核对。\n```toolflow-options\n"
        + (value ?? Contract()).ToJsonString() + "\n```";

    private static bool Capture(string content, out ToolFlowRecommendationSet? result) =>
        ToolFlowProposalParser.TryCaptureRecommendations([Message("user", "Make a development project"), Message("assistant", content)], 1, out result);

    [Fact]
    public void CapturesExactlyThreeCompleteAlternativesWithTheirOwnToolLists()
    {
        Assert.True(Capture(Plan(), out var result));
        Assert.Equal("Create my project with the constraints we discussed", result!.Goal);
        Assert.Equal("medium", result.RecommendedId);
        Assert.Equal(new[] { "light", "medium", "heavy" }, result.Options.Select(x => x.Id));
        Assert.All(result.Options, option =>
        {
            Assert.True(option.Available);
            Assert.Equal(3, option.Items.Count);
            Assert.Single(option.Warnings);
            Assert.Equal("agent", option.Items[0].Kind);
            Assert.Null(option.Items[0].Version);
            Assert.Null(option.Items[0].DownloadUrl);
            Assert.Null(option.Items[0].InstallTargetKey);
            Assert.All(option.Items, item => Assert.True(Guid.TryParseExact(item.ItemId, "D", out _)));
        });
    }

    [Fact]
    public void SelectingLightCreatesOnlyItsOwnProposalAndPreservesOriginalConversation()
    {
        var content = Plan();
        Assert.True(Capture(content, out var result));
        var proposal = result!.ToProposal("light")!;
        Assert.Equal("light project plan", proposal.Name);
        Assert.Equal(result.Goal, proposal.SuggestedProjectGoal);
        Assert.Equal(3, proposal.Items.Count);
        Assert.All(proposal.Items, item => Assert.StartsWith("light ", item.Name));
        Assert.Contains("light Agent", proposal.Text);
        Assert.Contains("准备要求", proposal.Text);
        Assert.DoesNotContain("medium Agent", proposal.Text);
        Assert.DoesNotContain("heavy Agent", proposal.Text);
        Assert.DoesNotContain("toolflow-options", proposal.Text);
        Assert.Equal(content, proposal.Conversation[^1].Content);
        Assert.Null(result.ToProposal("invalid"));
    }

    [Fact]
    public void EarlierMessageOwnsItsSnapshotEvenAfterTheConversationContinues()
    {
        var first = Plan();
        var later = Contract();
        later["goal"] = "A different later project";
        var messages = new List<ToolFlowConversationMessage>
        {
            Message("user", "First project"), Message("assistant", first),
            Message("user", "Different request"), Message("assistant", Plan(later)),
        };
        Assert.True(ToolFlowProposalParser.TryCaptureRecommendations(messages, 1, out var result));
        messages.Add(Message("user", "Yet another project"));
        Assert.Equal(2, result!.Conversation.Count);
        Assert.Equal(first, result.Conversation[^1].Content);
        Assert.Equal("Create my project with the constraints we discussed", result.Goal);
        Assert.DoesNotContain(result.Conversation, x => x.Content.Contains("different later project"));
    }

    [Fact]
    public void PureCaptureProducesStableDistinctItemIdentities()
    {
        var content = Plan();
        Assert.True(Capture(content, out var first));
        Assert.True(Capture(content, out var second));
        var ids = first!.Options.SelectMany(x => x.Items).Select(x => x.ItemId).ToArray();
        Assert.Equal(ids, second!.Options.SelectMany(x => x.Items).Select(x => x.ItemId));
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void UnavailableAlternativeRemainsAnExplanationAndCannotBeSelected()
    {
        var contract = Contract();
        var heavy = contract["options"]!.AsArray()[2]!.AsObject();
        heavy["available"] = false;
        heavy["unavailableReason"] = "The user's offline and hardware restrictions prevent this route";
        heavy["items"] = new JsonArray();
        Assert.True(Capture(Plan(contract), out var result));
        Assert.False(result!.Options[2].Available);
        Assert.Contains("offline", result.Options[2].UnavailableReason);
        Assert.Null(result.ToProposal("heavy"));
        Assert.NotNull(result.ToProposal("light"));
        contract["recommended"] = "heavy";
        Assert.False(Capture(Plan(contract), out _));
    }

    [Fact]
    public void UnavailableAlternativeRequiresAnHonestReason()
    {
        var value = Contract();
        FirstOption(value)["available"] = false;
        Assert.False(Capture(Plan(value), out _));
        FirstOption(value)["unavailableReason"] = " ";
        Assert.False(Capture(Plan(value), out _));
    }

    [Fact]
    public void WarningsAreOptionalButMustBeBoundedStringArraysWhenPresent()
    {
        var value = Contract();
        FirstOption(value).Remove("warnings");
        Assert.True(Capture(Plan(value), out var result));
        Assert.Empty(result!.Options[0].Warnings);
        FirstOption(value)["warnings"] = "A flat string is not the contract";
        Assert.False(Capture(Plan(value), out _));
        FirstOption(value)["warnings"] = new JsonArray(4);
        Assert.False(Capture(Plan(value), out _));
        FirstOption(value)["warnings"] = new JsonArray(Enumerable.Range(0, 17).Select(_ => (JsonNode?)JsonValue.Create("warning")).ToArray());
        Assert.False(Capture(Plan(value), out _));
    }

    [Theory]
    [InlineData("http://example.com/tool")]
    [InlineData("https://example.local/tool")]
    [InlineData("https://localhost/tool")]
    [InlineData("https://127.0.0.1/tool")]
    [InlineData("https://[::1]/tool")]
    [InlineData("https://example.com:444/tool")]
    [InlineData("https://user:password@example.com/tool")]
    [InlineData("https://example.com/tool#fragment")]
    [InlineData("file:///C:/tool.exe")]
    [InlineData("https://example.com\\tool")]
    [InlineData("https://example.com/tool\n")]
    public void UnsafeUrlsRejectTheWholeAlternativeOffer(string url)
    {
        var value = Contract();
        FirstItem(value)["sourceUrl"] = url;
        Assert.False(Capture(Plan(value), out _));
        FirstItem(value)["sourceUrl"] = "";
        FirstItem(value)["downloadUrl"] = url;
        Assert.False(Capture(Plan(value), out _));
    }

    [Fact]
    public void EmptyUnknownInformationIsPreservedWithoutSoftwareNameInference()
    {
        var value = Contract();
        FirstItem(value)["name"] = "Godot";
        FirstItem(value)["sourceUrl"] = "";
        FirstItem(value)["installTargetKey"] = "";
        Assert.True(Capture(Plan(value), out var result));
        Assert.Null(result!.Options[0].Items[0].InstallTargetKey);
        Assert.Null(result.Options[0].Items[0].SourceUrl);
        FirstItem(value)["installTargetKey"] = "unknown-custom-target";
        Assert.True(Capture(Plan(value), out result));
        Assert.Equal("unknown-custom-target", result!.Options[0].Items[0].InstallTargetKey);
    }

    [Theory]
    [InlineData("powershell -Command malicious")]
    [InlineData("C:\\tool.exe")]
    [InlineData("app;command")]
    [InlineData("https://example.com/install.ps1")]
    [InlineData("$(command)")]
    [InlineData("app\n")]
    public void InstallationCodesAcceptDataOnlyAndNeverCommandsOrPaths(string code)
    {
        var value = Contract();
        FirstItem(value)["installTargetKey"] = code;
        Assert.False(Capture(Plan(value), out _));
    }

    [Fact]
    public void MissingDuplicateAndUnrecognizedTierIdsAreRejected()
    {
        var value = Contract();
        value["options"]!.AsArray().RemoveAt(2);
        Assert.False(Capture(Plan(value), out _));
        value = Contract();
        value["options"]!.AsArray().Add(Option("heavy"));
        Assert.False(Capture(Plan(value), out _));
        value = Contract();
        FirstOption(value)["id"] = "medium";
        Assert.False(Capture(Plan(value), out _));
        FirstOption(value)["id"] = "custom";
        Assert.False(Capture(Plan(value), out _));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("goal")]
    [InlineData("recommended")]
    [InlineData("options")]
    public void RootContractRequiresEveryField(string key)
    {
        var value = Contract();
        value.Remove(key);
        Assert.False(Capture(Plan(value), out _));
    }

    [Fact]
    public void WrongSchemasWrongTypesUnknownFieldsAndDuplicatePropertiesAreRejected()
    {
        var value = Contract();
        value["schema"] = 2;
        Assert.False(Capture(Plan(value), out _));
        value["schema"] = "1";
        Assert.False(Capture(Plan(value), out _));
        value["schema"] = 1;
        value["execute"] = "install anything";
        Assert.False(Capture(Plan(value), out _));
        var duplicate = Plan().Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal);
        Assert.False(Capture(duplicate, out _));
        value = Contract();
        FirstOption(value)["available"] = "true";
        Assert.False(Capture(Plan(value), out _));
        value = Contract();
        FirstItem(value)["version"] = null;
        Assert.False(Capture(Plan(value), out _));
    }

    [Fact]
    public void EmptySelectableListsAndOversizedFieldsOrListsAreRejected()
    {
        var value = Contract();
        FirstOption(value)["items"] = new JsonArray();
        Assert.False(Capture(Plan(value), out _));
        value = Contract();
        FirstOption(value)["name"] = new string('x', 121);
        Assert.False(Capture(Plan(value), out _));
        value = Contract();
        FirstOption(value)["items"] = new JsonArray(Enumerable.Range(0, 33).Select(i => (JsonNode?)Item("Item " + i)).ToArray());
        Assert.False(Capture(Plan(value), out _));
        value = Contract();
        value["goal"] = new string('x', 4097);
        Assert.False(Capture(Plan(value), out _));
        Assert.False(Capture(new string('x', 65537) + Plan(), out _));
        value = Contract();
        FirstItem(value)["name"] = "bad\u0000name";
        Assert.False(Capture(Plan(value), out _));
    }

    [Fact]
    public void LiteralSamePlanWithOnlyTierLabelsChangedIsRejected()
    {
        var value = Contract();
        var duplicate = FirstOption(value).DeepClone().AsObject();
        duplicate["id"] = "medium";
        duplicate["name"] = "Medium label on the same plan";
        value["options"]!.AsArray()[1] = duplicate;
        Assert.False(Capture(Plan(value), out _));
        duplicate["requirements"] = "Same main environment but a separately configured automation pipeline";
        Assert.True(Capture(Plan(value), out _));
    }

    [Fact]
    public void CapturesCanonicalTierOrderEvenIfModelReturnsAnUnorderedArray()
    {
        var value = Contract();
        value["options"] = new JsonArray(Option("heavy"), Option("light"), Option("medium"));
        Assert.True(Capture(Plan(value), out var result));
        Assert.Equal(new[] { "light", "medium", "heavy" }, result!.Options.Select(x => x.Id));
    }

    [Fact]
    public void DuplicateMixedAndIncompleteContractsDoNotOfferASelection()
    {
        var content = Plan();
        Assert.False(Capture(content + "\n" + content, out _));
        var single = "工具流名称：Single plan\nExplanation\n```toolflow-items\nApp | software | | | |\n```";
        Assert.False(Capture(content + "\n" + single, out _));
        Assert.False(ToolFlowProposalParser.TryCapture([Message("user", "Help"), Message("assistant", content + "\n" + single)], 1, out _));
        Assert.False(Capture(content[..content.LastIndexOf("```", StringComparison.Ordinal)], out _));
        Assert.False(Capture(content + "\n```toolflow-options\n", out _));
        Assert.False(Capture(content + "\n```toolflow-opt", out _));
        Assert.True(Capture(content, out _));
    }

    [Fact]
    public void PlainAnswersAndProtocolExamplesInsideOrdinaryFencesAreNotOffers()
    {
        Assert.False(Capture("Here is ordinary advice", out _));
        var example = "````text\n" + Plan() + "\n````";
        Assert.False(Capture(example, out _));
        Assert.Equal(example, ToolFlowProposalParser.GetVisibleContent(example));
    }

    private static string GenericOffer(JsonObject? value = null) =>
        "三档都包含 Agent、模型接入、引擎和安卓构建环境，我推荐中量档。\n```json\n"
        + (value ?? Contract()).ToJsonString() + "\n```";

    [Fact]
    public void GenericJsonOfferFromAnExistingGoalCapturesCardsAndKeepsOriginalText()
    {
        var content = GenericOffer();
        ToolFlowConversationMessage[] context =
        [Message("user", "我想做一款 2D 游戏"), Message("assistant", "只安卓还是双平台？"),
         Message("user", "1"), Message("assistant", content)];
        Assert.True(ToolFlowProposalParser.TryCaptureRecommendations(context, 3, out var result));
        Assert.Equal("medium", result!.RecommendedId);
        Assert.Equal(3, result.Options.Count);
        var chosen = result.ToProposal("medium")!;
        Assert.All(chosen.Items, item => Assert.StartsWith("medium ", item.Name));
        Assert.Equal(content, chosen.Conversation[^1].Content);
        var visible = ToolFlowProposalParser.GetVisibleContent(content);
        Assert.Contains("推荐中量档", visible);
        Assert.DoesNotContain("\"schema\"", visible);
        Assert.DoesNotContain("```", visible);
    }

    [Theory]
    [InlineData("这是推荐三档卡片的 JSON 格式示例。")]
    [InlineData("三档 schema example, I recommend this format.")]
    [InlineData("推荐阅读以下内容。")]
    [InlineData("轻量中量重量的字段如下。")]
    public void GenericContractsWithoutActualRecommendationIntentRemainReadableExamples(string introduction)
    {
        var content = introduction + "\n```json\n" + Contract().ToJsonString() + "\n```";
        Assert.False(Capture(content, out _));
        Assert.Equal(content, ToolFlowProposalParser.GetVisibleContent(content));
    }

    [Fact]
    public void GenericCompatibilityDoesNotHideOrdinaryJsonOrNestedExamples()
    {
        var ordinary = "三档推荐中量。\n```json\n{\"message\":\"ordinary JSON\"}\n```";
        Assert.False(Capture(ordinary, out _));
        Assert.Equal(ordinary, ToolFlowProposalParser.GetVisibleContent(ordinary));
        var outerExample = "````text\n" + GenericOffer() + "\n````";
        Assert.False(Capture(outerExample, out _));
        Assert.Equal(outerExample, ToolFlowProposalParser.GetVisibleContent(outerExample));
    }

    [Fact]
    public void MixedDuplicateAndPartialGenericOffersCannotBeChosen()
    {
        var offer = GenericOffer();
        Assert.False(Capture(offer + "\n" + offer, out _));
        Assert.False(Capture(offer + "\n" + Plan(), out _));
        var single = "工具流名称：Single plan\nExplanation\n```toolflow-items\nApp | software | | | |\n```";
        Assert.False(Capture(offer + "\n" + single, out _));
        Assert.False(ToolFlowProposalParser.TryCapture(
            [Message("user", "Help"), Message("assistant", offer + "\n" + single)], 1, out _));
        var incomplete = offer[..offer.LastIndexOf("```", StringComparison.Ordinal)];
        Assert.False(Capture(incomplete, out _));
        Assert.DoesNotContain("\"schema\"", ToolFlowProposalParser.GetVisibleContent(incomplete));
        Assert.False(Capture(offer + "\n" + incomplete, out _));
        var invalid = Contract();
        FirstOption(invalid)["items"] = new JsonArray();
        var invalidDraft = GenericOffer(invalid);
        Assert.False(Capture(offer + "\n" + invalidDraft, out _));
        Assert.Equal(invalidDraft, ToolFlowProposalParser.GetVisibleContent(invalidDraft));
        var header = "三档推荐中量。\n```json\n{\"schema\":1,\"goal\":\"Android game\",\"recommended\":\"medium\",\"options\":[{";
        Assert.False(Capture(header, out _));
        Assert.Equal("三档推荐中量。\n", ToolFlowProposalParser.GetVisibleContent(header));
    }

    [Fact]
    public void GenericOffersStillRequireTheWholeStrictContract()
    {
        var value = Contract();
        FirstItem(value)["installTargetKey"] = "powershell -Command run";
        Assert.False(Capture(GenericOffer(value), out _));
        value = Contract();
        value["schema"] = 2;
        Assert.False(Capture(GenericOffer(value), out _));
        value = Contract();
        value["execute"] = "arbitrary command";
        Assert.False(Capture(GenericOffer(value), out _));
        Assert.False(Capture(GenericOffer().Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal), out _));
    }

    [Fact]
    public void ConflictingClosedGenericDraftIsRejectedRegardlessOfPropertyOrder()
    {
        var invalid = Contract();
        FirstOption(invalid)["items"] = new JsonArray();
        var reordered = new JsonObject
        {
            ["options"] = invalid["options"]!.DeepClone(), ["schema"] = 1,
            ["goal"] = invalid["goal"]!.DeepClone(), ["recommended"] = "medium",
        };
        Assert.False(Capture(GenericOffer() + "\n" + GenericOffer(reordered), out _));
        Assert.Equal(GenericOffer(reordered), ToolFlowProposalParser.GetVisibleContent(GenericOffer(reordered)));
    }

    [Fact]
    public void MissingUserOwnershipAndInvalidMessageIndicesAreRejected()
    {
        Assert.False(ToolFlowProposalParser.TryCaptureRecommendations([Message("assistant", Plan())], 0, out _));
        Assert.False(ToolFlowProposalParser.TryCaptureRecommendations([Message("user", Plan())], 0, out _));
        Assert.False(ToolFlowProposalParser.TryCaptureRecommendations([Message("user", "Help")], -1, out _));
        Assert.False(ToolFlowProposalParser.TryCaptureRecommendations([Message("user", "Help")], 1, out _));
    }

    [Fact]
    public void DisplayStripsOnlyProtocolDataIncludingAnUnfinishedStreamingBlock()
    {
        var ordinary = "说明\n```csharp\nvar answer = 42;\n```\n";
        var content = ordinary + Plan() + "\n后续说明";
        var visible = ToolFlowProposalParser.GetVisibleContent(content);
        Assert.StartsWith(ordinary, visible);
        Assert.Contains("建议按配置成本", visible);
        Assert.Contains("后续说明", visible);
        Assert.DoesNotContain("\"schema\"", visible);
        Assert.DoesNotContain("toolflow-options", visible);
        Assert.Equal(ordinary, ToolFlowProposalParser.GetVisibleContent(ordinary + "```toolflow-options\n{\"schema\":1"));
        Assert.Equal(ordinary, ToolFlowProposalParser.GetVisibleContent(ordinary + "```toolflow-items\nApp | soft"));
        Assert.Equal(ordinary, ToolFlowProposalParser.GetVisibleContent(ordinary + "```toolflow-opt"));
        Assert.Equal("Before\r\nAfter", ToolFlowProposalParser.GetVisibleContent("Before\r\n```toolflow-items\r\nApp | software | | | |\r\n```\r\nAfter"));
    }

    [Fact]
    public void SelectedTextPreservesSpecialPunctuationAsJsonAndDoesNotMergeAlternatives()
    {
        var value = Contract();
        FirstItem(value)["name"] = "My app | with punctuation";
        FirstOption(value)["summary"] = "Line one\nLine two";
        Assert.True(Capture(Plan(value), out var result));
        var proposal = result!.ToProposal("light")!;
        var start = proposal.Text.IndexOf("```toolflow-selected\n", StringComparison.Ordinal);
        // AppendLine uses the host newline; JSON is extracted independently of it.
        if (start < 0) start = proposal.Text.IndexOf("```toolflow-selected\r\n", StringComparison.Ordinal);
        var jsonStart = proposal.Text.IndexOf('\n', start) + 1;
        var jsonEnd = proposal.Text.LastIndexOf("```", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(proposal.Text[jsonStart..jsonEnd]);
        Assert.Equal("My app | with punctuation", document.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal("Line one\nLine two", document.RootElement.GetProperty("summary").GetString());
        Assert.False(document.RootElement.TryGetProperty("options", out _));
    }

    [Fact]
    public void SharedPromptDescribesThreeCardsAndSingleSoftwareCompatibilityWithoutInventingTargets()
    {
        var prompt = ToolFlowProposalParser.BuildOutputInstructions(["z-target", "a-target"]);
        Assert.Contains("toolflow-options", prompt);
        Assert.Contains("schema:1", prompt);
        Assert.Contains("light", prompt);
        Assert.Contains("medium", prompt);
        Assert.Contains("heavy", prompt);
        Assert.Contains("available:false", prompt);
        Assert.Contains("核心AI Agent", prompt);
        Assert.Contains("兼容", prompt);
        Assert.Contains("toolflow-items", prompt);
        Assert.Contains("Workflow name", prompt);
        Assert.Contains("a-target, z-target", prompt);
        Assert.DoesNotContain("godot", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
