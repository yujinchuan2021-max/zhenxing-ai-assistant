using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>Uses saved-plan fixtures and injected entry evidence. No discovery, IO or launch occurs.</summary>
public sealed class ToolFlowDeliveryPresentationTests
{
    [Fact]
    public void RequiredAccountBlocksDeliveryEvenWhenTheAgentHasALocalEntry()
    {
        var view = View(Item("agent", "Codex", "agent", "codex"), Item("model", "Model account", "model"));
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: [Console("codex")]);
        Assert.NotNull(guide.CurrentStep);
        Assert.Equal(ToolFlowDeliveryAction.None, guide.Delivery!.Action);
        Assert.Contains("完成接入", guide.Stage);
    }

    [Fact]
    public void ReadyAgentHasAnEntryBeforeTheEngineButNotAClaimOfConnectionVerification()
    {
        var view = View(Item("engine", "Godot", "software", "godot"), Item("agent", "Codex", "agent", "codex"));
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: [Gui("godot"), Console("codex")]);
        var delivery = guide.Delivery!;
        Assert.Equal(ToolFlowDeliveryAction.OpenAgentConsole, delivery.Action);
        Assert.Equal("agent", delivery.ItemId);
        Assert.Equal("codex", delivery.AccessEntry!.TargetKey);
        Assert.Contains("核对", delivery.Hint);
        Assert.DoesNotContain("连接已验证", guide.Summary + delivery.Hint);
        Assert.Empty(view.Selection.Events);
    }

    [Fact]
    public void DirectoryOnlyAgentOffersLocationWithoutClaimingItCanBeLaunched()
    {
        var view = View(Item("agent", "Codex", "agent", "codex"));
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: [new("codex", "Codex", null, @"C:\TestTools\codex", false)]);
        Assert.Equal(ToolFlowDeliveryAction.OpenLocation, guide.Delivery!.Action);
        Assert.Contains("工具位置", guide.Delivery.ActionLabel);
    }

    [Fact]
    public void GenericCommandLineToolsCannotBePresentedAsInteractiveAgents()
    {
        var view = View(Item("ffmpeg", "FFmpeg", "agent", "ffmpeg"));
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: [Console("ffmpeg")]);
        Assert.Equal(ToolFlowDeliveryAction.OpenLocation, guide.Delivery!.Action);
    }

    [Fact]
    public void OptionalGuiToolNeverReplacesTheRequiredServiceEntry()
    {
        var view = View(Item("service", "Suno", "service", source: "https://suno.com/"),
            Item("optional", "Optional editor", "software", "godot"));
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: [Gui("godot")]);
        Assert.Equal(ToolFlowDeliveryAction.OpenSource, guide.Delivery!.Action);
        Assert.Equal("service", guide.Delivery.ItemId);
        Assert.Equal("https://suno.com/", guide.Delivery.SourceUrl);
    }

    [Fact]
    public void CommandLineDependencyDoesNotTakeTheMainEntryAwayFromAService()
    {
        var view = View(Item("ffmpeg", "FFmpeg", "software", "ffmpeg"),
            Item("service", "Suno", "service", source: "https://suno.com/"));
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: [Console("ffmpeg")]);
        Assert.Equal(ToolFlowDeliveryAction.OpenSource, guide.Delivery!.Action);
        Assert.Equal("service", guide.Delivery.ItemId);
    }

    [Fact]
    public void SelfReportedServiceCanBeTriedWithoutBecomingVerified()
    {
        var view = View(Item("service", "Suno", "service", source: "https://suno.com/"));
        view = view with { Rows = [view.Rows[0] with { State = ToolFlowResumeItemState.UserReportedDone }] };
        var guide = ToolFlowGoalGuide.Create(view);
        Assert.Equal(ToolFlowDeliveryAction.OpenSource, guide.Delivery!.Action);
        Assert.Equal(ToolFlowGoalStepState.UserConfirmed, guide.ReadySteps[0].State);
        Assert.Contains("你确认", guide.Summary);
        Assert.Contains("尚未验证", guide.Summary);
        Assert.Equal(ToolFlowResumeItemState.UserReportedDone, view.Rows[0].State);
    }

    [Theory]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://suno.com:8443/")]
    [InlineData("https://user:password@suno.com/")]
    [InlineData("javascript:alert(1)")]
    public void UnsafeServiceEntriesFallBackToNotes(string source)
    {
        var guide = ToolFlowGoalGuide.Create(View(Item("service", "Suno", "service", source: source)));
        Assert.Equal(ToolFlowDeliveryAction.Handoff, guide.Delivery!.Action);
        Assert.Null(guide.Delivery.SourceUrl);
    }

    [Fact]
    public void UnrelatedOrAmbiguousLocalEvidenceCannotSupplyALaunchTarget()
    {
        var view = View(Item("engine", "Godot", "software", "godot"));
        var unrelated = ToolFlowGoalGuide.Create(view, accessEntries: [Gui("cursor")]);
        var duplicate = ToolFlowGoalGuide.Create(view, accessEntries: [Gui("godot"), Gui("godot")]);
        Assert.Equal(ToolFlowDeliveryAction.Handoff, unrelated.Delivery!.Action);
        Assert.Equal(ToolFlowDeliveryAction.Handoff, duplicate.Delivery!.Action);
    }

    [Theory]
    [InlineData("codex.exe")]
    [InlineData("\\\\remote\\tools\\codex.exe")]
    [InlineData("C:\\TestTools\\codex.exe:payload")]
    public void UnsafeConsolePathsCannotBecomeALaunchEntry(string path)
    {
        var view = View(Item("agent", "Codex", "agent", "codex"));
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: [new("codex", "Codex", path, null, false)]);
        Assert.Equal(ToolFlowDeliveryAction.Handoff, guide.Delivery!.Action);
        Assert.Null(guide.Delivery.AccessEntry);
    }

    [Fact]
    public void DuplicateSavedItemIdentitiesCannotSupplyALaunchOrWebsiteEntry()
    {
        var item = Item("engine", "Godot", "software", "godot", "https://godotengine.org/");
        var guide = ToolFlowGoalGuide.Create(View(item, item), accessEntries: [Gui("godot")]);
        Assert.Equal(ToolFlowDeliveryAction.None, guide.Delivery!.Action);
        Assert.Null(guide.Delivery.AccessEntry);
        Assert.Null(guide.Delivery.SourceUrl);
    }

    [Fact]
    public void EmptyPlanCannotClaimATryEntry()
    {
        var guide = ToolFlowGoalGuide.Create(View(), accessEntries: [Gui("godot")]);
        Assert.Equal(ToolFlowDeliveryAction.None, guide.Delivery!.Action);
        Assert.Null(guide.Delivery.AccessEntry);
        Assert.Empty(guide.ReadySteps);
    }

    private static ToolFlowToolAccessEntry Gui(string target) => new(target, target,
        @"C:\TestTools\" + target + @"\" + target + ".exe", @"C:\TestTools\" + target, true);

    private static ToolFlowToolAccessEntry Console(string target) => new(target, target,
        @"C:\TestTools\" + target + @"\" + target + ".exe", @"C:\TestTools\" + target, false);

    private static ToolFlowItem Item(string id, string name, string kind, string? target = null, string? source = null) =>
        new() { ItemId = id, Name = name, Kind = kind, InstallTargetKey = target, SourceUrl = source };

    private static ToolFlowResumeView View(params ToolFlowItem[] items)
    {
        var selection = new ToolFlowSelection { FlowId = "fixture-flow", SubmissionId = "fixture-submission",
            Origin = ToolFlowOrigin.User, SelectedAtUtc = DateTimeOffset.Parse("2026-10-04T01:00:00Z"),
            FlowName = "Fixture plan", ProjectGoal = "Prepare the tools for my goal", GoalDescription = "Fixture goal",
            FlowText = "Fixture plan", UploadEnabledAtSelection = false, Conversation = [], Items = items.ToList() };
        var rows = items.Select(item => new ToolFlowResumeItemRow { ItemId = item.ItemId, Name = item.Name,
            Kind = item.Kind, State = item.Kind == "model" ? ToolFlowResumeItemState.NeedsUserAssist
                : ToolFlowResumeItemState.InstalledOrDetected, StatusLine = "Fixture installation evidence" }).ToArray();
        return new(selection, rows, [], rows.Count(row => row.State == ToolFlowResumeItemState.InstalledOrDetected),
            0, 0, rows.Count(row => row.State == ToolFlowResumeItemState.NeedsUserAssist));
    }
}
