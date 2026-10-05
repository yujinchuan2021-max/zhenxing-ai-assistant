using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    private Action? _refreshToolFlowGoalDialog;
    private sealed record ToolFlowResumeRequest(ToolFlowResumeAction Action, IReadOnlyList<string>? ItemIds = null);

    private async Task<ToolFlowResumeRequest> ShowToolFlowResumeDialogAsync(
        ToolFlowSelectionStore store, ToolFlowSelection initial, ToolFlowInstallResult? lastResult)
    {
        var current = store.GetBySubmissionId(initial.SubmissionId) ?? initial;
        var epoch = _displayEpoch;
        var viewEpoch = _toolFlowViewEpoch;
        var conversationId = _session?.Id;
        bool open = true, busy = false, readFailed = false;
        bool CanReview() => open && !_lease.IsClosed && IsLoaded && epoch == _displayEpoch &&
            viewEpoch == _toolFlowViewEpoch && string.Equals(conversationId, _session?.Id, StringComparison.Ordinal) &&
            _taskSelection?.SubmissionId == initial.SubmissionId;
        await RefreshToolAccessAsync(current, force: true);
        if (!CanReview()) return new(ToolFlowResumeAction.None);
        var control = new ToolFlowGoalControl { Width = Math.Min(580, Math.Max(200, XamlRoot.Size.Width - 100)) };
        var request = new ToolFlowResumeRequest(ToolFlowResumeAction.None);
        Uri? pendingSource = null;
        bool canOpenSource = false;
        var outcome = new ToolFlowResumeRequest(ToolFlowResumeAction.None);
        ToolFlowGoalGuide? shown = null;
        ToolFlowDeliveryPresentation? pendingDelivery = null;
        ToolFlowToolAccessEntry? pendingSetupEntry = null;
        var dialog = new ContentDialog { Content = control, XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close };

        void Render()
        {
            if (!CanReview()) return;
            try
            {
                current = store.GetBySubmissionId(initial.SubmissionId)
                    ?? throw new InvalidOperationException(ToolFlowGoalText.Get("AiGoal_RecordMissing", "本机方案记录已移除，请返回对话重新选择。"));
                shown = ToolFlowGoalGuide.Create(CurrentToolFlowReadiness(current, lastResult), accessEntries: _taskToolEntries);
                control.Update(shown, busy);
                dialog.Title = ToolFlowGoalText.Get("AiGoal_DialogTitle", "继续实现你的目标");
                dialog.CloseButtonText = ToolFlowGoalText.Get("AiGoal_BackToChat", "返回对话");
                RefreshToolFlowTaskCard();
            }
            catch (Exception ex)
            {
                busy = true;
                if (shown is not null) control.Update(shown, busy: true);
                if (!readFailed) AddSystemBubble(ToolFlowGoalText.Get("AiGoal_ActionFailed", "这一步暂未处理：") + ex.Message);
                readFailed = true;
            }
        }

        control.ActionRequested += async (step, action) =>
        {
            if (!CanReview() || busy) return;
            try
            {
                var latest = store.GetBySubmissionId(initial.SubmissionId)
                    ?? throw new InvalidOperationException(ToolFlowGoalText.Get("AiGoal_RecordMissing", "本机方案记录已移除，请返回对话重新选择。"));
                var guide = ToolFlowGoalGuide.Create(CurrentToolFlowReadiness(latest, lastResult), accessEntries: _taskToolEntries);
                var active = guide.CurrentStep?.Id == step.Id ? guide.CurrentStep
                    : guide.LaterSteps.FirstOrDefault(candidate => candidate.Id == step.Id && candidate.IsOptional);
                if (active?.AccessEntry is { } setupEntry && control.CurrentStepId == active.Id &&
                    action == (setupEntry.IsGui ? ToolFlowGoalAction.OpenTool : ToolFlowGoalAction.OpenAgentConsole))
                {
                    pendingSetupEntry = setupEntry;
                    dialog.Hide();
                    return;
                }
                if (guide.CurrentStep is null && control.CurrentStepId is null &&
                    (action is ToolFlowGoalAction.OpenTool or ToolFlowGoalAction.OpenAgentConsole or
                        ToolFlowGoalAction.OpenLocation or ToolFlowGoalAction.OpenSource) && guide.Delivery is { } delivery &&
                    (delivery.Action is ToolFlowDeliveryAction.OpenTool or ToolFlowDeliveryAction.OpenAgentConsole or
                        ToolFlowDeliveryAction.OpenLocation or ToolFlowDeliveryAction.OpenSource))
                {
                    var expected = delivery.Action switch
                    {
                        ToolFlowDeliveryAction.OpenTool => ToolFlowGoalAction.OpenTool,
                        ToolFlowDeliveryAction.OpenAgentConsole => ToolFlowGoalAction.OpenAgentConsole,
                        ToolFlowDeliveryAction.OpenLocation => ToolFlowGoalAction.OpenLocation,
                        _ => ToolFlowGoalAction.OpenSource,
                    };
                    if (action != expected || shown?.CurrentStep is not null ||
                        !step.Rows.Any(row => row.ItemId == delivery.ItemId)) return;
                    pendingDelivery = delivery;
                    dialog.Hide();
                    return;
                }
                if (action == ToolFlowGoalAction.Handoff)
                {
                    if (guide.CurrentStep is not null || shown?.CurrentStep is not null) return;
                }
                else if (active is null || control.CurrentStepId != step.Id) return;
                busy = true;
                Render();
                if (readFailed) return;
                switch (action)
                {
                    case ToolFlowGoalAction.ContinueAutomatic:
                        // Resuming the confirmed plan continues every unfinished automatic item.
                        // An explicitly opened optional helper still keeps its own narrow scope.
                        var pendingRows = active!.IsOptional ? active.Rows
                            : CurrentToolFlowReadiness(latest, lastResult).PendingAutomaticItems;
                        var ids = pendingRows.Where(row => row.CanContinueAutomatically)
                            .Select(row => row.ItemId).ToArray();
                        if (ids.Length == 0) break;
                        request = new(ToolFlowResumeAction.ContinueAutomatic, ids);
                        dialog.Hide();
                        break;
                    case ToolFlowGoalAction.Handoff:
                        request = new(ToolFlowResumeAction.Handoff);
                        dialog.Hide();
                        break;
                    case ToolFlowGoalAction.OpenSource:
                        if (active?.SourceUrl is { } source && InternalBrowserLink.TryGetWebUri(source, out var sourceUri))
                        {
                            pendingSource = sourceUri;
                            dialog.Hide();
                        }
                        break;
                    case ToolFlowGoalAction.ConfirmManual:
                        if (active is not { CanConfirmManual: true } currentStep) break;
                        foreach (var row in currentStep.Rows.Where(row => !row.CanContinueAutomatically &&
                            row.State is ToolFlowResumeItemState.NeedsUserAssist or ToolFlowResumeItemState.Failed))
                            current = store.MarkItemUserReportedDone(initial.SubmissionId, row.ItemId);
                        _taskSelection = current;
                        // These are the user's statements. No Verified event or upload is created.
                        Render();
                        break;
                    case ToolFlowGoalAction.CheckAgain:
                        await RefreshToolAccessAsync(latest, force: true);
                        if (CanReview())
                        {
                            lastResult = null; // An explicit fresh check replaces the earlier in-memory run view.
                            _taskResult = null;
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                if (CanReview()) AddSystemBubble(ToolFlowGoalText.Get("AiGoal_ActionFailed", "这一步暂未处理：") + ex.Message);
            }
            finally
            {
                busy = readFailed;
                if (CanReview()) Render();
            }
        };
        _refreshToolFlowGoalDialog = Render;
        try
        {
            Render();
            await dialog.ShowAsync();
            canOpenSource = CanReview();
            outcome = canOpenSource ? request : new(ToolFlowResumeAction.None);
        }
        finally
        {
            open = false;
            if (_refreshToolFlowGoalDialog == Render) _refreshToolFlowGoalDialog = null;
        }
        // Finish the modal before replacing its page, including its closing animation.
        if (canOpenSource && pendingSetupEntry is not null && CanReviewAfterClose())
            await UseInstalledToolAsync(pendingSetupEntry,
                pendingSetupEntry.IsGui ? ToolAccessAction.OpenTool : ToolAccessAction.OpenAgentConsole);
        else if (canOpenSource && pendingSource is not null && !_lease.IsClosed)
            BrowserPage.Open(pendingSource.AbsoluteUri);
        else if (canOpenSource && pendingDelivery is not null && CanReviewAfterClose())
            await UseToolFlowDeliveryAsync(current, pendingDelivery, CanReviewAfterClose);
        return outcome;

        bool CanReviewAfterClose() => !_lease.IsClosed && IsLoaded && epoch == _displayEpoch &&
            viewEpoch == _toolFlowViewEpoch && string.Equals(conversationId, _session?.Id, StringComparison.Ordinal) &&
            _taskSelection?.SubmissionId == initial.SubmissionId;
    }

    private async Task ShowToolFlowUsageAsync(ToolFlowSelection selection, ToolFlowInstallResult? result,
        Func<bool>? canDisplay = null)
    {
        if (_lease.IsClosed || !(canDisplay?.Invoke() ?? true)) return;
        await RefreshToolAccessAsync(selection);
        if (_lease.IsClosed || !(canDisplay?.Invoke() ?? true)) return;
        var guide = ToolFlowGoalGuide.Create(CurrentToolFlowReadiness(selection, result), accessEntries: _taskToolEntries);
        var epoch = _displayEpoch;
        var viewEpoch = _toolFlowViewEpoch;
        bool CanUse() => !_lease.IsClosed && IsLoaded && epoch == _displayEpoch && viewEpoch == _toolFlowViewEpoch &&
            _taskSelection?.SubmissionId == selection.SubmissionId && (canDisplay?.Invoke() ?? true);
        var delivery = guide.Delivery;
        var hasAgent = selection.Items.Any(ToolFlowItemSemantics.IsAgent);
        var canStart = delivery is { Action: not ToolFlowDeliveryAction.None } &&
            (delivery.Action != ToolFlowDeliveryAction.Handoff || hasAgent);
        var panel = new StackPanel { Spacing = 12, MaxWidth = 570 };
        var dialog = new ContentDialog { Title = delivery?.Title ?? guide.Stage,
            Content = new ScrollViewer { Content = panel, MaxHeight = 460,
                HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = canStart ? delivery!.ActionLabel : "",
            SecondaryButtonText = hasAgent && delivery?.Action != ToolFlowDeliveryAction.Handoff
                ? LocalizationService.L("AiFlow_BriefCopy", "复制任务说明") : "",
            CloseButtonText = ToolFlowGoalText.Get("AiGoal_BackToChat", "返回对话"),
            DefaultButton = canStart ? ContentDialogButton.Primary : ContentDialogButton.Close, XamlRoot = XamlRoot };
        panel.Children.Add(new TextBlock { Text = ToolFlowGoalText.Get("AiGoal_YourGoal", "你的目标：") + selection.ProjectGoal,
            FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = guide.CurrentStep is { } step ? step.Title + "\n" + step.Hint
            : delivery?.Hint ?? guide.Summary, TextWrapping = TextWrapping.Wrap });
        var access = new ToolFlowToolAccessControl();
        access.Update(_taskToolEntries, _toolAccessBusy || _toolFlowInstalling, expandInitially: false);
        ToolFlowToolAccessEntry? pendingTool = null;
        ToolAccessAction pendingAction = default;
        access.ActionRequested += (tool, action) =>
        {
            if (!CanUse()) return;
            pendingTool = tool; pendingAction = action; dialog.Hide();
        };
        panel.Children.Add(access);
        panel.Children.Add(new Expander
        {
            Header = LocalizationService.L("AiFlow_ExpandReply", "展开说明"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new TextBlock { Text = selection.FlowText, TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true },
        });
        var answer = await dialog.ShowAsync();
        if (!CanUse()) return;
        if (pendingTool is not null) await UseInstalledToolAsync(pendingTool, pendingAction);
        else if (answer == ContentDialogResult.Primary && canStart)
            await UseToolFlowDeliveryAsync(selection, delivery!, CanUse);
        else if (answer == ContentDialogResult.Secondary && hasAgent)
            await ShowAgentHandoffAsync(selection, result, CanUse);
    }

    private async Task OpenToolFlowPrimaryAsync(ToolFlowSelection selection, ToolFlowInstallResult? result,
        Func<bool> canDisplay)
    {
        await RefreshToolAccessAsync(selection, force: true);
        if (!canDisplay()) return;
        var guide = ToolFlowGoalGuide.Create(CurrentToolFlowReadiness(selection, result), accessEntries: _taskToolEntries);
        if (guide.Delivery is { Action: not ToolFlowDeliveryAction.None and not ToolFlowDeliveryAction.Handoff } delivery)
            await UseToolFlowDeliveryAsync(selection, delivery, canDisplay);
        else await ShowToolFlowUsageAsync(selection, result, canDisplay);
    }

    private async Task UseToolFlowDeliveryAsync(ToolFlowSelection selection, ToolFlowDeliveryPresentation delivery,
        Func<bool> canDisplay)
    {
        if (!canDisplay() || _taskSelection is not { } latest || latest.SubmissionId != selection.SubmissionId) return;
        // Re-project after asynchronous checks. Old cards cannot open an entry for a revised or pending plan.
        var current = ToolFlowGoalGuide.Create(CurrentToolFlowReadiness(latest, _taskResult), accessEntries: _taskToolEntries).Delivery;
        if (current is null || current.Action != delivery.Action || current.ItemId != delivery.ItemId ||
            current.AccessEntry != delivery.AccessEntry || current.SourceUrl != delivery.SourceUrl) return;
        if (delivery.AccessEntry is { } entry)
        {
            var action = delivery.Action switch
            {
                ToolFlowDeliveryAction.OpenTool => ToolAccessAction.OpenTool,
                ToolFlowDeliveryAction.OpenAgentConsole => ToolAccessAction.OpenAgentConsole,
                _ => ToolAccessAction.OpenLocation,
            };
            await UseInstalledToolAsync(entry, action);
        }
        else if (delivery.Action == ToolFlowDeliveryAction.OpenSource &&
            InternalBrowserLink.TryGetWebUri(delivery.SourceUrl, out var source))
            BrowserPage.Open(source.AbsoluteUri);
        else if (delivery.Action == ToolFlowDeliveryAction.Handoff && latest.Items.Any(ToolFlowItemSemantics.IsAgent))
            await ShowAgentHandoffAsync(latest, _taskResult, canDisplay);
    }
}
