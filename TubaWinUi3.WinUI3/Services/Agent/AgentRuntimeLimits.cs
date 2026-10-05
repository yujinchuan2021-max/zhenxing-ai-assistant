namespace TubaWinUi3.Services.Agent;

internal static class AgentRuntimeLimits
{
    internal const int DefaultMaxRounds = 30;
    internal const int ContinueMaxRounds = 10;
    internal const float DefaultTemperature = 0.4f;
    internal const int MaxReasoningChars = 6000;
    internal const int HistoryBudgetChars = 40000;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    internal static string? TruncateReasoning(string? reasoning)
    {
        if (string.IsNullOrEmpty(reasoning) || reasoning.Length <= MaxReasoningChars)
            return reasoning;

        var cut = reasoning[..MaxReasoningChars];
        if (cut.Length > 0 && char.IsHighSurrogate(cut[^1]))
            cut = cut[..^1];
        return cut + MiscTexts.T("\n\n[思维过程过长，已截断]");
    }
}
