namespace TubaWinUi3.Services.CloudTools;

public sealed record CloudToolCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; }
    public string PublishedAt { get; init; } = "";
    public string MinClientVersion { get; init; } = "0.1.0";
    public CloudToolDefinition[] Tools { get; init; } = [];
}

public sealed record CloudToolDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string[] Categories { get; init; } = [];
    public string Description { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public string Homepage { get; init; } = "";
    public string LegacyPath { get; set; } = "";
    public int Order { get; set; }
    public CloudToolPackage[] Packages { get; init; } = [];
}

public sealed record CloudToolPackage
{
    public string Architecture { get; init; } = "";
    public string Url { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public string EntryPoint { get; init; } = "";
    public string Kind { get; init; } = "portable-zip";
}

public enum CloudToolStatus
{
    NotInstalled, Downloading, Installing, Installed, Updating,
    PendingUpdate, Removing, Failed, Unsupported
}

public sealed record CloudToolState(
    string Id, string Name, string Version, string AvailableVersion,
    CloudToolStatus Status, double Progress = 0, string? Error = null,
    string? EntryPath = null, bool PendingUpdate = false, bool IsManaged = false)
{
    // Only an operation initiated through this manager supplies this evidence.
    // A catalog/path read error alone must not appear as a user download task.
    public bool HasOperationActivity { get; init; }
    public bool IsInstalled => !string.IsNullOrEmpty(EntryPath);
    public bool IsBusy => Status is CloudToolStatus.Downloading or CloudToolStatus.Installing
        or CloudToolStatus.Updating or CloudToolStatus.Removing;
}

public sealed record CloudToolOperationResult(bool Success, string Message, CloudToolState? State);

// The receipt lives inside the installation directory. A directory swap therefore also
// commits its version/entry point, without a separately committed installed database.
internal sealed record CloudToolReceipt
{
    public string Owner { get; init; } = "zhenxing-cloud-tools-v1";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string EntryPoint { get; init; } = "";
    public string PackageUrl { get; init; } = "";
    public long PackageSize { get; init; }
    // Original package files only. User data carried across updates is never added
    // to this baseline, so subsequent updates can still recognize and preserve it.
    public Dictionary<string, string>? Files { get; init; }
}

internal sealed record CloudToolTransaction(string Id, string StageName, string BackupName);
