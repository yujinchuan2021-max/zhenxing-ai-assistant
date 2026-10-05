using System.Text.RegularExpressions;
using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Services.ToolFlows;

internal enum ToolFlowRequirementKind { ExactTarget, ArchiveExtraction, SevenZipCli, SevenZipApi, SevenZipCliAndApi }

internal sealed record ToolFlowRequirement(ToolFlowRequirementKind Kind, string? Version = null);

/// <summary>Evidence describes the actual installed provider, never the requested replacement.</summary>
internal sealed record InstalledToolEvidence(string TargetKey, string Name, string? ExecutablePath = null,
    string? Version = null, bool HasSevenZipCli = false, bool HasSevenZipApi = false);

/// <summary>Stable, path-free evidence in existing Verified events. Only the fixed provider is accepted.</summary>
internal static class ToolFlowReuseEvidence
{
    internal const string WinRarArchiveDetail = "[reused-tool:winrar:archive-extraction] Ordinary archive extraction is satisfied by existing WinRAR; 7-Zip was not installed.";

    internal static bool IsWinRarArchiveReuse(ToolFlowItem item, string? planText, string? detail)
        => detail == WinRarArchiveDetail && item.InstallTargetKey?.Trim().Equals("7zip", StringComparison.OrdinalIgnoreCase) == true
           && ToolFlowPreparationService.GetRequirement(item, planText).Kind == ToolFlowRequirementKind.ArchiveExtraction;

    internal static bool IsWinRarArchiveReuse(ToolFlowItem item, string? planText, ToolFlowInstallItemResult result)
        => result.Status == ToolFlowInstallItemStatus.ReusedInstalledTool
           && result.ExistingToolTargetKey == "winrar"
           && item.InstallTargetKey?.Trim().Equals("7zip", StringComparison.OrdinalIgnoreCase) == true
           && ToolFlowPreparationService.GetRequirement(item, planText).Kind == ToolFlowRequirementKind.ArchiveExtraction;
}

internal enum ToolFlowPreparationState
{
    PendingAutomatic, AlreadyInstalled, ReusedInstalledTool, NeedsUserAssist, DetectionFailed,
}

internal sealed record ToolFlowPreparationItem(ToolFlowItem Item, ToolFlowRequirement Requirement,
    ToolFlowPreparationState State, InstalledToolEvidence? ExistingTool = null)
{
    /// <summary>Only explicit fixed installation targets can be prepared automatically; probes do not grant this permission.</summary>
    public bool AutomaticInstallationAllowed { get; init; }
    /// <summary>False for purely manual items that have no supported read-only probe.</summary>
    public bool DetectionAttempted { get; init; }
    /// <summary>A fixed CLI target cannot satisfy an item explicitly selected as a desktop Agent.</summary>
    public string? VariantMismatchReason { get; init; }

    public bool IsSatisfied => State is ToolFlowPreparationState.AlreadyInstalled
        or ToolFlowPreparationState.ReusedInstalledTool;

    public string Message => VariantMismatchReason ?? (State switch
    {
        ToolFlowPreparationState.AlreadyInstalled => Format("AiFlow_ReuseDetected",
            "已检测到 {0}；无需重复准备。", "Detected {0}; no additional preparation is needed.", ExistingTool?.Name ?? Item.Name),
        ToolFlowPreparationState.ReusedInstalledTool => Format("AiFlow_ReuseArchive",
            "普通解压可复用现有 {0}；无需准备 {1}。", "Existing {0} can handle ordinary archive extraction; {1} is unnecessary.",
            ExistingTool?.Name ?? "", Item.Name),
        ToolFlowPreparationState.NeedsUserAssist => Text("AiFlow_ReuseNeedsUser",
            "此项需按方案由用户完成。", "Complete this item following the workflow instructions.")
            + (string.IsNullOrWhiteSpace(Item.ManualHint) ? "" : " " + Item.ManualHint.Trim()),
        ToolFlowPreparationState.DetectionFailed => Text("AiFlow_ReuseProbeFailed",
            "安装状态未能确认；需重新检测，不能据此开始安装。", "Installation status could not be confirmed; check again before installing.")
            + (string.IsNullOrWhiteSpace(Item.ManualHint) ? "" : " " + Item.ManualHint.Trim()),
        _ => Text("AiFlow_ReusePending", "待准备：未检测到满足方案要求的软件。",
            "Preparation needed: no installed tool satisfying the workflow requirements was detected."),
    });

    private static string Text(string key, string zh, string en) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? en : zh);
    private static string Format(string key, string zh, string en, params object[] args)
        => string.Format(Text(key, zh, en), args);
}

