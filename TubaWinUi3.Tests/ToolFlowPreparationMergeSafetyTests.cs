using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowPreparationMergeSafetyTests
{
    [Fact]
    public async Task AManualHintSurvivesFreshPreparationAndResumeMerge()
    {
        var item = Item("Model account", null) with { Kind = "service", ManualHint = "Create an account, then choose the local model." };
        var selection = Selection(item);
        var preparation = await ManualService().PrepareAsync(selection.Items);
        Assert.Contains(item.ManualHint, preparation.Rows[0].Message);
        var merged = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(selection), preparation);
        Assert.Contains(item.ManualHint, merged.Rows[0].StatusLine);
        Assert.Equal(item.ManualHint, merged.Rows[0].ManualHint);
        Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, merged.Rows[0].State);
        Assert.False(merged.Rows[0].CanContinueAutomatically);
    }

    [Theory]
    [InlineData(ToolFlowEventKind.Verified, ToolFlowResumeItemState.InstalledOrDetected)]
    [InlineData(ToolFlowEventKind.InstallFailed, ToolFlowResumeItemState.Failed)]
    public async Task AnUnprobeableManualItemPreservesItsVerifiedOrFailedHistory(ToolFlowEventKind kind,
        ToolFlowResumeItemState expected)
    {
        var item = Item("Custom manual software", null) with { ManualHint = "Follow the saved manual instructions." };
        var selection = Selection(item) with { Events = [Event(item, kind)] };
        var oldView = ToolFlowResume.Build(selection);
        var preparation = await ManualService().PrepareAsync(selection.Items);
        Assert.False(preparation.Rows[0].DetectionAttempted);
        var merged = ToolFlowResume.ApplyPreparation(oldView, preparation);
        Assert.Equal(expected, merged.Rows[0].State);
        Assert.Equal(oldView.Rows[0], merged.Rows[0]);
    }

    [Fact]
    public async Task CheckedMissingFfmpegDowngradesHistoricalVerificationWithoutEnablingInstallation()
    {
        var item = Item("FFmpeg", null) with { ManualHint = "Use the format converter to prepare its FFmpeg component." };
        var selection = Selection(item) with { Events = [Event(item, ToolFlowEventKind.Verified)] };
        var preparation = await new ToolFlowPreparationService([], (_, _) => Task.FromResult<InstalledToolEvidence?>(null))
            .PrepareAsync(selection.Items);
        Assert.True(preparation.Rows[0].DetectionAttempted);
        var merged = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(selection), preparation);
        Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, merged.Rows[0].State);
        Assert.Contains(item.ManualHint, merged.Rows[0].StatusLine);
        Assert.Empty(merged.PendingAutomaticItems);
        Assert.Equal(0, merged.InstalledOrDetectedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualSelfReportsRemainUnverifiedAndProbeErrorsTakePriority(bool probeFails)
    {
        var item = Item("FFmpeg", "ffmpeg");
        var selection = Selection(item) with
        {
            ItemMarks = [new() { ItemId = item.ItemId, Kind = ToolFlowItemMark.UserReportedDone, AtUtc = DateTimeOffset.UtcNow }],
        };
        var preparation = await new ToolFlowPreparationService([], (_, _) => probeFails
            ? throw new IOException("PRIVATE-LOCAL-PATH") : Task.FromResult<InstalledToolEvidence?>(null))
            .PrepareAsync(selection.Items);
        var merged = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(selection), preparation);
        Assert.Equal(probeFails ? ToolFlowResumeItemState.Failed : ToolFlowResumeItemState.UserReportedDone, merged.Rows[0].State);
        Assert.Equal(0, merged.InstalledOrDetectedCount);
        Assert.Empty(merged.PendingAutomaticItems);
        Assert.DoesNotContain("PRIVATE-LOCAL-PATH", merged.Rows[0].StatusLine);
    }

    [Fact]
    public async Task CachedDetectedEvidenceCannotOverwriteThisRunsFailure()
    {
        var item = Item("Godot", "godot");
        var selection = Selection(item);
        var preparation = await new ToolFlowPreparationService(["godot"], (_, _) =>
            Task.FromResult<InstalledToolEvidence?>(new("godot", "Godot"))).PrepareAsync(selection.Items);
        var view = ToolFlowResume.Build(selection, new([new(item.ItemId, item.Name, ToolFlowInstallItemStatus.Failed, "Verification failed.")]));
        var merged = ToolFlowResume.ApplyPreparation(view, preparation);
        Assert.Equal(ToolFlowResumeItemState.Failed, merged.Rows[0].State);
        Assert.Equal(view.Rows[0], merged.Rows[0]);
        Assert.Equal(0, merged.InstalledOrDetectedCount);
    }

    [Fact]
    public async Task ANewReadOnlyCheckCanRecoverAnEarlierProbeFailure()
    {
        var item = Item("FFmpeg", "ffmpeg");
        var selection = Selection(item);
        var failedPreparation = await new ToolFlowPreparationService([], (_, _) => throw new IOException("Unreadable candidate"))
            .PrepareAsync(selection.Items);
        var failedView = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(selection), failedPreparation);
        Assert.Equal(ToolFlowResumeItemState.Failed, failedView.Rows[0].State);
        var recovered = await new ToolFlowPreparationService([], (_, _) =>
            Task.FromResult<InstalledToolEvidence?>(new("ffmpeg", "FFmpeg", @"C:\FAKE\ffmpeg.exe")))
            .PrepareAsync(selection.Items);
        var merged = ToolFlowResume.ApplyPreparation(failedView, recovered);
        Assert.Equal(ToolFlowResumeItemState.InstalledOrDetected, merged.Rows[0].State);
        Assert.Empty(merged.PendingAutomaticItems);
    }

    [Theory]
    [InlineData("ffmpeg")]
    [InlineData("arbitrary-command")]
    public void ExpandedDetectionListsAndForgedPreparationPermissionCannotExpandAutomaticInstallation(string target)
    {
        var item = Item("Other tool", target);
        var selection = Selection(item);
        var view = ToolFlowResume.Build(selection);
        Assert.False(view.Rows[0].CanContinueAutomatically);
        var preparation = new ToolFlowPreparation([new(item, new(ToolFlowRequirementKind.ExactTarget),
            ToolFlowPreparationState.PendingAutomatic) { AutomaticInstallationAllowed = true, DetectionAttempted = true }]);
        var merged = ToolFlowResume.ApplyPreparation(view, preparation);
        Assert.False(merged.Rows[0].CanContinueAutomatically);
        Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, merged.Rows[0].State);
        Assert.Empty(merged.PendingAutomaticItems);
    }

    [Fact]
    public async Task ExplicitFakeInstallationScopeRemainsSupportedWithoutMergingTheProbeList()
    {
        var editor = Item("Editor", "editor");
        var encoder = Item("FFmpeg", "ffmpeg");
        var selection = Selection(editor) with { Items = [editor, encoder] };
        var preparation = await new ToolFlowPreparationService(["editor"],
            (_, _) => Task.FromResult<InstalledToolEvidence?>(null), ["editor", "ffmpeg"])
            .PrepareAsync(selection.Items);
        var merged = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(selection, knownTargets: ["editor"]), preparation);
        Assert.True(merged.Rows[0].CanContinueAutomatically);
        Assert.False(merged.Rows[1].CanContinueAutomatically);
        Assert.Equal(new[] { editor.ItemId }, merged.PendingAutomaticItems.Select(x => x.ItemId));
    }

    private static ToolFlowPreparationService ManualService()
        => new([], (_, _) => throw new Exception("Pure manual items must not trigger a probe"));
    private static ToolFlowItem Item(string name, string? target) => new()
    {
        ItemId = Guid.NewGuid().ToString("D"), Name = name, Kind = "software", InstallTargetKey = target,
    };
    private static ToolFlowSelection Selection(ToolFlowItem item) => new()
    {
        FlowId = Guid.NewGuid().ToString("D"), SubmissionId = Guid.NewGuid().ToString("D"),
        Origin = ToolFlowOrigin.Assistant, SelectedAtUtc = DateTimeOffset.UtcNow,
        GoalDescription = "Prepare the saved plan", FlowText = "Prepare the saved plan", UploadEnabledAtSelection = false,
        Conversation = [], Items = [item],
    };
    private static ToolFlowExecutionEvent Event(ToolFlowItem item, ToolFlowEventKind kind) => new()
    {
        Id = Guid.NewGuid().ToString("D"), ItemId = item.ItemId, Kind = kind,
        AtUtc = DateTimeOffset.Parse("2026-09-24T09:00:00Z"), Detail = "Saved local execution evidence.",
    };
}
