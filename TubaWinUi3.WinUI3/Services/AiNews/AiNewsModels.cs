using System.Text.Json;

namespace TubaWinUi3.Services.AiNews;

internal sealed record AiNewsItem(string Id, string Title, string Summary, string Source,
    string OriginalUrl, DateTimeOffset? PublishedAt, string Category, string Reason = "",
    bool AiGenerated = false, bool Featured = false, bool ServerEdited = false, bool ServerSelected = false);

internal sealed record AiNewsQuery(string Category = "", string Search = "", string Cursor = "")
{
    internal AiNewsQuery FirstPage => this with { Cursor = "" };
    internal string QueryString => "mode=all&limit=20" +
        (Category.Length == 0 ? "" : "&category=" + Uri.EscapeDataString(Category)) +
        (Search.Length == 0 ? "" : "&q=" + Uri.EscapeDataString(Search.Trim()[..Math.Min(Search.Trim().Length, 120)])) +
        (Cursor.Length == 0 ? "" : "&cursor=" + Uri.EscapeDataString(Cursor));
}

internal sealed record AiNewsBatch(IReadOnlyList<AiNewsItem> Items, string NextCursor,
    DateTimeOffset RetrievedAt, bool FromCache = false);

internal static class AiNewsDocument
{
    internal const int MaxBytes = 512 * 1024;
    internal static bool IsSafeLink(string? raw) => Uri.TryCreate(raw, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && !uri.IsLoopback;

    internal static AiNewsBatch Parse(string json, DateTimeOffset retrievedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
            || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() > 100 || !root.TryGetProperty("page", out var page)
            || page.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid news response.");
        var result = new List<AiNewsItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid news item.");
            string id = Text(item, "id", 128), title = Text(item, "title", 400);
            var source = item.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object ? Text(s, "name", 160) : "";
            var link = item.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Object ? Text(l, "original", 2048) : "";
            if (id.Length == 0 || title.Length == 0 || source.Length == 0 || !IsSafeLink(link))
                throw new InvalidDataException("Incomplete news item.");
            DateTimeOffset? published = DateTimeOffset.TryParse(Text(item, "publishedAt", 80),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date : null;
            bool serverEdited = item.TryGetProperty("editorial", out var editorial) && editorial.ValueKind == JsonValueKind.Object
                && Text(editorial, "status", 40) == "ready";
            bool serverSelected = serverEdited && item.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True;
            if (ids.Add(id)) result.Add(new(id, title, Text(item, "summary", 2000), source, link,
                published, Text(item, "category", 80), serverEdited ? Text(item, "reason", 128) : "",
                ServerEdited: serverEdited, ServerSelected: serverSelected));
        }
        if (!page.TryGetProperty("hasMore", out var m) || m.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Invalid news pagination.");
        var more = m.GetBoolean();
        var cursor = Text(page, "nextCursor", 2048);
        if (more && cursor.Length == 0) throw new InvalidDataException("Missing news cursor.");
        return new(result, more ? cursor : "", retrievedAt);
    }
    internal static string Text(JsonElement node, string key, int maximum)
    {
        if (node.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid news object.");
        if (!node.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid news text.");
        var text = value.GetString()?.Trim() ?? "";
        if (text.Length > maximum) throw new InvalidDataException("News field too long.");
        return text;
    }
}