internal sealed record ToolFlowPreparation(IReadOnlyList<ToolFlowPreparationItem> Rows)
{
    // Do not replace the proposal or selection's Items with this presentation subset.
    public IReadOnlyList<ToolFlowItem> AllItems => Rows.Select(x => x.Item).ToArray();
    public IReadOnlyList<ToolFlowItem> ConfirmationItems => Rows.Where(x => !x.IsSatisfied).Select(x => x.Item).ToArray();
    public IReadOnlyList<ToolFlowPreparationItem> DetectedItems => Rows.Where(x => x.IsSatisfied).ToArray();
    public IReadOnlyList<ToolFlowPreparationItem> PendingItems => Rows.Where(x => x.State == ToolFlowPreparationState.PendingAutomatic).ToArray();
    public IReadOnlyList<ToolFlowPreparationItem> NeedsUserAssist => Rows.Where(x => x.State == ToolFlowPreparationState.NeedsUserAssist).ToArray();
}

/// <summary>Read-only preflight shared by review and execution. Tests supply evidence without probing the OS.</summary>
internal sealed class ToolFlowPreparationService
{
    private readonly HashSet<string> _automaticTargets;
    private readonly HashSet<string> _detectableTargets;
    private readonly Func<string, CancellationToken, Task<InstalledToolEvidence?>> _probe;

    public ToolFlowPreparationService() : this(SystemInstaller.KnownTargets,
        ToolFlowInstalledToolProbe.ProbeAsync) { }

    internal ToolFlowPreparationService(IEnumerable<string> knownTargets,
        Func<string, CancellationToken, Task<InstalledToolEvidence?>> probe,
        IEnumerable<string>? detectableTargets = null)
    {
        ArgumentNullException.ThrowIfNull(knownTargets);
        _automaticTargets = new(knownTargets, StringComparer.OrdinalIgnoreCase);
        _detectableTargets = new(detectableTargets ?? _automaticTargets.Concat(["ffmpeg", "winrar"]), StringComparer.OrdinalIgnoreCase);
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async Task<ToolFlowPreparation> PrepareAsync(IReadOnlyList<ToolFlowItem> items,
        string? planText = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var rows = new List<ToolFlowPreparationItem>(items.Count);
        // A cache is scoped to this read-only snapshot, never reused for the runner's later checks.
        var cache = new Dictionary<string, InstalledToolEvidence?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(await EvaluateCoreAsync(item, planText, ProbeOnceAsync, cancellationToken));
        }
        return new(rows.AsReadOnly());

        async Task<InstalledToolEvidence?> ProbeOnceAsync(string key, CancellationToken ct)
        {
            if (cache.TryGetValue(key, out var value)) return value;
            return cache[key] = await _probe(key, ct);
        }
    }

    public Task<ToolFlowPreparationItem> EvaluateAsync(ToolFlowItem item,
        string? planText = null, CancellationToken cancellationToken = default)
        => EvaluateCoreAsync(item, planText, _probe, cancellationToken);

    private async Task<ToolFlowPreparationItem> EvaluateCoreAsync(ToolFlowItem item, string? planText,
        Func<string, CancellationToken, Task<InstalledToolEvidence?>> probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        var requirement = GetRequirement(item, planText);
        if (ToolFlowAgentVariantPolicy.GetMismatchReason(item) is { } mismatch)
            return new(item, requirement, ToolFlowPreparationState.NeedsUserAssist)
            {
                VariantMismatchReason = mismatch,
                AutomaticInstallationAllowed = false,
                DetectionAttempted = false,
            };
        if (ToolFlowItemSemantics.NeedsUserSetup(item) || ToolFlowItemSemantics.HasConflictingTarget(item))
            return new(item, requirement, ToolFlowPreparationState.NeedsUserAssist)
            { AutomaticInstallationAllowed = false, DetectionAttempted = false };
        var explicitKey = (item.InstallTargetKey ?? "").Trim().ToLowerInvariant();
        var canInstall = _automaticTargets.Contains(explicitKey);
        var readOnlyTarget = ToolFlowItemSemantics.TryGetReadOnlyTarget(item);
        var key = _detectableTargets.Contains(explicitKey) ? explicitKey
            : explicitKey.Length == 0 ? readOnlyTarget ?? LegacyDetectionKey(item) : null;
        if (key is null || !_detectableTargets.Contains(key))
            return Row(ToolFlowPreparationState.NeedsUserAssist, detectionAttempted: false);
        try
        {
            var exact = await probe(key, ct);
            ct.ThrowIfCancellationRequested();
            if (exact is not null && exact.TargetKey.Equals(key, StringComparison.OrdinalIgnoreCase)
                && (readOnlyTarget is null || !string.IsNullOrWhiteSpace(exact.ExecutablePath) && Path.IsPathFullyQualified(exact.ExecutablePath))
                && (key != "ffmpeg" || ToolFlowInstalledToolProbe.IsFfmpegExecutablePath(exact.ExecutablePath))
                && Satisfies(exact, requirement))
                return Row(ToolFlowPreparationState.AlreadyInstalled, exact);
            if (key == "7zip" && requirement.Kind == ToolFlowRequirementKind.ArchiveExtraction)
            {
                var archive = await probe("winrar", ct);
                ct.ThrowIfCancellationRequested();
                if (archive is not null && archive.TargetKey.Equals("winrar", StringComparison.OrdinalIgnoreCase))
                    return Row(ToolFlowPreparationState.ReusedInstalledTool, archive);
            }
            return Row(canInstall ? ToolFlowPreparationState.PendingAutomatic : ToolFlowPreparationState.NeedsUserAssist);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Row(ToolFlowPreparationState.DetectionFailed); }

        ToolFlowPreparationItem Row(ToolFlowPreparationState state, InstalledToolEvidence? tool = null,
            bool detectionAttempted = true) => new(item, requirement, state, tool)
        {
            AutomaticInstallationAllowed = canInstall,
            DetectionAttempted = detectionAttempted,
        };
    }

