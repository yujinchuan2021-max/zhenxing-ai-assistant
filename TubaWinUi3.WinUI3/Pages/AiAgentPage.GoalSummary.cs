using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    private void RefreshConversationGoalSummary()
    {
        // A confirmed task already has its own fixed goal card. Avoid two competing summaries.
        if (ToolFlowTaskHost.Visibility == Visibility.Visible)
        {
            ConversationGoalBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var newTaskStart = ConversationGoalPresentation.FindExplicitNewTaskStart(_toolFlowVisibleMessages);
        var proposal = _toolFlowMessageActions.LastOrDefault(action =>
            action.Epoch == _displayEpoch && action.Ready && !action.CompletionFailed &&
            action.ConversationIndex >= newTaskStart)?.Proposal;
        var question = _modelChoiceActions.LastOrDefault(action => action.Ready && !action.Failed &&
            action.SelectedId is null && action.Bubble.Epoch == _displayEpoch &&
            action.ConversationIndex == _toolFlowVisibleMessages.Count - 1 &&
            MsgPanel.Children.Contains(action.Bubble.Root));
        var presentation = ConversationGoalPresentation.Create(null, proposal?.SuggestedProjectGoal,
            _toolFlowVisibleMessages, _isProcessing, question is not null,
            _toolFlowMessageActions.Any(action => action.Epoch == _displayEpoch && action.Ready &&
                !action.CompletionFailed && !action.Selected &&
                action.ConversationIndex == _toolFlowVisibleMessages.Count - 1 && MsgPanel.Children.Contains(action.MessageRoot)));
        ConversationGoalBanner.Visibility = presentation is null ? Visibility.Collapsed : Visibility.Visible;
        if (presentation is null) return;
        ConversationGoalText.Text = GoalSummaryText("AiGoal_HeaderGoal", "当前需求：", "Current request: ") + presentation.Goal;
        ToolTipService.SetToolTip(ConversationGoalText, presentation.Goal);
        ConversationGoalStageText.Text = presentation.Stage switch
        {
            ConversationGoalStage.ChoosingPlan => GoalSummaryText("AiGoal_HeaderChoosing", "下一步：选择一套方案", "Next: choose a plan"),
            ConversationGoalStage.AnsweringQuestion => GoalSummaryText("AiGoal_HeaderQuestion", "下一步：点击下方选项", "Next: select an option below"),
            _ when _isProcessing => GoalSummaryText("AiGoal_HeaderThinking", "正在分析当前需求…", "Considering your request…"),
            _ => GoalSummaryText("AiGoal_HeaderUnderstanding", "当前步骤：理解需求", "Current step: understand your request"),
        };
    }

    private static string GoalSummaryText(string key, string zh, string en) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? en : zh);
}
