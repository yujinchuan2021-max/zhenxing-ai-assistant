using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace TubaWinUi3.Tests;

/// <summary>
/// Page wiring contracts that can be checked without constructing AiAgentPage or
/// initializing any product service. Native control behavior lives in GoalGuideCases.
/// These tests read repository source only; they never open user data or perform actions.
/// </summary>
public sealed class ToolFlowGoalUiContractTests
{
    [Fact]
    public void WorkbenchAndConversationHaveIndependentScrollRegionsAndOneComposer()
    {
        var document = XDocument.Load(PagePath("AiAgentPage.xaml"));
        var name = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        var taskHost = document.Descendants().Single(element => (string?)element.Attribute(name) == "ToolFlowTaskHost");
        var messages = document.Descendants().Single(element => (string?)element.Attribute(name) == "MsgScroll");
        var workbench = document.Descendants().Single(element => (string?)element.Attribute(name) == "WorkbenchScroll");
        var assistant = document.Descendants().Single(element => (string?)element.Attribute(name) == "AssistantPane");
        var composer = document.Descendants().Single(element => (string?)element.Attribute(name) == "ComposerHost");

        Assert.Equal("ScrollViewer", workbench.Name.LocalName);
        Assert.Contains(workbench, taskHost.Ancestors());
        Assert.DoesNotContain(messages, taskHost.Ancestors());
        Assert.Contains(assistant, messages.Ancestors());
        Assert.Equal(workbench.Parent, assistant.Parent);
        Assert.NotEqual((string?)workbench.Attribute("Grid.Column") ?? "0", (string?)assistant.Attribute("Grid.Column") ?? "0");
        Assert.Equal(assistant, composer.Parent);
        var messageRegion = messages.Ancestors().Single(element => element.Parent == assistant);
        Assert.True(int.Parse((string?)messageRegion.Attribute("Grid.Row") ?? "0") < int.Parse(composer.Attribute("Grid.Row")!.Value));
        Assert.Single(document.Descendants(), element => (string?)element.Attribute(name) == "InputBox");
        Assert.Equal("Stretch", taskHost.Attribute("HorizontalContentAlignment")!.Value);
    }

    [Fact]
    public void TaskSummaryAndGoalDialogUseTheSamePreparedReadinessProjection()
    {
        var task = Source("AiAgentPage.Task.cs");
        var dialog = Source("AiAgentPage.GoalFlow.cs");
        var tools = Source("AiAgentPage.Tools.cs");
        var readiness = Between(tools, "private ToolFlowResumeView CurrentToolFlowReadiness", "private async Task UseInstalledToolAsync");

        Assert.Contains("ToolFlowGoalGuide.Create(CurrentToolFlowReadiness(", task);
        Assert.Contains("ToolFlowGoalGuide.Create(CurrentToolFlowReadiness(", dialog);
        Assert.Contains("await RefreshToolAccessAsync(current, force: true)", dialog);
        Assert.DoesNotContain("new ToolFlowPreparationService", dialog); // no second independent dialog probe
        Assert.Contains("_toolAccessSelectionId == selection.SubmissionId", readiness);
        Assert.Contains("_taskPreparation", readiness);
        Assert.Contains("ToolFlowResume.ApplyPreparation(view, preparation)", readiness);
    }

    [Fact]
    public void DetectionPublishesOnlyAfterTheCurrentPageAndSelectionAreChecked()
    {
        var source = Source("AiAgentPage.Tools.cs");
        var probe = Between(source, "private async Task RefreshToolAccessAsync", "private void RefreshToolAccessControl");
        var guard = Regex.Match(probe, @"bool IsCurrent\(\)\s*=>\s*(?<body>[^;]+);").Groups["body"].Value;

        Assert.Contains("!_lease.IsClosed", guard);
        Assert.Contains("IsLoaded", guard);
        Assert.Contains("epoch == _displayEpoch", guard);
        Assert.Contains("viewEpoch == _toolFlowViewEpoch", guard);
        Assert.Contains("conversationId, _session?.Id", guard);
        Assert.Contains("ReferenceEquals(_toolAccessProbe, probe)", guard);
        Assert.Contains("_taskSelection?.SubmissionId == selection.SubmissionId", guard);
        Assert.Matches(@"if\s*\(!IsCurrent\(\)\)\s*return;\s*_taskPreparation\s*=\s*preparation;\s*_toolAccessSelectionId\s*=\s*selection.SubmissionId;", probe);
    }

    [Fact]
    public void LeavingThePageCancelsDetectionAndClearsItsCachedEvidence()
    {
        var page = Source("AiAgentPage.xaml.cs");
        var unload = Between(page, "Unloaded +=", "SizeChanged +=");
        var clear = Between(Source("AiAgentPage.Tools.cs"), "private void ClearToolAccess()", "private async Task RefreshToolAccessAsync");

        Assert.Contains("_toolFlowViewEpoch++", unload);
        Assert.Contains("ClearToolAccess()", unload);
        Assert.Contains("_toolAccessProbe?.Cancel()", clear);
        Assert.Contains("_toolAccessSelectionId = null", clear);
        Assert.Contains("_toolAccessProbeId = null", clear);
        Assert.Contains("_taskPreparation = null", clear);
        Assert.Contains("_taskToolEntries = []", clear);
    }

