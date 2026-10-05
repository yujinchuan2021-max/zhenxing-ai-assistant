using System.Text;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>
/// Builds a user-reviewable handoff for the AI Agent the user will use.
/// This is text only: it does not read conversation history, send data, or infer
/// that an account, configuration, tutorial, or project milestone is complete.
/// </summary>
public static class ToolFlowAgentHandoff
{
    public static string Build(ToolFlowSelection selection, ToolFlowInstallResult? result)
        => BuildCore(selection, result, null);

    internal static string Build(ToolFlowSelection selection, ToolFlowInstallResult? result,
        ToolFlowPreparation preparation) => BuildCore(selection, result, preparation);

    private static string BuildCore(ToolFlowSelection selection, ToolFlowInstallResult? result,
        ToolFlowPreparation? freshEvidence)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var preparation = ToolFlowResume.Build(selection, result);
        if (freshEvidence is not null) preparation = ToolFlowResume.ApplyPreparation(preparation, freshEvidence);

        var text = new StringBuilder();
        text.AppendLine("请作为我接下来使用的 AI Agent，帮助我完成下面的项目。先根据实际准备状态制定第一个可执行里程碑，再一步步指导我完成；不要把尚未验证的工具当成可用，也不要声称作品已经完成。");
        text.AppendLine();
        text.AppendLine("我确认的项目目标：");
        text.AppendLine(selection.ProjectGoal.Trim());
        text.AppendLine();
        text.Append("我选择的工具流：").AppendLine(selection.FlowName.Trim());
        text.AppendLine("我确认的完整方案（方案中提到的版本和来源仍须在使用前核对）：");
        text.AppendLine(selection.FlowText.Trim());
        text.AppendLine();
        text.AppendLine("工具准备状态（依据本轮检查或最新本机记录，不代表账号登录、配置或实际创作已完成）：");

        if (selection.Items.Count == 0)
            text.AppendLine("- 尚无已确认的工具或资源清单，请先和我核对。");

        for (var index = 0; index < selection.Items.Count; index++)
        {
            var item = selection.Items[index];
            var description = ItemDescription(preparation.Rows[index]);
            text.Append("- ").Append(item.Name).Append("：").Append(description);
            if (!string.IsNullOrWhiteSpace(item.Version))
                text.Append("；方案版本 ").Append(item.Version.Trim()).Append("（实际安装版本未核实）");
            text.AppendLine();
        }

        text.AppendLine();
        var unfinished = UnfinishedNames(preparation);
        text.AppendLine("尚未完成的准备项（判定依据：本机执行记录，不代表创作进度）：");
        if (unfinished.Count == 0)
            text.AppendLine(preparation.Rows.Any(x => x.IsExistingToolReuse)
                ? "- 无：所有准备项已有安装或现有能力复用证据（仍不代表账号、配置或创作已完成）。"
                : "- 无：所有项都有已验证的安装记录（仍不代表账号、配置或创作已完成）。");
        else
            foreach (var entry in unfinished) text.AppendLine("- " + entry);

        if (!selection.Items.Any(ToolFlowItemSemantics.IsAgent))
        {
            text.AppendLine();
            text.AppendLine("这份方案里没有包含 AI Agent 或代码辅助工具：你可以自行选择任意 AI Agent（也可以自己写代码），应用不指定品牌；把这份说明粘贴给你选的那个即可。");
        }

        text.AppendLine();
        text.AppendLine("请先给我第一个里程碑、具体操作和能自行核对的完成标准。对需要账号、素材、安装或排障的项目，先指导我完成必要步骤，再继续制作。只推荐能核对来源且适合实际工具版本的教学资料；没有把握就明确说明，不要编造链接或替我登录、付款。");
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// 尚未完成的准备项：沿用恢复界面的逐项状态；只列名称与简短依据，
    /// 不回显安装器的原始消息、事件详情或对话内容。
    /// </summary>
    private static IReadOnlyList<string> UnfinishedNames(ToolFlowResumeView preparation)
    {
        var rows = new List<string>();
        foreach (var item in preparation.Rows)
        {
            if (item.State == ToolFlowResumeItemState.InstalledOrDetected) continue;
            var reason = item.State switch
            {
                ToolFlowResumeItemState.Failed => item.CurrentRunStatus == ToolFlowInstallItemStatus.Failed
                    ? "本轮未成功，需要处理" : "最近一次未成功，需要处理",
                ToolFlowResumeItemState.NeedsUserAssist when item.CurrentRunStatus == ToolFlowInstallItemStatus.ManualStep
                    => "需要我参与处理",
                ToolFlowResumeItemState.UserReportedDone => "我已自报完成，但应用未验证",
                _ => "尚无验证记录",
            };
            rows.Add(item.Name + "：" + reason);
        }
        return rows;
    }

    // 不使用恢复行的 StatusLine：那里可能包含安装器错误或本机事件详情，不能带入交接文本。
    private static string ItemDescription(ToolFlowResumeItemRow row) => row.State switch
    {
        ToolFlowResumeItemState.InstalledOrDetected when row.IsExistingToolReuse => row.CurrentRunStatus == ToolFlowInstallItemStatus.ReusedInstalledTool
            ? "本轮检测到现有 WinRAR 可满足普通解压需求；未安装 7-Zip，不代表已有 7z.exe、7z.dll 或其专用接口"
            : "最新本机记录显示复用 WinRAR 满足普通解压需求；本轮未重新检查，未验证 7-Zip、7z.exe、7z.dll 或其专用接口",
        ToolFlowResumeItemState.InstalledOrDetected => row.CurrentRunStatus switch
        {
            ToolFlowInstallItemStatus.Installed => "本轮安装后状态检查通过；具体配置和能否完成项目任务尚未验证",
            ToolFlowInstallItemStatus.AlreadyInstalled => "本轮状态检查发现已安装；具体配置和能否完成项目任务尚未验证",
            _ => "最新本机执行记录已验证安装；本轮未重新检查，具体配置和能否完成项目任务尚未验证",
        },
        ToolFlowResumeItemState.Failed => row.CurrentRunStatus == ToolFlowInstallItemStatus.Failed
            ? "本轮安装或状态检查未成功；尚未验证"
            : "最近一次安装或状态检查未成功；尚未验证",
        ToolFlowResumeItemState.UserReportedDone => "我已自报完成；应用未验证，不应当作已检测到安装",
        ToolFlowResumeItemState.NeedsUserAssist when row.CurrentRunStatus == ToolFlowInstallItemStatus.ManualStep
            => "需要我参与处理；尚未验证",
        _ => "尚无本轮核验结果；不要当作已安装",
    };
}
