namespace TubaWinUi3.Services.ToolFlows;

/// <summary>A compact read-only view of the selected plan. It never probes, installs or infers project completion.</summary>
internal sealed record ToolFlowTaskPresentation(string Goal, string Plan, string Summary, string NextStep,
    bool CanPrepare, bool Running)
{
    internal static ToolFlowTaskPresentation Create(ToolFlowSelection selection,
        ToolFlowInstallResult? result = null, bool running = false, bool stopped = false,
        string? activeStep = null, IReadOnlyCollection<string>? knownTargets = null)
    {
        var view = ToolFlowResume.Build(selection, result, knownTargets);
        var summary = string.Format(L("AiTask_InstallCount", "已具备 {0}/{1} 项"),
            view.InstalledOrDetectedCount, view.Rows.Count);
        if (view.FailedCount > 0) summary += string.Format(L("AiTask_FailedCount", " · 未成功 {0} 项"), view.FailedCount);
        var manual = view.NeedsUserAssistCount + view.UserReportedDoneCount;
        if (manual > 0) summary += string.Format(L("AiTask_ManualCount", " · 人工确认 {0} 项"), manual);
        string next;
        if (running)
            next = activeStep ?? L("AiTask_Preparing", "正在读取清单，准备逐项检测…");
        else if (view.PendingAutomaticItems.Count > 0)
            next = view.FailedCount > 0
                ? L("AiTask_RetryNext", "下一步：查看未成功的项目，再继续准备。")
                : L("AiTask_PrepareNext", "下一步：继续准备清单中的工具，已安装的会跳过。");
        else if (view.Rows.FirstOrDefault(r => r.State is ToolFlowResumeItemState.NeedsUserAssist
                     or ToolFlowResumeItemState.Failed) is { } row)
            next = string.Format(L("AiTask_ManualNext", "下一步：处理「{0}」，点击清单查看操作提示。"), row.Name);
        else if (view.UserReportedDoneCount > 0)
            next = L("AiTask_SelfReportedNext", "下一步：核对人工配置，再交给你的 AI Agent；自报完成尚未验证。");
        else
            next = view.Rows.Count == 0
                ? L("AiTask_EmptyNext", "下一步：补齐工具清单，再开始准备。")
                : L("AiTask_HandoffNext", "下一步：交给你的 AI Agent，核对登录、模型配置和实际运行。");
        if (stopped && !running)
            summary = L("AiTask_Stopped", "后续准备已停止，已有结果已保留") + " · " + summary;
        return new(selection.ProjectGoal, selection.FlowName, summary, next,
            view.PendingAutomaticItems.Count > 0, running);
    }

    private static string L(string key, string fallback) => LocalizationService.L(key, fallback);
}
