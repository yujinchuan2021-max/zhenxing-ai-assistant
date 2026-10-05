namespace TubaWinUi3.Services.ToolFlows;

internal enum ConversationGoalStage { Understanding, AnsweringQuestion, ChoosingPlan, Preparing }

/// <summary>A read-only goal anchor. It never edits a selected plan or treats a reply as user confirmation.</summary>
internal sealed record ConversationGoalPresentation(string Goal, ConversationGoalStage Stage, bool IsConfirmed)
{
    internal static ConversationGoalPresentation? Create(string? selectedGoal, string? proposedGoal,
        IEnumerable<ToolFlowConversationMessage> messages, bool processing, bool hasPendingQuestion,
        bool hasPendingPlan = true)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var selected = Clean(selectedGoal);
        var proposed = Clean(proposedGoal);
        var history = messages.ToArray();
        var newTaskStart = FindExplicitNewTaskStart(history);
        var original = history.Skip(Math.Max(0, newTaskStart)).FirstOrDefault(message => message.Role == "user" &&
            !string.IsNullOrWhiteSpace(message.Content) &&
            !message.Content.StartsWith("[TOOL_RESULT]", StringComparison.OrdinalIgnoreCase) &&
            !message.Content.StartsWith("[ACTION_CONFIRMED]", StringComparison.OrdinalIgnoreCase))?.Content;
        var goal = selected ?? Clean(original) ?? proposed;
        if (goal is null) return null;
        var stage = selected is not null ? ConversationGoalStage.Preparing
            : processing ? ConversationGoalStage.Understanding
            : hasPendingQuestion ? ConversationGoalStage.AnsweringQuestion
            : proposed is not null && hasPendingPlan ? ConversationGoalStage.ChoosingPlan
            : ConversationGoalStage.Understanding;
        return new(goal, stage, selected is not null);
    }

    // Only explicit task changes reset the anchor. Budget replies and ordinary follow-ups do not.
    internal static int FindExplicitNewTaskStart(IReadOnlyList<ToolFlowConversationMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
            if (messages[index].Role == "user" && StartsExplicitNewTask(messages[index].Content)) return index;
        return -1;
    }

    internal static bool StartsExplicitNewTask(string text)
    {
        var input = text.TrimStart();
        string[] prefixes = ["换个目标", "换一个目标", "换个任务", "换一个任务", "新目标", "新任务",
            "不做这个了", "先不做这个", "改做", "改成做", "start a new task", "new goal", "new task"];
        return prefixes.Any(prefix => input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasNewTaskAfterSelection(ToolFlowSelection selection,
        IReadOnlyList<ToolFlowConversationMessage> messages)
    {
        // An explicit resume can have another chat's snapshot. Only compare a matching visible prefix.
        var snapshot = selection.Conversation;
        if (snapshot.Count == 0 || messages.Count < snapshot.Count) return false;
        for (var index = 0; index < snapshot.Count; index++)
            if (snapshot[index].Role != messages[index].Role || snapshot[index].Content != messages[index].Content)
                return false;
        return FindExplicitNewTaskStart(messages) >= snapshot.Count;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
