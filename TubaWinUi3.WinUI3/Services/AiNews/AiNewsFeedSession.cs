namespace TubaWinUi3.Services.AiNews;

internal enum AiNewsAiState { Unconfigured, Original, Working, Ready, Failed }
internal sealed record AiNewsViewState(AiNewsQuery Query, IReadOnlyList<AiNewsItem> Items,
    string NextCursor = "", bool Loading = false, bool Failed = false,
    DateTimeOffset? RetrievedAt = null, bool FromCache = false, AiNewsAiState AiState = AiNewsAiState.Unconfigured);

/// <summary>One visible page owns its requests. Delayed results cannot settle a newer navigation/query.</summary>
internal sealed class AiNewsFeedSession(IAiNewsReader reader, AiNewsEnricher enricher,
    Func<AiNewsModelSelection?> captureModel, Func<string> language)
{
    private long _epoch;
    private bool _active;
    private CancellationTokenSource? _pending;
    private IReadOnlyList<AiNewsItem> _originals = [];
    private string? _configurationIdentity;
    internal AiNewsViewState State { get; private set; } = new(new(), []);
    internal event Action<AiNewsViewState>? Changed;
    internal void Enter() => _active = true;

    /// <summary>Returning from global settings reuses the visible feed and its reading position.</summary>
    internal async Task ResumeAsync()
    {
        Enter();
        if (State.RetrievedAt is null) { await LoadAsync(State.Query, force: false); return; }
        if (State.Loading || State.AiState == AiNewsAiState.Working) return;
        var selected = CaptureModel();
        if (_configurationIdentity == selected?.ConfigurationIdentity) return;
        _configurationIdentity = selected?.ConfigurationIdentity;
        _pending?.Cancel();
        using var request = new CancellationTokenSource();
        _pending = request;
        long epoch = ++_epoch;
        bool Current() => _active && epoch == _epoch && !request.IsCancellationRequested;
        void Set(AiNewsViewState value) { if (Current()) { State = value; Changed?.Invoke(value); } }
        Set(State with { Items = _originals, AiState = selected is not null && _originals.Count > 0 ? AiNewsAiState.Working : AiNewsAiState.Unconfigured });
        try
        {
            if (selected is null || _originals.Count == 0) return;
            string outputLanguage = language();
            // One normal page per automatic configuration change; older loaded cards stay official.
            var enriched = await enricher.EnrichAsync(selected, _originals.Take(20).ToArray(), outputLanguage, request.Token);
            var byId = enriched.ToDictionary(i => i.Id, StringComparer.Ordinal);
            Set(language() == outputLanguage
                ? State with { Items = _originals.Select(i => byId.GetValueOrDefault(i.Id, i)).ToArray(), AiState = AiNewsAiState.Ready }
                : State with { AiState = AiNewsAiState.Original });
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception) { Set(State with { AiState = AiNewsAiState.Failed }); }
        finally { if (ReferenceEquals(_pending, request)) _pending = null; }
    }
    internal void Leave()
    {
        _active = false; _epoch++;
        _pending?.Cancel(); _pending = null;
        State = State with { Loading = false, AiState = State.AiState == AiNewsAiState.Working ? AiNewsAiState.Original : State.AiState };
    }

    internal async Task LoadAsync(AiNewsQuery query, bool force = true, bool more = false)
    {
        if (!_active || (more && (State.Loading || State.AiState == AiNewsAiState.Working || State.NextCursor.Length == 0))) return;
        if (!more && query.FirstPage == State.Query && (State.Loading || State.AiState == AiNewsAiState.Working)) return;
        _pending?.Cancel();
        using var request = new CancellationTokenSource();
        _pending = request;
        long epoch = ++_epoch;
        var baseQuery = query.FirstPage;
        var before = State;
        var previous = State.Query == baseQuery ? State.Items : [];
        string cursor = more ? State.NextCursor : "";
        bool Current() => _active && epoch == _epoch && !request.IsCancellationRequested;
        void Set(AiNewsViewState value) { if (Current()) { State = value; Changed?.Invoke(value); } }
        Set(before.Query == baseQuery ? before with { Loading = true, Failed = false }
            : new(baseQuery, previous, cursor, Loading: true));
        try
        {
            var selected = CaptureModel();
            var batch = await reader.ReadAsync(baseQuery with { Cursor = cursor }, force, request.Token);
            if (!Current()) return;
            _originals = Merge(more ? _originals : [], batch.Items);
            _configurationIdentity = selected?.ConfigurationIdentity;
            var displayed = Merge(more ? previous : [], batch.Items);
            Set(new(baseQuery, displayed, batch.NextCursor, RetrievedAt: batch.RetrievedAt, FromCache: batch.FromCache,
                AiState: selected is not null && batch.Items.Count > 0 ? AiNewsAiState.Working : AiNewsAiState.Unconfigured));
            if (selected is null || batch.Items.Count == 0) return;
            string outputLanguage = language();
            try
            {
                var enriched = await enricher.EnrichAsync(selected, batch.Items, outputLanguage, request.Token);
                Set(language() == outputLanguage
                    ? State with { Items = Merge(more ? previous : [], enriched), AiState = AiNewsAiState.Ready }
                    : State with { AiState = AiNewsAiState.Original });
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception) { Set(State with { AiState = AiNewsAiState.Failed }); }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception) { Set(before.Query == baseQuery ? before with { Loading = false, Failed = true }
            : new(baseQuery, previous, cursor, Failed: true)); }
        finally { if (ReferenceEquals(_pending, request)) _pending = null; }
    }
    private AiNewsModelSelection? CaptureModel() { try { return captureModel(); } catch { return null; } }
    private static IReadOnlyList<AiNewsItem> Merge(IReadOnlyList<AiNewsItem> previous, IReadOnlyList<AiNewsItem> batch)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return previous.Concat(batch).Where(i => seen.Add(i.Id)).ToArray();
    }
}
