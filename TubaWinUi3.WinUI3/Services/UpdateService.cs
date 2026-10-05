using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Services;

public static class UpdateService
{
    /// <summary>
    /// 独立发行保持此兼容查询为 false，不请求或安装上游程序更新。
    /// 程序更新已改走自有通道（见 OwnUpdateManifestUrl / CheckForUpdateResultAsync），
    /// ToolsBundle/单工具更新闸门保持关闭；原上游程序更新与安装链已删除。
    /// </summary>
    public static bool UpstreamUpdatesEnabled => false;

    /// <summary>
    /// 【自有更新通道】枕星自有稳定版清单（只经 HTTPS、只接受本域 zhenxingai.com）。
    /// 客户端程序更新的唯一来源；清单由服务器侧在候选包确定后发布。
    /// </summary>
    public const string OwnUpdateManifestUrl = "https://zhenxingai.com/updates/stable.json";

    /// <summary>自有通道唯一允许的域名（HTTPS）。</summary>
    public const string OwnUpdateHost = "zhenxingai.com";

    /// <summary>官网下载页（无法自动更新/清单缺失时引导用户手动下载）。</summary>
    public const string OwnDownloadPageUrl = "https://zhenxingai.com/download";

    /// <summary>自有清单的 channel 值。</summary>
    private const string OwnChannelName = "stable";


