namespace TubaWinUi3.Models;

public sealed class UpdateInfo
{
    public required string Version { get; init; }
    public required string HtmlUrl { get; init; }
    public string? Body { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public required List<UpdateAsset> Assets { get; init; }
}

public sealed class UpdateAsset
{
    public required string Name { get; init; }
    public required string BrowserDownloadUrl { get; init; }
    public string? OriginalDownloadUrl { get; init; }
    public long Size { get; init; }
    public string? ContentType { get; init; }
    public string? GitCodeDownloadUrl { get; set; }

    /// <summary>自有更新通道（zhenxingai.com 稳定版清单）提供的 SHA-256（小写十六进制，64 字符）。
    /// 上游通道的资产里没有该字段（保持 null）。</summary>
    public string? Sha256 { get; init; }
}

/// <summary>自有更新通道的检查结果。Failed 用于“清单无效/网络失败”，不得谎报“已是最新”。</summary>
public enum UpdateCheckStatus
{
    /// <summary>发现可自动下载的新版本（x64 便携 ZIP，校验字段齐全）。</summary>
    UpdateAvailable,

    /// <summary>发现新版本，但当前平台/安装形态不适用自动下载，应引导到官网手动下载。</summary>
    ManualDownload,

    /// <summary>已是最新版本。</summary>
    UpToDate,

    /// <summary>检查失败（网络不可用、清单缺失/无效等），Detail 说明原因。</summary>
    Failed
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, string? Detail);

public sealed class DownloadProgress
{
    public required long BytesReceived { get; init; }
    public required long TotalBytes { get; init; }
    public required double Percentage { get; init; }
    public required double SpeedMbps { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required TimeSpan? EstimatedRemaining { get; init; }
}

public enum UpdateState
{
    Checking,
    UpdateAvailable,
    NoUpdate,
    Downloading,
    ReadyToInstall,
    Error
}
