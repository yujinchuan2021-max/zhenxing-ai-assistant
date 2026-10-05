using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TubaWinUi3.Services;

internal sealed record ModelPreferenceChoice(string Id, string Label, string Answer, string? Hint = null);

/// <summary>
/// A narrow question contract, with no settings, credentials or install actions.
/// The caller must also require a successfully completed assistant reply before
/// allowing a choice; capture of a closed block during streaming is not consent.
/// </summary>
internal static class ModelPreferenceQuestion
{
    internal const int MaxMessageChars = 65536;
    internal const int MaxPayloadChars = 1024;
    private const string ProtocolLabel = "model-preference";
    private static readonly Regex FenceLine = new(@"^ {0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>Read current translations each time so language changes do not cache old answers.</summary>
    public static IReadOnlyList<ModelPreferenceChoice> Choices => Array.AsReadOnly(new ModelPreferenceChoice[]
    {
        new("paid_top", L("Ai_ModelPreference_PaidTop", "可以付费，并使用顶级模型", "Pay for a top-tier model"),
            L("Ai_ModelPreference_PaidTopAnswer", "我愿意付费使用顶级模型，请按这个偏好推荐。",
                "I am willing to pay for a top-tier model. Please recommend based on this preference.")),
        new("paid_value", L("Ai_ModelPreference_PaidValue", "可以付费，需要便宜的模型", "Pay for an affordable model"),
            L("Ai_ModelPreference_PaidValueAnswer", "我愿意付费，但希望使用便宜、性价比高的模型，请按这个偏好推荐。",
                "I am willing to pay, but I prefer an affordable model with good value. Please recommend based on this preference.")),
        new("local", L("Ai_ModelPreference_Local", "不想付费，用本地模型", "Use a local model without paying"),
            L("Ai_ModelPreference_LocalAnswer", "我不想付费，想使用本地模型。请先核对这台电脑的内存、显卡和显存等硬件，再推荐能运行的模型。",
                "I do not want to pay and would like to use a local model. Please check this computer's RAM, graphics card, VRAM, and other hardware first, then recommend a model it can run."),
            L("Ai_ModelPreference_LocalHint", "先核对硬件是否支持，再推荐。", "Check hardware support before recommending a model.")),
        new("recommend", L("Ai_ModelPreference_Recommend", "我也不知道，给我推荐", "Not sure; recommend one for me"),
            L("Ai_ModelPreference_RecommendAnswer", "我还不清楚，请结合我的需求、预算和电脑硬件推荐合适的模型使用方式。",
                "I am not sure yet. Please recommend a suitable way to use models based on my needs, budget, and computer hardware.")),
    });

    /// <summary>Recognize a root question tag for invalid-format notices, without validating or enabling choices.</summary>
    internal static bool HasQuestionProtocol(string original) => !string.IsNullOrEmpty(original)
        && FindRootFences(original).Any(x => IsQuestionProtocolLabel(x.Label));
    /// <summary>Only one complete root-level triple-backtick contract can become a question.</summary>
    public static bool TryCapture(string original)
    {
        if (string.IsNullOrWhiteSpace(original) || original.Length > MaxMessageChars) return false;
        var blocks = FindRootFences(original).Where(x => IsProtocolLabel(x.Label)).ToArray();
        return blocks.Length == 1 && IsSupported(blocks[0]) && blocks[0].Closed
            && TryReadPayload(blocks[0].Body);
    }

    /// <summary>
    /// Hide valid question payloads and recognizable trailing prefixes while streaming.
    /// Invalid contracts and ordinary code examples remain readable; no text is mutated in history.
    /// </summary>
    public static string GetVisibleContent(string original)
    {
        if (string.IsNullOrEmpty(original) || original.Length > MaxMessageChars) return original;
        var hidden = FindRootFences(original).Where(x => x.Marker == "```"
            && (x.Closed
                ? x.Label == ProtocolLabel && TryReadPayload(x.Body)
                : IsQuestionLabelPrefix(x.Label) && IsPayloadPrefix(x.Body))).ToArray();
        if (hidden.Length == 0) return original;
        var text = new StringBuilder(original.Length);
        var position = 0;
        foreach (var block in hidden)
        {
            text.Append(original, position, block.Start - position);
            position = block.Start + block.Length;
        }
        text.Append(original, position, original.Length - position);
        return text.ToString();
    }

    /// <summary>Copy the visible question and all four readable choices, without protocol JSON.</summary>
    public static string CopyWithChoices(string visibleBody)
    {
        var text = new StringBuilder(GetVisibleContent(visibleBody).Trim());
        if (text.Length > 0) text.AppendLine().AppendLine();
        text.AppendLine(L("Ai_ModelPreference_Title", "你希望怎么使用 AI 模型？", "How would you like to use AI models?"));
        var choices = Choices;
        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            text.Append(i + 1).Append(". ").AppendLine(choice.Label);
            if (!string.IsNullOrWhiteSpace(choice.Hint)) text.AppendLine(choice.Hint);
        }
        text.AppendLine(L("Ai_ModelPreference_CopyHint", "选择后会把你的偏好发送给助手继续讨论。", "Choosing an option sends your preference to the assistant to continue the conversation."));
        return text.ToString().Trim();
    }

