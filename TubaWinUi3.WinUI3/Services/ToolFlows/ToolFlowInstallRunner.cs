using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>为安装过程提供可替换接口；生产实现只接受 SystemInstaller 的固定目标。</summary>
public interface IToolFlowInstaller
{
    IReadOnlyCollection<string> KnownTargets { get; }
    Task<bool> IsInstalledAsync(string targetKey, CancellationToken cancellationToken);
    Task InstallAsync(string targetKey, CancellationToken cancellationToken);
}

public enum ToolFlowInstallItemStatus
{
    ManualStep,
    AlreadyInstalled,
    Installed,
    Failed,
    ReusedInstalledTool,
}

public sealed record ToolFlowInstallItemResult(
    string ItemId,
    string Name,
    ToolFlowInstallItemStatus Status,
    string Message)
{
    /// <summary>Actual existing provider; neither local paths nor raw installer output are carried here.</summary>
    public string? ExistingToolName { get; init; }
    public string? ExistingToolTargetKey { get; init; }
    /// <summary>Local usage entry; never uploaded with installation analytics.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ToolFlowPostInstallDeliveryResult? Delivery { get; init; }
}

public sealed record ToolFlowInstallResult(IReadOnlyList<ToolFlowInstallItemResult> Items)
{
    /// <summary>用户要求停止后续项目；已开始的项目完成检查后，其真实结果仍保留。</summary>
    public bool WasCanceled { get; init; }
    public int InstalledCount => Items.Count(x => x.Status == ToolFlowInstallItemStatus.Installed);
    public int AlreadyInstalledCount => Items.Count(x => x.Status == ToolFlowInstallItemStatus.AlreadyInstalled);
    public int ReusedInstalledCount => Items.Count(x => x.Status == ToolFlowInstallItemStatus.ReusedInstalledTool);
    public int ManualStepCount => Items.Count(x => x.Status == ToolFlowInstallItemStatus.ManualStep);
    public int FailedCount => Items.Count(x => x.Status == ToolFlowInstallItemStatus.Failed);
}

/// <summary>执行器实际能观察的阶段，不包含安装器内部下载进度。</summary>
public enum ToolFlowInstallPhase
{
    Checking,
    Installing,
    Verifying,
    Delivering,
    ItemCompleted,
    Completed,
    Stopped,
}

public sealed record ToolFlowInstallProgress(
    string? ItemId,
    string Name,
    ToolFlowInstallPhase Phase,
    int CompletedCount,
    int TotalCount,
    ToolFlowInstallItemResult? ItemResult = null);

/// <summary>
/// 只在用户明确选定工具流并要求安装后由 UI 调用。逐项执行白名单安装目标，
/// 其他账号、素材和未知工具保留为用户手动步骤。下载由 SystemInstaller 内部处理，
/// 此处无法观察下载结果，因此不记录下载事件。
/// </summary>
public sealed class ToolFlowInstallRunner
{
    private readonly ToolFlowSelectionStore _store;
    private readonly IToolFlowInstaller _installer;
    private readonly ToolFlowPreparationService _preparation;
    private readonly Func<ToolFlowItem, ToolFlowInstallItemResult, CancellationToken,
        Task<ToolFlowPostInstallDeliveryResult>>? _deliver;

    public ToolFlowInstallRunner(ToolFlowSelectionStore store, IToolFlowInstaller? installer = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _installer = installer ?? new SystemToolFlowInstaller();
        // Fake installers must not resolve real software or write the user's desktop.
        _deliver = installer is null ? new ToolFlowPostInstallDelivery().DeliverAsync : null;
        _preparation = installer is null ? new ToolFlowPreparationService()
            : new ToolFlowPreparationService(installer.KnownTargets, async (key, ct) =>
                await installer.IsInstalledAsync(key, ct)
                    ? new InstalledToolEvidence(key, key, HasSevenZipCli: key == "7zip", HasSevenZipApi: key == "7zip")
                    : null);
    }

    internal ToolFlowInstallRunner(ToolFlowSelectionStore store, IToolFlowInstaller installer,
        ToolFlowPreparationService preparation,
        Func<ToolFlowItem, ToolFlowInstallItemResult, CancellationToken,
            Task<ToolFlowPostInstallDeliveryResult>>? delivery = null) : this(store, installer)
    {
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        _deliver = delivery;
    }

