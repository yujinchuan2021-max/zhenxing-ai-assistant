using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowTaskPresentationTests
{
    [Fact]
    public void FailedAutomaticItemRemainsActionableAndDoesNotClaimReady()
    {
        var selection = Plan();
        var result = new ToolFlowInstallResult([new("engine", "Godot", ToolFlowInstallItemStatus.Failed, "check failed")]);
        var card = ToolFlowTaskPresentation.Create(selection, result, knownTargets: ["godot"]);
        Assert.True(card.CanPrepare);
        Assert.False(card.Running);
        Assert.Contains("0/2", card.Summary);
        Assert.Equal(selection.ProjectGoal, card.Goal);
        Assert.Equal(selection.FlowName, card.Plan);
    }

    [Fact]
    public void CheckedInstallationStillShowsTheManualStepInsteadOfProjectCompletion()
    {
        var result = new ToolFlowInstallResult([new("engine", "Godot", ToolFlowInstallItemStatus.Installed, "checked")]);
        var card = ToolFlowTaskPresentation.Create(Plan(), result, knownTargets: ["godot"]);
        Assert.False(card.CanPrepare);
        Assert.Contains("1/2", card.Summary);
        Assert.Contains("model account", card.NextStep);
    }

    [Fact]
    public void StoppedRunPreservesTheCheckedItemAndLeavesTheRestActionable()
    {
        var selection = Plan() with { Items = [Plan().Items[0], new ToolFlowItem { ItemId = "editor", Name = "Editor", Kind = "software", InstallTargetKey = "editor" }] };
        var partial = new ToolFlowInstallResult([new("engine", "Godot", ToolFlowInstallItemStatus.AlreadyInstalled, "checked")]) { WasCanceled = true };
        var card = ToolFlowTaskPresentation.Create(selection, partial, stopped: true, knownTargets: ["godot", "editor"]);
        Assert.True(card.CanPrepare);
        Assert.False(card.Running);
        Assert.Contains("1/2", card.Summary);
    }

    [Fact]
    public void RunningCardShowsOnlyTheReportedStageWithoutPretendingItIsInstalled()
    {
        var card = ToolFlowTaskPresentation.Create(Plan(), running: true, activeStep: "checking Godot", knownTargets: ["godot"]);
        Assert.True(card.Running);
        Assert.Equal("checking Godot", card.NextStep);
        Assert.Contains("0/2", card.Summary);
        Assert.DoesNotContain("%", card.Summary);
    }

    [Fact]
    public void SelfReportedManualCompletionIsNotAnInstallationVerification()
    {
        var selection = Plan() with { Items = [Plan().Items[1]], ItemMarks = [new ToolFlowItemMark
            { ItemId = "account", Kind = ToolFlowItemMark.UserReportedDone, AtUtc = DateTimeOffset.UtcNow }] };
        var card = ToolFlowTaskPresentation.Create(selection, knownTargets: ["godot"]);
        Assert.False(card.CanPrepare);
        Assert.Contains("0/1", card.Summary);
    }

    private static ToolFlowSelection Plan() => new()
    {
        FlowId = Guid.NewGuid().ToString("D"), SubmissionId = Guid.NewGuid().ToString("D"),
        Origin = ToolFlowOrigin.Assistant, SelectedAtUtc = DateTimeOffset.UtcNow,
        ConversationId = "isolated-chat", FlowName = "2D game toolchain", ProjectGoal = "My Android 2D game",
        GoalDescription = "My Android 2D game", FlowText = "Engine and external Agent setup",
        UploadEnabledAtSelection = false, Conversation = [],
        Items = [new ToolFlowItem { ItemId = "engine", Name = "Godot", Kind = "software", InstallTargetKey = "godot" },
            new ToolFlowItem { ItemId = "account", Name = "model account", Kind = "account", ManualHint = "Configure the external Agent model" }],
    };
}