    private static string L(string key, string zh, string en) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? en : zh);
    private static bool IsSupported(Fence block) => block.Marker == "```" && block.Label == ProtocolLabel;

    private static bool IsQuestionLabelPrefix(string label) => label.StartsWith("model-", StringComparison.Ordinal)
        && ProtocolLabel.StartsWith(label, StringComparison.Ordinal);

    // Partial competing suffixes also defeat uniqueness, even after a valid closed question.
    private static bool IsQuestionProtocolLabel(string label) => label.StartsWith("model-", StringComparison.OrdinalIgnoreCase)
        && ProtocolLabel.StartsWith(label, StringComparison.OrdinalIgnoreCase);

    private static bool IsProtocolLabel(string label) => IsQuestionProtocolLabel(label)
        || label.Equals("choice-question", StringComparison.OrdinalIgnoreCase)
        || (label.StartsWith("choice-", StringComparison.OrdinalIgnoreCase)
            && "choice-question".StartsWith(label, StringComparison.OrdinalIgnoreCase))
        || label.Equals("toolflow-options", StringComparison.OrdinalIgnoreCase)
        || label.Equals("toolflow-items", StringComparison.OrdinalIgnoreCase)
        || (label.StartsWith("toolflow-", StringComparison.OrdinalIgnoreCase)
            && ("toolflow-options".StartsWith(label, StringComparison.OrdinalIgnoreCase)
                || "toolflow-items".StartsWith(label, StringComparison.OrdinalIgnoreCase)));

    private static bool TryReadPayload(string body)
    {
        if (body.Length > MaxPayloadChars) return false;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var fields = root.EnumerateObject().ToArray();
            return fields.Length == 1 && fields[0].Name == "schema"
                && fields[0].Value.ValueKind == JsonValueKind.Number
                && fields[0].Value.TryGetInt32(out var version) && version == 1;
        }
        catch (JsonException) { return false; }
    }

    // Accept only prefixes of the one legal object, including JSON whitespace and
    // escaped spellings of the key. This is display-only and never captures a choice.
    private static bool IsPayloadPrefix(string body)
    {
        if (body.Length > MaxPayloadChars) return false;
        var position = 0;
        SkipWhitespace();
        if (position == body.Length) return true;
        if (body[position++] != '{') return false;
        SkipWhitespace();
        if (position == body.Length) return true;
        if (body[position++] != '"') return false;
        foreach (var expected in "schema")
        {
            if (position == body.Length) return true;
            var current = body[position++];
            if (current == '\\')
            {
                if (position == body.Length) return true;
                if (body[position++] != 'u') return false;
                var hex = ((int)expected).ToString("x4", System.Globalization.CultureInfo.InvariantCulture);
                foreach (var digit in hex)
                {
                    if (position == body.Length) return true;
                    if (char.ToLowerInvariant(body[position++]) != digit) return false;
                }
            }
            else if (current != expected) return false;
        }
        foreach (var expected in "\":1}")
        {
            if (expected != '"') SkipWhitespace();
            if (position == body.Length) return true;
            if (body[position++] != expected) return false;
        }
        var trailing = body[position..];
        SkipWhitespace();
        return position == body.Length || Regex.IsMatch(trailing, @"\A[ \t\r\n]*\n {0,3}`{1,2}\z",
            RegexOptions.CultureInvariant);

        void SkipWhitespace()
        {
            while (position < body.Length && body[position] is ' ' or '\t' or '\r' or '\n') position++;
        }
    }

    private sealed record Fence(string Marker, string Label, int Start, int Length, string Body, bool Closed);

    // Scan all root code fences, not just the protocol, so examples inside a wider
    // Markdown/text fence stay code. Indented and blockquoted examples are not root fences.
    private static IReadOnlyList<Fence> FindRootFences(string content)
    {
        var blocks = new List<Fence>();
        string? openMarker = null;
        string? label = null;
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
                var marker = match.Groups["fence"].Value;
                var info = match.Groups["info"].Value.TrimEnd(' ', '\t');
                if (openMarker is null)
                {
                    // Backticks are illegal inside a backtick fence's info string.
                    if (marker[0] != '`' || !info.Contains('`'))
                    {
                        openMarker = marker;
                        label = info;
                        blockStart = lineStart;
                        bodyStart = lineEnd;
                    }
                }
                else if (marker[0] == openMarker[0] && marker.Length >= openMarker.Length
                    && string.IsNullOrWhiteSpace(info))
                {
                    blocks.Add(new Fence(openMarker, label!, blockStart, lineEnd - blockStart,
                        content[bodyStart..lineStart], Closed: true));
                    openMarker = null;
                    label = null;
                }
            }
            lineStart = lineEnd;
        }
        if (openMarker is not null)
            blocks.Add(new Fence(openMarker, label!, blockStart, content.Length - blockStart,
                content[bodyStart..], Closed: false));
        return blocks;
    }
}
