using System.Text;
using System.Text.RegularExpressions;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Services;

internal sealed record AssistantReplyExcerpt(string Body, string Preview, bool HasDetails);

/// <summary>Display-only text: preserves the original message for history and plan capture.</summary>
internal static class AssistantReplyPresentation
{
    internal static AssistantReplyExcerpt Create(string original)
    {
        var body = HideLegacyActions(ChatChoiceQuestion.GetVisibleContent(ModelPreferenceQuestion.GetVisibleContent(
            ToolFlowProposalParser.GetVisibleContent(original)))).Trim();
        if (body.Length <= 650 && body.Count(c => c == '\n') < 12)
            return new(body, body, false);

        var first = body.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.None)[0].Trim();
        // Preview is plain text; do not split Markdown into executable or invalid fragments.
        first = Regex.Replace(first, @"\[([^\]]+)\]\([^\)]+\)", "$1");
        first = first.Replace("**", "").TrimStart('#', ' ', '\r', '\n');
        if (first.Length > 220) first = first[..220].TrimEnd() + "…";
        return new(body, first, true);
    }

    internal static string CopyWithRecommendations(string visibleBody, ToolFlowRecommendationSet set)
    {
        var text = new StringBuilder(visibleBody.Trim());
        text.Append("\n\n").AppendLine(set.Goal);
        foreach (var option in set.Options)
        {
            text.AppendLine().AppendLine(option.Name).AppendLine(option.Summary)
                .AppendLine(option.Fit).AppendLine(option.Cost).AppendLine(option.Requirements);
            if (!option.Available) text.AppendLine(option.UnavailableReason);
            foreach (var item in option.Items)
            {
                text.AppendLine(item.Name);
                if (!string.IsNullOrWhiteSpace(item.Version)) text.AppendLine(item.Version);
                if (!string.IsNullOrWhiteSpace(item.SourceUrl)) text.AppendLine(item.SourceUrl);
                if (!string.IsNullOrWhiteSpace(item.DownloadUrl) && item.DownloadUrl != item.SourceUrl)
                    text.AppendLine(item.DownloadUrl);
            }
            foreach (var warning in option.Warnings) text.AppendLine(warning);
        }
        return text.ToString().Trim();
    }

    // The legacy ACTION protocol contains commands, not chat prose. Respect ordinary
    // code fences so examples stay readable; hide complete and still-streaming payloads.
    private static string HideLegacyActions(string content)
    {
        if (!content.Contains("[ACTION]", StringComparison.OrdinalIgnoreCase)) return content;
        var result = new StringBuilder();
        string? fence = null;
        string? actionFence = null;
        var inAction = false;
        var removedAction = false;
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            var mark = Regex.Match(trimmed, @"^(?<fence>`{3,}|~{3,})");
            var boundary = mark.Success ? mark.Groups["fence"].Value : null;
            if (inAction)
            {
                if (actionFence is not null)
                {
                    if (boundary is not null && boundary[0] == actionFence[0] && boundary.Length >= actionFence.Length)
                        actionFence = null;
                    continue;
                }
                if (boundary is not null) { actionFence = boundary; continue; }
                if (string.IsNullOrWhiteSpace(trimmed) || "[{\"},]".Contains(trimmed[0])) continue;
                inAction = false;
            }
            if (fence is null && trimmed.StartsWith("[ACTION]", StringComparison.OrdinalIgnoreCase))
            {
                inAction = true;
                removedAction = true;
                continue;
            }
            if (boundary is not null)
            {
                if (fence is null) fence = boundary;
                else if (boundary[0] == fence[0] && boundary.Length >= fence.Length) fence = null;
            }
            result.AppendLine(line);
        }
        return removedAction ? result.ToString().TrimEnd('\r', '\n') : content;
    }
}
