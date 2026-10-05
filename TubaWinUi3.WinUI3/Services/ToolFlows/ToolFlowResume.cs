using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>
/// 已选工具流的逐项执行状态（「继续最近选定」界面用）。只由本地快照（执行事件 / 用户自报标记）
/// 与本轮 runner 结果推导：不探测系统、不安装、不上传、不生成新快照。
/// </summary>
public enum ToolFlowResumeItemState
{
    /// <summary>已检测/已安装（来自快照执行记录或本轮检测）。</summary>
    InstalledOrDetected,
    /// <summary>待自动处理（在应用内固定目标清单里、尚未验证完成）。</summary>
    PendingAutomatic,
    /// <summary>需要用户协助（账号、素材、付费等；提示来自保存时快照）。</summary>
    NeedsUserAssist,
    /// <summary>用户自报完成：只代表用户自述，应用未验证。</summary>
    UserReportedDone,
    /// <summary>上次未成功（含下一步建议）。</summary>
    Failed,
}

public sealed record ToolFlowResumeItemRow
{
    public required string ItemId { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required ToolFlowResumeItemState State { get; init; }
    /// <summary>逐项状态说明与下一步（失败项含来自快照的记录说明）。</summary>
    public required string StatusLine { get; init; }
    /// <summary>需要用户协助时的具体操作提示：来自保存时快照（旧快照可能为 null）。</summary>
    public string? ManualHint { get; init; }
    /// <summary>true = 属于「继续自动项」可处理的范围（固定目标清单内且尚未完成）。</summary>
    public bool CanContinueAutomatically { get; init; }
    /// <summary>Retains the Build call's fixed installation scope when a fresh check invalidates historical success.</summary>
    internal bool AutomaticInstallationAllowed { get; init; }
    /// <summary>A later read-only check may supersede an earlier probe, but never a failure from the actual runner.</summary>
    internal bool IsPreparationEvidence { get; init; }
    /// <summary>只有当前结果中该项唯一出现时才保留；用于区分本轮与历史证据。</summary>
    public ToolFlowInstallItemStatus? CurrentRunStatus { get; init; }
    /// <summary>Existing archive capability, distinct from installing the selected product.</summary>
    public bool IsExistingToolReuse { get; init; }
    public string? ExistingToolName { get; init; }
    public string? ExistingToolTargetKey { get; init; }
}

public sealed record ToolFlowResumeView(
    ToolFlowSelection Selection,
    IReadOnlyList<ToolFlowResumeItemRow> Rows,
    IReadOnlyList<ToolFlowResumeItemRow> PendingAutomaticItems,
    int InstalledOrDetectedCount,
    int UserReportedDoneCount,
    int FailedCount,
    int NeedsUserAssistCount);

/// <summary>
/// 「继续最近选定的 Godot 工具流」的窄模型：从本地快照计算逐项状态与可继续项。
/// 恢复与浏览不会安装任何软件（安装只在用户点「继续自动项」后由现有 runner 执行），
/// 不会重新提交分享（上传只发生在用户当初「选定此方案」时），也不会生成新的快照。
/// </summary>
public static class ToolFlowResume
{
    /// <summary>
    /// Godot 工具流的稳定识别前缀（**与当前界面语言无关**）：同时认既有中文快照与英文界面产生的快照，
    /// 避免英文快照丢失恢复入口、或误选更旧的中文快照。跨语言切换后识别依然有效。
    /// </summary>
    public static readonly string[] GodotFlowNamePrefixes = ["2D 游戏 · ", "2D game · "];

