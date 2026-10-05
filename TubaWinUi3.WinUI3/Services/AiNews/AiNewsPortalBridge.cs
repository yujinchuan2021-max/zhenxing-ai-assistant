using System.Text.Json;

namespace TubaWinUi3.Services.AiNews;

internal sealed record AiNewsPortalCommand(string Type, AiNewsQuery? Query = null, string? Url = null);

/// <summary>The portal receives public news and appearance only. Model credentials stay in the native session.</summary>
internal static class AiNewsPortalBridge
{
    internal const string OfficialUrl = "https://zhenxingai.com/ai-news/";
    private static readonly HashSet<string> Categories = ["", "ai-models", "ai-products", "industry", "paper", "tip"];

    internal static bool IsPortal(string? source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "zhenxingai.com" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/ai-news/";

    internal static AiNewsPortalCommand? Read(string? source, string json, AiNewsViewState state)
    {
        if (!IsPortal(source) || json.Length > 4096) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string type = AiNewsDocument.Text(root, "type", 40);
            if (type is "ready" or "more" or "configure") return new(type);
            if (type is "query" or "refresh")
            {
                string category = AiNewsDocument.Text(root, "category", 80);
                string search = AiNewsDocument.Text(root, "search", 121).Trim();
                return Categories.Contains(category) && search.Length <= 120 ? new(type, new(category, search)) : null;
            }
            if (type == "original")
            {
                string id = AiNewsDocument.Text(root, "id", 128);
                var item = state.Items.FirstOrDefault(i => i.Id == id);
                return item is not null && AiNewsDocument.IsSafeLink(item.OriginalUrl) ? new(type, Url: item.OriginalUrl) : null;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        return null;
    }

    internal static string StateJson(AiNewsViewState state, string language, bool dark) => JsonSerializer.Serialize(new
    {
        type = "state", language, theme = dark ? "dark" : "light",
        query = new { category = state.Query.Category, search = state.Query.Search },
        items = state.Items.Select(i => new { id = i.Id, title = i.Title, summary = i.Summary, source = i.Source,
            originalUrl = i.OriginalUrl, publishedAt = i.PublishedAt, category = i.Category,
            aiGenerated = i.AiGenerated, featured = i.Featured,
            serverEdited = i.ServerEdited, selected = i.ServerSelected, reason = i.Reason }),
        nextCursor = state.NextCursor, loading = state.Loading, failed = state.Failed,
        retrievedAt = state.RetrievedAt, fromCache = state.FromCache, aiState = state.AiState.ToString().ToLowerInvariant()
    });
}
