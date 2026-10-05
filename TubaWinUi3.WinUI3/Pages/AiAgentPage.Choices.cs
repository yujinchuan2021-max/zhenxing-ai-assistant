using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    private readonly List<ModelChoiceAction> _modelChoiceActions = [];

    private sealed class ModelChoiceAction
    {
        public ModelPreferenceControl? ModelControl { get; init; }
        public ChatChoiceControl? QuestionControl { get; init; }
        public ChatChoicePrompt? Question { get; init; }
        public required AssistantBubble Bubble { get; init; }
        public required int ConversationIndex { get; init; }
        public bool Ready { get; set; }
        public bool Failed { get; set; }
        public bool Sending { get; set; }
        public string? SelectedId { get; set; }
    }

    private bool AddModelChoiceAction(AssistantBubble bubble, string content, int index, bool ready, bool failed)
    {
        if (!ModelPreferenceQuestion.TryCapture(content))
            return AddChatChoiceAction(bubble, content, index, ready, failed);
        var control = new ModelPreferenceControl();
        var action = new ModelChoiceAction
        {
            ModelControl = control, Bubble = bubble, ConversationIndex = index, Ready = ready, Failed = failed,
        };
        control.ChoiceSelected += async choice =>
        {
            // Read by stable ID so a language change never sends an outdated label.
            var current = ModelPreferenceQuestion.Choices.FirstOrDefault(x => x.Id == choice.Id);
            if (current is not null) await SendChoiceAnswerAsync(action, current.Id, current.Answer);
        };
        _modelChoiceActions.Add(action);
        bubble.ReadableContent = ModelPreferenceQuestion.CopyWithChoices(bubble.ReadableContent);
        bubble.ToolFlowActions.Children.Add(control);
        RefreshModelChoiceActions();
        return true;
    }

    private bool AddChatChoiceAction(AssistantBubble bubble, string content, int index, bool ready, bool failed)
    {
        var question = ChatChoiceQuestion.TryCapture(content);
        if (question is null) return false;
        var control = new ChatChoiceControl(question);
        var action = new ModelChoiceAction
        {
            QuestionControl = control, Question = question, Bubble = bubble,
            ConversationIndex = index, Ready = ready, Failed = failed,
        };
        control.ChoiceSelected += async choice =>
        {
            var current = question.Options.FirstOrDefault(x => x.Id == choice.Id);
            if (current is not null) await SendChoiceAnswerAsync(action, current.Id, current.Answer);
        };
        _modelChoiceActions.Add(action);
        // For a completed natural-language choice question, remove its duplicate
        // prose list after capture; streaming prose was never rewritten or guessed.
        RenderAssistantContent(bubble, ChatChoiceQuestion.GetQuestionVisibleContent(content, question));
        bubble.ReadableContent = ChatChoiceQuestion.CopyWithChoices(content, question);
        bubble.ToolFlowActions.Children.Add(control);
        RefreshModelChoiceActions();
        return true;
    }

    private async Task SendChoiceAnswerAsync(ModelChoiceAction action, string id, string answer)
    {
        if (!CanAnswerModelChoice(action)) return;
        // Disable immediately, including while a missing-service prompt is open.
        // A blocked send does not consume the choice, draft, or attachments.
        action.Sending = true;
        RefreshModelChoiceActions();
        try
        {
            await SendAsync(answer, preserveDraft: true, onAccepted: () =>
            {
                action.SelectedId = id;
                RefreshModelChoiceActions();
            }, continuesCurrentTask: true);
        }
        catch
        {
            if (!_lease.IsClosed && action.Bubble.Epoch == _displayEpoch)
                AddSystemBubble(LocalizationService.L("AiChoice_SendFailed", "这次选择未能发送，请检查 AI 服务后重试。"));
        }
        finally
        {
            action.Sending = false;
            if (!_lease.IsClosed && action.Bubble.Epoch == _displayEpoch) RefreshModelChoiceActions();
        }
    }

    private bool CanAnswerModelChoice(ModelChoiceAction action) =>
        !_lease.IsClosed && IsLoaded && action.Ready && !action.Failed && !action.Sending && action.SelectedId is null &&
        action.Bubble.Epoch == _displayEpoch && MsgPanel.Children.Contains(action.Bubble.Root) &&
        action.ConversationIndex == _toolFlowVisibleMessages.Count - 1 &&
        !_isProcessing && !_awaitingConfirmation && !_toolFlowActionOpen && !_toolFlowInstalling &&
        !_godotPlanDialogOpen && !_toolFlowResumeOpen;

    private void CompleteModelChoiceActions(int epoch, bool succeeded)
    {
        foreach (var action in _modelChoiceActions)
            if (action.Bubble.Epoch == epoch && action.ConversationIndex >= _toolFlowRoundStart)
            {
                action.Ready = succeeded;
                action.Failed = !succeeded;
            }
    }

    private void RefreshModelChoiceActions()
    {
        var busy = _isProcessing || _awaitingConfirmation || _toolFlowActionOpen || _toolFlowInstalling ||
            _godotPlanDialogOpen || _toolFlowResumeOpen;
        foreach (var action in _modelChoiceActions)
        {
            var current = action.Bubble.Epoch == _displayEpoch &&
                action.ConversationIndex == _toolFlowVisibleMessages.Count - 1 &&
                MsgPanel.Children.Contains(action.Bubble.Root);
            if (action.Question is { } question)
            {
                action.QuestionControl!.SetActionState(action.Ready, busy || !current || action.Sending, action.SelectedId, action.Failed);
                action.Bubble.ReadableContent = ChatChoiceQuestion.CopyWithChoices(action.Bubble.RawContent.ToString(), question);
            }
            else
            {
                action.ModelControl!.SetActionState(action.Ready, busy || !current || action.Sending, action.SelectedId, action.Failed);
                action.Bubble.ReadableContent = ModelPreferenceQuestion.CopyWithChoices(
                    AssistantReplyPresentation.Create(action.Bubble.RawContent.ToString()).Body);
            }
        }
        RefreshWorkbenchAttention();
    }
}
