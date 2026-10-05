using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using TubaWinUi3.Services.AiNews;

namespace TubaWinUi3.Services.Agent;

/// <summary>Read the same local catalogue and official feed that the client presents.</summary>
internal static class ClientContentTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Description("查询本客户端内置与随包工具，返回分类、用途、实际路径及是否需要下载。先查询现有工具再建议安装；仅列目录不等于执行或已验证可运行。可按 category/search 筛选并分页。")]
    public static string ListClientTools(string? category = null, string? search = null, int offset = 0, int limit = 30)
    {
        if (offset < 0 || offset > 10000 || limit < 1 || limit > 50)
            throw new ArgumentException("offset must be 0..10000; limit must be 1..50.");
        category = Bound(category, 80);
        search = Bound(search, 120);
        var entries = ToolCatalog.GetAllToolsCached().Select(tool => new
        {
            id = tool.BuiltinToolId ?? tool.RelativePath, name = tool.Name,
            category = tool.Category, categories = tool.Categories,
            description = tool.Description ?? "", kind = tool.IsBuiltinLink ? "builtin" : "bundled",
            path = tool.IsBuiltinLink ? null : tool.EffectivePath, needsDownload = tool.NeedsDownload,
        }).ToList();
        // Not every built-in page is physically mounted in the bundled Tools tree.
        var mounted = entries.Where(entry => entry.kind == "builtin").Select(entry => entry.id).ToHashSet(StringComparer.Ordinal);
        entries.AddRange(BuiltinToolRegistry.Tools.Where(tool => !mounted.Contains(tool.Id)).Select(tool => new
        {
            id = tool.Id, name = tool.Name, category = tool.Category,
            categories = (IReadOnlyList<string>)new[] { tool.Category }, description = tool.Description,
            kind = "builtin", path = (string?)null, needsDownload = false,
        }));
        var filtered = entries.Where(entry => (category.Length == 0 || entry.category.Equals(category, StringComparison.OrdinalIgnoreCase)
            || entry.categories.Contains(category, StringComparer.OrdinalIgnoreCase))
            && (search.Length == 0 || entry.name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.description.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.id.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.category, StringComparer.Ordinal).ThenBy(entry => entry.name, StringComparer.Ordinal).ToArray();
        var page = filtered.Skip(offset).Take(limit).ToArray();
        return JsonSerializer.Serialize(new
        {
            source = "client-tool-catalog", total = filtered.Length, offset,
            nextOffset = offset + page.Length < filtered.Length ? (int?)(offset + page.Length) : null,
            items = page,
            note = "目录查询不执行工具；内置页面通过客户端使用。需要 CLI 参数时调用 get_cli_tool_usage。",
        }, JsonOptions);
    }

    [Description("直接读取枕星AI资讯官方数据，无需用户粘贴或截图。返回标题、摘要、来源、原文链接、发布时间及缓存标记；不包含完整文章正文，不得假称已读原文。search 可搜主题，cursor 用上页 nextCursor。")]
    public static Task<string> ReadAiNewsAsync(string? search = null, string? category = null,
        string? cursor = null, CancellationToken cancellationToken = default)
        => ReadAiNewsAsync(AiNewsClient.CreateDefault(), search, category, cursor, cancellationToken);

    internal static async Task<string> ReadAiNewsAsync(IAiNewsReader reader, string? search, string? category,
        string? cursor, CancellationToken cancellationToken)
    {
        AiNewsBatch batch;
        try
        {
            batch = await reader.ReadAsync(new AiNewsQuery(Bound(category, 80), Bound(search, 120), Bound(cursor, 2048)),
                false, cancellationToken).ConfigureAwait(false);
        }
        catch (AiNewsUnavailableException)
        {
            throw new InvalidOperationException("暂时无法读取枕星AI资讯，请稍后重试；这次未获得资讯内容。");
        }
        return JsonSerializer.Serialize(new
        {
            source = "zhenxing-ai-news", contentKind = "summaries-with-original-links", fullArticleIncluded = false,
            batch.RetrievedAt, batch.FromCache, batch.NextCursor, batch.Items,
        }, JsonOptions);
    }

    internal static JsonArray BuildToolList() => new(
        Definition("list_client_tools", "读取本客户端现有工具的名称、分类、用途与路径，优先复用。仅查询，不执行。", new JsonObject
        {
            ["category"] = Text("可选分类，使用目录返回的实际分类名"),
            ["search"] = Text("可选工具名或用途关键词，最多120字"),
            ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 10000 },
            ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50, ["default"] = 30 },
        }),
        Definition("get_cli_tool_usage", "读取本客户端随包命令行工具的完整说明、路径与参数示例。不运行工具。", new JsonObject
        { ["name"] = Text("工具名，来自客户端工具目录") }, "name"),
        Definition("read_ai_news", "直接查询枕星AI资讯的官方标题、摘要、来源和原文链接。无需用户提供截图；没有全文，不得当作已读完整原文。", new JsonObject
        {
            ["search"] = Text("可选主题关键词，最多120字"), ["category"] = Text("可选资讯分类"),
            ["cursor"] = Text("继续读取时原样传入上一页 nextCursor"),
        }));

    internal static bool IsContentTool(string? name) => name is "list_client_tools" or "get_cli_tool_usage" or "read_ai_news";

    internal static Task<string> CallAsync(string name, JsonObject? arguments) => name switch
    {
        "list_client_tools" => Task.FromResult(ListClientTools(arguments?["category"]?.GetValue<string>(),
            arguments?["search"]?.GetValue<string>(), arguments?["offset"]?.GetValue<int>() ?? 0,
            arguments?["limit"]?.GetValue<int>() ?? 30)),
        "get_cli_tool_usage" => Task.FromResult(CliToolboxAgentTool.GetCliToolUsage(arguments?["name"]?.GetValue<string>() ?? "")),
        "read_ai_news" => ReadAiNewsAsync(arguments?["search"]?.GetValue<string>(), arguments?["category"]?.GetValue<string>(),
            arguments?["cursor"]?.GetValue<string>()),
        _ => throw new ArgumentException("Unknown client content tool."),
    };

    private static string Bound(string? value, int length)
    {
        var text = value?.Trim() ?? "";
        if (text.Length > length) throw new ArgumentException($"Query exceeds {length} characters.");
        return text;
    }
    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Definition(string name, string description, JsonObject properties, string? required = null)
        => new()
        {
            ["name"] = name, ["description"] = description,
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties,
                ["required"] = required is null ? new JsonArray() : new JsonArray(required) },
        };
}