    public static string CurrentArchitecture { get; } = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64"
    };

    private static HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        var client = ProxyService.CreateClient(timeout ?? TimeSpan.FromSeconds(30));
        if (!client.DefaultRequestHeaders.Contains("User-Agent"))
            client.DefaultRequestHeaders.Add("User-Agent", "TubaWinUi3-UpdateChecker");
        return client;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is not null ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 1, 0);
        }
    }

    /// <summary>
    /// 检查更新（自有通道）：发现可自动下载的新版时返回其 UpdateInfo；已是最新、仅需手动下载或失败时返回 null。
    /// 静默查新路径用本方法；需要失败原因（设置页手动检查）请用 <see cref="CheckForUpdateResultAsync"/>。
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var result = await CheckForUpdateResultAsync(ct);
        return result.Status == UpdateCheckStatus.UpdateAvailable ? result.Update : null;
    }

    /// <summary>
    /// 检查自有更新通道并返回完整状态：UpdateAvailable / ManualDownload / UpToDate / Failed。
    /// 清单无效或网络失败返回 Failed（带原因），不再谎报“已是最新版本”。
    /// </summary>
    public static Task<UpdateCheckResult> CheckForUpdateResultAsync(CancellationToken ct = default)
        => CheckOwnChannelAsync(FetchOwnManifestJsonAsync, ct);

    /// <summary>拉取自有稳定版清单原文（仅 HTTPS 本域 zhenxingai.com）。失败返回 null，由调用方判为 Failed。</summary>
    private static async Task<string?> FetchOwnManifestJsonAsync(CancellationToken ct)
    {
        try
        {
            using var client = CreateHttpClient(TimeSpan.FromSeconds(15));
            using var resp = await client.GetAsync(OwnUpdateManifestUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    /// <summary>
    /// 自有通道核心：清单获取函数可注入（离线窄测试用固定 JSON，不发起真实网络请求）。
    /// 获取失败 → Failed；否则解析并严格校验清单。
    /// </summary>
    internal static async Task<UpdateCheckResult> CheckOwnChannelAsync(
        Func<CancellationToken, Task<string?>> fetchManifest, CancellationToken ct = default)
    {
        string? json;
        try { json = await fetchManifest(ct); }
        catch { json = null; }

        if (string.IsNullOrWhiteSpace(json))
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, MiscTexts.T("无法获取更新清单（网络不可用或服务尚未就绪）"));

        return ParseOwnManifest(json);
    }

    /// <summary>
    /// 解析并严格校验自有稳定版清单（离线可测）。字段名按下发规范（camelCase，大小写不敏感容错）：
    /// channel / version / publishedAt / notesUrl / package{architecture, type, url, sizeBytes, sha256}。
    /// 只接受 zhenxingai.com 的 HTTPS 链接、x64 便携 ZIP、合法版本与安全文件名、sizeBytes&gt;0、64 位十六进制 SHA-256。
    /// 版本不高于当前程序集版本 → UpToDate；平台/形态不匹配 → ManualDownload（引导官网）；否则 → UpdateAvailable。
    /// </summary>
    internal static UpdateCheckResult ParseOwnManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return InvalidManifest(MiscTexts.T("更新清单格式无效"));

            var channel = TryGetProp(root, "channel", out var chEl) ? chEl.GetString() : null;
            if (!string.Equals(channel, OwnChannelName, StringComparison.OrdinalIgnoreCase))
                return InvalidManifest(MiscTexts.T("更新清单通道无效"));

            var versionStr = TryGetProp(root, "version", out var vEl) ? vEl.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(versionStr) || !Version.TryParse(versionStr, out var remoteVersion))
                return InvalidManifest(MiscTexts.T("更新清单版本号无效"));

            if (remoteVersion <= CurrentVersion)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, null);

            var publishedAt = TryGetProp(root, "publishedAt", out var pEl) && pEl.TryGetDateTimeOffset(out var pAt)
                ? pAt
                : DateTimeOffset.UtcNow;

            Uri? notesUri = null;
            if (TryGetProp(root, "notesUrl", out var nEl) &&
                TryValidateOwnHttpsUrl(nEl.GetString(), requireZip: false, out var parsedNotes, out _))
                notesUri = parsedNotes;

            if (!TryGetProp(root, "package", out var pkg) || pkg.ValueKind != JsonValueKind.Object)
                return InvalidManifest(MiscTexts.T("更新清单缺少 package"));

            var arch = TryGetProp(pkg, "architecture", out var aEl) ? aEl.GetString() : null;
            var type = TryGetProp(pkg, "type", out var tEl) ? tEl.GetString() : null;

            // 只自动处理与当前平台匹配的便携 ZIP；其他架构/安装形态引导到官网手动下载（避免无响应按钮）
            if (!string.Equals(arch, CurrentArchitecture, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(type, "portable-zip", StringComparison.OrdinalIgnoreCase))
            {
                var manual = new UpdateInfo
                {
                    Version = versionStr!,
                    HtmlUrl = notesUri?.ToString() ?? OwnDownloadPageUrl,
                    PublishedAt = publishedAt,
                    Assets = []
                };
                return new UpdateCheckResult(UpdateCheckStatus.ManualDownload, manual,
                    MiscTexts.TSub($"发现新版本 v{versionStr}，但当前平台（{CurrentArchitecture}）暂不支持自动下载便携更新包，请前往官网手动下载：{OwnDownloadPageUrl}"));
            }

            var urlStr = TryGetProp(pkg, "url", out var uEl) ? uEl.GetString() : null;
            if (!TryValidateOwnHttpsUrl(urlStr, requireZip: true, out var uri, out var fileName))
                return InvalidManifest(MiscTexts.T("更新包地址无效（只接受 zhenxingai.com 的 HTTPS .zip 直链）"));

            var size = TryGetProp(pkg, "sizeBytes", out var sEl) && sEl.TryGetInt64(out var s) ? s : 0;
            if (size <= 0) return InvalidManifest(MiscTexts.T("更新包大小无效"));

            var sha = TryGetProp(pkg, "sha256", out var shaEl) ? shaEl.GetString()?.Trim() : null;
            if (!IsValidSha256(sha)) return InvalidManifest(MiscTexts.T("更新包 SHA-256 无效"));

            var info = new UpdateInfo
            {
                Version = versionStr!,
                HtmlUrl = notesUri?.ToString() ?? OwnDownloadPageUrl,
                PublishedAt = publishedAt,
                Assets =
                [
                    new UpdateAsset
                    {
                        Name = fileName!,
                        BrowserDownloadUrl = uri!.ToString(),
                        OriginalDownloadUrl = uri.ToString(),
                        Size = size,
                        ContentType = "application/zip",
                        Sha256 = sha!.ToLowerInvariant()
                    }
                ]
            };
            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, info, null);
        }
        catch
        {
            return InvalidManifest(MiscTexts.T("更新清单解析失败"));
        }
    }

    private static UpdateCheckResult InvalidManifest(string detail)
        => new(UpdateCheckStatus.Failed, null, detail);

    /// <summary>在对象里按名称取属性（大小写不敏感容错，兼容下发布可能的大小写差异）。</summary>
    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty(name, out value)) return true;
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    /// <summary>校验自有通道链接：仅 HTTPS、仅本域、默认端口、无凭据、安全文件名（可选要求 .zip）。</summary>
    internal static bool TryValidateOwnHttpsUrl(string? url, bool requireZip, out Uri? uri, out string? fileName)
    {
        uri = null;
        fileName = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(parsed.Host, OwnUpdateHost, StringComparison.OrdinalIgnoreCase)) return false;
        if (!parsed.IsDefaultPort || !string.IsNullOrEmpty(parsed.UserInfo)) return false;

        var name = Path.GetFileName(parsed.AbsolutePath);
        if (string.IsNullOrEmpty(name) || name.Contains("..", StringComparison.Ordinal)) return false;
        if (!OwnFileNameRegex.IsMatch(name)) return false;
        if (requireZip && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;

        uri = parsed;
        fileName = name;
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex OwnFileNameRegex =
        new(@"^[A-Za-z0-9._\-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>SHA-256 是否为合法的 64 位十六进制字符串。</summary>
    internal static bool IsValidSha256(string? sha)
        => !string.IsNullOrEmpty(sha) && sha.Length == 64 && sha.All(Uri.IsHexDigit);

    /// <summary>
    /// 校验下载文件完整性：存在、非空、大小与清单一致、SHA-256 与清单一致。
    /// 任一不满足返回 false（截断/被替换/校验失败都算失败）。
    /// </summary>
    internal static async Task<bool> VerifyFileAsync(string filePath, long expectedSize, string expectedSha256, CancellationToken ct = default)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length <= 0) return false;
            if (expectedSize > 0 && info.Length != expectedSize) return false;
            if (!IsValidSha256(expectedSha256)) return false;

            await using var fs = File.OpenRead(filePath);
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct);
            return Convert.ToHexString(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string SkipVersionFilePath => ConfigManager.GetSkippedVersionPath();

    public static string? GetSkippedVersion()
    {
        try
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            return settings.Values["SkippedUpdateVersion"] as string;
        }
        catch { }

        try
        {
            if (File.Exists(SkipVersionFilePath))
                return File.ReadAllText(SkipVersionFilePath).Trim();
        }
        catch { }

        return null;
    }

    public static void SetSkippedVersion(string version)
    {
        try
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            settings.Values["SkippedUpdateVersion"] = version;
            return;
        }
        catch { }

        try
        {
            var dir = Path.GetDirectoryName(SkipVersionFilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SkipVersionFilePath, version);
        }
        catch { }
    }

    private static string? _pendingUpdateVersion;
    private static DownloadItem? _pendingDownloadItem;

    public static event Action<UpdateInfo>? UpdateDownloaded;

    public static string? PendingUpdateVersion => _pendingUpdateVersion;
    public static DownloadItem? PendingDownloadItem => _pendingDownloadItem;

    /// <summary>离线窄测试可临时改指向隔离目录；生产恒为 %TEMP%\TubaWinUi3_Update。</summary>
    internal static string? TempDirOverrideForTest { get; set; }

    private static string UpdateTempDir => TempDirOverrideForTest ?? Path.Combine(Path.GetTempPath(), "TubaWinUi3_Update");


    /// <summary>
    /// “更新是否已下载且绑定当前清单”（快速同步判定，不重算哈希）：只接受便携 ZIP 资产，
    /// 且验证记录必须与当前清单的文件名/大小/SHA-256 完全一致、文件仍在且长度相符。
    /// 旧记录、同版本换包、被替换的同大小文件都不会仅凭本方法得到“已就绪”（提示前还会重算哈希，见 IsUpdateReadyAsync）。
    /// </summary>
    public static bool IsUpdateAlreadyDownloaded(UpdateInfo update)
    {
        if (!TryGetBoundPortableZipAsset(update, out var asset)) return false;
        if (!TryGetVerifiedUpdateRecord(out var record) || record is null) return false;
        if (!RecordMatchesManifest(record, update, asset)) return false;
        var path = Path.Combine(UpdateTempDir, asset.Name);
        return File.Exists(path) && new FileInfo(path).Length == asset.Size;
    }

    /// <summary>
    /// “更新是否已下载且验证通过”完整判定（异步，重算当前文件 SHA-256）：
    /// 供“准备提示已通过校验”与启动恢复路径使用；下载后文件被替换（哪怕同大小）也会判为不可用。
    /// </summary>
    public static async Task<bool> IsUpdateReadyAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (!TryGetBoundPortableZipAsset(update, out var asset)) return false;
        if (!TryGetVerifiedUpdateRecord(out var record) || record is null) return false;
        if (!RecordMatchesManifest(record, update, asset)) return false;
        return await VerifyFileAsync(Path.Combine(UpdateTempDir, asset.Name), asset.Size, asset.Sha256, ct);
    }

    /// <summary>可自动更新的资产 = 第一个合法的便携 ZIP 条目（.zip 文件名 + 合法 SHA-256）。</summary>
    private static bool TryGetBoundPortableZipAsset(UpdateInfo update, out UpdateAsset asset)
    {
        asset = null!;
        var candidate = update.Assets.FirstOrDefault();
        if (candidate is null) return false;
        if (string.IsNullOrEmpty(candidate.Name) ||
            !candidate.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsValidSha256(candidate.Sha256)) return false;
        asset = candidate;
        return true;
    }

    /// <summary>验证记录必须绑定当前清单：版本 + 文件名 + 大小 + SHA-256 全一致（同版本换包会因后两项不符被拒）。</summary>
    private static bool RecordMatchesManifest(VerifiedUpdateRecord record, UpdateInfo update, UpdateAsset asset)
    {
        return string.Equals(record.Version, update.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(record.FileName, asset.Name, StringComparison.OrdinalIgnoreCase)
            && record.SizeBytes == asset.Size
            && string.Equals(record.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>下载后通过大小 + SHA-256 校验才写入的“已验证更新包”记录。</summary>
    internal sealed record VerifiedUpdateRecord(string Version, string FileName, long SizeBytes, string Sha256, DateTimeOffset VerifiedAt);

    private static string VerifiedUpdateRecordPath => Path.Combine(UpdateTempDir, "verified-update.json");

    /// <summary>下载并校验通过后落盘“已验证”记录（失败不影响文件本身，下次启动按“未验证”处理）。</summary>
    internal static void WriteVerifiedUpdateRecord(UpdateInfo update, string filePath)
    {
        try
        {
            Directory.CreateDirectory(UpdateTempDir);
            var record = new VerifiedUpdateRecord(
                update.Version,
                Path.GetFileName(filePath),
                new FileInfo(filePath).Length,
                update.Assets.FirstOrDefault()?.Sha256 ?? string.Empty,
                DateTimeOffset.Now);
            File.WriteAllText(VerifiedUpdateRecordPath, JsonSerializer.Serialize(record));
        }
        catch { }
    }

    /// <summary>
    /// 下载失败时清除可能残留的同名“已验证”记录：失败不留下可被误认的旧记录。
    /// </summary>
    internal static void InvalidateVerifiedUpdateRecordFor(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        try
        {
            if (!TryGetVerifiedUpdateRecord(out var record) || record is null) return;
            if (!string.Equals(record.FileName, fileName, StringComparison.OrdinalIgnoreCase)) return;
            File.Delete(VerifiedUpdateRecordPath);
        }
        catch { }
    }

    internal static bool TryGetVerifiedUpdateRecord(out VerifiedUpdateRecord? record)
    {
        record = null;
        try
        {
            if (!File.Exists(VerifiedUpdateRecordPath)) return false;
            record = JsonSerializer.Deserialize<VerifiedUpdateRecord>(File.ReadAllText(VerifiedUpdateRecordPath));
            return record is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 发现新版后由用户主动点击触发的下载（自有通道 · x64 便携 ZIP，直链来自清单）。
    /// 下载完成后校验大小与 SHA-256：失败则条目呈现失败状态且不写“已验证”记录；
    /// 校验通过才写记录，Banner 才能显示“更新已就绪”。
    /// </summary>
    public static DownloadItem? AutoDownloadUpdate(UpdateInfo update)
    {
        var asset = update.Assets.FirstOrDefault();
        if (asset is null || string.IsNullOrEmpty(asset.BrowserDownloadUrl) || !IsValidSha256(asset.Sha256))
            return null;

        var expectedSha = asset.Sha256!;
        var item = DownloadQueueService.Enqueue(
            displayName: MiscTexts.TSub($"软件更新 v{update.Version}"),
            downloadUrl: asset.BrowserDownloadUrl,
            destinationPath: UpdateTempDir,
            postProcessor: new DelegatePostProcessor(MiscTexts.T("校验更新包"), async (path, _, progress, ct) =>
            {
                progress?.Report(MiscTexts.T("正在校验更新包…"));
                if (!await VerifyFileAsync(path, asset.Size, expectedSha, ct))
                    throw new IOException(MiscTexts.T("更新包校验失败（大小或 SHA-256 不匹配），文件可能不完整，请重新下载。"));
                WriteVerifiedUpdateRecord(update, path);
            }),
            description: MiscTexts.TSub($"便携版压缩包 · {FormatSize(asset.Size)}"),
            glyph: "\uE895");

        _pendingUpdateVersion = update.Version;
        _pendingDownloadItem = item;
        item.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != nameof(DownloadItem.State)) return;
            if (item.State == DownloadItemState.Completed)
            {
                UpdateDownloaded?.Invoke(update);
            }
            else if (item.State == DownloadItemState.Failed)
            {
                // 失败不留下可被误认为“已校验”的旧记录
                InvalidateVerifiedUpdateRecordFor(asset.Name);
            }
        };
        return item;
    }


    /// <summary>在资源管理器中打开更新下载目录。</summary>
    public static void OpenUpdateFolder()
    {
        try
        {
            Directory.CreateDirectory(UpdateTempDir);
            Process.Start("explorer.exe", UpdateTempDir);
        }
        catch { }
    }

    /// <summary>
    /// 校验更新文件是否可用：存在、非空；exe 额外校验 PE 魔数（MZ 头）。
    /// 返回 false 时文件大概率已损坏或被外部改动，应删除后重新下载。
    /// </summary>
    public static bool IsInstallerFileValid(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length <= 0) return false;

            if (filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var magic = new byte[2];
                return fs.Read(magic, 0, 2) == 2 && magic[0] == (byte)'M' && magic[1] == (byte)'Z';
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void ClearPendingUpdate()
    {
        _pendingUpdateVersion = null;
        _pendingDownloadItem = null;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{(double)bytes / (1L << 30):F2} GB";
        if (bytes >= 1L << 20) return $"{(double)bytes / (1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{(double)bytes / (1L << 10):F1} KB";
        return $"{bytes} B";
    }

    public static string FormatSpeed(double mbps)
    {
        if (mbps >= 1000) return $"{mbps / 1000:F2} Gbps";
        if (mbps >= 1) return $"{mbps:F2} Mbps";
        return $"{mbps * 1000:F0} Kbps";
    }

    public static string FormatTime(TimeSpan? time)
    {
        if (time is null || time.Value.TotalSeconds <= 0) return "--";
        var t = time.Value;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

}
