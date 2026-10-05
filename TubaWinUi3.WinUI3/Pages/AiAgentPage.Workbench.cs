using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    private string? _workbenchSelectionId;
    private bool _workbenchShowConversation;
    private bool _refreshingWorkbench;
    private string? _workbenchAttentionKey;
    // A split is useful only when both the current step and an actual plan card
    // have room to read. This is the workspace width after the conversation rail.
    private const double WorkbenchSplitWidth = 1080;
    private const double WorkbenchAssistantMinWidth = 640;
    private const double WorkbenchTaskMaxWidth = 560;
    private const double WorkbenchPaneSpacing = 12;

    // This is a view preference only. Installation authority and conversation ownership
    // continue to come from the existing selection/session checks in Task.cs.
    private void UpdateWorkbenchTask(string? submissionId)
    {
        if (!string.Equals(_workbenchSelectionId, submissionId, StringComparison.Ordinal))
        {
            _workbenchSelectionId = submissionId;
            _workbenchShowConversation = false;
            _workbenchAttentionKey = null;
            WorkbenchScroll.ChangeView(null, 0, null, disableAnimation: true);
        }
        RefreshWorkbenchLayout();
    }

    private void WorkspaceBody_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshWorkbenchLayout();

    private void WorkbenchTaskTab_Click(object sender, RoutedEventArgs e)
    {
        _workbenchShowConversation = false;
        RefreshWorkbenchLayout();
    }

    private void WorkbenchChatTab_Click(object sender, RoutedEventArgs e)
    {
        _workbenchShowConversation = true;
        RefreshWorkbenchLayout();
    }

    private bool WorkbenchNeedsAnswer => _awaitingConfirmation || _modelChoiceActions.Any(action =>
        action.Ready && !action.Failed && action.SelectedId is null &&
        action.Bubble.Epoch == _displayEpoch &&
        action.ConversationIndex == _toolFlowVisibleMessages.Count - 1 &&
        MsgPanel.Children.Contains(action.Bubble.Root));

    private void RefreshWorkbenchAttention()
    {
        if (_workbenchSelectionId is null) return;
        // A new pending question must not become invisible behind the compact workbench.
        // Remember its identity so a deliberate return to the task is not undone on every tick.
        var needsAnswer = WorkbenchNeedsAnswer;
        var key = needsAnswer ? $"{_displayEpoch}:{_toolFlowVisibleMessages.Count}:{_awaitingConfirmation}" : null;
        if (key is not null && !string.Equals(key, _workbenchAttentionKey, StringComparison.Ordinal))
            _workbenchShowConversation = true;
        _workbenchAttentionKey = key;
        RefreshWorkbenchLayout();
    }

    private void RefreshWorkbenchLayout()
    {
        // Named XAML elements may raise SizeChanged while InitializeComponent is still running.
        if (_refreshingWorkbench || WorkspaceBody is null || AssistantPane is null ||
            WorkbenchTabs is null || ComposerHost is null || _taskCard is null) return;
        _refreshingWorkbench = true;
        try
        {
            var active = _workbenchSelectionId is not null;
            var split = active && WorkspaceBody.ActualWidth >= WorkbenchSplitWidth;
            var showTask = active && (split || !_workbenchShowConversation);
            var showChat = !active || split || _workbenchShowConversation;
            var availableWidth = Math.Max(0, WorkspaceBody.ActualWidth - (split ? WorkbenchPaneSpacing : 0));
            var taskWidth = split ? Math.Min(WorkbenchTaskMaxWidth,
                Math.Min(availableWidth * .4, availableWidth - WorkbenchAssistantMinWidth)) : availableWidth;
            WorkbenchColumn.Width = !showTask ? new GridLength(0) : split
                ? new GridLength(taskWidth) : new GridLength(1, GridUnitType.Star);
            AssistantColumn.Width = showChat ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            WorkspaceBody.ColumnSpacing = split ? WorkbenchPaneSpacing : 0;
            WorkbenchScroll.Visibility = showTask ? Visibility.Visible : Visibility.Collapsed;
            AssistantPane.Visibility = showChat ? Visibility.Visible : Visibility.Collapsed;
            WorkbenchTabs.Visibility = active && !split ? Visibility.Visible : Visibility.Collapsed;
            WorkbenchTaskTab.IsChecked = !_workbenchShowConversation;
            WorkbenchChatTab.IsChecked = _workbenchShowConversation;
            AssistantPaneHeading.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

            var english = LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage;
            string T(string key, string zh, string en) => LocalizationService.L(key, english ? en : zh);
            WorkbenchTaskTab.Content = T("AiWorkbench_Task", "任务工作台", "Workbench");
            WorkbenchChatTab.Content = WorkbenchNeedsAnswer
                ? T("AiWorkbench_ReplyNeeded", "对话 · 等你回答", "Chat · reply needed")
                : T("AiWorkbench_Chat", "与助手沟通", "Talk to assistant");
            WorkbenchAssistantTitle.Text = T("AiWorkbench_Assistant", "枕星助手", "Zhenxing assistant");
            WorkbenchAssistantHint.Text = T("AiWorkbench_ChatHint", "补充需求、调整方案或询问当前一步。",
                "Refine your request, adjust the plan, or ask about this step.");
            if (!split && _taskSelection is { } task)
                WorkbenchAssistantHint.Text = T("AiWorkbench_CurrentGoal", "当前目标：", "Current goal: ") + task.ProjectGoal;
            ToolTipService.SetToolTip(WorkbenchAssistantHint, WorkbenchAssistantHint.Text);
            AutomationProperties.SetName(WorkbenchTaskTab, WorkbenchTaskTab.Content.ToString());
            AutomationProperties.SetName(WorkbenchChatTab, WorkbenchChatTab.Content.ToString());
            var paneWidth = split ? availableWidth - taskWidth : WorkspaceBody.ActualWidth;
            var narrowComposer = paneWidth < 640;
            MsgPanel.Padding = active ? new Thickness(10, 14, 10, 14)
                : new Thickness(ActualWidth < 1000 ? 14 : 26, ActualWidth < 1000 ? 18 : 28,
                    ActualWidth < 1000 ? 14 : 26, 20);
            ComposerHost.Padding = active ? new Thickness(10, 8, 10, 14)
                : narrowComposer ? new Thickness(14, 8, 14, 14) : new Thickness(22, 8, 22, 14);
            ToolFlowTaskHost.Margin = split ? new Thickness(18, 18, 6, 24)
                : new Thickness(WorkspaceBody.ActualWidth < 540 ? 14 : 24, 22,
                    WorkspaceBody.ActualWidth < 540 ? 14 : 24, 24);
            foreach (var message in MsgPanel.Children.OfType<Border>())
                if (message.Tag is AssistantBubble bubble) ApplyAssistantBubbleLayout(bubble);
            SkillsButtonLabel.Visibility = narrowComposer ? Visibility.Collapsed : Visibility.Visible;
            FullAccessToggle.Visibility = narrowComposer || _compactView ? Visibility.Collapsed : Visibility.Visible;
            if (narrowComposer) TokenUsageBubble.Visibility = Visibility.Collapsed;
            else UpdateTokenUsage();
            if (active)
                InputBox.PlaceholderText = T("AiWorkbench_Input", "想调整方案或遇到问题？告诉我…",
                    "Want to adjust the plan or need help?");
            else
                InputBox.PlaceholderText = _compactView
                    ? Ui("CompactInputPlaceholder", "输入问题（Enter 发送，Shift+Enter 换行）")
                    : Ui("InputPlaceholder", "说说你想完成什么，例如做一款 2D 游戏或解决电脑问题…");
        }
        finally { _refreshingWorkbench = false; }
    }

    private void ApplyAssistantBubbleLayout(AssistantBubble bubble)
    {
        // Update existing replies in place when entering/leaving the workbench;
        // the original controls, choice state, draft and scroll host stay alive.
        bubble.Root.Padding = new Thickness(_workbenchSelectionId is null ? 16 : 12);
    }
}
