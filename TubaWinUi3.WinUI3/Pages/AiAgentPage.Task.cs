using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;
using Microsoft.UI.Xaml;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    private ToolFlowTaskControl? _taskCard;
    private ToolFlowSelection? _taskSelection;
    private ToolFlowInstallResult? _taskResult;
    private ToolFlowInstallProgress? _taskProgress;
    private CancellationTokenSource? _toolFlowInstallCts;
    private bool _taskStopped;
    private bool _taskExplicitlyResumed;
    private string? _taskResumeConversationId;
    private string? _skillTaskRestoredConversationId;
    private string? _skillTaskRestoredSelectionId;

    private void InitializeToolFlowTaskCard()
    {
        _taskCard = new ToolFlowTaskControl();
        ToolFlowTaskHost.Content = _taskCard;
        _taskCard.PrimaryRequested += async () => await OpenCurrentToolFlowTaskAsync(handoff: false, currentStep: true);
        _taskCard.OpenDetails += async () => await OpenCurrentToolFlowTaskAsync(handoff: false);
        _taskCard.OpenHandoff += async () => await OpenCurrentToolFlowTaskAsync(handoff: true);
        _taskCard.StopRequested += StopToolFlowPreparation;
        _taskCard.ToolAccess.ActionRequested += async (entry, action) => await UseInstalledToolAsync(entry, action);
    }

    private void ClearToolFlowTask()
    {
        ClearToolAccess();
        _taskSelection = null;
        _taskResult = null;
        _taskProgress = null;
        _taskStopped = false;
        _taskExplicitlyResumed = false;
        _taskResumeConversationId = null;
        _skillTaskRestoredConversationId = null;
        _skillTaskRestoredSelectionId = null;
        ToolFlowTaskHost.Visibility = Visibility.Collapsed;
        UpdateWorkbenchTask(null);
        RefreshConversationGoalSummary();
    }

    private void AttachExplicitTaskToNewSession()
    {
        // An explicit resume in an empty chat survives its first message, without rewriting the saved plan's owner.
        if (_taskExplicitlyResumed && _taskResumeConversationId is null && _session is not null)
        {
            _taskResumeConversationId = _session.Id;
            if (_taskSelection is { } selection) RestoreCustomSkillsForSelectedTask(selection, force: true);
        }
        RefreshToolFlowTaskCard();
    }

    private void RestoreCustomSkillsForSelectedTask(ToolFlowSelection selection, bool force = false)
    {
        var conversationId = _session?.Id;
        if (string.IsNullOrEmpty(conversationId) || !force &&
            !TubaWinUi3.Services.Agent.SkillTaskContinuity.ShouldRestoreConfirmedTask(
                _skillTaskRestoredConversationId, _skillTaskRestoredSelectionId, conversationId, selection.SubmissionId)) return;
        if (_session is TubaWinUi3.Services.Agent.AgentSession builtin)
            builtin.RestoreCustomSkillTask(selection.ProjectGoal);
        else if (_session is TubaWinUi3.Services.Ai.Dsh.DshSession dsh)
            dsh.RestoreCustomSkillTask(selection.ProjectGoal);
        else return;
        _skillTaskRestoredConversationId = conversationId;
        _skillTaskRestoredSelectionId = selection.SubmissionId;
    }

    private void RestoreCurrentToolFlowTask()
    {
        if (_lease.IsClosed || _toolFlowInstalling) { RefreshToolFlowTaskCard(); return; }
        var conversationId = _session?.Id;
        ToolFlowSelection? selection = null;
        try
        {
            if (_taskExplicitlyResumed && _taskSelection is { } resumed &&
                string.Equals(_taskResumeConversationId, conversationId, StringComparison.Ordinal))
                selection = new ToolFlowSelectionStore().GetBySubmissionId(resumed.SubmissionId);
            else if (!string.IsNullOrEmpty(conversationId))
                selection = new ToolFlowSelectionStore().FindLatestForConversation(conversationId);
        }
        catch { }
        if (selection is null) { ClearToolFlowTask(); return; }
        if (ConversationGoalPresentation.HasNewTaskAfterSelection(selection, _toolFlowVisibleMessages))
        { ClearToolFlowTask(); return; }
        if (_taskSelection?.SubmissionId != selection.SubmissionId)
        {
            ClearToolAccess();
            _taskResult = null;
            _taskProgress = null;
            _taskStopped = false;
        }
        RestoreCustomSkillsForSelectedTask(selection);
        _taskSelection = selection;
        _ = RefreshToolAccessAsync(selection);
        RefreshToolFlowTaskCard();
    }

    private void RefreshToolFlowTaskCard()
    {
        if (_taskCard is null || _lease.IsClosed) return;
        if (_taskSelection is not { } selection ||
            !(_taskExplicitlyResumed && string.Equals(_taskResumeConversationId, _session?.Id, StringComparison.Ordinal)) &&
            (string.IsNullOrEmpty(_session?.Id) || !string.Equals(selection.ConversationId, _session.Id, StringComparison.Ordinal)))
        {
            ToolFlowTaskHost.Visibility = Visibility.Collapsed;
            UpdateWorkbenchTask(null);
            RefreshConversationGoalSummary();
            return;
        }
        ToolFlowTaskHost.Visibility = Visibility.Visible;
        UpdateWorkbenchTask(selection.SubmissionId);
        RefreshConversationGoalSummary();
        RefreshToolAccessControl();
        string? active = null;
        if (_taskProgress is { } p)
        {
            var phase = p.Phase switch
            {
                ToolFlowInstallPhase.Checking => LocalizationService.L("AiTask_Checking", "正在检测：{0}"),
                ToolFlowInstallPhase.Installing => LocalizationService.L("AiTask_Installing", "正在安装：{0}"),
                ToolFlowInstallPhase.Verifying => LocalizationService.L("AiTask_Verifying", "正在检查安装结果：{0}"),
                ToolFlowInstallPhase.Delivering => LocalizationService.L("AiTask_Delivering", "正在准备使用入口：{0}"),
                _ => LocalizationService.L("AiTask_Handled", "已处理 {0} 项，继续下一项…"),
            };
            active = string.Format(phase, p.Phase is ToolFlowInstallPhase.Checking
                or ToolFlowInstallPhase.Installing or ToolFlowInstallPhase.Verifying or ToolFlowInstallPhase.Delivering
                ? (object)p.Name : p.CompletedCount);
        }
        var guide = ToolFlowGoalGuide.Create(CurrentToolFlowReadiness(selection, _taskResult), _toolFlowInstalling, active,
            accessEntries: _taskToolEntries);
        if (_toolFlowInstalling && _taskProgress is { ItemId: not null } progress)
            guide = guide.WithActiveItem(progress.ItemId, active);
        if (_taskStopped && !_toolFlowInstalling)
            guide = guide with { Stage = LocalizationService.L("AiTask_Stopped", "后续准备已停止，已有结果已保留") };
        _taskCard.Update(guide, selection.FlowName, _toolFlowInstalling,
            _isProcessing || _awaitingConfirmation || _toolFlowActionOpen || _toolFlowResumeOpen ||
                _toolAccessBusy ||
                _toolAccessProbeId == selection.SubmissionId,
            _toolFlowInstalling && _toolFlowInstallCts?.IsCancellationRequested == true,
            _taskExplicitlyResumed);
    }

    private void StopToolFlowPreparation()
    {
        if (!_toolFlowInstalling || _toolFlowInstallCts is null) return;
        _toolFlowInstallCts.Cancel();
        RefreshToolFlowTaskCard();
    }

    private async Task OpenCurrentToolFlowTaskAsync(bool handoff, bool currentStep = false)
    {
        if (_taskSelection is null || _toolFlowInstalling || _toolFlowResumeOpen || _isProcessing ||
            _awaitingConfirmation || _toolFlowActionOpen || _godotPlanDialogOpen || _lease.IsClosed || !IsLoaded) return;
        var epoch = _displayEpoch;
        var view = _toolFlowViewEpoch;
        var conversationId = _session?.Id;
        var submissionId = _taskSelection.SubmissionId;
        bool CanDisplay() => !_lease.IsClosed && IsLoaded && epoch == _displayEpoch && view == _toolFlowViewEpoch &&
            string.Equals(conversationId, _session?.Id, StringComparison.Ordinal) &&
            _taskSelection?.SubmissionId == submissionId;
        _toolFlowResumeOpen = true;
        UpdateInputState();
        try
        {
            var store = new ToolFlowSelectionStore();
            var selection = store.GetBySubmissionId(submissionId);
            if (selection is null || !CanDisplay() || (!_taskExplicitlyResumed && selection.ConversationId != conversationId)) return;
            if (handoff) await OpenToolFlowPrimaryAsync(selection, _taskResult, CanDisplay);
            else if (currentStep)
            {
                // Only an explicit primary-button click may continue an already-confirmed
                // snapshot. Refreshing or resizing the workbench never enters this path.
                var readiness = CurrentToolFlowReadiness(selection, _taskResult);
                var guide = ToolFlowGoalGuide.Create(readiness, accessEntries: _taskToolEntries);
                if (guide.CurrentStep is { HasAutomaticItems: true })
                {
                    var pendingIds = readiness.PendingAutomaticItems.Where(row => row.CanContinueAutomatically)
                        .Select(row => row.ItemId).ToArray();
                    if (pendingIds.Length > 0 && CanDisplay())
                        await RunToolFlowInstallWithBubblesAsync(store, selection, resumed: true,
                            canDisplay: CanDisplay, itemIds: pendingIds);
                }
                else if (guide.CurrentStep?.AccessEntry is { } entry && CanDisplay())
                    await UseInstalledToolAsync(entry, entry.IsGui ? ToolAccessAction.OpenTool : ToolAccessAction.OpenAgentConsole);
                else if (guide.CurrentStep?.SourceUrl is { } source &&
                    InternalBrowserLink.TryGetWebUri(source, out var uri) && CanDisplay())
                    BrowserPage.Open(uri.AbsoluteUri);
                else
                    // Manual declarations keep the existing per-requirement review.
                    // A primary button never silently marks accounts or payments complete.
                    await ResumeToolFlowSelectionAsync(store, selection, CanDisplay);
            }
            else await ResumeToolFlowSelectionAsync(store, selection, CanDisplay);
        }
        catch (Exception ex)
        {
            if (CanDisplay()) AddSystemBubble(LocalizationService.L("AiFlow_SnapshotReadFailed", "读取本机工具流快照失败：") + ex.Message);
        }
        finally
        {
            _toolFlowResumeOpen = false;
            if (!_lease.IsClosed) { RestoreCurrentToolFlowTask(); UpdateInputState(); }
        }
    }
}