    [Fact]
    public void AManualConfirmationRechecksTheCurrentStepAndWritesOnlyASelfReport()
    {
        var dialog = Source("AiAgentPage.GoalFlow.cs");
        var handler = Between(dialog, "control.ActionRequested +=", "_refreshToolFlowGoalDialog = Render;");
        var confirmation = Between(handler, "case ToolFlowGoalAction.ConfirmManual:", "case ToolFlowGoalAction.CheckAgain:");

        Assert.Contains("if (!CanReview() || busy) return", handler);
        Assert.Contains("store.GetBySubmissionId(initial.SubmissionId)", handler);
        Assert.Contains("guide.CurrentStep?.Id == step.Id ? guide.CurrentStep", handler);
        Assert.Contains("guide.LaterSteps.FirstOrDefault(candidate => candidate.Id == step.Id && candidate.IsOptional)", handler);
        Assert.Contains("active is null || control.CurrentStepId != step.Id", handler);
        Assert.True(handler.IndexOf("busy = true", StringComparison.Ordinal) < handler.IndexOf("MarkItemUserReportedDone", StringComparison.Ordinal));
        Assert.Contains("CanConfirmManual: true", confirmation);
        Assert.Contains("!row.CanContinueAutomatically", confirmation);
        Assert.Contains("ToolFlowResumeItemState.NeedsUserAssist or ToolFlowResumeItemState.Failed", confirmation);
        Assert.Contains("MarkItemUserReportedDone", confirmation);
        Assert.DoesNotContain("ToolFlowEventKind.Verified", confirmation);
        Assert.DoesNotContain("ToolFlowUploadClient", confirmation);
    }

    [Fact]
    public void ResumeContinuesTheConfirmedPlanWhileAnExplicitOptionalActionKeepsItsScope()
    {
        var page = Source("AiAgentPage.xaml.cs");
        var localization = Between(page, "public void ApplyLocalization()", "SidebarNewChatText.Text");
        var goal = Source("AiAgentPage.GoalFlow.cs");
        var automatic = Between(goal, "case ToolFlowGoalAction.ContinueAutomatic:", "case ToolFlowGoalAction.Handoff:");
        var flow = Source("AiAgentPage.ToolFlows.cs");

        Assert.Contains("_refreshToolFlowGoalDialog?.Invoke()", localization);
        Assert.Contains("active!.IsOptional ? active.Rows", automatic);
        Assert.Contains("CurrentToolFlowReadiness(latest, lastResult).PendingAutomaticItems", automatic);
        Assert.Contains("pendingRows.Where(row => row.CanContinueAutomatically)", automatic);
        Assert.Contains("new(ToolFlowResumeAction.ContinueAutomatic, ids)", automatic);
        Assert.Contains("itemIds: action.ItemIds", flow);
        Assert.Contains("itemIds: itemIds", flow);
    }

    [Fact]
    public void WholePlanConfirmationIsNeverLimitedByTheVisualCurrentStep()
    {
        var flow = Source("AiAgentPage.ToolFlows.cs");
        var run = Between(flow, "private async Task<ToolFlowInstallResult?> RunToolFlowInstallWithBubblesAsync",
            "private async Task RunToolFlowFollowUpAsync");
        Assert.DoesNotContain("itemIds ??=", run);
        Assert.DoesNotContain(".CurrentStep", run);
        Assert.Contains("itemIds: itemIds", run);
        Assert.Contains("RunToolFlowInstallWithBubblesAsync(store, chosen, canDisplay: CanDisplay)", flow);
    }

    [Fact]
    public void ManualButtonsOpenAMemberReviewBeforeAnySelfReportCanBeRequested()
    {
        var control = File.ReadAllText(Path.Combine(FontSingleSourceTests.RepoRoot, "TubaWinUi3.WinUI3",
            "Controls", "AgentChat", "ToolFlowGoalControl.cs"));
        var build = Between(control, "private void BuildConfirmation()", "private void Request(");
        var request = Between(control, "private void Request(", "private static Expander NewExpander()");

        Assert.Contains("_confirm.Click += (_, _) => BeginManualConfirmation()", control);
        Assert.Contains("if (!step.HasAutomaticItems && step.SourceUrl is null) BeginManualConfirmation()", control);
        Assert.Contains("_saveConfirmation.IsEnabled && ActiveStep is", control);
        Assert.Contains("row.ManualHint", build);
        Assert.Contains("new CheckBox", build);
        Assert.Contains("IsChecked = _checkedManualIds.Contains(row.ItemId)", build);
        Assert.Contains("rows.All(row => _checkedManualIds.Contains(row.ItemId))", build);
        Assert.Contains("!_confirmationShown", request);
        Assert.Contains("!ManualRows(step).All(row => _checkedManualIds.Contains(row.ItemId))", request);
    }

    private static string PagePath(string file) => Path.Combine(FontSingleSourceTests.RepoRoot,
        "TubaWinUi3.WinUI3", "Pages", file);

    private static string Source(string file) => File.ReadAllText(PagePath(file));

    private static string Between(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Missing source contract start: " + startMarker);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, "Missing source contract end: " + endMarker);
        return source[start..end];
    }
}