    // Compatibility only for the exact product name, optionally followed by one explanatory pair of parentheses.
    // Do not interpret URLs, commands, arbitrary paths, hints or mentions elsewhere in the plan.
    private static string? LegacyDetectionKey(ToolFlowItem item)
        => item.Kind.Trim().Equals("software", StringComparison.OrdinalIgnoreCase)
           && Regex.IsMatch(item.Name.Trim(), @"\AFFmpeg(?:\.exe| CLI)?(?:\s*(?:（[^（）()]*）|\([^（）()]*\)))?\z",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "ffmpeg" : null;

    internal static ToolFlowRequirement GetRequirement(ToolFlowItem item, string? planText = null)
    {
        if (!string.Equals(item.InstallTargetKey?.Trim(), "7zip", StringComparison.OrdinalIgnoreCase))
            return new(ToolFlowRequirementKind.ExactTarget);
        var own = string.Join("\n", item.Name, item.Kind, item.ManualHint);
        // Examine only archive-specific plan lines; Codex CLI and model APIs elsewhere are unrelated.
        var related = string.Join("\n", (planText ?? "").Split('\n').Where(line =>
            Regex.IsMatch(line, @"7[ -]?zip|\b7z(?:a|r)?(?:\.exe|\.dll)?\b", RegexOptions.IgnoreCase)));
        var text = own + "\n" + related;
        // The schema's Version is descriptive. Bind it only when the plan explicitly demands that version.
        var pinnedVersion = Regex.IsMatch(text,
            @"(?:必须|指定|要求|需要).{0,50}版本|(?:must|required?|exact|pinned|specific).{0,50}\bversion\b|\bversion\b.{0,50}(?:required|only)",
            RegexOptions.IgnoreCase);
        var version = pinnedVersion ? RequiredVersion(item.Version) : null;
        var api = Regex.IsMatch(text, @"7z\.dll|7[ -]?zip.{0,80}\b(?:API|SDK)\b|\b(?:API|SDK)\b.{0,80}7[ -]?zip", RegexOptions.IgnoreCase);
        var cli = Regex.IsMatch(text, @"\b7z(?:a|r)?\.exe\b|\b7z(?:a|r)?\s+(?:x|e|a|l|t)\b|\bCLI\b|命令行|命令接口|command.line", RegexOptions.IgnoreCase);
        if (api) return new(cli ? ToolFlowRequirementKind.SevenZipCliAndApi : ToolFlowRequirementKind.SevenZipApi, version);
        if (cli)
            return new(ToolFlowRequirementKind.SevenZipCli, version);
        if (pinnedVersion || Regex.IsMatch(text,
            @"(?:必须|只能|专用|指定|must|only|exact|specific).{0,40}7[ -]?zip|7[ -]?zip.{0,40}(?:必须|专用|接口|only|required)|(?:创建|生成|压缩为|create).{0,30}\.7z",
            RegexOptions.IgnoreCase))
            return new(ToolFlowRequirementKind.ExactTarget, version);
        return new(ToolFlowRequirementKind.ArchiveExtraction);
    }

    private static bool Satisfies(InstalledToolEvidence tool, ToolFlowRequirement requirement)
        => (requirement.Version is null || SameVersion(requirement.Version, tool.Version))
           && (requirement.Kind is not (ToolFlowRequirementKind.SevenZipCli or ToolFlowRequirementKind.SevenZipCliAndApi) || tool.HasSevenZipCli)
           && (requirement.Kind is not (ToolFlowRequirementKind.SevenZipApi or ToolFlowRequirementKind.SevenZipCliAndApi) || tool.HasSevenZipApi);

    private static string? RequiredVersion(string? value)
    {
        var version = (value ?? "").Trim();
        return version.Length == 0 || Regex.IsMatch(version,
            @"\A(?:latest|current|unknown|最新版?|待核(?:实|对)?|未知|不详)\z", RegexOptions.IgnoreCase) ? null : version;
    }

    private static bool SameVersion(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        var requested = expected.Trim().TrimStart('v', 'V');
        var found = actual.Trim().TrimStart('v', 'V');
        if (requested.Equals(found, StringComparison.OrdinalIgnoreCase)) return true;
        // OS executable versions often append zero build/revision components.
        return System.Version.TryParse(requested, out var a) && System.Version.TryParse(found, out var b)
            && a.Major == b.Major && a.Minor == b.Minor && Math.Max(0, a.Build) == Math.Max(0, b.Build)
            && Math.Max(0, a.Revision) == Math.Max(0, b.Revision);
    }
}