    public static bool IsGodotFlow(ToolFlowSelection? selection)
    {
        if (selection is null) return false;
        foreach (var prefix in GodotFlowNamePrefixes)
            if (selection.FlowName.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    public static ToolFlowResumeView Build(
        ToolFlowSelection selection,
        ToolFlowInstallResult? lastResult = null,
        IReadOnlyCollection<string>? knownTargets = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var supported = new HashSet<string>(
            knownTargets ?? SystemInstaller.KnownTargets, StringComparer.OrdinalIgnoreCase);

        var rows = new List<ToolFlowResumeItemRow>(selection.Items.Count);
        foreach (var item in selection.Items)
        {
            if (ToolFlowAgentVariantPolicy.GetMismatchReason(item) is { } mismatch)
            {
                rows.Add(VariantMismatch(item, mismatch));
                continue;
            }
            var mark = selection.ItemMarks.FirstOrDefault(
                m => m.ItemId == item.ItemId && m.Kind == ToolFlowItemMark.UserReportedDone);
            var lastEvent = selection.Events
                .Where(e => e.ItemId == item.ItemId)
                .OrderBy(e => e.AtUtc)
                .LastOrDefault();

            var runMatches = lastResult?.Items.Where(r => r.ItemId == item.ItemId).Take(2).ToArray() ?? [];
            var runStatus = runMatches.Length == 1 ? runMatches[0].Status : (ToolFlowInstallItemStatus?)null;
            var runMessage = runMatches.Length == 1 ? runMatches[0].Message : null;

            // A resource's own historical verification is not executable evidence.
            // Keep that legacy record only when no software target or current run is involved.
            var retainResourceRecord = string.IsNullOrWhiteSpace(item.InstallTargetKey) && runMatches.Length == 0 &&
                item.Kind.Trim().ToLowerInvariant() is "asset" or "assets" or "resource" or "resources";
            if (ToolFlowItemSemantics.NeedsUserSetup(item) && !retainResourceRecord || ToolFlowItemSemantics.HasConflictingTarget(item))
            {
                // Install evidence cannot verify access/configuration. Actual
                // failures still take precedence over older user declarations.
                var runFailed = runStatus == ToolFlowInstallItemStatus.Failed;
                var historicalFailure = (lastEvent?.Kind is ToolFlowEventKind.InstallFailed or ToolFlowEventKind.DownloadFailed) &&
                    (mark is null || mark.AtUtc <= lastEvent.AtUtc);
                rows.Add(new()
                {
                    ItemId = item.ItemId, Name = item.Name, Kind = item.Kind,
                    State = runFailed || historicalFailure ? ToolFlowResumeItemState.Failed
                        : mark is null ? ToolFlowResumeItemState.NeedsUserAssist : ToolFlowResumeItemState.UserReportedDone,
                    StatusLine = runFailed ? MiscTexts.T("本轮未成功：") + Short(runMessage, 160) + NextStep(false)
                        : historicalFailure ? MiscTexts.T("上次未成功：") + Short(lastEvent!.Detail, 160) + NextStep(false)
                        : mark is null ? ManualStep(item) : UserReported(mark),
                    ManualHint = item.ManualHint, CurrentRunStatus = runStatus,
                });
                continue;
            }

            var canAutomatic = !string.IsNullOrWhiteSpace(item.InstallTargetKey) &&
                               supported.Contains(item.InstallTargetKey!.Trim());

            ToolFlowResumeItemState state;
            string line;
            var reuse = runMatches.Length == 1 && ToolFlowReuseEvidence.IsWinRarArchiveReuse(item, selection.FlowText, runMatches[0]);
            var historicalReuse = runMatches.Length == 0 && lastEvent?.Kind == ToolFlowEventKind.Verified
                && ToolFlowReuseEvidence.IsWinRarArchiveReuse(item, selection.FlowText, lastEvent.Detail);
            if (reuse || historicalReuse)
            {
                state = ToolFlowResumeItemState.InstalledOrDetected;
                line = new ToolFlowPreparationItem(item, ToolFlowPreparationService.GetRequirement(item, selection.FlowText),
                    ToolFlowPreparationState.ReusedInstalledTool, new InstalledToolEvidence("winrar", "WinRAR")).Message
                    + (historicalReuse ? ReuseHistoricalNotice() : "");
            }
            else if (runStatus is ToolFlowInstallItemStatus.Installed)
            {
                state = ToolFlowResumeItemState.InstalledOrDetected;
                line = MiscTexts.T("已安装并验证（本轮）；账号、配置与创作进度仍未验证。");
            }
            else if (runStatus is ToolFlowInstallItemStatus.AlreadyInstalled)
            {
                state = ToolFlowResumeItemState.InstalledOrDetected;
                line = MiscTexts.T("已检测到安装（本轮）；未重复安装。");
            }
            // 明确的本轮失败必须优先于历史验证和旧的用户标记。
            else if (runStatus is ToolFlowInstallItemStatus.Failed)
            {
                state = ToolFlowResumeItemState.Failed;
                line = MiscTexts.T("本轮未成功：") + Short(runMessage, 160) + NextStep(canAutomatic);
            }
            else if (runStatus is ToolFlowInstallItemStatus.ManualStep)
            {
                state = mark is null ? ToolFlowResumeItemState.NeedsUserAssist : ToolFlowResumeItemState.UserReportedDone;
                line = mark is null ? ManualStep(item) : UserReported(mark);
            }
            else if (runMatches.Length > 1)
            {
                // 相互冲突的本轮结果不能靠较旧的成功记录解释成完成。
                state = canAutomatic ? ToolFlowResumeItemState.PendingAutomatic : ToolFlowResumeItemState.NeedsUserAssist;
                line = MiscTexts.T("本轮结果不明确，尚未确认完成。");
            }
            else if (runStatus is ToolFlowInstallItemStatus.ReusedInstalledTool ||
                lastEvent?.Kind == ToolFlowEventKind.Verified && lastEvent.Detail.StartsWith("[reused-tool:", StringComparison.Ordinal))
            {
                state = canAutomatic ? ToolFlowResumeItemState.PendingAutomatic : ToolFlowResumeItemState.NeedsUserAssist;
                line = MiscTexts.T("本轮结果不明确，尚未确认完成。");
            }
            else if (lastEvent?.Kind == ToolFlowEventKind.Verified)
            {
                state = ToolFlowResumeItemState.InstalledOrDetected;
                line = MiscTexts.T("已安装并验证（本机执行记录 ") + Local(lastEvent.AtUtc) + MiscTexts.T("）。");
            }
            else if ((lastEvent?.Kind is ToolFlowEventKind.InstallFailed or ToolFlowEventKind.DownloadFailed) &&
                     (mark is null || mark.AtUtc <= lastEvent.AtUtc))
            {
                state = ToolFlowResumeItemState.Failed;
                line = MiscTexts.T("上次未成功：") + Short(lastEvent.Detail, 160) + NextStep(canAutomatic);
            }
            else if (mark is not null)
            {
                state = ToolFlowResumeItemState.UserReportedDone;
                line = UserReported(mark);
            }
            else if (canAutomatic)
            {
                state = ToolFlowResumeItemState.PendingAutomatic;
                line = MiscTexts.T("待自动处理：点「继续自动项」会先检测；已安装的项会跳过，不会重复安装。");
            }
            else
            {
                state = ToolFlowResumeItemState.NeedsUserAssist;
                line = ManualStep(item);
            }

            rows.Add(new ToolFlowResumeItemRow
            {
                ItemId = item.ItemId,
                Name = item.Name,
                Kind = item.Kind,
                State = state,
                StatusLine = line,
                ManualHint = item.ManualHint,
                AutomaticInstallationAllowed = canAutomatic,
                CanContinueAutomatically = canAutomatic &&
                    (state is ToolFlowResumeItemState.PendingAutomatic or ToolFlowResumeItemState.Failed),
                CurrentRunStatus = runStatus,
                IsExistingToolReuse = reuse || historicalReuse,
                ExistingToolName = reuse || historicalReuse ? "WinRAR" : null,
                ExistingToolTargetKey = reuse || historicalReuse ? "winrar" : null,
            });
        }

        // 保持旧字段名兼容调用方；这里也包含需要重新检测/重试的自动失败项。
        var pending = rows.Where(r => r.CanContinueAutomatically).ToArray();
        return new ToolFlowResumeView(
            selection,
            rows,
            pending,
            rows.Count(r => r.State == ToolFlowResumeItemState.InstalledOrDetected),
            rows.Count(r => r.State == ToolFlowResumeItemState.UserReportedDone),
            rows.Count(r => r.State == ToolFlowResumeItemState.Failed),
            rows.Count(r => r.State == ToolFlowResumeItemState.NeedsUserAssist));
    }

    /// <summary>Pure merge of fresh read-only evidence. Keeps the full selection and does not persist or install.</summary>
    internal static ToolFlowResumeView ApplyPreparation(ToolFlowResumeView view, ToolFlowPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(preparation);
        var rows = view.Rows.Select(row =>
        {
            var item = view.Selection.Items.SingleOrDefault(x => x.ItemId == row.ItemId);
            if (item is null) return row;
            // Historical success, user marks and stale/malformed probe evidence
            // cannot turn the selected desktop variant into a verified CLI item.
            if (ToolFlowAgentVariantPolicy.GetMismatchReason(item) is { } mismatch)
                return VariantMismatch(item, mismatch);
            if (ToolFlowItemSemantics.NeedsUserSetup(item) || ToolFlowItemSemantics.HasConflictingTarget(item)) return row;
            var matches = preparation.Rows.Where(x => x.Item.ItemId == row.ItemId).Take(2).ToArray();
            if (matches.Length != 1) return row;
            var evidence = matches[0];
            // Evidence from another or modified proposal must not overwrite this snapshot.
            if (item != evidence.Item) return row;
            // A cached check must never hide the actual failure returned by this run.
            if (row.CurrentRunStatus == ToolFlowInstallItemStatus.Failed && !row.IsPreparationEvidence) return row;
            if (evidence.IsSatisfied)
                return row with
                {
                    State = ToolFlowResumeItemState.InstalledOrDetected,
                    StatusLine = evidence.Message, CanContinueAutomatically = false,
                    IsPreparationEvidence = true,
                    CurrentRunStatus = evidence.State == ToolFlowPreparationState.ReusedInstalledTool
                        ? ToolFlowInstallItemStatus.ReusedInstalledTool : ToolFlowInstallItemStatus.AlreadyInstalled,
                    IsExistingToolReuse = evidence.State == ToolFlowPreparationState.ReusedInstalledTool,
                    ExistingToolName = evidence.ExistingTool?.Name, ExistingToolTargetKey = evidence.ExistingTool?.TargetKey,
                };
            // An unsupported manual item was not checked. Keep its history and exact instructions.
            if (evidence.State == ToolFlowPreparationState.NeedsUserAssist)
            {
                if (!evidence.DetectionAttempted && (row.State is ToolFlowResumeItemState.InstalledOrDetected
                    or ToolFlowResumeItemState.UserReportedDone or ToolFlowResumeItemState.Failed))
                    return row;
                if (row.State == ToolFlowResumeItemState.UserReportedDone) return row;
            }
            var canAutomatic = row.AutomaticInstallationAllowed && evidence.AutomaticInstallationAllowed;
            return row with
            {
                State = evidence.State == ToolFlowPreparationState.NeedsUserAssist ? ToolFlowResumeItemState.NeedsUserAssist
                    : evidence.State == ToolFlowPreparationState.DetectionFailed ? ToolFlowResumeItemState.Failed
                    : canAutomatic ? ToolFlowResumeItemState.PendingAutomatic : ToolFlowResumeItemState.NeedsUserAssist,
                StatusLine = evidence.State == ToolFlowPreparationState.NeedsUserAssist ? ManualStep(item) : evidence.Message,
                CanContinueAutomatically = canAutomatic && (evidence.State is ToolFlowPreparationState.PendingAutomatic
                    or ToolFlowPreparationState.DetectionFailed),
                IsPreparationEvidence = true,
                CurrentRunStatus = evidence.State == ToolFlowPreparationState.NeedsUserAssist ? ToolFlowInstallItemStatus.ManualStep
                    : evidence.State == ToolFlowPreparationState.DetectionFailed ? ToolFlowInstallItemStatus.Failed : null,
                IsExistingToolReuse = false, ExistingToolName = null, ExistingToolTargetKey = null,
            };
        }).ToArray();
        return new(view.Selection, rows, rows.Where(x => x.CanContinueAutomatically).ToArray(),
            rows.Count(x => x.State == ToolFlowResumeItemState.InstalledOrDetected),
            rows.Count(x => x.State == ToolFlowResumeItemState.UserReportedDone),
            rows.Count(x => x.State == ToolFlowResumeItemState.Failed),
            rows.Count(x => x.State == ToolFlowResumeItemState.NeedsUserAssist));
    }

    private static ToolFlowResumeItemRow VariantMismatch(ToolFlowItem item, string reason) => new()
    {
        ItemId = item.ItemId, Name = item.Name, Kind = item.Kind,
        State = ToolFlowResumeItemState.NeedsUserAssist,
        StatusLine = reason,
        // Project the correction into the current-step hint without changing the
        // saved snapshot. Preserve its optional marker and other explicit scope.
        ManualHint = reason + (string.IsNullOrWhiteSpace(item.ManualHint) ? "" : "\n" + item.ManualHint),
        AutomaticInstallationAllowed = false,
        CanContinueAutomatically = false,
        CurrentRunStatus = ToolFlowInstallItemStatus.ManualStep,
    };

    private static string ReuseHistoricalNotice() => LocalizationService.L("AiFlow_ReuseHistorical",
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage
            ? " (Latest local evidence; not checked again in this view.)" : "（最新本机复用记录；本次展示尚未重新检测。）");

    /// <summary>状态的中文标签（界面统一口径；失败项明确写「未成功」而不是「待处理」）。</summary>
    public static string Label(ToolFlowResumeItemState state) => state switch
    {
        ToolFlowResumeItemState.InstalledOrDetected => MiscTexts.T("已检测/已安装"),
        ToolFlowResumeItemState.PendingAutomatic => MiscTexts.T("待自动处理"),
        ToolFlowResumeItemState.NeedsUserAssist => MiscTexts.T("需要你协助"),
        ToolFlowResumeItemState.UserReportedDone => MiscTexts.T("用户自报完成（未验证）"),
        ToolFlowResumeItemState.Failed => MiscTexts.T("未成功，需处理"),
        _ => MiscTexts.T("未知"),
    };

    private static string Local(DateTimeOffset atUtc) =>
        atUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string UserReported(ToolFlowItemMark mark) =>
        MiscTexts.T("你已标记完成（应用未验证、安装状态未检测；") + Local(mark.AtUtc) + MiscTexts.T("）。");

    private static string ManualStep(ToolFlowItem item) => string.IsNullOrWhiteSpace(item.ManualHint)
        ? MiscTexts.T("需要你协助（账号、素材或付费步骤）；本快照没有更细的提示，可回看完整方案。")
        : MiscTexts.T("需要你协助（保存时的提示）：") + item.ManualHint!.Trim();

    private static string NextStep(bool canAutomatic) => canAutomatic
        ? MiscTexts.T(" 下一步：可重新点「继续自动项」重试，或按方案说明手动处理。")
        : MiscTexts.T(" 下一步：请按方案说明手动处理。");

    private static string Short(string? text, int max)
    {
        var value = (text ?? "").Trim().ReplaceLineEndings(" ");
        if (value.Length == 0) value = MiscTexts.T("（没有更多记录说明）");
        return value.Length <= max ? value : value[..max] + "…";
    }
}
