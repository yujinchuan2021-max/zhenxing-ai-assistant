using TubaWinUi3.Services;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    internal event Action? NewConversationRequested;
    internal event Action<ConversationMeta>? OpenConversationRequested;
    internal event Action? ConversationStateChanged;
    internal event Action<ConversationMeta>? DeleteConversationRequested;
    internal event Action<ConversationMeta, string>? RenameConversationRequested;
    internal Func<IReadOnlyList<ConversationMeta>>? WindowConversations { get; set; }
    private readonly string _draftConversationId = "draft-" + Guid.NewGuid().ToString("N");
    private readonly DateTime _viewCreatedAt = DateTime.Now;
    private string? _draftTitle;

    internal string ConversationId => _session?.Id ?? _draftConversationId;
    internal bool CanNavigateConversations => !_lease.IsClosed &&
        !_toolFlowResumeOpen && !_toolFlowActionOpen && !_godotPlanDialogOpen && !_skillEditorOpen;
    internal bool CanDeleteConversation => CanNavigateConversations && !_toolFlowInstalling && !_isProcessing &&
        !_awaitingConfirmation && _session?.IsRunning != true;
    internal bool IsUntouchedNewConversation => _session is null && _taskSelection is null && MsgPanel.Children.Count == 0 &&
        string.IsNullOrWhiteSpace(InputBox.Text) && _attachments.Count == 0;

    internal bool CanEvictCachedConversation
    {
        get
        {
            if (_session is null || !CanDeleteConversation || _attachments.Count > 0 ||
                !string.IsNullOrWhiteSpace(InputBox.Text) || _taskSelection is not null ||
                _streamingBubble is not null || _activeChain is not null) return false;
            // Save methods deliberately swallow IO failures. An idle page is only
            // disposable when its actual visible transcript can be read back.
            try
            {
                var persisted = AiAssistantService.LoadConversationDisplay(_session.Id);
                var snapshotMatches = _session is TubaWinUi3.Services.Agent.AgentSession builtin
                    ? builtin.MatchesPersistedDisplay(persisted)
                    : System.Text.Json.JsonSerializer.Serialize(_displayLog.Where(item => item.Type is "text" or "steps")) ==
                      System.Text.Json.JsonSerializer.Serialize(persisted.Where(item => item.Type is "text" or "steps"));
                if (!snapshotMatches) return false;
                var saved = persisted
                    .Where(item => item.Type == "text" && (item.Role is "user" or "assistant") &&
                        !string.IsNullOrWhiteSpace(item.Content)).ToArray();
                return _toolFlowVisibleMessages.Count > 0 && saved.Length == _toolFlowVisibleMessages.Count &&
                    saved.Zip(_toolFlowVisibleMessages).All(pair => pair.First.Role == pair.Second.Role &&
                        pair.First.Content == pair.Second.Content) &&
                    AiAssistantService.ListConversations().Any(meta => meta.Id == _session.Id && meta.Title == _session.Title);
            }
            catch { return false; }
        }
    }

    internal void RefreshVisibleConversationHistory() => RefreshConversationList();
    internal void ShowConversationBusyNotice() => AddSystemBubble(
        Ui("ConversationBusy", "该会话仍在生成内容或等待确认，请回到该会话停止或完成后再删除。"));
    internal void ShowConversationOpenElsewhereNotice() => AddSystemBubble(
        Ui("ConversationOpenElsewhere", "该会话正由另一个窗口打开。为避免两个窗口互相覆盖记录，已阻止在本窗口加载——请先在另一窗口关闭它，或点「新对话」。"));

    internal ConversationMeta? GetWindowConversation()
    {
        if (_session is null && _taskSelection is null && string.IsNullOrWhiteSpace(InputBox.Text) && _attachments.Count == 0) return null;
        return new ConversationMeta
        {
            Id = ConversationId,
            Title = _session?.Title ?? _draftTitle ?? Ui("DraftConversation", "未发送草稿"),
            CreatedAt = _viewCreatedAt,
            MessageCount = _toolFlowVisibleMessages.Count,
            PersonaId = _selectedPersonaId,
        };
    }

    internal void RenameWindowConversation(string title)
    {
        _draftTitle = title;
        if (_session is { } session)
        {
            session.Rename(title);
            SaveSessionGuarded(session);
            PersistDshConversation();
        }
        TitleText.Text = title;
    }

    internal bool TryOpenConversation(ConversationMeta meta)
    {
        LoadConversation(meta);
        return _session?.Id == meta.Id;
    }

    private void RequestNewConversation()
    {
        if (!CanNavigateConversations) return;
        if (NewConversationRequested is { } request) request();
        else if (!_toolFlowInstalling && !_isProcessing && !_awaitingConfirmation) ResetToNewChat();
    }

    private void RequestOpenConversation(ConversationMeta meta)
    {
        if (!CanNavigateConversations || meta.Id == _session?.Id) return;
        if (OpenConversationRequested is { } request) request(meta);
        else if (!_toolFlowInstalling) LoadConversation(meta);
    }

    private bool OwnsSession(TubaWinUi3.Services.Agent.IAgentSession session) =>
        !_lease.IsClosed && ReferenceEquals(_session, session);
}
