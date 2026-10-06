namespace TubaWinUi3.Services.CloudTools;

public enum CloudToolBatchUpdateOutcome
{
    Queued, InProgress, PendingUpdate, Succeeded, Failed, Skipped
}

public enum CloudToolBatchUpdateSkipReason
{
    None, NotManaged, NotInstalled, MissingEntry, Busy, PendingUpdate,
    AlreadyCurrent, CatalogMissing, CatalogChanged, UnsupportedPackage,
    AmbiguousIdentity, Cancelled
}

/// <summary>The exact version and original package approved by the user.</summary>
public sealed record CloudToolBatchUpdateCandidate(
    string Id, string Name, string InstalledVersion, string TargetVersion,
    string PackageSha256, long SizeBytes, string PackageArchitecture);

public sealed record CloudToolBatchUpdateItemResult(
    string Id, string Name, string InstalledVersion, string TargetVersion,
    CloudToolBatchUpdateOutcome Outcome,
    CloudToolBatchUpdateSkipReason SkipReason = CloudToolBatchUpdateSkipReason.None,
    string Message = "", CloudToolState? State = null);

public sealed record CloudToolBatchUpdatePlan(
    string Architecture, IReadOnlyList<CloudToolBatchUpdateCandidate> Candidates,
    IReadOnlyList<CloudToolBatchUpdateItemResult> Skipped)
{
    public int CandidateCount => Candidates.Count;
    public long TotalSizeBytes => Candidates.Sum(candidate => candidate.SizeBytes);
}

/// <summary>Only confirmed candidates contribute to batch totals; excluded tools are separate.</summary>
public sealed record CloudToolBatchUpdateSnapshot(
    Guid Id, bool IsRunning, bool CancellationRequested,
    IReadOnlyList<CloudToolBatchUpdateItemResult> Items,
    IReadOnlyList<CloudToolBatchUpdateItemResult> Excluded)
{
    public int TotalCount => Items.Count;
    public int CompletedCount => Items.Count(item => item.Outcome is not
        (CloudToolBatchUpdateOutcome.Queued or CloudToolBatchUpdateOutcome.InProgress));
    public int SucceededCount => Items.Count(item => item.Outcome == CloudToolBatchUpdateOutcome.Succeeded);
    public int FailedCount => Items.Count(item => item.Outcome == CloudToolBatchUpdateOutcome.Failed);
    public int PendingUpdateCount => Items.Count(item => item.Outcome == CloudToolBatchUpdateOutcome.PendingUpdate);
    public int SkippedCount => Items.Count(item => item.Outcome == CloudToolBatchUpdateOutcome.Skipped);
    public CloudToolBatchUpdateItemResult? Current => Items.FirstOrDefault(item =>
        item.Outcome == CloudToolBatchUpdateOutcome.InProgress);
    public double Progress => TotalCount == 0 ? 0 : Math.Min(100,
        100d * (CompletedCount + Math.Clamp(Current?.State?.Progress ?? 0, 0, 100) / 100d) / TotalCount);
}
