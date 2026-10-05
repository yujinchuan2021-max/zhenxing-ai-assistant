using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using TubaWinUi3.Services.AppManagement;
using TubaWinUi3.Services.ToolFlows;
using TubaWinUi3.Controls.AgentChat;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    private readonly List<ToolFlowConversationMessage> _toolFlowVisibleMessages = [];
    private readonly List<ToolFlowMessageAction> _toolFlowMessageActions = [];
    private int _toolFlowRoundStart;
    private int _toolFlowDisplayRoundStart;
    private bool _toolFlowRoundSucceeded;
    private bool _toolFlowActionOpen;
    private bool _toolFlowInstalling;
    private int _toolFlowViewEpoch;

    private sealed class ToolFlowMessageAction
    {
        public required Button Button { get; init; }
        public required Border MessageRoot { get; init; }
        public required ToolFlowProposal Proposal { get; init; }
        public required int ConversationIndex { get; init; }
        public required int Epoch { get; init; }
        public bool Ready { get; set; }
        public bool CompletionFailed { get; set; }
        public bool Selected { get; set; }
        public ToolFlowOptionCard? Card { get; init; }
    }

    // Captured at this exact bubble, never resolved by searching the last assistant reply.
    private void AddToolFlowMessageAction(AssistantBubble bubble, string content, bool restored = false,
        bool? replyCompletedSuccessfully = null)
    {
        if (bubble.Epoch != _displayEpoch || string.IsNullOrWhiteSpace(content)) return;
        _toolFlowVisibleMessages.Add(new ToolFlowConversationMessage { Role = "assistant", Content = content });
        var index = _toolFlowVisibleMessages.Count - 1;
        var ready = restored ? replyCompletedSuccessfully == true : !_isProcessing && _toolFlowRoundSucceeded;
        if (AddModelChoiceAction(bubble, content, index, ready, restored && replyCompletedSuccessfully != true))
            return;
        if (ToolFlowProposalParser.TryCaptureRecommendations(_toolFlowVisibleMessages, index, out var recommendations))
        {
            var cards = new ToolFlowCardsControl(recommendations!);
            foreach (var card in cards.Cards)
            {
                var proposalForCard = recommendations!.ToProposal(card.OptionId);
                if (proposalForCard is null) continue; // Unavailable choices remain visible, never actionable.
                AttachToolFlowAction(bubble, index, proposalForCard, card, ready,
                    restored && replyCompletedSuccessfully != true);
            }
            bubble.ReadableContent = AssistantReplyPresentation.CopyWithRecommendations(
                bubble.ReadableContent, recommendations!);
            bubble.ToolFlowActions.Children.Add(cards);
            RefreshToolFlowMessageActions();
            return;
        }
        if (!ToolFlowProposalParser.TryCapture(_toolFlowVisibleMessages, index, out var proposal))
        {
            var hasQuestionProtocol = ModelPreferenceQuestion.HasQuestionProtocol(content)
                || ChatChoiceQuestion.HasQuestionProtocol(content);
            var noticeKey = hasQuestionProtocol
                ? "AiChoice_IncompleteFormat" : "AiFlow_IncompleteFormat";
            if ((hasQuestionProtocol || AssistantReplyPresentation.Create(content).Body != content.Trim())
                && bubble.ContentHost.Content is StackPanel display)
                display.Children.Add(new TextBlock
                {
                    Text = LocalizationService.L(noticeKey, "内容尚未整理完整，可以让助手重新生成。"),
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75,
                    Tag = noticeKey,
                });
            return;
        }
        var single = new ToolFlowRecommendationOption("single", proposal!.Name,
            LocalizationService.L("AiFlow_SingleSummary", "核对这套工具与必要的人工步骤。"),
            proposal.SuggestedProjectGoal,
            LocalizationService.L("AiFlow_CostInDetails", "按所选工具与服务核对，详见方案说明。"),
            LocalizationService.L("AiFlow_RequirementsInDetails", "开始前核对网络、账号与设备条件。"),
            [], proposal.Items, true, "", proposal.Text);
        var singleCard = new ToolFlowOptionCard(single, recommended: false);
        AttachToolFlowAction(bubble, index, proposal, singleCard, ready,
            restored && replyCompletedSuccessfully != true);
        bubble.ToolFlowActions.Children.Add(singleCard);
        RefreshToolFlowMessageActions();
    }

    private void AttachToolFlowAction(AssistantBubble bubble, int index, ToolFlowProposal proposal,
        ToolFlowOptionCard card, bool ready, bool failed)
    {
        var action = new ToolFlowMessageAction
        {
            Button = card.SelectButton, MessageRoot = bubble.Root, Proposal = proposal,
            ConversationIndex = index, Epoch = _displayEpoch,
            Ready = ready, CompletionFailed = failed, Card = card,
        };
        card.SelectButton.Click += async (_, _) =>
        {
            try { await UseToolFlowProposalAsync(action); }
            catch (Exception ex) { AddSystemBubble(LocalizationService.L("AiFlow_SaveFailed", "工具流未保存：") + ex.Message); }
        };
        _toolFlowMessageActions.Add(action);
    }

    private void CompleteToolFlowMessageActions(int epoch, bool succeeded)
    {
        if (epoch != _displayEpoch || _lease.IsClosed) return;
        _toolFlowRoundSucceeded = succeeded;
        ConversationDisplayItem.RecordReplyCompletion(_displayLog, _toolFlowDisplayRoundStart,
            succeeded ? AgentSendOutcome.Completed : AgentSendOutcome.Failed);
        CompleteModelChoiceActions(epoch, succeeded);
        foreach (var action in _toolFlowMessageActions)
            if (action.Epoch == epoch && action.ConversationIndex >= _toolFlowRoundStart)
            {
                action.Ready = succeeded;
                action.CompletionFailed = !succeeded;
            }
        RefreshToolFlowMessageActions();
    }

    private void RefreshToolFlowMessageActions()
    {
        RefreshModelChoiceActions();
        RefreshConversationGoalSummary();
        var busy = _isProcessing || _awaitingConfirmation || _toolFlowActionOpen || _toolFlowInstalling;
        var newTaskStart = ConversationGoalPresentation.FindExplicitNewTaskStart(_toolFlowVisibleMessages);
        foreach (var action in _toolFlowMessageActions)
        {
            if (action.Card is { } card)
                card.SetActionState(action.Ready, busy || action.Epoch != _displayEpoch || action.ConversationIndex < newTaskStart,
                    action.Selected, action.CompletionFailed);
            else
            {
                action.Button.Content = LocalizationService.L("AiFlow_UseThisPlan", "使用这套方案");
                action.Button.Visibility = action.Ready ? Visibility.Visible : Visibility.Collapsed;
                action.Button.IsEnabled = action.Ready && !busy && action.Epoch == _displayEpoch && action.ConversationIndex >= newTaskStart;
            }
        }
        foreach (var root in MsgPanel.Children.OfType<Border>())
        {
            if (root.Tag is not AssistantBubble bubble) continue;
            ToolTipService.SetToolTip(bubble.CopyButton, LocalizationService.L("AiFlow_CopyReply", "复制回复"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bubble.CopyButton,
                LocalizationService.L("AiFlow_CopyReply", "复制回复"));
            if (bubble.ReplyDetails is { } details)
                details.Header = LocalizationService.L("AiFlow_ExpandReply", "展开说明");
            if (bubble.ContentHost.Content is StackPanel body)
                foreach (var notice in body.Children.OfType<TextBlock>().Where(x =>
                    x.Tag as string is "AiFlow_IncompleteFormat" or "AiChoice_IncompleteFormat"))
                    notice.Text = LocalizationService.L((string)notice.Tag, "内容尚未整理完整，可以让助手重新生成。");
            foreach (var view in bubble.ToolFlowActions.Children.OfType<ToolFlowCardsControl>())
                view.ApplyLocalization();
        }
    }

    private bool IsToolFlowMessageCurrent(ToolFlowMessageAction action, int viewEpoch) =>
        !_lease.IsClosed && IsLoaded && viewEpoch == _toolFlowViewEpoch
        && action.Epoch == _displayEpoch && MsgPanel.Children.Contains(action.MessageRoot)
        && action.ConversationIndex >= ConversationGoalPresentation.FindExplicitNewTaskStart(_toolFlowVisibleMessages);

    /// <summary>Lightweight review of the clicked message; no internal install-code editor.</summary>
    private async Task UseToolFlowProposalAsync(ToolFlowMessageAction action)
    {
        var viewEpoch = _toolFlowViewEpoch;
        if (!action.Ready || _isProcessing || _awaitingConfirmation || _toolFlowActionOpen
            || _toolFlowInstalling || _godotPlanDialogOpen || _toolFlowResumeOpen
            || !IsToolFlowMessageCurrent(action, viewEpoch)) return;
        _toolFlowActionOpen = true;
        UpdateInputState();
        try
        {
            var proposal = action.Proposal;
            var projectGoal = proposal.SuggestedProjectGoal.Trim();
            if (string.IsNullOrWhiteSpace(projectGoal) || projectGoal.Length > 65536)
            {
                AddSystemBubble(string.IsNullOrWhiteSpace(projectGoal)
                    ? LocalizationService.L("AiFlow_MissingProposalGoal", "这个方案缺少明确目标，请在对话中补充目标后重新生成方案。")
                    : LocalizationService.L("AiFlow_RefineProposalGoal", "这个方案的目标过长，请在对话中整理需求后重新生成方案。"));
                return;
            }
            var conversation = proposal.Conversation.ToList();
            var conversationUploadSupported = ToolFlowUploadClient.IsConversationUploadSupported(conversation);
            var preparation = await new ToolFlowPreparationService().PrepareAsync(proposal.Items, proposal.Text);
            if (!IsToolFlowMessageCurrent(action, viewEpoch)) return;
            var panel = new StackPanel { Spacing = 10, Width = 520 };
            panel.Children.Add(new TextBlock
            {
                Text = proposal.Name, FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = LocalizationService.L("AiFlow_ReviewWholePlanNotice", "确认一次后，将连续安装清单内的软件，复用已有工具，并为桌面软件准备快捷方式。需要登录或付费的项目会单独提示。"),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = LocalizationService.L("AiFlow_GoalHeader", "已确定目标"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            panel.Children.Add(new TextBlock
            {
                Text = ShortenResumeText(projectGoal, 280),
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            });
            if (projectGoal.Length > 280)
                panel.Children.Add(new Expander
                {
                    Header = LocalizationService.L("AiFlow_ReadFullGoal", "查看完整目标"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 180,
                        Content = new TextBlock
                        {
                            Text = projectGoal, TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                        },
                    },
                });
            var known = new HashSet<string>(SystemInstaller.KnownTargets, StringComparer.OrdinalIgnoreCase);
            foreach (var item in preparation.ConfirmationItems)
            {
                var automatic = item.InstallTargetKey is { } key && known.Contains(key);
                var check = preparation.Rows.Single(row => row.Item.ItemId == item.ItemId);
                var itemPanel = new StackPanel { Spacing = 3 };
                itemPanel.Children.Add(new TextBlock
                {
                    Text = item.Name + " · " + (check.State == ToolFlowPreparationState.DetectionFailed
                        ? LocalizationService.L("AiFlow_ReuseProbeFailed", "安装状态未能确认；需重新检测，不能据此开始安装。")
                        : automatic
                        ? LocalizationService.L("AiFlow_AutomaticStep", "可自动安装")
                        : LocalizationService.L("AiFlow_ManualStep", "需要你协助")),
                    TextWrapping = TextWrapping.Wrap,
                });
                if (!string.IsNullOrWhiteSpace(item.Version))
                    itemPanel.Children.Add(new TextBlock { Text = item.Version, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrWhiteSpace(item.SourceUrl))
                    itemPanel.Children.Add(new TextBlock { Text = item.SourceUrl, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                if (!string.IsNullOrWhiteSpace(item.DownloadUrl) && item.DownloadUrl != item.SourceUrl)
                    itemPanel.Children.Add(new TextBlock { Text = item.DownloadUrl, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                panel.Children.Add(itemPanel);
            }
            if (preparation.ConfirmationItems.Count == 0)
                panel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.L("AiFlow_ReuseAllReady", "所需软件已具备，无需重复安装。确认后可查看下一步。"),
                    TextWrapping = TextWrapping.Wrap,
                });
            panel.Children.Add(new TextBlock
            {
                Text = ToolFlowUploadClient.IsUploadEligibleNow()
                    ? conversationUploadSupported
                        ? LocalizationService.L("AiFlow_ShareOnConfigured", "分享已开启：最终选定后，项目目标、工具流名称、完整方案、此对话中用户与助手可见的正文，以及实际安装结果和下载计数会发送到枕星服务器，用于分析与改进推荐。可在设置关闭。")
                        : LocalizationService.L("AiFlow_ConversationTooLong", "这次内容较长，仅保存在本机，准备流程可继续。")
                    : LocalizationService.L("AiFlow_ShareOff", "分享开关当前为关闭：这次选定只保存在本机，即使日后重新开启也不会补传这次记录。"),
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75,
            });
            var dialog = new ContentDialog
            {
                Title = LocalizationService.L("AiFlow_ReviewProposal", "核对方案"),
                Content = new ScrollViewer { Content = panel, MaxHeight = 510 },
                PrimaryButtonText = LocalizationService.L("AiFlow_ConfirmPlanAndStart", "确认并开始"),
                CloseButtonText = LocalizationService.L("Common_Cancel", "取消"),
                DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!IsToolFlowMessageCurrent(action, viewEpoch)) { args.Cancel = true; dialog.Hide(); return; }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || !IsToolFlowMessageCurrent(action, viewEpoch)) return;
            var chosen = new ToolFlowSelection
            {
                FlowId = Guid.NewGuid().ToString("D"), SubmissionId = Guid.NewGuid().ToString("D"),
                Origin = ToolFlowOrigin.Assistant, SelectedAtUtc = DateTimeOffset.UtcNow,
                ConversationId = _session?.Id,
                FlowName = proposal.Name, ProjectGoal = projectGoal, GoalDescription = projectGoal,
                FlowText = proposal.Text, Conversation = conversation, Items = proposal.Items.ToList(),
                UploadEnabledAtSelection = ToolFlowUploadClient.IsUploadEligibleNow(conversation),
            };
            var store = new ToolFlowSelectionStore();
            store.SelectForInstall(chosen);
            ClearToolFlowTask(); // A newly confirmed plan replaces any temporary legacy resume view.
            foreach (var sibling in _toolFlowMessageActions.Where(x => x.ConversationIndex == action.ConversationIndex))
                sibling.Selected = ReferenceEquals(sibling, action);
            RefreshToolFlowMessageActions();
            RefreshToolFlowResumeEntry();
            // The review modal has closed. Installation belongs to this cached conversation and may continue in the background.
            _toolFlowActionOpen = false;
            bool CanDisplay() => IsToolFlowMessageCurrent(action, viewEpoch);
            var result = await RunToolFlowInstallWithBubblesAsync(store, chosen, canDisplay: CanDisplay);
            await RunToolFlowFollowUpAsync(chosen, store, result, canDisplay: CanDisplay);
        }
        finally
        {
            _toolFlowActionOpen = false;
            if (!_lease.IsClosed) UpdateInputState();
        }
    }

    /// <summary>
    /// 在用户对固定清单作出明确选择后，按现有 runner 逐项处理可自动安装的目标；
    /// 返回本轮逐项结果（供后续本地入门卡与任务说明使用）。展示/切换方案不会走到这里；
    /// 恢复入口的「继续自动项」复用同一方法（resumed=true 只影响文案）。
    /// </summary>
    private async Task<ToolFlowInstallResult?> RunToolFlowInstallWithBubblesAsync(
        ToolFlowSelectionStore store, ToolFlowSelection chosen, bool resumed = false, Func<bool>? canDisplay = null,
        IReadOnlyCollection<string>? itemIds = null)
    {
        ToolFlowInstallResult? installResult = null;
        // Confirmation authorizes the whole saved plan. Only an explicit item-level retry
        // supplies itemIds; the visual "current step" must never truncate a confirmed plan.
        var epoch = _displayEpoch;
        var viewEpoch = _toolFlowViewEpoch;
        var conversationId = _session?.Id;
        bool CanDisplay() => !_lease.IsClosed && epoch == _displayEpoch && viewEpoch == _toolFlowViewEpoch &&
            string.Equals(conversationId, _session?.Id, StringComparison.Ordinal) && (canDisplay?.Invoke() ?? true);
        void Report(string text) { if (CanDisplay()) AddSystemBubble(text); }
        using var stop = new CancellationTokenSource();
        _toolFlowInstallCts = stop;
        bool CanTrackTask() => !_lease.IsClosed && epoch == _displayEpoch &&
            string.Equals(conversationId, _session?.Id, StringComparison.Ordinal) &&
            ReferenceEquals(_toolFlowInstallCts, stop) && _taskSelection?.SubmissionId == chosen.SubmissionId;
        if ((chosen.ConversationId == conversationId && !string.IsNullOrEmpty(conversationId)) ||
            (_taskExplicitlyResumed && _taskSelection?.SubmissionId == chosen.SubmissionId))
        {
            _taskSelection = store.GetBySubmissionId(chosen.SubmissionId) ?? chosen;
            _taskResult = null;
            _taskProgress = null;
            _taskStopped = false;
        }
        _toolFlowInstalling = true;
        var resumeWasOpen = _toolFlowResumeOpen;
        _toolFlowResumeOpen = false;
        ToolFlowResumeButton.IsEnabled = false;
        UpdateInputState();
        try
        {
            Report(resumed
                ? LocalizationService.L("AiFlow_ContinueConfirmedPlan", "继续准备已确认的软件，已有的会复用。你可以切换会话，进度会保留。")
                : LocalizationService.L("AiFlow_PrepareConfirmedPlan", "方案已确认，正在连续安装并准备使用入口。你可以切换会话，进度会保留。"));
            var completed = new List<ToolFlowInstallItemResult>();
            var stages = new Progress<ToolFlowInstallProgress>(step =>
            {
                if (!CanTrackTask() || !_toolFlowInstalling) return;
                _taskProgress = step;
                if (step.ItemResult is { } itemResult)
                {
                    completed.Add(itemResult);
                    _taskResult = new ToolFlowInstallResult(completed.ToArray());
                    try { _taskSelection = store.GetBySubmissionId(chosen.SubmissionId) ?? _taskSelection; }
                    catch { } // A lost/corrupt snapshot must not throw from a posted UI progress callback.
                }
                if (IsLoaded) RefreshToolFlowTaskCard();
            });
            installResult = await new ToolFlowInstallRunner(store).RunAsync(chosen.SubmissionId,
                cancellationToken: stop.Token, stageProgress: stages, itemIds: itemIds);
            if (CanTrackTask())
            {
                _taskResult = installResult;
                _taskStopped = installResult.WasCanceled;
            }
            Report(installResult.WasCanceled
                ? LocalizationService.L("AiTask_StoppedReply", "已停止后续准备，完成的项目已保留；稍后可以从清单继续。")
                : string.Format(LocalizationService.L("AiTask_WholePlanResult", "已就绪 {0} 项 · 需要你处理 {1} 项 · 未成功 {2} 项。打开方式见工具入口。"),
                    installResult.InstalledCount + installResult.AlreadyInstalledCount + installResult.ReusedInstalledCount,
                    installResult.ManualStepCount, installResult.FailedCount));
            var deliveryProblems = installResult.Items.Count(item => item.Delivery?.Kind is
                ToolFlowPostInstallDeliveryKind.Failed or ToolFlowPostInstallDeliveryKind.Unavailable);
            if (deliveryProblems > 0)
                Report(string.Format(LocalizationService.L("AiTask_DeliveryNeedsAttention",
                    "有 {0} 项软件已具备，但使用入口仍需核对；安装结果已保留，可在工具入口重试。"), deliveryProblems));
        }
        catch (Exception ex)
        {
            Report(LocalizationService.L("AiFlow_InstallAborted", MiscTexts.T("工具流已保存，但自动安装流程中断：")) + ex.Message);
        }
        finally
        {
            _toolFlowInstalling = false;
            _toolFlowResumeOpen = resumeWasOpen;
            _toolFlowInstallCts = null;
            ToolFlowResumeButton.IsEnabled = true;
            if (!_lease.IsClosed)
            {
                RestoreCurrentToolFlowTask();
                if (_taskSelection is { } installedTask) _ = RefreshToolAccessAsync(installedTask, force: true);
                UpdateInputState();
            }
        }
        return installResult;
    }

    /// <summary>选定保存后刷新任务卡；人工步骤与 Agent 交接按需打开，分享仍服从现有开关。</summary>
    private async Task RunToolFlowFollowUpAsync(
        ToolFlowSelection chosen, ToolFlowSelectionStore store, ToolFlowInstallResult? installResult, Func<bool>? canDisplay = null)
    {
        var epoch = _displayEpoch;
        var viewEpoch = _toolFlowViewEpoch;
        var conversationId = _session?.Id;
        bool CanDisplay() => !_lease.IsClosed && epoch == _displayEpoch && viewEpoch == _toolFlowViewEpoch &&
            string.Equals(conversationId, _session?.Id, StringComparison.Ordinal) &&
            (chosen.ConversationId is null || chosen.ConversationId == conversationId) && (canDisplay?.Invoke() ?? true);
        void Report(string text) { if (CanDisplay()) AddSystemBubble(text); }
        // The task card keeps manual steps and the editable Agent handoff available on demand.
        // Reload persisted events rather than handing an earlier selection snapshot to the UI.
        try { chosen = store.GetBySubmissionId(chosen.SubmissionId) ?? chosen; }
        catch (Exception ex)
        {
            Report(LocalizationService.L("AiFlow_SnapshotReadFailed", "读取本机工具流快照失败：") + ex.Message);
            return;
        }
        if (CanDisplay()) RestoreCurrentToolFlowTask();

        if (!chosen.UploadEnabledAtSelection)
        {
            Report(LocalizationService.L("AiFlow_ShareSkippedLocal", "本次选定不参与分享，仅保存在本机；日后开启分享也不会补传这次记录。"));
            return;
        }
        if (!AppSettings.IsToolflowUploadEnabled)
        {
            Report(LocalizationService.L("AiFlow_ShareTurnedOffNow", MiscTexts.T("分享设置现在已关闭，本次没有发送；本机记录已保留。")));
            return;
        }

        var uploader = ToolFlowUploadClient.TryCreateConfigured(store);
        if (uploader is null)
        {
            Report(LocalizationService.L("AiFlow_NoServerNoUpload", "本次未发送；已选记录和实际安装结果保存在本机。"));
            return;
        }
        try
        {
            var upload = await uploader.UploadPendingAsync();
            Report(upload.PendingRequests == 0
                ? LocalizationService.L("AiFlow_UploadDelivered", "已选记录及可确认的执行结果已送达枕星服务器。")
                : string.Format(LocalizationService.L("AiFlow_UploadPending", MiscTexts.T("仍有 {0} 项分享记录待重试；本机记录已保留。")), upload.PendingRequests));
        }
        catch (Exception ex)
        {
            Report(LocalizationService.L("AiFlow_ShareIncomplete", MiscTexts.T("分享暂未完成，本机记录已保留：")) + ex.Message);
        }

        // 结构化计数汇总（下载按钮点击 / 真实下载请求）与工具流共用同一开关与服务器地址；
        // 单次尝试、静默失败（计数保留在本地），不影响上面的选定与安装结果。
        try
        {
            var metricsUploader = DownloadMetricsUploader.TryCreateConfigured();
            if (metricsUploader is not null) await metricsUploader.UploadPendingAsync();
        }
        catch { }
    }

    private async Task ShowAgentHandoffAsync(ToolFlowSelection selection, ToolFlowInstallResult? result, Func<bool>? canDisplay = null)
    {
        if (_lease.IsClosed || !(canDisplay?.Invoke() ?? true)) return;
        var preparation = await new ToolFlowPreparationService().PrepareAsync(selection.Items, selection.FlowText);
        if (_lease.IsClosed || !(canDisplay?.Invoke() ?? true)) return;
        var draft = new TextBox
        {
            Header = LocalizationService.L("AiFlow_BriefHeader", MiscTexts.T("发给所选 AI Agent 的任务说明（可先修改）")),
            Text = ToolFlowAgentHandoff.Build(selection, result, preparation),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 260,
            MaxHeight = 420,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var panel = new StackPanel { Spacing = 10, Width = 550 };
        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.L("AiFlow_BriefNotice", MiscTexts.T("请先检查项目目标、方案和执行状态，删除不想分享的内容。复制后由你自行粘贴给所选 AI Agent；枕星不会代你发送。")),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(draft);
        var dialog = new ContentDialog
        {
            Title = LocalizationService.L("AiFlow_BriefTitle", MiscTexts.T("下一步：交给你的 AI Agent")),
            Content = panel,
            PrimaryButtonText = LocalizationService.L("AiFlow_BriefCopy", MiscTexts.T("复制任务说明")),
            CloseButtonText = LocalizationService.L("AiFlow_BriefSkip", MiscTexts.T("暂不复制")),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || _lease.IsClosed || !(canDisplay?.Invoke() ?? true)) return;

        var package = new DataPackage();
        package.SetText(draft.Text);
        Clipboard.SetContent(package);
        AddSystemBubble(LocalizationService.L("AiFlow_BriefCopied", MiscTexts.T("任务说明已复制；请检查内容后粘贴给你选择的 AI Agent。枕星仍可继续帮你处理安装和电脑问题。")));
    }

    private List<ToolFlowConversationMessage> CaptureVisibleConversation()
    {
        List<ConversationDisplayItem> display;
        if (_session is TubaWinUi3.Services.Ai.Dsh.DshSession)
            display = BuildDisplaySnapshot();
        else if (_session is { } session)
        {
            SaveSessionGuarded(session);
            display = AiAssistantService.LoadConversationDisplay(session.Id);
            if (display.Count == 0)
                display = AiAssistantService.LoadConversation(session.Id)
                    .Where(m => (m.Role is "user" or "assistant") && !string.IsNullOrWhiteSpace(m.Content))
                    .Select(m => new ConversationDisplayItem
                    {
                        Type = "text", Role = m.Role, Content = m.Content,
                    }).ToList();
        }
        else
            display = [];

        // 不把隐藏推理、工具日志或系统提示词伪装为用户对话；历史展示记录无逐条时间。
        return display
            .Where(x => x.Type == "text" && (x.Role is "user" or "assistant") && !string.IsNullOrWhiteSpace(x.Content))
            .Select(x => new ToolFlowConversationMessage
            {
                Role = x.Role,
                Content = x.Content,
                AtUtc = null,
            }).ToList();
    }

    // ---------- 可见方案：2D 游戏示例模板（展示/切换不安装；选定只保存/按设置分享） ----------

    // 提示状态按会话代次（_displayEpoch）管理，而非页面实例一次性布尔：
    // 新对话/切换会话后可再次提示；重新打开含 2D 目标的历史时也可恢复入口。
    private readonly GodotPlanHintState _godotPlanHint = new();
    private bool _godotPlanDialogOpen;
    private string? _godotPlanGoalSeed;
    private string? _godotPlanAssetSeed;

    /// <summary>
    /// 用户明确提出 2D 游戏目标时，在页面上给出「可见方案」入口：只加一个提示气泡，不弹窗、不安装。
    /// 判据与本地入门卡共用同一正则；提示按会话代次管理（见 GodotPlanHintState）。
    /// </summary>
    private void MaybeOfferGodotTwoDPlan(string? userText)
    {
        if (!_godotPlanHint.ShouldOffer(_displayEpoch, userText)) return;
        _godotPlanGoalSeed = TrimPlanSeed(userText);
        _godotPlanAssetSeed = GodotTwoDPlans.GuessAssetRoute(userText);
        AddGodotPlanHintBubble();
    }

    /// <summary>
    /// 重新打开历史会话时恢复「可见方案」入口：历史里存在明确的 2D 游戏目标时，按当前会话代次提示一次。
    /// 入口是临时 UI 元素（不写入对话记录、不冒充已持久化消息）；每次重开历史只要目标仍在，都能再次拿到入口。
    /// </summary>
    private void MaybeOfferGodotPlanFromRestoredHistory(
        IReadOnlyList<TubaWinUi3.Services.Agent.ConversationDisplayItem> display,
        IReadOnlyList<AiChatMessage> protocolMessages)
    {
        var texts = new List<string?>();
        foreach (var item in display)
            if (item.Type == "text" && item.Role == "user")
                texts.Add(item.Content);
        if (texts.Count == 0)
            foreach (var message in protocolMessages)
                if (message.Role == "user" && message.Content is { Length: > 0 } content &&
                    !content.StartsWith("[TOOL_RESULT]", StringComparison.OrdinalIgnoreCase) &&
                    !content.StartsWith("[ACTION_CONFIRMED]", StringComparison.OrdinalIgnoreCase))
                    texts.Add(content);
        var seed = GodotTwoDPlans.FindFirst2DGoal(texts);
        if (seed is null || !_godotPlanHint.ShouldOffer(_displayEpoch, seed)) return;
        _godotPlanGoalSeed = TrimPlanSeed(seed);
        _godotPlanAssetSeed = GodotTwoDPlans.GuessAssetRoute(seed);
        AddGodotPlanHintBubble();
    }

    // ---------- 继续最近选定的工具流（恢复入口；只读本机快照，不安装/不上传/不新建快照） ----------

    private bool _toolFlowResumeOpen;

    /// <summary>
    /// 刷新「继续工具流」入口：本机存在最近一次可读的选定快照时显示，否则隐藏。
    /// 只读本地文件；读取失败按「没有」处理，不抛给页面。
    /// </summary>
    private void RefreshToolFlowResumeEntry()
    {
        ToolFlowSelection? latest = null;
        try
        {
            latest = new ToolFlowSelectionStore().FindLatestResumable();
        }
        catch
        {
            latest = null;
        }
        ToolFlowResumeButton.Visibility = latest is null ? Visibility.Collapsed : Visibility.Visible;
        RestoreCurrentToolFlowTask();
    }

    /// <summary>
    /// 恢复入口：打开最近选定方案的可继续界面。打开本身不安装、不上传、不生成新快照；
    /// 安装只在用户点「继续自动项」后走现有 runner。
    /// </summary>
    private async void ToolFlowResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_toolFlowResumeOpen || _godotPlanDialogOpen || _toolFlowActionOpen || _toolFlowInstalling) return;
        if (_isProcessing)
        {
            AddSystemBubble(LocalizationService.L("AiFlow_BusyResumeBlocked", MiscTexts.T("请等当前 AI 回复完成，再继续工具流清单。")));
            return;
        }
        _toolFlowResumeOpen = true;
        UpdateInputState();
        try
        {
            await ShowToolFlowResumeAsync();
        }
        catch (Exception ex)
        {
            if (!_lease.IsClosed) AddSystemBubble(LocalizationService.L("AiFlow_SnapshotReadFailed", "读取本机工具流快照失败：") + ex.Message);
        }
        finally
        {
            _toolFlowResumeOpen = false;
            if (!_lease.IsClosed) UpdateInputState();
        }
    }

    private async Task ShowToolFlowResumeAsync()
    {
        ToolFlowSelectionStore store;
        ToolFlowSelection? selection;
        try
        {
            store = new ToolFlowSelectionStore();
            selection = _taskSelection is null
                ? store.FindLatestResumable()
                : store.GetBySubmissionId(_taskSelection.SubmissionId);
        }
        catch (Exception ex)
        {
            AddSystemBubble(LocalizationService.L("AiFlow_SnapshotReadFailed", MiscTexts.T("读取本机工具流快照失败：")) + ex.Message);
            return;
        }
        if (selection is null)
        {
            AddSystemBubble(LocalizationService.L("AiFlow_NoSavedFlow", MiscTexts.T("本机还没有已保存的工具流。先在对话里说出目标并选定一套方案，这里就能继续。")));
            RefreshToolFlowResumeEntry();
            return;
        }

        var epoch = _displayEpoch;
        var viewEpoch = _toolFlowViewEpoch;
        var conversationId = _session?.Id;
        await ResumeToolFlowSelectionAsync(store, selection, () => !_lease.IsClosed && epoch == _displayEpoch &&
            viewEpoch == _toolFlowViewEpoch && string.Equals(conversationId, _session?.Id, StringComparison.Ordinal));
        RefreshToolFlowResumeEntry();
    }

    private async Task ResumeToolFlowSelectionAsync(ToolFlowSelectionStore store,
        ToolFlowSelection selection, Func<bool> canDisplay)
    {
        if (_taskSelection?.SubmissionId != selection.SubmissionId) { ClearToolAccess(); _taskResult = null; _taskStopped = false; }
        _taskSelection = selection;
        _taskExplicitlyResumed = string.IsNullOrEmpty(_session?.Id) || selection.ConversationId != _session.Id;
        _taskResumeConversationId = _session?.Id;
        RestoreCustomSkillsForSelectedTask(selection, force: true);
        RefreshToolFlowTaskCard();
        ToolFlowInstallResult? lastResult = _taskSelection?.SubmissionId == selection.SubmissionId ? _taskResult : null;
        while (canDisplay())
        {
            selection = store.GetBySubmissionId(selection.SubmissionId) ?? selection;
            var action = await ShowToolFlowResumeDialogAsync(store, selection, lastResult);
            if (!canDisplay()) break;
            if (action.Action == ToolFlowResumeAction.ContinueAutomatic)
            {
                lastResult = await RunToolFlowInstallWithBubblesAsync(store, selection, resumed: true, canDisplay: canDisplay, itemIds: action.ItemIds);
                break; // Results stay in the task card; do not reopen a modal after preparation.
            }
            if (action.Action == ToolFlowResumeAction.Handoff)
            {
                try
                {
                    await ShowToolFlowUsageAsync(store.GetBySubmissionId(selection.SubmissionId) ?? selection, lastResult, canDisplay);
                }
                catch (Exception ex)
                {
                    if (canDisplay()) AddSystemBubble(LocalizationService.L("AiFlow_AgentBriefUnavailable", MiscTexts.T("AI Agent 任务说明暂未显示：")) + ex.Message);
                }
            }
            break;   // 稍后 / 关闭：什么都不改
        }
        if (canDisplay()) RestoreCurrentToolFlowTask();
    }

    private enum ToolFlowResumeAction { None, ContinueAutomatic, Handoff }

    private static string ShortenResumeText(string? text, int max)
    {
        var value = (text ?? "").Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    private static string? TrimPlanSeed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= 1000 ? trimmed : trimmed[..1000];
    }

    private void AddGodotPlanHintBubble()
    {
        var text = new TextBlock
        {
            Text = LocalizationService.L("AiFlow_Notice2DDetected", MiscTexts.T("检测到你想做 2D 游戏：我准备了一份可修改的任务简报和一套示例方案（1 套主方案 + 2 套有实际差异的备选），每项都写明用途、依赖、取得方式、账号/费用与未知项。查看和切换方案不会安装任何软件；分享按你的分享设置执行。此提示是应用给的操作入口，不保存进对话记录。")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var open = new Button
        {
            Content = LocalizationService.L("AiFlow_OpenPlans2D", MiscTexts.T("打开 2D 游戏方案（1 主 + 2 备选）")),
            Padding = new Thickness(12, 7, 12, 7),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
        };
        open.Click += (_, _) => _ = ShowGodotTwoDPlanDialogAsync();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(text);
        panel.Children.Add(open);
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 16, 10),
            MaxWidth = 700,
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = panel,
        };
        MsgPanel.Children.Add(bubble);
        AnimateMessageIn(bubble, fromY: 10);
        EmptyStateBlock.Visibility = Visibility.Collapsed;
        QuickPillPanel.Visibility = Visibility.Collapsed;
        var hintTheme = ThemeRefreshScope.AttachRenderedContent(bubble);
        hintTheme.Bind(bubble, Border.BackgroundProperty, "AssistantSoftFillBrush");
        hintTheme.Bind(text, TextBlock.ForegroundProperty, "AssistantSecondaryTextBrush");
    }

    /// <summary>
    /// 可见方案对话框：如实标注的 Godot 2D 示例模板——可修改的任务简报、1 套主方案 + 2 套备选，
    /// 每项含用途/依赖/取得方式/账号费用网络/适用依据/未知项。切换方案只更新展示（不写盘、不安装）；
    /// 只有点「选定此方案（仅保存）」才写入现有本地快照；安装必须随后在固定清单确认里再次明确选择。
    /// </summary>
    private async Task ShowGodotTwoDPlanDialogAsync()
    {
        if (_godotPlanDialogOpen || _toolFlowActionOpen || _toolFlowInstalling || _toolFlowResumeOpen) return;
        _godotPlanDialogOpen = true;
        try
        {
            UpdateInputState();
            var conversation = CaptureVisibleConversation();
            var plans = GodotTwoDPlans.Catalog;
            var sharingNotice = GodotTwoDPlans.SharingNotice(
                AppSettings.IsToolflowUploadEnabled);

            var goalSource = _godotPlanGoalSeed is null ? GodotTwoDPlans.SourcePreset : GodotTwoDPlans.SourceUserText;
            var goalBox = new TextBox
            {
                Header = string.Format(LocalizationService.L("AiFlow_GoalFieldHeader", MiscTexts.T("目标（{0}，可修改）")), goalSource),
                Text = _godotPlanGoalSeed ?? GodotTwoDPlans.DefaultGoal,
                MaxLength = 4096,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 64,
            };
            var platformSource = GodotTwoDPlans.SourcePreset;
            var platformPicker = new RadioButtons
            {
                Header = string.Format(LocalizationService.L("AiFlow_PlatformHeader", MiscTexts.T("目标平台（{0}）")), platformSource),
                ItemsSource = GodotTwoDPlans.PlatformOptions.Select(o => GodotFlowTexts.T(o)).ToList(),
                SelectedIndex = 0,
            };
            var scaleSource = GodotTwoDPlans.SourcePreset;
            var scalePicker = new RadioButtons
            {
                Header = string.Format(LocalizationService.L("AiFlow_ScaleHeader", MiscTexts.T("游戏体量（{0}）")), scaleSource),
                ItemsSource = GodotTwoDPlans.ScaleOptions.Select(o => GodotFlowTexts.T(o)).ToList(),
                SelectedIndex = 0,
            };
            var assetOptions = GodotTwoDPlans.AssetRouteOptions.ToList();
            var assetIndex = assetOptions.IndexOf(_godotPlanAssetSeed ?? GodotTwoDPlans.SuggestOption);
            var assetSource = _godotPlanAssetSeed is null ? GodotTwoDPlans.SourcePreset : GodotTwoDPlans.SourceUserText;
            var assetPicker = new RadioButtons
            {
                Header = string.Format(LocalizationService.L("AiFlow_AssetHeader", MiscTexts.T("素材路线（{0}；这一项会影响方案；可选「你建议」或暂不决定）")), assetSource),
                ItemsSource = assetOptions.Select(o => GodotFlowTexts.T(o)).ToList(),
                SelectedIndex = assetIndex < 0 ? assetOptions.IndexOf(GodotTwoDPlans.SuggestOption) : assetIndex,
            };
            var codeSource = GodotTwoDPlans.SourcePreset;
            var codePicker = new RadioButtons
            {
                Header = string.Format(LocalizationService.L("AiFlow_CodeHeader", MiscTexts.T("代码辅助（{0}）")), codeSource),
                ItemsSource = GodotTwoDPlans.CodeAssistOptions.Select(o => GodotFlowTexts.T(o)).ToList(),
                SelectedIndex = 0,
            };

            var planPanel = new StackPanel { Spacing = 6 };
            var planRadios = new List<RadioButton>();
            var detailPanel = new StackPanel { Spacing = 8 };
            var conflictText = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };

            GodotTwoDPlanBrief BuildBriefFromUi() => new()
            {
                Goal = string.IsNullOrWhiteSpace(goalBox.Text) ? GodotTwoDPlans.DefaultGoal : goalBox.Text.Trim(),
                Platform = GodotTwoDPlans.PlatformOptions[Math.Max(0, platformPicker.SelectedIndex)],
                Scale = GodotTwoDPlans.ScaleOptions[Math.Max(0, scalePicker.SelectedIndex)],
                AssetRoute = GodotTwoDPlans.AssetRouteOptions[Math.Max(0, assetPicker.SelectedIndex)],
                CodeAssist = GodotTwoDPlans.CodeAssistOptions[Math.Max(0, codePicker.SelectedIndex)],
                GoalSource = goalSource,
                PlatformSource = platformSource,
                ScaleSource = scaleSource,
                AssetRouteSource = assetSource,
                CodeAssistSource = codeSource,
            };

            // 偏好与方案冲突时即时提示；真正拦截在「选定此方案」点击处（不自动覆盖用户的选择）。
            void RefreshConflictHint()
            {
                var currentPlan = planRadios.FirstOrDefault(x => x.IsChecked == true)?.Tag as GodotTwoDPlan ?? plans[0];
                var conflict = GodotTwoDPlans.FindSelectionConflict(currentPlan, BuildBriefFromUi());
                conflictText.Text = conflict ?? "";
                conflictText.Visibility = conflict is null ? Visibility.Collapsed : Visibility.Visible;
            }

            static void AddDetailLine(StackPanel target, string label, string value) => target.Children.Add(new TextBlock
            {
                Text = MiscTexts.TSub($"{label}：{value}"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
            });

            void RefreshDetail(GodotTwoDPlan plan)
            {
                detailPanel.Children.Clear();
                detailPanel.Children.Add(new TextBlock
                {
                    Text = string.Format(LocalizationService.L("AiFlow_PlanDetailTitle", MiscTexts.T("所选方案详情：{0}")), GodotFlowTexts.T(plan.Name)),
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
                foreach (var item in plan.Items)
                {
                    var lines = new StackPanel { Spacing = 3 };
                    lines.Children.Add(new TextBlock
                    {
                        Text = MiscTexts.TSub($"{GodotFlowTexts.T(item.Name)}（{GodotFlowTexts.T(GodotTwoDPlans.KindLabel(item.Kind))}）"),
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    });
                    AddDetailLine(lines, LocalizationService.L("AiFlow_FieldUsage", MiscTexts.T("用途")), GodotFlowTexts.T(item.Purpose));
                    AddDetailLine(lines, LocalizationService.L("AiFlow_FieldDependency", MiscTexts.T("依赖")), GodotFlowTexts.T(item.Dependency));
                    AddDetailLine(lines, LocalizationService.L("AiFlow_FieldHowToGet", MiscTexts.T("取得方式")), GodotFlowTexts.T(item.Acquisition));
                    AddDetailLine(lines, LocalizationService.L("AiFlow_FieldAccountCost", MiscTexts.T("账号/费用/网络")), GodotFlowTexts.T(item.Requirements));
                    AddDetailLine(lines, LocalizationService.L("AiFlow_FieldRationale", MiscTexts.T("适用依据")), GodotFlowTexts.T(item.Applicability));
                    AddDetailLine(lines, LocalizationService.L("AiFlow_FieldUnknowns", MiscTexts.T("未知项")), GodotFlowTexts.T(item.Unknowns));
                    var detailCard = new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 8, 10, 8),
                        Child = lines,
                    };
                    ThemeBind(detailCard, Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
                    detailPanel.Children.Add(detailCard);
                }
                if (plan.Unknowns.Count > 0)
                {
                    var unknowns = new StackPanel { Spacing = 2 };
                    unknowns.Children.Add(new TextBlock
                    {
                        Text = LocalizationService.L("AiFlow_UnknownsTitle", MiscTexts.T("本方案未知项（待核实）")),
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    });
                    foreach (var unknown in plan.Unknowns)
                        unknowns.Children.Add(new TextBlock
                        {
                            Text = "· " + GodotFlowTexts.T(unknown),
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                            Opacity = 0.8,
                        });
                    detailPanel.Children.Add(unknowns);
                }
                if (plan.Notes.Count > 0)
                {
                    var notes = new StackPanel { Spacing = 2 };
                    notes.Children.Add(new TextBlock
                    {
                        Text = LocalizationService.L("AiFlow_Remarks", MiscTexts.T("备注")),
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    });
                    foreach (var note in plan.Notes)
                        notes.Children.Add(new TextBlock
                        {
                            Text = "· " + GodotFlowTexts.T(note),
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                            Opacity = 0.8,
                        });
                    detailPanel.Children.Add(notes);
                }
            }

            void AddPlanCard(GodotTwoDPlan plan)
            {
                var (automatic, _) = GodotTwoDPlans.DescribeInstallScope(plan);
                var summary = new StackPanel { Spacing = 2 };
                summary.Children.Add(new TextBlock
                {
                    Text = $"{GodotFlowTexts.T(plan.Badge)} · {GodotFlowTexts.T(plan.Name)}",
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
                summary.Children.Add(new TextBlock
                {
                    Text = GodotFlowTexts.T(plan.Positioning),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85,
                });
                summary.Children.Add(new TextBlock
                {
                    Text = LocalizationService.L("AiFlow_Difference", MiscTexts.T("差异：")) + GodotFlowTexts.T(plan.Difference),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85,
                });
                summary.Children.Add(new TextBlock
                {
                    Text = GodotFlowTexts.T(plan.CompareLine),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75,
                });
                summary.Children.Add(new TextBlock
                {
                    Text = automatic.Count > 0
                        ? LocalizationService.L("AiFlow_AutoPrefixShort", MiscTexts.T("可自动安装：")) + string.Join(MiscTexts.T("、"), automatic) + LocalizationService.L("AiFlow_RestManual", MiscTexts.T("；其余需人工完成。"))
                        : LocalizationService.L("AiFlow_AutoNone2", MiscTexts.T("本方案没有可自动安装的固定目标；全部需人工完成。")),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75,
                });
                var radio = new RadioButton { GroupName = "godotPlanChoice", Content = summary, Tag = plan };
                radio.Checked += (_, _) =>
                {
                    RefreshDetail(plan);
                    RefreshConflictHint();
                };
                planRadios.Add(radio);
                planPanel.Children.Add(radio);
            }

            foreach (var plan in plans) AddPlanCard(plan);
            planRadios[0].IsChecked = true;
            RefreshDetail(plans[0]);
            RefreshConflictHint();

            // 素材路线变化时，自动把「更贴合」的方案标为选中（用户随后仍可自由改选）。
            void ApplyAssetSuggestion(string? route)
            {
                var suggested = GodotTwoDPlans.SuggestPlanForAssetRoute(route);
                if (suggested is null) return;
                foreach (var radio in planRadios)
                    if (ReferenceEquals(radio.Tag, suggested)) radio.IsChecked = true;
            }
            goalBox.TextChanged += (_, _) =>
            {
                var edited = !string.Equals((goalBox.Text ?? "").Trim(),
                    _godotPlanGoalSeed ?? GodotTwoDPlans.DefaultGoal, StringComparison.Ordinal);
                goalSource = edited
                    ? GodotTwoDPlans.SourceUserChoice
                    : (_godotPlanGoalSeed is null ? GodotTwoDPlans.SourcePreset : GodotTwoDPlans.SourceUserText);
                goalBox.Header = string.Format(LocalizationService.L("AiFlow_GoalFieldHeader", MiscTexts.T("目标（{0}，可修改）")), goalSource);
            };
            platformPicker.SelectionChanged += (_, _) =>
            {
                platformSource = GodotTwoDPlans.SourceUserChoice;
                platformPicker.Header = string.Format(LocalizationService.L("AiFlow_PlatformHeader", MiscTexts.T("目标平台（{0}）")), platformSource);
                RefreshConflictHint();
            };
            scalePicker.SelectionChanged += (_, _) =>
            {
                scaleSource = GodotTwoDPlans.SourceUserChoice;
                scalePicker.Header = string.Format(LocalizationService.L("AiFlow_ScaleHeader", MiscTexts.T("游戏体量（{0}）")), scaleSource);
                RefreshConflictHint();
            };
            codePicker.SelectionChanged += (_, _) =>
            {
                codeSource = GodotTwoDPlans.SourceUserChoice;
                codePicker.Header = string.Format(LocalizationService.L("AiFlow_CodeHeader", MiscTexts.T("代码辅助（{0}）")), codeSource);
                RefreshConflictHint();
            };
            assetPicker.SelectionChanged += (_, _) =>
            {
                if (assetPicker.SelectedItem is string route)
                {
                    assetSource = GodotTwoDPlans.SourceUserChoice;
                    assetPicker.Header = string.Format(LocalizationService.L("AiFlow_AssetHeader", MiscTexts.T("素材路线（{0}；这一项会影响方案；可选「你建议」或暂不决定）")), assetSource);
                    ApplyAssetSuggestion(route);
                }
                RefreshConflictHint();
            };
            if (_godotPlanAssetSeed is { } initialRoute) ApplyAssetSuggestion(initialRoute);

            var body = new StackPanel { Spacing = 10, Width = 636 };
            body.Children.Add(new TextBlock
            {
                Text = LocalizationService.L("AiFlow_TemplateNotice", MiscTexts.T("这是一份如实标注的示例模板（由应用内置整理，不依赖 AI 回复是否给出结构化方案）：任务简报逐字段标注来源——「来自你的描述」=你在对话里说过的，「你的选择」=你在这里改的，「应用预选（默认）」=应用的默认建议、不是你的输入。本页只做整理与选择，不会安装任何软件。")),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
            });
            body.Children.Add(new TextBlock
            {
                Text = LocalizationService.L("AiFlow_HostPlatformPrefix", MiscTexts.T("本机系统（应用只读读取）：")) + GodotTwoDPlans.DescribeHostPlatform() + LocalizationService.L("AiFlow_HostPlatformSuffix", MiscTexts.T("；这些工具是否已安装，本页不检查。")),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
            });
            body.Children.Add(new TextBlock
            {
                Text = sharingNotice,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
            });
            body.Children.Add(new TextBlock
            {
                Text = LocalizationService.L("AiFlow_BriefSectionTitle", MiscTexts.T("任务简报（可修改）")),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            body.Children.Add(goalBox);
            body.Children.Add(assetPicker);
            body.Children.Add(platformPicker);
            body.Children.Add(scalePicker);
            body.Children.Add(codePicker);
            body.Children.Add(new TextBlock
            {
                Text = LocalizationService.L("AiFlow_BudgetNotice", MiscTexts.T("暂未逐项盘问：编程经验、时间投入与发布计划，等有需要时再告诉我即可。预算口径：免费开源优先，费用与账号要求逐项标注，价格不编造。")),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            });
            body.Children.Add(new TextBlock
            {
                Text = LocalizationService.L("AiFlow_PlansSectionTitle", MiscTexts.T("工具流方案（1 主 + 2 备选，切换只比较、不安装）")),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            body.Children.Add(planPanel);
            body.Children.Add(detailPanel);

            var dialog = new ContentDialog
            {
                Title = LocalizationService.L("AiFlow_PlansDialogTitle", MiscTexts.T("2D 游戏 · 工具流方案（示例模板）")),
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children = { new ScrollViewer { Content = body, MaxHeight = 520 }, conflictText },
                },
                PrimaryButtonText = LocalizationService.L("AiFlow_PlansSelectPrimary", MiscTexts.T("选定此方案（不启动安装）")),
                CloseButtonText = LocalizationService.L("Common_Cancel", MiscTexts.T("取消")),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                // 显式偏好与方案冲突时不选定、不保存：留在本页让用户调整偏好或方案（不自动覆盖其选择）。
                var currentPlan = planRadios.FirstOrDefault(x => x.IsChecked == true)?.Tag as GodotTwoDPlan ?? plans[0];
                var conflict = GodotTwoDPlans.FindSelectionConflict(currentPlan, BuildBriefFromUi());
                if (conflict is null) return;
                conflictText.Text = conflict;
                conflictText.Visibility = Visibility.Visible;
                args.Cancel = true;
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var selectedPlan = planRadios.FirstOrDefault(x => x.IsChecked == true)?.Tag as GodotTwoDPlan ?? plans[0];
            var brief = BuildBriefFromUi();

            ToolFlowSelection chosen;
            ToolFlowSelectionStore store;
            try
            {
                chosen = GodotTwoDPlans.BuildSelection(selectedPlan, brief, conversation,
                    ToolFlowUploadClient.IsUploadEligibleNow()) with { ConversationId = _session?.Id };
                store = new ToolFlowSelectionStore();
                store.SelectForInstall(chosen);
                ClearToolFlowTask();
            }
            catch (Exception ex)
            {
                AddSystemBubble(LocalizationService.L("AiFlow_SaveFailed", MiscTexts.T("工具流未保存：")) + ex.Message);
                return;
            }

            RefreshToolFlowResumeEntry();
            AddSystemBubble(string.Format(LocalizationService.L("AiFlow_ChosenSaved", MiscTexts.T("已选定并保存到本机：{0}。这一步不安装任何软件（安装需要你在接下来的确认里明确选择）；分享按你的分享设置执行，下面会说明结果。")), chosen.FlowName));
            ToolFlowInstallResult? installResult = null;
            if (await ConfirmGodotPlanInstallAsync(selectedPlan))
                installResult = await RunToolFlowInstallWithBubblesAsync(store, chosen);
            else
                AddSystemBubble(LocalizationService.L("AiFlow_ChosenLater", MiscTexts.T("已按「稍后再说」保留方案——「稍后」只表示暂不安装；分享按你的分享设置照常执行。想安装时，重新打开方案页面核对固定清单后再选择即可。")));
            await RunToolFlowFollowUpAsync(chosen, store, installResult);
        }
        finally
        {
            _godotPlanDialogOpen = false;
            if (!_lease.IsClosed) UpdateInputState();
        }
    }

    /// <summary>
    /// 安装前的固定清单确认：显示本次将自动处理的固定目标与需人工完成的项；
    /// 只有用户明确选择「开始安装」才会调用现有 runner。
    /// </summary>
    private async Task<bool> ConfirmGodotPlanInstallAsync(GodotTwoDPlan plan)
    {
        var (automatic, manual) = GodotTwoDPlans.DescribeInstallScope(plan);
        var panel = new StackPanel { Spacing = 10, Width = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = automatic.Count > 0
                ? LocalizationService.L("AiFlow_AutoListPrefix", MiscTexts.T("将自动处理（应用内固定目标；逐项安装后会再次检测是否已装）：\n· ")) + string.Join("\n· ", automatic)
                : LocalizationService.L("AiFlow_AutoListNone", MiscTexts.T("本方案没有可自动安装的固定目标。")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = manual.Count > 0
                ? LocalizationService.L("AiFlow_ManualListPrefix", MiscTexts.T("需人工完成（账号、素材与下载按说明由你操作）：\n· ")) + string.Join("\n· ", manual)
                : LocalizationService.L("AiFlow_ManualNone", MiscTexts.T("需人工完成：无。")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        });
        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.L("AiFlow_ExecNotice", MiscTexts.T("不会运行方案中提到的任何外部网址；不会替你注册、登录或付费。")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        var dialog = new ContentDialog
        {
            Title = LocalizationService.L("AiFlow_ExecConfirmTitle", MiscTexts.T("执行安装确认（仅处理上方固定清单）")),
            Content = panel,
            PrimaryButtonText = LocalizationService.L("AiCategory_StartInstall", MiscTexts.T("开始安装")),
            CloseButtonText = LocalizationService.L("AiFlow_ExecLater", MiscTexts.T("稍后再说（暂不安装）")),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