    public async Task<ToolFlowInstallResult> RunAsync(
        string submissionId,
        IProgress<ToolFlowInstallItemResult>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<ToolFlowInstallProgress>? stageProgress = null,
        IReadOnlyCollection<string>? itemIds = null)
    {
        var selection = _store.GetBySubmissionId(submissionId)
            ?? throw new InvalidOperationException(MiscTexts.T("请先明确选定并保存工具流，再开始安装。"));
        var supported = new HashSet<string>(_installer.KnownTargets, StringComparer.OrdinalIgnoreCase);
        var requested = itemIds is null ? null : new HashSet<string>(itemIds, StringComparer.Ordinal);
        if (requested is not null && requested.Any(id => !selection.Items.Any(item => item.ItemId == id)))
            throw new ArgumentException("Requested preparation item is not in the selected workflow.", nameof(itemIds));
        var items = OrderSelectedDependencies(requested is null ? selection.Items
            : selection.Items.Where(item => requested.Contains(item.ItemId)).ToList());
        var results = new List<ToolFlowInstallItemResult>(items.Count);

        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                ReportStage(null, ToolFlowInstallPhase.Stopped);
                return new ToolFlowInstallResult(results) { WasCanceled = true };
            }

            if (ToolFlowAgentVariantPolicy.GetMismatchReason(item) is { } mismatch)
            {
                RecordResult(new(item.ItemId, item.Name, ToolFlowInstallItemStatus.ManualStep, mismatch));
                continue;
            }

            // 「停止后续」不打断第三方安装器。已经开始的项目完成检测和持久记录后，
            // 下一项边界才响应停止，以免留下半安装状态或把取消误记为安装失败。
            var target = item.InstallTargetKey?.Trim();
            var canInstall = !string.IsNullOrEmpty(target) && supported.Contains(target);

            ToolFlowPreparationItem preparation;
            try
            {
                if (canInstall) ReportStage(item, ToolFlowInstallPhase.Checking);
                preparation = await _preparation.EvaluateAsync(item, selection.FlowText, CancellationToken.None);
                if (preparation.State == ToolFlowPreparationState.DetectionFailed)
                    throw new InvalidOperationException(preparation.Message);
            }
            catch (Exception ex)
            {
                string prefix = MiscTexts.T("安装前检测失败，未启动安装：");
                Append(item, ToolFlowEventKind.InstallFailed,
                    LocalizationService.L("AiFlow_PrecheckFailedDetail", "安装前检测失败，尚未启动安装；需重新检测。"));
                RecordResult(new(item.ItemId, item.Name, ToolFlowInstallItemStatus.Failed,
                    prefix + ex.Message));
                continue;
            }

            if (preparation.IsSatisfied)
            {
                var reused = preparation.State == ToolFlowPreparationState.ReusedInstalledTool;
                AppendVerified(item, reused ? ToolFlowReuseEvidence.WinRarArchiveDetail :
                    MiscTexts.T("安装前检测到目标已存在；本次未重复安装。"));
                await RecordReadyAsync(ToolFlowToolAccess.AccessItem(preparation), new(item.ItemId, item.Name, reused ? ToolFlowInstallItemStatus.ReusedInstalledTool
                    : ToolFlowInstallItemStatus.AlreadyInstalled, preparation.Message)
                {
                    ExistingToolName = preparation.ExistingTool?.Name,
                    ExistingToolTargetKey = preparation.ExistingTool?.TargetKey,
                });
                continue;
            }

            // Read-only detection (including legacy FFmpeg) may prove an existing
            // tool is ready, but it never authorises installing an unknown target.
            if (!canInstall || !preparation.AutomaticInstallationAllowed)
            {
                RecordResult(new(item.ItemId, item.Name, ToolFlowInstallItemStatus.ManualStep,
                    MiscTexts.T("此项没有受支持的自动安装目标，请按工具流说明由用户完成。")));
                continue;
            }

            bool installedAfterAttempt;
            try
            {
                // 不把安装器的文字返回值解释成成功；必须再次检测已安装状态。
                ReportStage(item, ToolFlowInstallPhase.Installing);
                await _installer.InstallAsync(target!, CancellationToken.None);
                ReportStage(item, ToolFlowInstallPhase.Verifying);
                var after = await _preparation.EvaluateAsync(item, selection.FlowText, CancellationToken.None);
                installedAfterAttempt = after.State == ToolFlowPreparationState.AlreadyInstalled;
            }
            catch (Exception ex)
            {
                var message = MiscTexts.T("安装或安装后验证失败，未能确认成功：") + ex.Message;
                Append(item, ToolFlowEventKind.InstallFailed, MiscTexts.T("安装过程或安装后验证失败，未确认成功。"));
                RecordResult(new(item.ItemId, item.Name, ToolFlowInstallItemStatus.Failed, message));
                continue;
            }

