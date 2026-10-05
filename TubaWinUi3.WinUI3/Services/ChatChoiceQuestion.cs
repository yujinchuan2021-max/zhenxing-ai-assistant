using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TubaWinUi3.Services;

internal sealed record ChatChoiceOption(string Id, string Label, string Answer);

internal sealed record ChatChoicePrompt(string Question, IReadOnlyList<ChatChoiceOption> Options,
    bool IsTextFallback = false)
{
    internal string? TextFallbackFragment { get; init; }
}

/// <summary>
/// A clarification question only sends the selected answer back to its owning conversation.
/// There are no commands, URLs, settings changes, or installation actions in this contract.
/// The page must require a completed successful reply before enabling the buttons.
/// </summary>
internal static class ChatChoiceQuestion
{
    internal const int MaxMessageChars = 65536;
    internal const int MaxPayloadChars = 4096;
    private const string ProtocolLabel = "choice-question";
    private static readonly Regex FenceLine = new(@"^ {0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex IdPattern = new(@"\A[a-z][a-z0-9_-]{0,47}\z", RegexOptions.CultureInvariant);
    private static readonly Regex BulletLine = new(@"^ {0,3}[-*+] (?<text>\S.*?)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex ResponseInstruction = new(
        @"\A(?:回一个就行|选一个就行|请选择一项|请选择一个|选择一项|选择一个|回一个|选一个|Pick one|Choose one|Select one)(?:[，,:：；;。.!！].*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static bool HasQuestionProtocol(string original) => !string.IsNullOrEmpty(original)
        && FindRootFences(original).Any(x => IsQuestionProtocolLabel(x.Label));

    internal static ChatChoicePrompt? TryCapture(string original, bool allowTextFallback = true)
    {
        if (string.IsNullOrWhiteSpace(original) || original.Length > MaxMessageChars) return null;
        var blocks = FindRootFences(original);
        var contracts = blocks.Where(x => IsCompetingProtocolLabel(x.Label)).ToArray();
        if (contracts.Length != 0)
            return contracts.Length == 1 && contracts[0].Marker == "```"
                && contracts[0].Label == ProtocolLabel && contracts[0].Closed
                ? TryReadPayload(contracts[0].Body) : null;

        // Compatibility is intentionally narrow. A list of tools, ordered steps,
        // quoted examples, and questions mixed with any code block are not buttons.
        return allowTextFallback && blocks.Count == 0 ? TryCaptureTextQuestion(original) : null;
    }

    /// <summary>Only protocol data is hidden while streaming; ordinary prose is not guessed.</summary>
    internal static string GetVisibleContent(string original)
    {
        if (string.IsNullOrEmpty(original) || original.Length > MaxMessageChars) return original;
        var hidden = FindRootFences(original).Where(x => x.Marker == "```"
            && (x.Closed
                ? x.Label == ProtocolLabel && TryReadPayload(x.Body) is not null
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

    /// <summary>Called after capture, so an ordinary completed choice list is not duplicated above its card.</summary>
    internal static string GetQuestionVisibleContent(string original, ChatChoicePrompt prompt)
    {
        var visible = GetVisibleContent(original);
        if (prompt.TextFallbackFragment is { Length: > 0 } fragment)
        {
            var position = visible.IndexOf(fragment, StringComparison.Ordinal);
            if (position >= 0 && visible.IndexOf(fragment, position + fragment.Length, StringComparison.Ordinal) < 0)
                visible = visible.Remove(position, fragment.Length);
        }
        return visible.Trim();
    }

    internal static string CopyWithChoices(string original, ChatChoicePrompt prompt)
    {
        var text = new StringBuilder(GetQuestionVisibleContent(original, prompt));
        if (text.Length > 0) text.AppendLine().AppendLine();
        text.AppendLine(prompt.Question);
        for (var i = 0; i < prompt.Options.Count; i++)
            text.Append(i + 1).Append(". ").AppendLine(prompt.Options[i].Label);
        return text.ToString().Trim();
    }

    private static ChatChoicePrompt? TryReadPayload(string body)
    {
        if (body.Length > MaxPayloadChars) return null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!HasExactProperties(root, "schema", "question", "options")
                || !root.GetProperty("schema").TryGetInt32(out var version) || version != 1
                || !TryReadText(root.GetProperty("question"), 160, out var question)
                || root.GetProperty("options").ValueKind != JsonValueKind.Array) return null;
            var options = root.GetProperty("options").EnumerateArray().ToArray();
            if (options.Length is < 2 or > 6) return null;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var answers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var captured = new List<ChatChoiceOption>();
            foreach (var option in options)
            {
                if (!HasExactProperties(option, "id", "label", "answer")
                    || !TryReadText(option.GetProperty("id"), 48, out var id) || !IdPattern.IsMatch(id)
                    || !TryReadText(option.GetProperty("label"), 80, out var label)
                    || !TryReadText(option.GetProperty("answer"), 200, out var answer)
                    || !ids.Add(id) || !labels.Add(label) || !answers.Add(answer)) return null;
                captured.Add(new ChatChoiceOption(id, label, answer));
            }
            return new ChatChoicePrompt(question, Array.AsReadOnly(captured.ToArray()));
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = element.EnumerateObject().Select(x => x.Name).ToArray();
        return names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length
            && expected.All(x => names.Contains(x, StringComparer.Ordinal));
    }

    private static bool TryReadText(JsonElement element, int maxLength, out string text)
    {
        text = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() ?? "" : "";
        return IsPlainText(text, maxLength);
    }

    private static bool IsPlainText(string text, int maxLength) => text.Length is > 0 && text.Length <= maxLength
        && !text.Any(char.IsControl) && !text.Contains('`') && !text.Contains("~~~", StringComparison.Ordinal)
        && !text.Contains("http://", StringComparison.OrdinalIgnoreCase)
        && !text.Contains("https://", StringComparison.OrdinalIgnoreCase)
        && !text.Contains("www.", StringComparison.OrdinalIgnoreCase)
        && !text.Contains('<') && !text.Contains('>');

    private static ChatChoicePrompt? TryCaptureTextQuestion(string original)
    {
        // Root bullet options immediately follow the only actual question. Require
        // short labels rather than turning recommendations or instructions into actions.
        if (original.Any(c => c is '`' or '~') || original.Contains("\n>", StringComparison.Ordinal)
            || original.StartsWith('>') || original.Contains("\n    ", StringComparison.Ordinal)
            || original.Count(c => c is '?' or '？') != 1) return null;
        var lines = ReadLines(original);
        var questions = lines.Select((line, index) => (line, index))
            .Where(x => x.line.Text.TrimEnd().EndsWith('？') || x.line.Text.TrimEnd().EndsWith('?')).ToArray();
        if (questions.Length != 1) return null;
        var (questionLine, questionIndex) = questions[0];
        var question = questionLine.Text.Trim();
        if (!IsPlainText(question, 160) || question.Count(c => c is '?' or '？') != 1
            || question.StartsWith("- ", StringComparison.Ordinal) || question.StartsWith("* ", StringComparison.Ordinal)
            || question.StartsWith("+ ", StringComparison.Ordinal)
            || !IsExplicitChoiceQuestion(question)) return null;
        var optionIndex = questionIndex + 1;
        while (optionIndex < lines.Count && string.IsNullOrWhiteSpace(lines[optionIndex].Text)) optionIndex++;
        var options = new List<ChatChoiceOption>();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var end = questionLine.End;
        while (optionIndex < lines.Count)
        {
            var match = BulletLine.Match(lines[optionIndex].Text);
            if (!match.Success) break;
            var label = match.Groups["text"].Value.Trim();
            if (!IsPlainText(label, 40) || label.Any(c => c is '?' or '？' or ':' or '：' or '|' or '[' or ']' or '{' or '}')
                || label.Contains("**", StringComparison.Ordinal) || !labels.Add(label)) return null;
            options.Add(new ChatChoiceOption("option_" + (options.Count + 1), label, label));
            end = lines[optionIndex++].End;
        }
        if (options.Count is < 2 or > 6) return null;
        // A yes/no question can precede procedural bullets ("Can I install it
        // using these steps?"). Only answer-shaped labels become buttons in this
        // compatibility path; the explicit JSON contract has no such heuristic.
        if (!IsAlternativeQuestion(question) && options.Any(x => !IsShortStatusAnswer(x.Label))) return null;
        var suffix = original[end..].Trim();
        if (suffix.Length > 80 || (suffix.Length > 0 && (!IsPlainText(suffix, 80) || !ResponseInstruction.IsMatch(suffix))))
            return null;
        // Other bullets in the explanatory prefix suggest a list or multiple topics,
        // not a single clarification. A short introduction remains visible.
        if (lines.Take(questionIndex).Any(x => BulletLine.IsMatch(x.Text)
            || Regex.IsMatch(x.Text, @"^\s*\d+[.)、]\s", RegexOptions.CultureInvariant))) return null;
        var source = original[questionLine.Start..].TrimEnd();
        return new ChatChoicePrompt(question, Array.AsReadOnly(options.ToArray()), IsTextFallback: true)
        { TextFallbackFragment = source };
    }

    private static bool IsExplicitChoiceQuestion(string question)
    {
        // A "how" question followed by procedural bullets remains instructions.
        // Clear yes/no, either/or, and which-option questions can use compatibility.
        if (question.Contains("怎么", StringComparison.Ordinal) || question.Contains("如何", StringComparison.Ordinal)
            || Regex.IsMatch(question, @"\bhow\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)) return false;
        return question.Contains('吗') || question.Contains("是否", StringComparison.Ordinal)
            || question.Contains("能否", StringComparison.Ordinal) || question.Contains("哪个", StringComparison.Ordinal)
            || question.Contains("哪一个", StringComparison.Ordinal) || question.Contains("哪种", StringComparison.Ordinal)
            || question.Contains("选择", StringComparison.Ordinal) || question.Contains("还是", StringComparison.Ordinal)
            || question.Contains("愿意", StringComparison.Ordinal)
            || Regex.IsMatch(question, @"\b(?:can|could|would|which|is|are|do|does)\b",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static bool IsAlternativeQuestion(string question) =>
        question.Contains("哪个", StringComparison.Ordinal) || question.Contains("哪一个", StringComparison.Ordinal)
        || question.Contains("哪种", StringComparison.Ordinal) || question.Contains("选择", StringComparison.Ordinal)
        || question.Contains("还是", StringComparison.Ordinal)
        || Regex.IsMatch(question, @"\bwhich\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static bool IsShortStatusAnswer(string label) => Regex.IsMatch(label,
        @"\A(?:能|不能|可以|不可以|可用|不可用|正常|不正常|是|不是|否|有|没有|没|已有|已|尚未|未|不|无法|打不开|未知|愿意|不愿意|需要|不需要|先不|请帮我|帮我|yes\b|no\b|not\b|unsure\b|unknown\b|available\b|unavailable\b|i\s+(?:can|cannot|can't|don't|do\s+not|am\s+not)\b)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private sealed record TextLine(string Text, int Start, int End);

    private static IReadOnlyList<TextLine> ReadLines(string original)
    {
        var lines = new List<TextLine>();
        for (var start = 0; start < original.Length;)
        {
            var newline = original.IndexOf('\n', start);
            var end = newline < 0 ? original.Length : newline + 1;
            lines.Add(new TextLine(original[start..(newline < 0 ? end : newline)].TrimEnd('\r'), start, end));
            start = end;
        }
        return lines;
    }

    private static bool IsQuestionLabelPrefix(string label) => label.StartsWith("choice-", StringComparison.Ordinal)
        && ProtocolLabel.StartsWith(label, StringComparison.Ordinal);

    private static bool IsQuestionProtocolLabel(string label) => label.StartsWith("choice-", StringComparison.OrdinalIgnoreCase)
        && ProtocolLabel.StartsWith(label, StringComparison.OrdinalIgnoreCase);

    private static bool IsCompetingProtocolLabel(string label) => IsQuestionProtocolLabel(label)
        || IsPrefix(label, "model-preference", "model-")
        || IsPrefix(label, "toolflow-options", "toolflow-")
        || IsPrefix(label, "toolflow-items", "toolflow-");

    private static bool IsPrefix(string label, string protocol, string minimum) =>
        label.StartsWith(minimum, StringComparison.OrdinalIgnoreCase) && protocol.StartsWith(label, StringComparison.OrdinalIgnoreCase);

    private static bool IsPayloadPrefix(string body)
    {
        if (body.Length > MaxPayloadChars) return false;
        var trimmed = body.TrimStart();
        // This is a display rule only. No incomplete payload ever yields buttons.
        return trimmed.Length == 0 || trimmed.StartsWith('{');
    }

    private sealed record Fence(string Marker, string Label, int Start, int Length, string Body, bool Closed);

    private static IReadOnlyList<Fence> FindRootFences(string content)
    {
        var blocks = new List<Fence>();
        string? markerOpen = null;
        string? label = null;
        var blockStart = 0;
        var bodyStart = 0;
        for (var start = 0; start < content.Length;)
        {
            var newline = content.IndexOf('\n', start);
            var end = newline < 0 ? content.Length : newline + 1;
            var line = content[start..(newline < 0 ? end : newline)].TrimEnd('\r');
            var match = FenceLine.Match(line);
            if (match.Success)
            {
                var marker = match.Groups["fence"].Value;
                var info = match.Groups["info"].Value.TrimEnd(' ', '\t');
                if (markerOpen is null)
                {
                    if (marker[0] != '`' || !info.Contains('`'))
                    {
                        markerOpen = marker; label = info; blockStart = start; bodyStart = end;
                    }
                }
                else if (marker[0] == markerOpen[0] && marker.Length >= markerOpen.Length && string.IsNullOrWhiteSpace(info))
                {
                    blocks.Add(new Fence(markerOpen, label!, blockStart, end - blockStart, content[bodyStart..start], true));
                    markerOpen = null; label = null;
                }
            }
            start = end;
        }
        if (markerOpen is not null)
            blocks.Add(new Fence(markerOpen, label!, blockStart, content.Length - blockStart, content[bodyStart..], false));
        return blocks;
    }
}
