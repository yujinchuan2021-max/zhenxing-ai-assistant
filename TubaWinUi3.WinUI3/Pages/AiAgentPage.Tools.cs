using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    private string? _toolAccessSelectionId;
    private string? _toolAccessProbeId;
    private CancellationTokenSource? _toolAccessProbe;
    private ToolFlowPreparation? _taskPreparation;
    private IReadOnlyList<ToolFlowToolAccessEntry> _taskToolEntries = [];
    private bool _toolAccessBusy;
    private int _toolAccessActionEpoch;
    private CancellationTokenSource? _toolAccessActionCancellation;
    private readonly Dictionary<string, ToolFlowPostInstallDeliveryResult> _toolAccessLocalDelivery =
        new(StringComparer.OrdinalIgnoreCase);

    private void ClearToolAccess()
    {
        _toolAccessProbe?.Cancel();
        _toolAccessActionCancellation?.Cancel();
        _toolAccessSelectionId = null;
        _toolAccessProbeId = null;
        _taskPreparation = null;
        _taskToolEntries = [];
        _toolAccessLocalDelivery.Clear();
        _toolAccessBusy = false;
        _toolAccessActionEpoch++;
        _taskCard?.ToolAccess.Reset();
    }

    private async Task RefreshToolAccessAsync(ToolFlowSelection selection, bool force = false)
    {
        if (_lease.IsClosed || !IsLoaded || (!force &&
            (_toolAccessSelectionId == selection.SubmissionId || _toolAccessProbeId == selection.SubmissionId))) return;
        if (_toolAccessSelectionId != selection.SubmissionId && _toolAccessProbeId != selection.SubmissionId)
        {
            _toolAccessSelectionId = null;
            _taskPreparation = null;
            _taskToolEntries = [];
            _toolAccessLocalDelivery.Clear();
            _taskCard?.ToolAccess.Reset();
        }
        _toolAccessProbe?.Cancel();
        using var probe = new CancellationTokenSource();
        _toolAccessProbe = probe;
        _toolAccessProbeId = selection.SubmissionId;
        var epoch = _displayEpoch;
        var viewEpoch = _toolFlowViewEpoch;
        var conversationId = _session?.Id;
        bool IsCurrent() => !_lease.IsClosed && IsLoaded && epoch == _displayEpoch && viewEpoch == _toolFlowViewEpoch &&
            string.Equals(conversationId, _session?.Id, StringComparison.Ordinal) &&
            ReferenceEquals(_toolAccessProbe, probe) && _taskSelection?.SubmissionId == selection.SubmissionId;
        RefreshToolAccessControl();
        RefreshToolFlowTaskCard();
        try
        {
            var found = new List<ToolFlowToolAccessEntry>();
            var preparation = await new ToolFlowPreparationService().PrepareAsync(
                selection.Items, selection.FlowText, probe.Token);
            foreach (var row in preparation.Rows)
            {
                probe.Token.ThrowIfCancellationRequested();
                var entry = await ToolFlowToolAccess.ResolveAsync(ToolFlowToolAccess.AccessItem(row), probe.Token);
                if (entry is not null && found.All(x => x.TargetKey != entry.TargetKey)) found.Add(entry);
            }
            if (!IsCurrent()) return;
            _taskPreparation = preparation;
            _toolAccessSelectionId = selection.SubmissionId;
            _taskToolEntries = found;
            RefreshToolFlowTaskCard();
        }
        catch (OperationCanceledException) { }
        catch { /* Missing or changed installations do not replace the task's actual preparation result. */ }
        finally
        {
            if (ReferenceEquals(_toolAccessProbe, probe))
            {
                var canRefresh = IsCurrent();
                _toolAccessProbe = null;
                _toolAccessProbeId = null;
                if (canRefresh) RefreshToolFlowTaskCard();
            }
        }
    }

    private void RefreshToolAccessControl() =>
        _taskCard?.ToolAccess.Update(_taskToolEntries, _toolAccessBusy || _toolFlowInstalling ||
            _toolAccessProbeId is not null, deliveryByTarget: CurrentToolAccessDelivery());

    private IReadOnlyDictionary<string, ToolFlowPostInstallDeliveryResult> CurrentToolAccessDelivery()
    {
        var deliveries = new Dictionary<string, ToolFlowPostInstallDeliveryResult>(StringComparer.OrdinalIgnoreCase);
        if (_taskSelection is not { } selection) return deliveries;
        void Add(ToolFlowInstallItemResult result)
        {
            if (result.Delivery is not { TargetKey: { } target } delivery || delivery.ItemId != result.ItemId) return;
            var item = selection.Items.FirstOrDefault(x => x.ItemId == result.ItemId);
            var expectedTarget = result.Status == ToolFlowInstallItemStatus.ReusedInstalledTool
                ? result.ExistingToolTargetKey : item?.InstallTargetKey;
            if (item is null || !string.Equals(target, expectedTarget?.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            deliveries[target] = delivery;
        }
        if (_taskResult is { } completed)
            foreach (var result in completed.Items) Add(result);
        if (_taskProgress?.ItemResult is { } current) Add(current);
        foreach (var (target, delivery) in _toolAccessLocalDelivery)
            if (_taskToolEntries.Any(x => string.Equals(x.TargetKey, target, StringComparison.OrdinalIgnoreCase)))
                deliveries[target] = delivery;
        return deliveries;
    }

    private ToolFlowResumeView CurrentToolFlowReadiness(ToolFlowSelection selection, ToolFlowInstallResult? result)
    {
        var view = ToolFlowResume.Build(selection, result);
        return _toolAccessSelectionId == selection.SubmissionId && _taskPreparation is { } preparation
            ? ToolFlowResume.ApplyPreparation(view, preparation) : view;
    }

    private async Task UseInstalledToolAsync(ToolFlowToolAccessEntry entry, ToolAccessAction action)
    {
        if (_lease.IsClosed || !IsLoaded || _toolAccessBusy || _toolFlowInstalling || _toolAccessProbeId is not null ||
            _taskSelection is not { } selection || !_taskToolEntries.Contains(entry)) return;
        var epoch = _displayEpoch;
        var viewEpoch = _toolFlowViewEpoch;
        var actionEpoch = ++_toolAccessActionEpoch;
        var submissionId = selection.SubmissionId;
        var conversationId = _session?.Id;
        bool CanDisplay() => !_lease.IsClosed && IsLoaded && epoch == _displayEpoch && viewEpoch == _toolFlowViewEpoch &&
            string.Equals(conversationId, _session?.Id, StringComparison.Ordinal) &&
            actionEpoch == _toolAccessActionEpoch &&
            _taskSelection?.SubmissionId == submissionId;
        _toolAccessBusy = true;
        using var actionCancellation = new CancellationTokenSource();
        _toolAccessActionCancellation = actionCancellation;
        RefreshToolAccessControl();
        RefreshToolFlowTaskCard();
        try
        {
            if (action == ToolAccessAction.OpenAgentConsole)
            {
                var projectDirectory = await Win32Dialogs.PickFolderAsync();
                if (!CanDisplay() || string.IsNullOrEmpty(projectDirectory)) return;
                // Revalidate before any side effect; navigation during the asynchronous lookup cancels the launch.
                await ToolFlowToolAccess.OpenInteractiveAgentAsync(entry, projectDirectory, actionCancellation.Token);
                return;
            }
            switch (action)
            {
                case ToolAccessAction.OpenTool: await ToolFlowToolAccess.OpenToolAsync(entry, actionCancellation.Token); break;
                case ToolAccessAction.OpenLocation: await ToolFlowToolAccess.OpenLocationAsync(entry, actionCancellation.Token); break;
                case ToolAccessAction.CreateShortcut:
                    var shortcut = await ToolFlowToolAccess.CreateShortcutAsync(entry, actionCancellation.Token);
                    if (CanDisplay())
                    {
                        _toolAccessLocalDelivery[entry.TargetKey] = new("", entry.Name,
                            ToolFlowPostInstallDeliveryKind.DesktopShortcutReady, "")
                            { TargetKey = entry.TargetKey, ShortcutPath = shortcut };
                        AddSystemBubble(string.Format(ToolAccessText("AiTools_ShortcutCreated", "已为 {0} 创建桌面图标。",
                            "A desktop shortcut for {0} is ready."), entry.Name));
                    }
                    break;
                case ToolAccessAction.CopyCliCommand:
                    var instruction = await ToolFlowToolAccess.RevalidateCliLaunchInstructionAsync(entry, actionCancellation.Token);
                    if (!CanDisplay()) return;
                    actionCancellation.Token.ThrowIfCancellationRequested();
                    var clipboard = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    clipboard.SetText(instruction.Command);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(clipboard);
                    AddSystemBubble(ToolAccessText("AiTools_CliCommandCopied", "启动命令已复制。",
                        "The launch command has been copied."));
                    break;
            }
        }
        catch (OperationCanceledException) when (actionCancellation.IsCancellationRequested) { }
        catch
        {
            if (CanDisplay()) AddSystemBubble(LocalizationService.L("AiTools_ActionFailed",
                "工具位置已变化或暂时无法打开，请从清单重新检查。"));
        }
        finally
        {
            if (ReferenceEquals(_toolAccessActionCancellation, actionCancellation)) _toolAccessActionCancellation = null;
            if (actionEpoch == _toolAccessActionEpoch)
            {
                _toolAccessBusy = false;
                if (CanDisplay()) { RefreshToolAccessControl(); RefreshToolFlowTaskCard(); }
            }
        }
    }

    private static string ToolAccessText(string key, string chinese, string english) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? english : chinese);
}