            if (installedAfterAttempt)
            {
                Append(item, ToolFlowEventKind.InstallSucceeded, MiscTexts.T("安装后检测到目标已存在。"));
                AppendVerified(item, MiscTexts.T("安装后验证通过。"));
                await RecordReadyAsync(item, new(item.ItemId, item.Name, ToolFlowInstallItemStatus.Installed,
                    MiscTexts.T("已安装并验证。")));
            }
            else
            {
                string message = MiscTexts.T("安装已执行，但安装后未检测到目标；需要用户排查。");
                Append(item, ToolFlowEventKind.InstallFailed, message);
                RecordResult(new(item.ItemId, item.Name, ToolFlowInstallItemStatus.Failed, message));
            }
        }

        ReportStage(null, ToolFlowInstallPhase.Completed);
        return new ToolFlowInstallResult(results);

        async Task RecordReadyAsync(ToolFlowItem item, ToolFlowInstallItemResult result)
        {
            if (_deliver is not null)
            {
                ReportStage(item, ToolFlowInstallPhase.Delivering);
                try
                {
                    result = result with { Delivery = await _deliver(item, result, CancellationToken.None) };
                }
                catch
                {
                    // A desktop or launch-entry problem must not invalidate a verified installation
                    // or prevent the rest of the confirmed plan from being prepared.
                    result = result with { Delivery = new(item.ItemId, item.Name,
                        ToolFlowPostInstallDeliveryKind.Failed,
                        MiscTexts.T("软件已具备，使用入口暂未准备好；可在工具入口中重试。")) };
                }
            }
            RecordResult(result);
        }

        void RecordResult(ToolFlowInstallItemResult result)
        {
            results.Add(result);
            progress?.Report(result);
            stageProgress?.Report(new ToolFlowInstallProgress(result.ItemId, result.Name,
                ToolFlowInstallPhase.ItemCompleted, results.Count, items.Count, result));
        }

        void AppendVerified(ToolFlowItem item, string detail)
        {
            // 每轮检查都是新事实；旧成功后曾失败的项目需要本轮 Verified 恢复最新状态。
            Append(item, ToolFlowEventKind.Verified, detail);
        }

        void ReportStage(ToolFlowItem? item, ToolFlowInstallPhase phase)
            => stageProgress?.Report(new ToolFlowInstallProgress(item?.ItemId, item?.Name ?? "",
                phase, results.Count, items.Count));

        void Append(ToolFlowItem item, ToolFlowEventKind kind, string detail)
            => _store.AppendEvent(submissionId, new ToolFlowExecutionEvent
            {
                Id = Guid.NewGuid().ToString("D"),
                ItemId = item.ItemId,
                Kind = kind,
                AtUtc = DateTimeOffset.UtcNow,
                Detail = detail,
            });
    }

    /// <summary>Reorder only explicitly selected items. Dependencies never add an installation target.</summary>
    internal static IReadOnlyList<ToolFlowItem> OrderSelectedDependencies(IReadOnlyList<ToolFlowItem> items)
    {
        var ordered = new List<ToolFlowItem>(items.Count);
        var visited = new HashSet<int>();
        for (var index = 0; index < items.Count; index++) Visit(index);
        return ordered;

        void Visit(int index)
        {
            if (!visited.Add(index)) return;
            foreach (var dependency in SystemInstaller.GetInstallDependencies(items[index].InstallTargetKey ?? ""))
                for (var candidate = 0; candidate < items.Count; candidate++)
                    if (string.Equals(items[candidate].InstallTargetKey?.Trim(), dependency, StringComparison.OrdinalIgnoreCase))
                        Visit(candidate);
            ordered.Add(items[index]);
        }
    }

    private sealed class SystemToolFlowInstaller : IToolFlowInstaller
    {
        public IReadOnlyCollection<string> KnownTargets { get; } =
            SystemInstaller.KnownTargets.ToArray();

        public Task<bool> IsInstalledAsync(string targetKey, CancellationToken cancellationToken)
            => SystemInstaller.IsInstalledAsync(targetKey, cancellationToken);

        public async Task InstallAsync(string targetKey, CancellationToken cancellationToken)
        {
            // SystemInstaller 的返回值是面向用户的说明文本；调用方只信后续检测。
            _ = await SystemInstaller.InstallAsync(targetKey, progress: null, ct: cancellationToken);
        }
    }
}
