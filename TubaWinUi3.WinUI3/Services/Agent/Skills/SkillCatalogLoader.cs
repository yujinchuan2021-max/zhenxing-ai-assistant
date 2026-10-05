namespace TubaWinUi3.Services.Agent;

internal sealed record SkillCatalogLoadProgress(SkillCatalogItem[] Items, bool IsComplete);

/// <summary>Reads all metadata pages; a failed request can resume without dropping earlier pages.</summary>
internal sealed class SkillCatalogLoader
{
    private const int MaxPages = 100;
    private const int MaxItems = 10000;
    private readonly List<SkillCatalogItem> _items = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private volatile SkillCatalogItem[] _snapshot = [];
    private int _nextPage;
    private string? _updatedAt;
    internal bool IsComplete { get; private set; }
    internal int LoadedPages => _nextPage;
    internal SkillCatalogItem[] Items => _snapshot.ToArray();

    internal async Task LoadAsync(Func<int, CancellationToken, Task<SkillCatalog>> fetch,
        Action<SkillCatalogLoadProgress> progress, CancellationToken token, int? maxAdditionalPages = null)
    {
        if (maxAdditionalPages is <= 0) throw new ArgumentOutOfRangeException(nameof(maxAdditionalPages));
        // A cached page can remount before its previous request observes cancellation.
        // Serialize the shared cursor so an old response cannot duplicate a page.
        await _loadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var loaded = 0;
            while (!IsComplete && (!maxAdditionalPages.HasValue || loaded < maxAdditionalPages.Value))
            {
                token.ThrowIfCancellationRequested();
                if (_nextPage >= MaxPages) throw new InvalidDataException("技能目录页数过多，请刷新重试。");
                var page = await fetch(_nextPage, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (page.SchemaVersion != 1 || page.Page != _nextPage || page.Items is null || page.Items.Length > 400 ||
                    _items.Count + page.Items.Length > MaxItems ||
                    (_nextPage > 0 && page.UpdatedAt != _updatedAt))
                    throw new InvalidDataException("技能目录已变化或格式无效，请刷新重试。");
                var newIds = new HashSet<string>(StringComparer.Ordinal);
                if (page.Items.Any(item => item is null || string.IsNullOrEmpty(item.Id) || item.Id.Length > 100 ||
                        item.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) ||
                        _ids.Contains(item.Id) || !newIds.Add(item.Id)) ||
                    (page.HasMore && page.Items.Length == 0))
                    throw new InvalidDataException("技能目录重复或缺页，请刷新重试。");
                _updatedAt = page.UpdatedAt;
                _items.AddRange(page.Items);
                _ids.UnionWith(newIds);
                _nextPage++;
                loaded++;
                _snapshot = _items.ToArray();
                IsComplete = !page.HasMore;
                progress(new(Items, IsComplete));
            }
        }
        finally { _loadGate.Release(); }
    }

    internal static SkillCatalogItem[] Filter(IEnumerable<SkillCatalogItem> items, string category, string search)
    {
        search = search.Trim();
        return items.Where(item => Matches(item, category, search)).ToArray();
    }

    internal static bool Matches(SkillCatalogItem item, string category, string search) =>
        (category == "all" || item.Category == category) &&
        (search.Length == 0 || Contains(item.DisplayName, search) || Contains(item.Description, search) ||
            Contains(item.Author, search) || Contains(item.Details, search));

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
}
