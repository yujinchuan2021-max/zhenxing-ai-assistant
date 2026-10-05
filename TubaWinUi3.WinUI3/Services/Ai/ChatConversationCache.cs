namespace TubaWinUi3.Services.Ai;

/// <summary>
/// Window-owned conversations stay alive while another conversation is visible.
/// Selecting a view never disposes its peers; closing the window closes every view.
/// Keys are read on lookup because a new conversation obtains its ID on first send.
/// </summary>
internal sealed class ChatConversationCache<T>(Func<T, string?> conversationId) where T : class
{
    private readonly List<T> _views = [];
    internal T? Active { get; private set; }
    internal bool IsClosed { get; private set; }
    internal IReadOnlyList<T> Views => _views;

    internal T? Find(string id) => string.IsNullOrWhiteSpace(id) ? null :
        _views.FirstOrDefault(view => string.Equals(conversationId(view), id, StringComparison.Ordinal));

    internal bool Activate(T view)
    {
        if (IsClosed) return false;
        if (!_views.Contains(view)) _views.Add(view);
        Active = view;
        return true;
    }

    internal bool Remove(T view)
    {
        if (ReferenceEquals(Active, view)) return false;
        return _views.Remove(view);
    }

    internal void Trim(int maxRetained, Func<T, bool> canEvict, Action<T> evict)
    {
        if (maxRetained < 1) throw new ArgumentOutOfRangeException(nameof(maxRetained));
        if (IsClosed) return;
        foreach (var view in _views.ToArray())
        {
            if (_views.Count <= maxRetained) break;
            if (ReferenceEquals(view, Active) || !canEvict(view)) continue;
            try
            {
                evict(view);
                _views.Remove(view);
            }
            catch { /* Retain a view if closing it failed; other views can still be considered. */ }
        }
    }

    internal void Close(Action<T> close)
    {
        if (IsClosed) return;
        IsClosed = true;
        var views = _views.ToArray();
        _views.Clear();
        Active = null;
        foreach (var view in views)
        {
            try { close(view); } catch { /* Every owned view must be closed even if one fails. */ }
        }
    }
}
