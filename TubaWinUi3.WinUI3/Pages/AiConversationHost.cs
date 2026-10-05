using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Ai;

namespace TubaWinUi3.Pages;

/// <summary>Each window keeps independent conversation pages, including their running replies and drafts.</summary>
public sealed class AiConversationHost : UserControl, ILocalizablePage
{
    private readonly ChatConversationCache<AiAgentPage> _conversations = new(page => page.ConversationId);
    private readonly bool _compact;

    public AiConversationHost() : this(compact: false, autoLoadLatest: true) { }

    internal AiConversationHost(bool compact, bool autoLoadLatest = true)
    {
        _compact = compact;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Activate(CreatePage(autoLoadLatest));
    }

    internal AiAgentPage? ActivePage => _conversations.Active;
    internal IReadOnlyList<AiAgentPage> ConversationPages => _conversations.Views;

    private AiAgentPage CreatePage(bool autoLoadLatest = false)
    {
        var page = new AiAgentPage(_compact, autoLoadLatest);
        page.NewConversationRequested += NewConversation;
        page.OpenConversationRequested += OpenConversation;
        page.ConversationStateChanged += RefreshVisibleHistory;
        page.DeleteConversationRequested += DeleteConversation;
        page.RenameConversationRequested += RenameConversation;
        page.WindowConversations = () => _conversations.Views.Select(view => view.GetWindowConversation())
            .OfType<ConversationMeta>().ToArray();
        return page;
    }

    private void Activate(AiAgentPage page)
    {
        if (_conversations.IsClosed || ReferenceEquals(page, ActivePage)) return;
        ActivePage?.OnNavigatedAway();
        Content = null;
        if (!_conversations.Activate(page)) return;
        page.ApplyLocalization();
        Content = page;
        page.RefreshVisibleConversationHistory();
        _conversations.Trim(8, candidate => candidate.CanEvictCachedConversation, ReleasePage);
    }

    private void NewConversation()
    {
        if (_conversations.IsClosed || ActivePage?.CanNavigateConversations == false) return;
        // Repeated New chat clicks on an untouched empty view do not allocate more pages.
        if (ActivePage is { IsUntouchedNewConversation: true }) return;
        Activate(CreatePage());
    }

    private void OpenConversation(ConversationMeta meta)
    {
        if (_conversations.IsClosed || ActivePage?.CanNavigateConversations == false) return;
        if (_conversations.Find(meta.Id) is { } cached)
        {
            Activate(cached);
            return;
        }
        var page = CreatePage();
        if (page.TryOpenConversation(meta)) Activate(page);
        else
        {
            page.Unload();
            ActivePage?.ShowConversationOpenElsewhereNotice();
        }
    }

    private void RefreshVisibleHistory() => ActivePage?.RefreshVisibleConversationHistory();

    private void DeleteConversation(ConversationMeta meta)
    {
        var page = _conversations.Find(meta.Id);
        if (page is not null && !page.CanDeleteConversation)
        {
            ActivePage?.ShowConversationBusyNotice();
            return;
        }
        if (page is null && ConversationOwnershipRegistry.IsHeldByOther(
                AiAssistantService.HistoryRootKey, meta.Id, Guid.Empty))
        {
            ActivePage?.ShowConversationOpenElsewhereNotice();
            return;
        }
        if (page is not null)
        {
            if (ReferenceEquals(page, ActivePage)) Activate(CreatePage());
            if (_conversations.Remove(page)) ReleasePage(page);
        }
        // Close and persist the owned idle page before deleting its files, so a
        // Dispose/finally cannot recreate a conversation that was just deleted.
        AiAssistantService.DeleteConversation(meta.Id);
        RefreshVisibleHistory();
    }

    private void RenameConversation(ConversationMeta meta, string title)
    {
        _conversations.Find(meta.Id)?.RenameWindowConversation(title);
        AiAssistantService.RenameConversation(meta.Id, title);
        RefreshVisibleHistory();
    }

    public bool TryPrefillInput(string text) => ActivePage?.TryPrefillInput(text) == true;
    public void ZxRefreshProviders() => ActivePage?.ZxRefreshProviders();
    public void ApplyLocalization() => ActivePage?.ApplyLocalization();
    public void OnNavigatedAway() => ActivePage?.OnNavigatedAway();

    public void Unload()
    {
        Content = null;
        _conversations.Close(ReleasePage);
    }

    private void ReleasePage(AiAgentPage page)
    {
        page.NewConversationRequested -= NewConversation;
        page.OpenConversationRequested -= OpenConversation;
        page.ConversationStateChanged -= RefreshVisibleHistory;
        page.DeleteConversationRequested -= DeleteConversation;
        page.RenameConversationRequested -= RenameConversation;
        page.WindowConversations = null;
        page.Unload();
    }
}
