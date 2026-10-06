namespace TubaWinUi3.Services.CloudTools;

/// <summary>Read-only selection; never installs, opens a vendor program or performs network I/O.</summary>
public static class CloudToolBatchUpdatePlanner
{
    public static CloudToolBatchUpdatePlan CreatePlan(IEnumerable<CloudToolState> states,
        IEnumerable<CloudToolDefinition> catalog, string architecture,
        Func<string, bool> hasValidInstalledEntry)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hasValidInstalledEntry);
        if (architecture is not ("x86" or "x64" or "arm64"))
            throw new ArgumentException("不支持的客户端架构。", nameof(architecture));

        var definitions = catalog.GroupBy(tool => tool.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var candidates = new List<CloudToolBatchUpdateCandidate>();
        var skipped = new List<CloudToolBatchUpdateItemResult>();
        // Catalog-only entries are not software on this computer and do not inflate the batch.
        foreach (var group in states.Where(state => state.IsManaged || state.IsInstalled)
                     .GroupBy(state => state.Id, StringComparer.Ordinal))
        {
            var state = group.First();
            CloudToolBatchUpdateSkipReason reason;
            CloudToolPackage? package = null;
            if (group.Count() != 1 || !CloudToolValidation.IsId(state.Id))
                reason = CloudToolBatchUpdateSkipReason.AmbiguousIdentity;
            else if (!state.IsManaged) reason = CloudToolBatchUpdateSkipReason.NotManaged;
            else if (!state.IsInstalled) reason = CloudToolBatchUpdateSkipReason.NotInstalled;
            else if (state.IsBusy) reason = CloudToolBatchUpdateSkipReason.Busy;
            else if (state.PendingUpdate || state.Status == CloudToolStatus.PendingUpdate)
                reason = CloudToolBatchUpdateSkipReason.PendingUpdate;
            else if (!hasValidInstalledEntry(state.Id)) reason = CloudToolBatchUpdateSkipReason.MissingEntry;
            else if (!definitions.TryGetValue(state.Id, out var matching))
                reason = CloudToolBatchUpdateSkipReason.CatalogMissing;
            else if (matching.Length != 1) reason = CloudToolBatchUpdateSkipReason.AmbiguousIdentity;
            else if (string.IsNullOrWhiteSpace(state.AvailableVersion) ||
                     !string.Equals(matching[0].Version, state.AvailableVersion, StringComparison.Ordinal))
                reason = CloudToolBatchUpdateSkipReason.CatalogChanged;
            else if (!NeedsUpdate(state)) reason = CloudToolBatchUpdateSkipReason.AlreadyCurrent;
            else
            {
                package = CloudToolValidation.SelectPackage(matching[0], architecture);
                reason = ValidPackage(package) ? CloudToolBatchUpdateSkipReason.None
                    : CloudToolBatchUpdateSkipReason.UnsupportedPackage;
            }
            if (reason != CloudToolBatchUpdateSkipReason.None)
            {
                skipped.Add(new(state.Id, state.Name, state.Version, state.AvailableVersion,
                    CloudToolBatchUpdateOutcome.Skipped, reason, SkipMessage(reason), state));
                continue;
            }
            candidates.Add(new(state.Id, state.Name, state.Version, state.AvailableVersion,
                package!.Sha256.ToLowerInvariant(), package.SizeBytes, package.Architecture));
        }
        return new(architecture, candidates.AsReadOnly(), skipped.AsReadOnly());
    }

    internal static bool NeedsUpdate(CloudToolState state) => state.HasUpdate ||
        !string.Equals(state.AvailableVersion, state.Version, StringComparison.Ordinal);

    private static bool ValidPackage(CloudToolPackage? package) => package is { Kind: "portable-zip" } &&
        package.SizeBytes is > 0 and <= CloudToolValidation.MaxPackageBytes &&
        CloudToolValidation.IsPackageUrl(package.Url) && CloudToolValidation.IsSha256(package.Sha256) &&
        CloudToolValidation.IsToolEntryPoint(package.EntryPoint) && package.Mirrors is not null &&
        package.Mirrors.Length <= CloudToolValidation.MaxPackageMirrors &&
        package.Mirrors.All(CloudToolValidation.IsPackageUrl);

    internal static string SkipMessage(CloudToolBatchUpdateSkipReason reason) => reason switch
    {
        CloudToolBatchUpdateSkipReason.NotManaged => "此工具不由枕星管理，请按原厂方式更新。",
        CloudToolBatchUpdateSkipReason.NotInstalled => "没有本应用的可用安装入口。",
        CloudToolBatchUpdateSkipReason.MissingEntry => "本机入口未通过检查，未开始更新。",
        CloudToolBatchUpdateSkipReason.Busy => "此工具正在处理其他任务。",
        CloudToolBatchUpdateSkipReason.PendingUpdate => "已有更新等待工具退出后应用。",
        CloudToolBatchUpdateSkipReason.AlreadyCurrent => "此工具已是当前目录中的版本。",
        CloudToolBatchUpdateSkipReason.CatalogMissing => "当前目录中没有此工具的精确标识。",
        CloudToolBatchUpdateSkipReason.CatalogChanged => "目录或可用版本已经变化，请重新确认更新列表。",
        CloudToolBatchUpdateSkipReason.UnsupportedPackage => "当前架构没有可验证的便携更新包，请按原厂方式获取。",
        CloudToolBatchUpdateSkipReason.AmbiguousIdentity => "工具标识重复或无效，未选择任何近似条目。",
        CloudToolBatchUpdateSkipReason.Cancelled => "未继续此项更新；已完成的更新保留。",
        _ => ""
    };
}
