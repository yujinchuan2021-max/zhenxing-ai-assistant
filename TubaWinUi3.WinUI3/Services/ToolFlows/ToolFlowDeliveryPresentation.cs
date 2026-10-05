namespace TubaWinUi3.Services.ToolFlows;

internal enum ToolFlowDeliveryAction { None, OpenTool, OpenAgentConsole, OpenLocation, OpenSource, Handoff }

/// <summary>
/// A goal's next entry, projected only from the selected plan and ephemeral local evidence.
/// This does not launch anything or prove an account, model connection or project works.
/// </summary>
internal sealed record ToolFlowDeliveryPresentation(string Title, string Hint, string ActionLabel,
    ToolFlowDeliveryAction Action, ToolFlowToolAccessEntry? AccessEntry = null,
    string? SourceUrl = null, string? ItemId = null)
{
    internal static ToolFlowDeliveryPresentation Create(ToolFlowResumeView view,
        ToolFlowGoalGuide guide, IReadOnlyList<ToolFlowToolAccessEntry> accessEntries)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(guide);
        ArgumentNullException.ThrowIfNull(accessEntries);
        if (guide.CurrentStep is not null)
            return new("", "", "", ToolFlowDeliveryAction.None);
        if (view.Rows.Count == 0)
            return new(Text("AiDelivery_FallbackTitle", "下一步：核对使用入口", "Next: check your usage entry"),
                Text("AiGoal_SummaryEmpty", "当前方案还没有准备清单。", "This plan has no preparation list yet."),
                Text("AiDelivery_ViewUsage", "查看使用与交接说明", "View usage and handoff notes"),
                ToolFlowDeliveryAction.None);

        // Ambiguous identities cannot acquire a launch target. Optional helpers never lead delivery.
        var requiredRows = guide.ReadySteps.Where(step => !step.IsOptional).SelectMany(step => step.Rows)
            .Where(row => row.State is ToolFlowResumeItemState.InstalledOrDetected or ToolFlowResumeItemState.UserReportedDone)
            .Where(row => view.Rows.Count(candidate => candidate.ItemId == row.ItemId) == 1)
            .ToArray();
        var candidates = requiredRows.Select(row =>
        {
            var matching = view.Selection.Items.Where(item => item.ItemId == row.ItemId).Take(2).ToArray();
            return (Row: row, Item: matching.Length == 1 ? matching[0] : null);
        }).Where(candidate => candidate.Item is not null).ToArray();
        var local = candidates.Select(candidate =>
        {
            var target = ToolFlowItemSemantics.ReadyTarget(candidate.Item!, candidate.Row);
            var matches = accessEntries.Where(entry => string.Equals(entry.TargetKey.Trim(), target?.Trim(),
                StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return (candidate.Row, candidate.Item, Entry: matches.Length == 1 ? matches[0] : null);
        }).Where(candidate => candidate.Entry is not null)
            .OrderBy(candidate => IsAgent(candidate.Item!) ? 0 : candidate.Entry!.IsGui &&
                candidate.Entry.ExecutablePath is not null ? 1 : 2).ToArray();

        foreach (var candidate in local)
        {
            var entry = candidate.Entry!;
            if (IsAgent(candidate.Item!) && SafeLocalPath(entry.ExecutablePath) && ToolFlowToolAccess.IsInteractiveAgent(entry))
                return new(Text("AiDelivery_ToolTitle", "下一步：打开所选工具", "Next: open your selected tool"),
                    Text("AiDelivery_ToolHint", "先在工具中核对账号和模型连接，再开始第一步。",
                        "Check account access and the model connection in the tool, then begin the first step."),
                    Text("AiDelivery_OpenAgent", "打开所选 Agent", "Open selected Agent"),
                    ToolFlowDeliveryAction.OpenAgentConsole, entry, ItemId: candidate.Row.ItemId);
            if (entry.IsGui && SafeLocalPath(entry.ExecutablePath))
                return new(Text("AiDelivery_ToolTitle", "下一步：打开所选工具", "Next: open your selected tool"),
                    IsAgent(candidate.Item!)
                        ? Text("AiDelivery_ToolHint", "先在工具中核对账号和模型连接，再开始第一步。",
                            "Check account access and the model connection in the tool, then begin the first step.")
                        : Text("AiDelivery_NonAgentHint", "打开工具，按方案开始第一步；实际效果仍需在工具中核对。",
                            "Open the tool and begin the first step in your plan. Check the outcome inside the tool."),
                    IsAgent(candidate.Item!) ? Text("AiDelivery_OpenAgent", "打开所选 Agent", "Open selected Agent")
                        : Text("AiDelivery_OpenTool", "打开所选工具", "Open selected tool"),
                    ToolFlowDeliveryAction.OpenTool, entry, ItemId: candidate.Row.ItemId);
        }

        // Service entries take precedence over signup/subscription pages, development downloads and assets.
        foreach (var candidate in candidates.Where(candidate => IsService(candidate.Item!)))
            if (ToolFlowGoalGuide.SafeSourceUrl(candidate.Item!.SourceUrl) is { } source)
                return new(Text("AiDelivery_ServiceTitle", "下一步：进入所选服务试用", "Next: try your selected service"),
                    Text("AiDelivery_ServiceHint", "进入服务后核对登录和使用权限，再试一次生成或操作。",
                        "Check sign-in and access inside the service, then try one generation or operation."),
                    Text("AiDelivery_OpenService", "打开所选服务", "Open selected service"),
                    ToolFlowDeliveryAction.OpenSource, SourceUrl: source, ItemId: candidate.Row.ItemId);

        // A usable service wins over a dependency's directory (for example an FFmpeg folder).
        foreach (var candidate in local)
            if (SafeLocalPath(candidate.Entry!.DirectoryPath))
                return new(Text("AiDelivery_LocationTitle", "下一步：进入工具位置", "Next: open the tool location"),
                    Text("AiDelivery_LocationHint", "已找到工具位置；按使用说明启动并核对连接。",
                        "The tool location is available. Follow the usage notes to start it and check its connection."),
                    Text("AiDelivery_OpenLocation", "打开工具位置", "Open tool location"),
                    ToolFlowDeliveryAction.OpenLocation, candidate.Entry, ItemId: candidate.Row.ItemId);

        return new(Text("AiDelivery_FallbackTitle", "下一步：核对使用入口", "Next: check your usage entry"),
            Text("AiDelivery_FallbackHint", "准备记录已保留，暂未找到可直接打开的入口；查看使用说明继续。",
                "Preparation records are saved, but a direct entry is unavailable. Continue with the usage notes."),
            Text("AiDelivery_ViewUsage", "查看使用与交接说明", "View usage and handoff notes"),
            requiredRows.Length > 0 ? ToolFlowDeliveryAction.Handoff : ToolFlowDeliveryAction.None,
            ItemId: requiredRows.FirstOrDefault()?.ItemId);
    }

    private static bool IsAgent(ToolFlowItem item) => ToolFlowItemSemantics.IsAgent(item);

    private static bool IsService(ToolFlowItem item) => item.Kind.Trim().ToLowerInvariant() is
        "service" or "website" or "web" or "platform" or "online_service";

    private static bool SafeLocalPath(string? path) => !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal)
        && !path.Any(char.IsControl) && !path.Contains('"') && !path.Contains('%')
        && path.IndexOf(':', 2) < 0;

    private static string Text(string key, string zh, string en) => ToolFlowGoalText.Get(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? en : zh);
}
