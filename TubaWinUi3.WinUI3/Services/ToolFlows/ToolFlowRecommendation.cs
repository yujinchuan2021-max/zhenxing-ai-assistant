using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>Three alternatives owned by one assistant message, before any selection.</summary>
internal sealed record ToolFlowRecommendationSet(
    string Goal, string RecommendedId, IReadOnlyList<ToolFlowRecommendationOption> Options,
    IReadOnlyList<ToolFlowConversationMessage> Conversation)
{
    /// <summary>Creates the existing review input using only the explicitly chosen option.</summary>
    public ToolFlowProposal? ToProposal(string optionId)
    {
        var option = Options.SingleOrDefault(x => x.Id == optionId);
        return option is { Available: true } && option.Items.Count > 0
            ? new ToolFlowProposal(option.Name, option.Text, option.Items, Goal, Conversation)
            : null;
    }
}

internal sealed record ToolFlowRecommendationOption(
    string Id, string Name, string Summary, string Fit, string Cost, string Requirements,
    IReadOnlyList<string> Warnings, IReadOnlyList<ToolFlowItem> Items,
    bool Available, string UnavailableReason, string Text);

internal static partial class ToolFlowProposalParser
{
    private static readonly string[] OptionIds = ["light", "medium", "heavy"];
    private static readonly Regex FixedTargetCode = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex FenceLine = new(@"^[ \t]*(?<fence>`{3,}|~{3,})(?<info>.*)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex RecommendationIntent = new(@"推荐|\brecommend(?:ed|ation)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TierIntent = new(@"三[档张]|[轻中重]量|\b(?:light|medium|heavy)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FormatExampleIntent = new(
        @"格式示例|JSON\s*[示样]例|[示样]例结构|(?:这是|以下是).{0,12}[示样]例|\b(?:schema|format|json)\s+(?:example|sample)\b|\b(?:example|sample)\s+(?:schema|format|json)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions SelectedJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Pure data capture: validates a unique complete JSON contract and snapshots the
    /// clicked message's conversation prefix. The caller still gates actions on a
    /// successfully completed reply; a closed intermediate streaming block is not consent.
    /// </summary>
    public static bool TryCaptureRecommendations(IReadOnlyList<ToolFlowConversationMessage> visibleMessages,
        int assistantIndex, out ToolFlowRecommendationSet? recommendations)
    {
        recommendations = null;
        if (assistantIndex < 0 || assistantIndex >= visibleMessages.Count) return false;
        var message = visibleMessages[assistantIndex];
        if (message.Role != "assistant" || string.IsNullOrWhiteSpace(message.Content)
            || message.Content.Length > 65536) return false;
        var context = visibleMessages.Take(assistantIndex + 1).ToArray();
        if (!context.Any(x => x.Role == "user" && !string.IsNullOrWhiteSpace(x.Content))) return false;

        var blocks = FindProtocolBlocks(message.Content, includePartialLabels: true);
        // Reject mixed contracts, duplicate blocks and unfinished protocol suffixes.
        if (blocks.Count != 1 || blocks[0].Kind != "toolflow-options" || !blocks[0].Closed) return false;
        return TryReadRecommendations(blocks[0].Body, Array.AsReadOnly(context), out recommendations);
    }

    private static bool TryReadRecommendations(string body,
        IReadOnlyList<ToolFlowConversationMessage> context, out ToolFlowRecommendationSet? recommendations)
    {
        recommendations = null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (!ValidObject(root, ["schema", "goal", "recommended", "options"])
                || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var version) || version != 1
                || !ReadString(root, "goal", 4096, requiredText: true, out var goal)
                || !ReadString(root, "recommended", 6, requiredText: true, out var recommendedId)
                || !OptionIds.Contains(recommendedId, StringComparer.Ordinal)
                || !root.TryGetProperty("options", out var optionsJson) || optionsJson.ValueKind != JsonValueKind.Array
                || optionsJson.GetArrayLength() != 3) return false;

            var options = new List<ToolFlowRecommendationOption>(3);
            foreach (var optionJson in optionsJson.EnumerateArray())
            {
                if (!TryReadOption(optionJson, goal, out var option)
                    || options.Any(x => x.Id == option!.Id)) return false;
                options.Add(option!);
            }
            if (!options.Any(x => x.Id == recommendedId && x.Available)) return false;
            // Detect a literal copy with only the tier label/name replaced. Different
            // configuration or automation descriptions remain legitimate differences.
            var viable = options.Where(x => x.Available).ToArray();
            if (viable.Select(OptionSignature).Distinct(StringComparer.Ordinal).Count() != viable.Length) return false;

            recommendations = new ToolFlowRecommendationSet(goal, recommendedId,
                Array.AsReadOnly(OptionIds.Select(id => options.Single(x => x.Id == id)).ToArray()),
                context);
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Hides protocol fences and strictly validated JSON offers from display,
    /// including an identifiable trailing incomplete offer during streaming.
    /// Original messages remain untouched for capture/history/sharing; ordinary code
    /// and protocol-looking examples enclosed in another code fence are retained.
    /// </summary>
    public static string GetVisibleContent(string content)
    {
        var blocks = FindProtocolBlocks(content, includePartialLabels: true).Where(x => x.HideFromDisplay).ToList();
        if (blocks.Count == 0) return content;
        var text = new StringBuilder(content.Length);
        var position = 0;
        foreach (var block in blocks)
        {
            text.Append(content, position, block.Start - position);
            position = block.Start + block.Length;
        }
        text.Append(content, position, content.Length - position);
        return text.ToString();
    }

    private static bool TryReadOption(JsonElement value, string goal, out ToolFlowRecommendationOption? option)
    {
        option = null;
        if (!ValidObject(value, ["id", "name", "summary", "fit", "cost", "requirements", "warnings", "items", "available", "unavailableReason"])
            || !ReadString(value, "id", 6, true, out var id) || !OptionIds.Contains(id, StringComparer.Ordinal)
            || !ReadString(value, "name", 120, true, out var name)
            || !ReadString(value, "summary", 2000, true, out var summary)
            || !ReadString(value, "fit", 2000, true, out var fit)
            || !ReadString(value, "cost", 2000, true, out var cost)
            || !ReadString(value, "requirements", 4000, true, out var requirements)) return false;
        var available = true;
        if (value.TryGetProperty("available", out var availableJson))
        {
            if (availableJson.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            available = availableJson.GetBoolean();
        }
        var reason = "";
        if (value.TryGetProperty("unavailableReason", out _)
            && !ReadString(value, "unavailableReason", 2000, false, out reason)) return false;
        if (!available && string.IsNullOrWhiteSpace(reason)) return false;
        var warnings = new List<string>();
        if (value.TryGetProperty("warnings", out var warningsJson))
        {
            if (warningsJson.ValueKind != JsonValueKind.Array || warningsJson.GetArrayLength() > 16) return false;
            foreach (var warning in warningsJson.EnumerateArray())
            {
                if (warning.ValueKind != JsonValueKind.String) return false;
                var text = warning.GetString()!;
                if (string.IsNullOrWhiteSpace(text) || text.Length > 1024 || HasUnsafeControl(text)) return false;
                warnings.Add(text);
            }
        }
        if (!value.TryGetProperty("items", out var itemsJson) || itemsJson.ValueKind != JsonValueKind.Array
            || itemsJson.GetArrayLength() > 32 || (available && itemsJson.GetArrayLength() == 0)) return false;
        var items = new List<ToolFlowItem>(itemsJson.GetArrayLength());
        foreach (var row in itemsJson.EnumerateArray())
        {
            if (!ValidObject(row, ["name", "type", "version", "sourceUrl", "downloadUrl", "installTargetKey"])
                || !ReadString(row, "name", 120, true, out var itemName)
                || !ReadString(row, "type", 64, false, out var type)
                || !ReadString(row, "version", 80, false, out var itemVersion)
                || !ReadString(row, "sourceUrl", 2048, false, out var sourceUrl)
                || !ReadString(row, "downloadUrl", 2048, false, out var downloadUrl)
                || !ReadString(row, "installTargetKey", 100, false, out var target)
                || !ValidHttpsUrl(sourceUrl) || !ValidHttpsUrl(downloadUrl)
                || (target.Length > 0 && !FixedTargetCode.IsMatch(target))) return false;
            var identity = SHA256.HashData(Encoding.UTF8.GetBytes(id + "\n" + items.Count + "\n" + row.GetRawText()));
            string? Known(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
            items.Add(new ToolFlowItem
            {
                ItemId = new Guid(identity.AsSpan(0, 16)).ToString("D"), Name = itemName,
                Kind = Known(type) ?? "software", Version = Known(itemVersion),
                SourceUrl = Known(sourceUrl), DownloadUrl = Known(downloadUrl), InstallTargetKey = Known(target),
            });
        }
        var frozenItems = items.AsReadOnly();
        var frozenWarnings = warnings.AsReadOnly();
        option = new ToolFlowRecommendationOption(id, name, summary, fit, cost, requirements,
            frozenWarnings, frozenItems, available, reason,
            BuildSelectedText(goal, id, name, summary, fit, cost, requirements, frozenWarnings, frozenItems));
        return true;
    }

    private static string BuildSelectedText(string goal, string id, string name, string summary,
        string fit, string cost, string requirements, IReadOnlyList<string> warnings, IReadOnlyList<ToolFlowItem> items)
    {
        var text = new StringBuilder();
        text.Append("工具流名称：").AppendLine(name);
        text.Append("项目目标：").AppendLine(goal);
        text.Append("所选开发模式：").AppendLine(id switch { "light" => "轻量", "medium" => "中量", _ => "重量" });
        text.AppendLine(summary);
        text.Append("适合：").AppendLine(fit);
        text.Append("成本：").AppendLine(cost);
        text.Append("准备要求：").AppendLine(requirements);
        foreach (var warning in warnings) text.Append("注意：").AppendLine(warning);
        text.AppendLine("所选方案完整清单：");
        // A distinct selected-data fence avoids pretending this is a fresh alternative
        // offer, and JSON preserves punctuation/newlines without pipe-row corruption.
        text.AppendLine("```toolflow-selected");
        text.AppendLine(JsonSerializer.Serialize(new
        {
            schema = 1, id, goal, name, summary, fit, cost, requirements, warnings,
            items = items.Select(x => new
            {
                name = x.Name, type = x.Kind, version = x.Version ?? "", sourceUrl = x.SourceUrl ?? "",
                downloadUrl = x.DownloadUrl ?? "", installTargetKey = x.InstallTargetKey ?? "",
            }),
        }, SelectedJsonOptions));
        text.Append("```");
        return text.ToString();
    }

    private static string OptionSignature(ToolFlowRecommendationOption option) => JsonSerializer.Serialize(new
    {
        option.Summary, option.Fit, option.Cost, option.Requirements, option.Warnings,
        Items = option.Items.Select(x => new { x.Name, x.Kind, x.Version, x.SourceUrl, x.DownloadUrl, x.InstallTargetKey }),
    });

    private static bool ValidObject(JsonElement value, IReadOnlyCollection<string> allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !allowed.Contains(property.Name)) return false;
        return true;
    }

    private static bool ReadString(JsonElement value, string key, int maxLength, bool requiredText, out string text)
    {
        text = "";
        if (!value.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String) return false;
        text = field.GetString()!;
        return text.Length <= maxLength && (!requiredText || !string.IsNullOrWhiteSpace(text)) && !HasUnsafeControl(text);
    }

    private static bool HasUnsafeControl(string text) => text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    private sealed record ProtocolBlock(string Kind, int Start, int Length, string Body, bool Closed,
        bool HideFromDisplay = true);

    private static IReadOnlyList<ProtocolBlock> FindProtocolBlocks(string content, bool includePartialLabels = false)
    {
        var fences = FindTopLevelFences(content);
        // Some models follow the complete contract but label it `json`. Compatibility
        // requires recommendation intent outside code, not a formatting example.
        var prose = new StringBuilder(content.Length);
        var position = 0;
        foreach (var fence in fences)
        {
            prose.Append(content, position, fence.Start - position);
            position = fence.Start + fence.Length;
        }
        prose.Append(content, position, content.Length - position);
        var narrative = prose.ToString();
        var isOffer = content.Length <= 65536 && RecommendationIntent.IsMatch(narrative)
            && TierIntent.IsMatch(narrative) && !FormatExampleIntent.IsMatch(narrative);
        var blocks = new List<ProtocolBlock>();
        foreach (var fence in fences)
        {
            var info = fence.Kind;
            if (info.Equals("toolflow-options", StringComparison.OrdinalIgnoreCase)
                || info.Equals("toolflow-items", StringComparison.OrdinalIgnoreCase))
                blocks.Add(fence with { Kind = info.ToLowerInvariant() });
            else if (info.Equals("model-preference", StringComparison.OrdinalIgnoreCase))
                // Defeat mixed question/installation offers, but let the narrower
                // question parser own display hiding and ordinary-example rules.
                blocks.Add(fence with { Kind = "model-preference", HideFromDisplay = false });
            else if (includePartialLabels && info.StartsWith("model-", StringComparison.OrdinalIgnoreCase)
                && "model-preference".StartsWith(info, StringComparison.OrdinalIgnoreCase))
                blocks.Add(fence with { Kind = "model-preference", HideFromDisplay = false });
            else if (info.Equals("choice-question", StringComparison.OrdinalIgnoreCase)
                || includePartialLabels && info.StartsWith("choice-", StringComparison.OrdinalIgnoreCase)
                    && "choice-question".StartsWith(info, StringComparison.OrdinalIgnoreCase))
                blocks.Add(fence with { Kind = "choice-question", HideFromDisplay = false });
            else if (includePartialLabels && info.StartsWith("toolflow-", StringComparison.OrdinalIgnoreCase)
                && new[] { "toolflow-options", "toolflow-items" }.Any(x => x.StartsWith(info, StringComparison.OrdinalIgnoreCase)))
                blocks.Add(fence);
            else if (isOffer && info.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                if (fence.Closed && TryReadRecommendations(fence.Body, [], out _))
                    blocks.Add(fence with { Kind = "toolflow-options" });
                else if (fence.Closed ? HasClosedRecommendationHeader(fence.Body) : HasRecommendationHeader(fence.Body))
                    // An invalid second draft still defeats uniqueness. Keep closed
                    // invalid JSON readable; only a valid complete offer becomes cards.
                    blocks.Add(fence with { Kind = fence.Closed ? "toolflow-options-invalid" : "toolflow-options",
                        HideFromDisplay = !fence.Closed });
            }
        }
        return blocks;
    }

    // Closed JSON may order root properties differently from streamed output.
    // This only identifies a conflicting draft; it never validates an actionable offer.
    private static bool HasClosedRecommendationHeader(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var fields = root.EnumerateObject().ToArray();
            return fields.Any(x => x.Name == "schema" && x.Value.ValueKind == JsonValueKind.Number
                    && x.Value.TryGetInt32(out var version) && version == 1)
                && fields.Any(x => x.Name == "goal" && x.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(x.Value.GetString()))
                && fields.Any(x => x.Name == "recommended" && x.Value.ValueKind == JsonValueKind.String
                    && OptionIds.Contains(x.Value.GetString(), StringComparer.Ordinal))
                && fields.Any(x => x.Name == "options" && x.Value.ValueKind == JsonValueKind.Array);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Partial streaming detection only; never sufficient to capture or select.</summary>
    private static bool HasRecommendationHeader(string body)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(body), isFinalBlock: false,
                new JsonReaderState(new JsonReaderOptions { MaxDepth = 8 }));
            var schema = false;
            var goal = false;
            var recommended = false;
            string? property = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                    property = reader.GetString();
                else if (reader.CurrentDepth == 1)
                {
                    if (property == "schema" && reader.TokenType == JsonTokenType.Number)
                        schema = reader.TryGetInt32(out var version) && version == 1;
                    else if (property == "goal" && reader.TokenType == JsonTokenType.String)
                        goal = !string.IsNullOrWhiteSpace(reader.GetString());
                    else if (property == "recommended" && reader.TokenType == JsonTokenType.String)
                        recommended = OptionIds.Contains(reader.GetString(), StringComparer.Ordinal);
                    else if (property == "options" && reader.TokenType == JsonTokenType.StartArray)
                        return schema && goal && recommended;
                    property = null;
                }
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static IReadOnlyList<ProtocolBlock> FindTopLevelFences(string content)
    {
        var blocks = new List<ProtocolBlock>();
        string? openFence = null;
        string? kind = null;
        var blockStart = 0;
        var bodyStart = 0;
        for (var lineStart = 0; lineStart < content.Length;)
        {
            var newline = content.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? content.Length : newline + 1;
            var line = content[lineStart..(newline < 0 ? content.Length : newline)].TrimEnd('\r');
            var match = FenceLine.Match(line);
            if (match.Success)
            {
                var fence = match.Groups["fence"].Value;
                var info = match.Groups["info"].Value.Trim();
                if (openFence is null)
                {
                    openFence = fence;
                    kind = info;
                    blockStart = lineStart;
                    bodyStart = lineEnd;
                }
                else if (fence[0] == openFence[0] && fence.Length >= openFence.Length && info.Length == 0)
                {
                    if (kind is not null)
                        blocks.Add(new ProtocolBlock(kind, blockStart, lineEnd - blockStart,
                            content[bodyStart..lineStart], Closed: true));
                    openFence = null;
                    kind = null;
                }
            }
            lineStart = lineEnd;
        }
        if (kind is not null)
            blocks.Add(new ProtocolBlock(kind, blockStart, content.Length - blockStart, content[bodyStart..], Closed: false));
        return blocks;
    }
}
