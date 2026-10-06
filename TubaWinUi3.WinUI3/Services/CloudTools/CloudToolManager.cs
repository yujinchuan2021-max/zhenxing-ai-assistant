using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TubaWinUi3.Services.CloudTools;

/// <summary>
/// Portable packages only. The explicit root, transport and process probe permit fully
/// isolated tests; this class never uses global configuration, launches executables,
/// runs installers or enumerates the user's installed-software database.
/// </summary>
public sealed class CloudToolManager
{
    private const string ConfigurationConflict = "配置与新版本冲突，原版本保留；请先备份配置。";
    private static readonly uint[] ZipCrcTable = CreateZipCrcTable();
    private readonly string _root;
    private readonly string? _seedPath;
    private readonly string? _legacyRoot;
    private readonly HttpClient _http;
    private readonly string _architecture;
    private readonly Version _clientVersion;
    private readonly Func<string, bool> _isInUse;
    private readonly bool _allowNetwork;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CloudToolCatalog? _catalog;
    private string? _etag;
    private Uri? _etagEndpoint;
    private Uri? _activeCatalogEndpoint;
    private long _etagRevision = -1;
    private readonly ConcurrentDictionary<string, CloudToolState> _activity = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _blockedUpdates = new(StringComparer.Ordinal);

    public event EventHandler? Changed;
    public string? LastRefreshError { get; private set; }
    public long Revision { get { lock (_sync) return _catalog?.Revision ?? -1; } }
    public Uri CatalogEndpoint { get; }
    public Uri? FallbackCatalogEndpoint { get; }
    public Uri? ActiveCatalogEndpoint { get { lock (_sync) return _activeCatalogEndpoint; } }

    public CloudToolManager(string dataRoot, HttpClient httpClient, Uri catalogEndpoint,
        string architecture, Version clientVersion, string? seedPath = null,
        string? legacyRoot = null, bool allowNetwork = true, Func<string, bool>? isInUse = null,
        Uri? fallbackCatalogEndpoint = null)
    {
        _root = Path.GetFullPath(Path.Combine(dataRoot, "CloudTools"));
        CloudToolValidation.CheckNoReparse(_root);
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        CatalogEndpoint = catalogEndpoint ?? throw new ArgumentNullException(nameof(catalogEndpoint));
        FallbackCatalogEndpoint = fallbackCatalogEndpoint;
        _architecture = architecture;
        _clientVersion = clientVersion;
        _seedPath = seedPath;
        _legacyRoot = legacyRoot;
        _allowNetwork = allowNetwork;
        _isInUse = isInUse ?? IsDirectoryInUse;
        LoadCatalog();
        RecoverTransactions();
    }

    public IReadOnlyList<CloudToolDefinition> GetCatalog()
    {
        lock (_sync) return _catalog?.Tools.ToArray() ?? [];
    }

    public IReadOnlyList<CloudToolState> GetStates()
    {
        var tools = GetCatalog().ToDictionary(t => t.Id, StringComparer.Ordinal);
        var ids = new HashSet<string>(tools.Keys, StringComparer.Ordinal);
        try
        {
            var installed = CloudToolValidation.Under(_root, "Installed");
            if (Directory.Exists(installed))
                foreach (var dir in Directory.EnumerateDirectories(installed))
                {
                    var id = Path.GetFileName(dir);
                    if (CloudToolValidation.IsId(id) && ReadReceipt(dir, id) is not null) ids.Add(id);
                }
        }
        catch (IOException) { }
        catch (InvalidDataException) { }
        catch (UnauthorizedAccessException) { }
        foreach (var id in _activity.Keys) ids.Add(id);
        return ids.Order(StringComparer.Ordinal).Select(id =>
        {
            tools.TryGetValue(id, out var tool);
            var state = ReadState(id, tool);
            if (_activity.TryGetValue(id, out var activity))
                return activity with { EntryPath = state.EntryPath, IsManaged = state.IsManaged,
                    Version = state.Version, AvailableVersion = tool?.Version ?? activity.AvailableVersion,
                    HasOperationActivity = true, HasUpdate = state.HasUpdate };
            return state;
        }).ToArray();
    }

    public string? GetInstalledEntryPath(string id) => CloudToolValidation.IsId(id)
        ? ReadState(id, GetCatalog().FirstOrDefault(t => t.Id == id)).EntryPath : null;

    /// <summary>Resolve an old Tools-relative path inside an already owned download.
    /// This reads existing files only; it never downloads, installs or launches a tool.</summary>
    public string? ResolveLegacyToolPath(string relativeToolsPath)
    {
        if (!CloudToolValidation.IsRelativePath(relativeToolsPath)) return null;
        try
        {
            var relative = relativeToolsPath.Replace('\\', '/');
            var match = GetCatalog().Where(t => t.LegacyPath.Length != 0)
                .Select(t => (Tool: t, Legacy: t.LegacyPath.Replace('\\', '/')))
                .Where(t => relative.Equals(t.Legacy, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(t.Legacy + "/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Legacy.Length).FirstOrDefault();
            if (match.Tool is null) return null;
            var directory = InstallDir(match.Tool.Id);
            if (ReadReceipt(directory, match.Tool.Id) is null) return null;
            // The receipt proves ownership of the root. The catalog entry point
            // cannot be used to infer it: a newer catalog may name a different EXE.
            var suffix = relative.Length == match.Legacy.Length ? "" : relative[(match.Legacy.Length + 1)..];
            var path = suffix.Length == 0 ? directory : CloudToolValidation.Under(directory, suffix);
            CloudToolValidation.CheckNoReparse(path);
            if (!File.Exists(path) && !Directory.Exists(path)) return null;
            return (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0 ? path : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                      ArgumentException or NotSupportedException) { return null; }
    }

    public bool IsManaged(string id)
    {
        if (!CloudToolValidation.IsId(id)) return false;
        try { return ReadReceipt(InstallDir(id), id) is not null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_allowNetwork) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var endpoint = CatalogEndpoint;
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            string? previousEtag;
            lock (_sync) previousEtag = _catalog?.Revision == _etagRevision && _etagEndpoint == endpoint ? _etag : null;
            if (previousEtag is not null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(previousEtag));
            using var primaryResponse = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (primaryResponse.RequestMessage?.RequestUri is { } primaryUri && primaryUri != endpoint)
                throw new InvalidDataException("工具目录不接受重定向。");
            HttpResponseMessage? fallbackResponse = null;
            try
            {
                // A versioned API which is not deployed yet can use the old API.
                // An invalid 200, authorization failure or timeout never masks a
                // broken newer catalog by silently accepting unrelated old data.
                if (primaryResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented &&
                    FallbackCatalogEndpoint is { } fallback)
                {
                    endpoint = fallback;
                    using var fallbackRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    lock (_sync) previousEtag = _catalog?.Revision == _etagRevision && _etagEndpoint == endpoint ? _etag : null;
                    if (previousEtag is not null) fallbackRequest.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(previousEtag));
                    fallbackResponse = await _http.SendAsync(fallbackRequest, HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);
                }
                var response = fallbackResponse ?? primaryResponse;
                if (response.RequestMessage?.RequestUri is { } uri && uri != endpoint)
                    throw new InvalidDataException("工具目录不接受重定向。");
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    lock (_sync)
                        if (_catalog is null || previousEtag is null)
                            throw new InvalidDataException("服务器返回未修改，但没有对应的有效本地工具目录。");
                    lock (_sync) _activeCatalogEndpoint = endpoint;
                    LastRefreshError = null;
                    return;
                }
                response.EnsureSuccessStatusCode();
                var json = await ReadLimitedTextAsync(response.Content, CloudToolValidation.MaxCatalogBytes, timeout.Token);
                var incoming = CloudToolValidation.ParseCatalog(json, _clientVersion);
                lock (_sync)
                {
                    if (_catalog is not null && incoming.Revision < _catalog.Revision)
                        throw new InvalidDataException("工具目录版本回退，保留当前目录。");
                    if (_catalog is not null && incoming.Revision == _catalog.Revision &&
                        JsonSerializer.Serialize(incoming, CloudToolValidation.JsonOptions) !=
                        JsonSerializer.Serialize(_catalog, CloudToolValidation.JsonOptions))
                        throw new InvalidDataException("同一目录版本内容不一致，保留当前目录。");
                }
                WriteJson(CloudToolValidation.Under(_root, "catalog.json"), incoming);
                lock (_sync)
                {
                    _catalog = incoming;
                    _etag = response.Headers.ETag?.ToString();
                    _etagEndpoint = endpoint;
                    _etagRevision = incoming.Revision;
                    _activeCatalogEndpoint = endpoint;
                }
                LastRefreshError = null;
                RaiseChanged();
            }
            finally { fallbackResponse?.Dispose(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or
                                      JsonException or OperationCanceledException)
        {
            LastRefreshError = ex.Message;
            RaiseChanged();
        }
        finally { _gate.Release(); }
    }

    public Task<CloudToolOperationResult> InstallAsync(string id, CancellationToken cancellationToken = default)
        => InstallOrUpdateAsync(id, cancellationToken);

    // Explicit update of an old bundled copy creates a managed copy; it never overwrites the old directory.
    public Task<CloudToolOperationResult> UpdateAsync(string id, CancellationToken cancellationToken = default)
        => InstallOrUpdateAsync(id, cancellationToken);

    internal Task<CloudToolOperationResult> UpdateExpectedAsync(string id, string version, string sha256,
        CancellationToken cancellationToken = default, string? pendingOwner = null)
        => InstallOrUpdateAsync(id, cancellationToken, version, sha256, pendingOwner);

    internal bool IsInstalledPackageExpected(string id, string version, string sha256)
    {
        if (!CloudToolValidation.IsId(id) || !CloudToolValidation.IsSha256(sha256)) return false;
        var receipt = ReadReceipt(InstallDir(id), id);
        return receipt is not null && receipt.Version == version &&
            receipt.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase) && GetInstalledEntryPath(id) is not null;
    }

    internal async Task<bool> CancelPendingExpectedAsync(string id, string version, string sha256, string owner)
    {
        if (!CloudToolValidation.IsId(id) || !CloudToolValidation.IsSha256(sha256) ||
            owner.Length != 32 || !owner.All(Uri.IsHexDigit)) return false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!PendingTargetMatches(id, version, sha256, owner)) return false;
            ClearPending(id);
            _activity.TryRemove(id, out _);
            _retryAfter.TryRemove(id, out _);
            RaiseChanged();
            return true;
        }
        finally { _gate.Release(); }
    }

    // Only retry updates the user already requested. A newly published catalog is a notification.
    public Task ApplyPendingUpdatesAsync(CancellationToken cancellationToken = default)
        => UpdateManagedCoreAsync(onlyPending: true, cancellationToken);

    public Task UpdateManagedAsync(CancellationToken cancellationToken = default)
        => UpdateManagedCoreAsync(onlyPending: false, cancellationToken);

    private async Task UpdateManagedCoreAsync(bool onlyPending, CancellationToken cancellationToken)
    {
        if (!_allowNetwork) return;
        foreach (var state in GetStates().Where(s => s.IsManaged && !s.IsBusy))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onlyPending && !state.PendingUpdate) continue;
            var tool = GetCatalog().FirstOrDefault(t => t.Id == state.Id);
            if (tool is null || CloudToolValidation.SelectPackage(tool, _architecture) is not { Kind: "portable-zip" } package)
                continue;
            var pendingJson = onlyPending ? ReadText(PendingPath(state.Id), 32768) : null;
            if (onlyPending && pendingJson is null) continue;
            var receipt = ReadReceipt(InstallDir(state.Id), state.Id);
            if (receipt is null || (receipt.Version == tool.Version && receipt.Sha256.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (IsUpdateBlocked(state.Id, package.Sha256)) continue;
            if (_retryAfter.TryGetValue(state.Id, out var retry) && retry > DateTimeOffset.UtcNow) continue;
            _retryAfter[state.Id] = DateTimeOffset.UtcNow.AddMinutes(5);
            if (onlyPending)
                await InstallOrUpdateAsync(state.Id, cancellationToken, tool.Version, package.Sha256,
                    ReadPendingOwner(pendingJson!), pendingJson).ConfigureAwait(false);
            else await UpdateAsync(state.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CloudToolOperationResult> InstallOrUpdateAsync(string id, CancellationToken ct,
        string? expectedVersion = null, string? expectedSha256 = null, string? pendingOwner = null,
        string? expectedPendingJson = null)
    {
        if (!CloudToolValidation.IsId(id)) return new(false, "工具标识无效。", null);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? workspace = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var tool = GetCatalog().FirstOrDefault(t => t.Id == id);
            if (tool is null) return Result(id, false, "工具不在当前官方目录中。");
            var package = CloudToolValidation.SelectPackage(tool, _architecture);
            if (package is null || package.Kind != "portable-zip")
                return Fail(id, tool, "此工具需从来源网页获取，或当前平台暂不支持自动下载。", CloudToolStatus.Unsupported);
            // Recheck the exact saved intent under the same gate as application/cancellation.
            // A cancelled or replaced request must never be recreated by a background retry.
            if (expectedPendingJson is not null)
            {
                if (ReadText(PendingPath(id), 32768) != expectedPendingJson)
                    return Result(id, false, "待更新请求已取消或变化，未继续更新。");
                if (!PendingTargetMatches(id, tool.Version, package.Sha256))
                {
                    ClearPending(id);
                    return Fail(id, tool, "待处理的目标版本已变化，请重新确认更新；原版本已保留。");
                }
            }
            if (expectedVersion is not null && (tool.Version != expectedVersion ||
                !package.Sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)))
                return Result(id, false, "云端目标版本已变化，请刷新后重新确认更新；原版本已保留。");
            if (!_allowNetwork) return Fail(id, tool, "隔离模式未启用网络下载。");

            var target = InstallDir(id);
            var receipt = ReadReceipt(target, id);
            if (expectedVersion is not null && (receipt is null || GetInstalledEntryPath(id) is null))
                return Result(id, false, "本机受管工具状态已变化，请刷新后重试；没有安装其他工具。");
            if (Directory.Exists(target) && receipt is null)
                return Fail(id, tool, "目标目录没有本应用的有效安装记录，已保留原文件。");
            if (receipt is not null && receipt.Version == tool.Version &&
                receipt.Sha256.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase) &&
                GetInstalledEntryPath(id) is not null)
            {
                _activity.TryRemove(id, out _);
                ClearPending(id);
                RaiseChanged();
                return Result(id, true, "当前工具已就绪。");
            }
            if (receipt is not null && _isInUse(target)) return Pending(id, tool, "工具正在运行，关闭后将重试更新。", pendingOwner);
            if (receipt is not null && !CloudToolValidation.HasFileBaseline(receipt))
                return Fail(id, tool, "旧版本缺少文件校验记录，已保留；请先备份数据。");

            var token = Guid.NewGuid().ToString("N");
            var stageName = id + "-" + token;
            workspace = CloudToolValidation.Under(_root, "Staging/" + stageName);
            CloudToolValidation.CheckNoReparse(workspace);
            Directory.CreateDirectory(workspace);
            var zip = CloudToolValidation.Under(workspace, "package.zip");
            var stage = CloudToolValidation.Under(workspace, "payload");
            Publish(id, tool, receipt is null ? CloudToolStatus.Downloading : CloudToolStatus.Updating, 0);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            bool strictExecutable;
            lock (_sync) strictExecutable = _catalog?.SchemaVersion == 2;
            var sourceUrl = await DownloadAsync(id, tool, package, zip, stage, receipt is not null,
                strictExecutable, timeout.Token).ConfigureAwait(false);
            var packageFiles = await HashPackageFilesAsync(stage, timeout.Token).ConfigureAwait(false);
            var newReceipt = new CloudToolReceipt { Id = id, Name = tool.Name, Version = tool.Version,
                Architecture = package.Architecture, Sha256 = package.Sha256.ToLowerInvariant(),
                EntryPoint = package.EntryPoint, PackageUrl = sourceUrl, PackageSize = package.SizeBytes,
                Files = packageFiles };
            ct.ThrowIfCancellationRequested();
            CloudToolValidation.CheckTree(stage);
            if (receipt is not null && _isInUse(target)) return Pending(id, tool, "工具正在运行，已保留原版本，关闭后将重试。", pendingOwner);
            if (receipt is not null)
            {
                try { await PreserveUserFilesAsync(target, stage, receipt, packageFiles, timeout.Token).ConfigureAwait(false); }
                catch (InvalidDataException ex) when (ex.Message == ConfigurationConflict)
                {
                    BlockUpdate(id, package.Sha256);
                    ClearPending(id);
                    return Fail(id, tool, ConfigurationConflict);
                }
            }
            WriteJson(CloudToolValidation.Under(stage, CloudToolValidation.ReceiptFile), newReceipt);
            if (!SameReceipt(ReadReceipt(stage, id), newReceipt))
                throw new InvalidDataException("安装记录校验失败，原版本已保留；请稍后重试。");
            ct.ThrowIfCancellationRequested();
            CloudToolValidation.CheckTree(stage);
            if (receipt is not null && _isInUse(target)) return Pending(id, tool, "工具正在运行，已保留原版本，关闭后将重试。", pendingOwner);
            Commit(id, stageName, stage, target, receipt);
            ClearPending(id);
            ClearBlockedUpdate(id);
            _activity.TryRemove(id, out _);
            _retryAfter.TryRemove(id, out _);
            RaiseChanged();
            return Result(id, true, "工具已安装，可以打开。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return Fail(id, FindTool(id), "下载已取消，原有工具保持可用。"); }
        catch (OperationCanceledException)
        { return Fail(id, FindTool(id), "下载或安装超时，原有工具保持可用；请稍后重试。"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or JsonException)
        {
            return Fail(id, FindTool(id), ex.Message);
        }
        finally
        {
            if (workspace is not null) TryDelete(workspace);
            _gate.Release();
        }
    }

    public async Task<CloudToolOperationResult> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!CloudToolValidation.IsId(id)) return new(false, "工具标识无效。", null);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = InstallDir(id);
            if (ReadReceipt(dir, id) is null) return Result(id, false, "仅能移除本应用下载并管理的工具。");
            if (_isInUse(dir)) return Result(id, false, "工具正在运行，请关闭后再移除。");
            cancellationToken.ThrowIfCancellationRequested();
            Publish(id, FindTool(id), CloudToolStatus.Removing, 0);
            CloudToolValidation.CheckTree(dir);
            // Move the owned directory out of the active location first. If deletion fails,
            // recovery restores it instead of losing the installed entry/receipt.
            var backupName = id + "-" + Guid.NewGuid().ToString("N");
            var backup = CloudToolValidation.Under(_root, "Backups/" + backupName);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            var journal = new CloudToolTransaction(id, "", backupName);
            WriteJson(JournalPath(id), journal);
            Directory.Move(dir, backup);
            try { CloudToolValidation.DeleteTree(backup); }
            catch { if (!Directory.Exists(dir)) Directory.Move(backup, dir); throw; }
            DeleteJournal(id);
            ClearPending(id);
            ClearBlockedUpdate(id);
            _activity.TryRemove(id, out _);
            RaiseChanged();
            return Result(id, true, "已移除本应用管理的工具。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { return Fail(id, FindTool(id), ex.Message); }
        finally { _gate.Release(); }
    }

    private async Task<string> DownloadAsync(string id, CloudToolDefinition tool, CloudToolPackage package,
        string destination, string payload, bool updating, bool strictExecutable, CancellationToken ct)
    {
        var sources = CloudToolValidation.PackageSources(package);
        var errors = new List<Exception>();
        for (var index = 0; index < sources.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var source = sources[index];
            var attempt = CloudToolValidation.Under(Path.GetDirectoryName(destination)!, $"download-{index}.zip");
            var attemptPayload = CloudToolValidation.Under(Path.GetDirectoryName(destination)!, $"payload-{index}");
            try
            {
                // Each source starts with its own empty file, fresh hash and byte
                // count. A partial primary response cannot taint a backup copy.
                Publish(id, tool, updating ? CloudToolStatus.Updating : CloudToolStatus.Downloading, 0);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(8));
                await DownloadSourceAsync(id, tool, package, source, attempt, updating, timeout.Token).ConfigureAwait(false);
                CheckZipArchive(attempt);
                Publish(id, tool, CloudToolStatus.Installing, 100);
                await ExtractAsync(attempt, attemptPayload, timeout.Token).ConfigureAwait(false);
                var entry = CloudToolValidation.Under(attemptPayload, package.EntryPoint);
                if (!IsExecutable(entry)) throw new InvalidDataException("工具包缺少有效的 EXE 入口。");
                if (strictExecutable && !IsPortableExecutable(entry, package.Architecture, _architecture))
                    throw new InvalidDataException("工具包主程序不是有效的 Windows 程序，或与清单架构不符；原版本已保留。");
                ct.ThrowIfCancellationRequested();
                CloudToolValidation.CheckNoReparse(destination);
                File.Move(attempt, destination);
                CloudToolValidation.CheckTree(attemptPayload);
                CloudToolValidation.CheckNoReparse(payload);
                Directory.Move(attemptPayload, payload);
                return source.AbsoluteUri;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                errors.Add(new HttpRequestException("下载源响应超时。", ex));
            }
            catch (HttpRequestException ex)
            {
                var message = ex.StatusCode is { } status ? $"下载源返回 HTTP {(int)status}，未取得工具包。" :
                    "无法连接下载源，或下载连接已中断。";
                errors.Add(new HttpRequestException(message, ex, ex.StatusCode));
            }
            catch (InvalidDataException ex)
            {
                errors.Add(ex.Message.Any(character => character > 127) ? ex :
                    new InvalidDataException("工具包格式损坏或内容无效，未进行安装。", ex));
            }
            finally
            {
                CloudToolValidation.CheckNoReparse(attempt);
                if (File.Exists(attempt)) File.Delete(attempt);
                // Refuse to proceed to another source if its partial extraction
                // cannot safely be removed from this operation-owned workspace.
                CloudToolValidation.DeleteTree(attemptPayload);
            }
        }
        throw new InvalidDataException($"工具下载失败，已尝试 {sources.Count} 个下载源，原有工具保持可用。" +
            (errors.Count == 0 ? "" : " 原因：" + errors[^1].Message), new AggregateException(errors));
    }

    private async Task DownloadSourceAsync(string id, CloudToolDefinition tool, CloudToolPackage package,
        Uri source, string destination, bool updating, CancellationToken ct)
    {
        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headersTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead,
            headersTimeout.Token).ConfigureAwait(false);
        headersTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != source)
            throw new InvalidDataException("工具包不接受重定向。");
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidDataException("工具包不接受重定向。");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"下载源返回 HTTP {(int)response.StatusCode}，未取得工具包。", null, response.StatusCode);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("下载源返回了网页或错误信息，未取得 ZIP 工具包。");
        if (response.Content.Headers.ContentLength is { } length && length != package.SizeBytes)
            throw new InvalidDataException("下载大小与官方清单不一致。");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        CloudToolValidation.CheckNoReparse(destination);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        var signature = new byte[4];
        var signatureCount = 0;
        long total = 0;
        var clock = Stopwatch.StartNew();
        var last = TimeSpan.Zero;
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            quiet.CancelAfter(TimeSpan.FromSeconds(45));
            int count;
            try { count = await input.ReadAsync(buffer, quiet.Token).ConfigureAwait(false); }
            catch (IOException ex) { throw new HttpRequestException("下载过程中连接中断。", ex); }
            finally { quiet.CancelAfter(Timeout.InfiniteTimeSpan); }
            if (count == 0) break;
            if (signatureCount < signature.Length)
            {
                var copy = Math.Min(count, signature.Length - signatureCount);
                buffer.AsSpan(0, copy).CopyTo(signature.AsSpan(signatureCount));
                signatureCount += copy;
                if (signatureCount == signature.Length && !signature.AsSpan().SequenceEqual("PK\x03\x04"u8))
                    throw new InvalidDataException("下载源返回了网页、错误信息或无效文件，未取得 ZIP 工具包。");
            }
            total += count;
            if (total > package.SizeBytes) throw new InvalidDataException("下载超出官方清单的大小。");
            hash.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            if (clock.Elapsed - last > TimeSpan.FromMilliseconds(200))
            {
                Publish(id, tool, updating ? CloudToolStatus.Updating : CloudToolStatus.Downloading, total * 100d / package.SizeBytes);
                last = clock.Elapsed;
            }
        }
        if (total != package.SizeBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("工具包大小或 SHA-256 校验失败，已保留原版本。");
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void CheckZipArchive(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count is 0 or > CloudToolValidation.MaxZipEntries)
                throw new InvalidDataException("工具包文件数量无效。");
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("下载文件不是完整有效的 ZIP 工具包，未进行安装。", ex);
        }
    }

    private static async Task ExtractAsync(string zipPath, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        using var archive = OpenZipArchive(zipPath);
        if (archive.Entries.Count is 0 or > CloudToolValidation.MaxZipEntries)
            throw new InvalidDataException("工具包文件数量无效。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var isDirectory = name.EndsWith('/');
            var relative = isDirectory ? name.TrimEnd('/') : name;
            if (!CloudToolValidation.IsRelativePath(relative) || !paths.Add(relative) ||
                relative.Equals(CloudToolValidation.ReceiptFile, StringComparison.OrdinalIgnoreCase) ||
                ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("工具包包含不安全或重复路径。");
            var output = CloudToolValidation.Under(destination, relative);
            if (isDirectory) { Directory.CreateDirectory(output); continue; }
            if (entry.Length > CloudToolValidation.MaxExpandedBytes - expanded)
                throw new InvalidDataException("工具包解压大小超过限制。");
            expanded += entry.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using var input = entry.Open();
            await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long written = 0;
            var crc = uint.MaxValue;
            while (true)
            {
                int count;
                try { count = await input.ReadAsync(buffer, ct).ConfigureAwait(false); }
                catch (InvalidDataException ex)
                { throw new InvalidDataException("工具包条目损坏，无法解压；原版本已保留。", ex); }
                if (count == 0) break;
                written += count;
                if (written > entry.Length) throw new InvalidDataException("工具包条目大小不一致。");
                for (var i = 0; i < count; i++) crc = ZipCrcTable[(crc ^ buffer[i]) & 0xff] ^ (crc >> 8);
                await file.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            if (written != entry.Length) throw new InvalidDataException("工具包条目不完整。");
            if (~crc != entry.Crc32) throw new InvalidDataException("工具包条目 CRC 校验失败，原版本已保留。");
        }
    }

    private static ZipArchive OpenZipArchive(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch (InvalidDataException ex)
        { throw new InvalidDataException("工具包格式损坏，无法解压；原版本已保留。", ex); }
    }

    private static uint[] CreateZipCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
            table[index] = value;
        }
        return table;
    }

    private static async Task<Dictionary<string, string>> HashPackageFilesAsync(string stage, CancellationToken ct)
    {
        CloudToolValidation.CheckTree(stage);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(stage, file).Replace('\\', '/');
            var path = CloudToolValidation.Under(stage, relative);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            files.Add(relative, Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false)).ToLowerInvariant());
        }
        return files;
    }

    private static async Task PreserveUserFilesAsync(string source, string stage, CloudToolReceipt receipt,
        Dictionary<string, string> packageFiles, CancellationToken ct)
    {
        if (!CloudToolValidation.HasFileBaseline(receipt))
            throw new InvalidDataException("旧版本缺少文件校验记录，已保留；请先备份数据。");
        var baseline = receipt.Files!.ToDictionary(p => p.Key.Replace('\\', '/'), p => p.Value,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            CloudToolValidation.CheckTree(source);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                if (relative.Equals(CloudToolValidation.ReceiptFile, StringComparison.OrdinalIgnoreCase)) continue;
                var sourcePath = CloudToolValidation.Under(source, relative);
                await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
                // Unchanged old package files, including EXEs and DLLs, are replaced
                // solely by the verified new package rather than copied over it.
                if (baseline.TryGetValue(relative, out var original) && hash.Equals(original, StringComparison.OrdinalIgnoreCase)) continue;
                // Even matching content would turn this user-owned path into the
                // new vendor baseline and allow a later update to overwrite it.
                if (packageFiles.ContainsKey(relative)) throw new InvalidDataException(ConfigurationConflict);
                var destination = CloudToolValidation.Under(stage, relative);
                if (Directory.Exists(destination)) throw new InvalidDataException(ConfigurationConflict);
                for (var parent = Path.GetDirectoryName(destination); parent is not null &&
                     !parent.Equals(stage, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
                    if (File.Exists(parent)) throw new InvalidDataException(ConfigurationConflict);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                input.Position = 0;
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (InvalidDataException ex) when (ex.Message == ConfigurationConflict) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new InvalidDataException("无法保留工具数据，原版本已保留；请先备份数据。", ex); }
    }

    private static bool SameReceipt(CloudToolReceipt? current, CloudToolReceipt previous)
    {
        if (current is null || (current with { Files = null }) != (previous with { Files = null })) return false;
        if (current.Files is null || previous.Files is null) return current.Files is null && previous.Files is null;
        if (!CloudToolValidation.HasFileBaseline(current) || !CloudToolValidation.HasFileBaseline(previous) ||
            current.Files.Count != previous.Files.Count) return false;
        var files = current.Files.ToDictionary(p => p.Key.Replace('\\', '/'), p => p.Value, StringComparer.OrdinalIgnoreCase);
        return previous.Files.All(p => files.TryGetValue(p.Key.Replace('\\', '/'), out var hash) &&
            hash.Equals(p.Value, StringComparison.OrdinalIgnoreCase));
    }

    private void Commit(string id, string stageName, string stage, string target, CloudToolReceipt? previous)
    {
        CloudToolValidation.CheckNoReparse(target);
        if (previous is not null && !SameReceipt(ReadReceipt(target, id), previous))
            throw new InvalidDataException("安装记录已变化，取消替换。");
        var backupName = id + "-" + Guid.NewGuid().ToString("N");
        var backup = CloudToolValidation.Under(_root, "Backups/" + backupName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        WriteJson(JournalPath(id), new CloudToolTransaction(id, stageName, backupName));
        var movedOld = false;
        try
        {
            if (previous is not null)
            {
                CloudToolValidation.CheckTree(target);
                Directory.Move(target, backup);
                movedOld = true;
            }
            Directory.Move(stage, target);
        }
        catch
        {
            if (movedOld && !Directory.Exists(target)) Directory.Move(backup, target);
            throw;
        }
        // Retain the complete old directory, including anything written after the
        // migration snapshot. Completed transactions must never delete user data.
        DeleteJournal(id);
    }

    private void LoadCatalog()
    {
        CloudToolCatalog? best = null;
        foreach (var path in new[] { _seedPath, CloudToolValidation.Under(_root, "catalog.json") })
        {
            if (path is null) continue;
            try
            {
                var json = ReadText(path, CloudToolValidation.MaxCatalogBytes);
                if (json is null) continue;
                var candidate = CloudToolValidation.ParseCatalog(json, _clientVersion);
                if (best is null || candidate.Revision > best.Revision) best = candidate;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
        }
        lock (_sync) _catalog = best;
    }

    private CloudToolReceipt? ReadReceipt(string directory, string id)
    {
        try
        {
            var json = ReadText(CloudToolValidation.Under(directory, CloudToolValidation.ReceiptFile), CloudToolValidation.MaxReceiptBytes);
            if (json is null) return null;
            // Files was introduced after the first receipts. Missing or damaged
            // baselines block updates without hiding an otherwise owned installation.
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var fileFields = document.RootElement.EnumerateObject()
                .Where(p => p.Name.Equals("files", StringComparison.OrdinalIgnoreCase)).ToArray();
            using var core = new MemoryStream();
            using (var writer = new Utf8JsonWriter(core))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!property.Name.Equals("files", StringComparison.OrdinalIgnoreCase)) property.WriteTo(writer);
                writer.WriteEndObject();
            }
            var r = JsonSerializer.Deserialize<CloudToolReceipt>(core.ToArray(), CloudToolValidation.JsonOptions);
            if (r is not null && fileFields.Length == 1 && fileFields[0].Value.ValueKind == JsonValueKind.Object)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fields = fileFields[0].Value.EnumerateObject().ToArray();
                var valid = fields.Length <= CloudToolValidation.MaxZipEntries && fields.All(p =>
                    CloudToolValidation.IsRelativePath(p.Name) && paths.Add(p.Name.Replace('\\', '/')) &&
                    p.Value.ValueKind == JsonValueKind.String && CloudToolValidation.IsSha256(p.Value.GetString()));
                try
                {
                    if (valid) r = r with { Files = fileFields[0].Value.Deserialize<Dictionary<string, string>>(CloudToolValidation.JsonOptions) };
                }
                catch (JsonException) { /* Keep the owned receipt; no usable baseline means no update. */ }
            }
            return r is not null && r.Owner == "zhenxing-cloud-tools-v1" && r.Id == id &&
                CloudToolValidation.IsId(r.Id) && !string.IsNullOrWhiteSpace(r.Version) && r.Version.Length <= 100 &&
                !string.IsNullOrWhiteSpace(r.Name) && r.Name.Length <= 160 &&
                r.Architecture is ("x64" or "arm64" or "x86" or "any") && CloudToolValidation.IsSha256(r.Sha256) &&
                CloudToolValidation.IsPackageUrl(r.PackageUrl) && r.PackageSize is > 0 and <= CloudToolValidation.MaxPackageBytes &&
                CloudToolValidation.IsToolEntryPoint(r.EntryPoint)
                ? r : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private CloudToolState ReadState(string id, CloudToolDefinition? tool)
    {
        try
        {
            var receipt = ReadReceipt(InstallDir(id), id);
            var entry = receipt is not null ? SafeEntry(InstallDir(id), receipt.EntryPoint) : LegacyEntry(tool);
            var status = entry is not null ? CloudToolStatus.Installed : receipt is not null ? CloudToolStatus.Failed :
                tool is not null && CloudToolValidation.SelectPackage(tool, _architecture) is not { Kind: "portable-zip" }
                    ? CloudToolStatus.Unsupported : CloudToolStatus.NotInstalled;
            var pending = receipt is not null && File.Exists(PendingPath(id));
            if (pending && entry is not null) status = CloudToolStatus.PendingUpdate;
            var blocked = receipt is not null && tool is not null &&
                CloudToolValidation.SelectPackage(tool, _architecture) is { } package && IsUpdateBlocked(id, package.Sha256);
            var availablePackage = tool is null ? null : CloudToolValidation.SelectPackage(tool, _architecture);
            var hasUpdate = receipt is not null && entry is not null && tool is not null &&
                availablePackage is { Kind: "portable-zip" } && (receipt.Version != tool.Version ||
                !receipt.Sha256.Equals(availablePackage.Sha256, StringComparison.OrdinalIgnoreCase));
            if (blocked && entry is not null) status = CloudToolStatus.Failed;
            return new(id, tool?.Name ?? receipt?.Name ?? id, receipt?.Version ?? "", tool?.Version ?? receipt?.Version ?? "",
                status, Error: receipt is not null && entry is null ? "受管理工具的入口文件缺失或不可用。" :
                    blocked ? ConfigurationConflict :
                    status == CloudToolStatus.Unsupported ? "此工具需从来源网页获取，或当前平台暂不支持自动下载。" :
                    pending ? "工具待更新，关闭正在运行的工具后会重试。" : null,
                EntryPath: entry, PendingUpdate: pending, IsManaged: receipt is not null) { HasUpdate = hasUpdate };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(id, tool?.Name ?? id, "", tool?.Version ?? "", CloudToolStatus.Failed, Error: ex.Message);
        }
    }

    private string? LegacyEntry(CloudToolDefinition? tool)
    {
        if (tool is null || _legacyRoot is null || tool.LegacyPath.Length == 0 ||
            CloudToolValidation.SelectPackage(tool, _architecture) is not { } package) return null;
        try { return SafeEntry(CloudToolValidation.Under(_legacyRoot, tool.LegacyPath), package.EntryPoint); }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? SafeEntry(string directory, string relative)
    {
        try
        {
            var path = CloudToolValidation.Under(directory, relative);
            return IsExecutable(path) ? path : null;
        }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool IsExecutable(string path)
    {
        CloudToolValidation.CheckNoReparse(path);
        if (!File.Exists(path)) return false;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return file.ReadByte() == 'M' && file.ReadByte() == 'Z';
    }

    // New schema-2 packages must have bounded DOS, PE/COFF, optional and section
    // headers. Legacy receipt lookup keeps its existing compatibility behavior.
    // This examines bytes only and never loads or starts a vendor executable.
    private static bool IsPortableExecutable(string path, string architecture, string hostArchitecture)
    {
        CloudToolValidation.CheckNoReparse(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 64) return false;
        Span<byte> dos = stackalloc byte[64];
        file.ReadExactly(dos);
        if (dos[0] != 'M' || dos[1] != 'Z') return false;
        var offset = BinaryPrimitives.ReadInt32LittleEndian(dos[60..]);
        if (offset < 64 || offset > file.Length - 24) return false;
        file.Position = offset;
        Span<byte> header = stackalloc byte[24];
        file.ReadExactly(header);
        if (!header[..4].SequenceEqual("PE\0\0"u8)) return false;
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        var sections = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header[20..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[22..]);
        var nativeMachine = hostArchitecture switch { "x86" => 0x014c, "x64" => 0x8664, "arm64" => 0xaa64, _ => 0 };
        if (machine is not (0x014c or 0x8664 or 0xaa64) || sections is 0 or > 96 ||
            nativeMachine == 0 || (machine != nativeMachine && machine != 0x014c) ||
            (flags & 0x0002) == 0 || (flags & 0x2000) != 0 ||
            (architecture == "x64" && machine != 0x8664) ||
            (architecture == "x86" && machine != 0x014c) ||
            (architecture == "arm64" && machine != 0xaa64) ||
            file.Length - file.Position < optionalSize + sections * 40L || optionalSize < 2) return false;
        Span<byte> magicBytes = stackalloc byte[2];
        file.ReadExactly(magicBytes);
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(magicBytes);
        if ((machine == 0x014c && (magic != 0x010b || optionalSize < 96)) ||
            (machine != 0x014c && (magic != 0x020b || optionalSize < 112))) return false;
        file.Position = offset + 24L + optionalSize;
        Span<byte> section = stackalloc byte[40];
        for (var index = 0; index < sections; index++)
        {
            file.ReadExactly(section);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(section[16..]);
            var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(section[20..]);
            if (size != 0 && (rawOffset > file.Length || size > file.Length - rawOffset)) return false;
        }
        return true;
    }

    private void RecoverTransactions()
    {
        string transactions;
        try { transactions = CloudToolValidation.Under(_root, "Transactions"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return; }
        if (!Directory.Exists(transactions)) return;
        foreach (var path in Directory.EnumerateFiles(transactions, "*.json"))
        {
            try
            {
                var json = ReadText(path, 32768);
                if (json is null) continue;
                var t = JsonSerializer.Deserialize<CloudToolTransaction>(json, CloudToolValidation.JsonOptions);
                if (t is null || !CloudToolValidation.IsId(t.Id) || t.StageName is null || Path.GetFileName(path) != t.Id + ".json" ||
                    !SafeTransactionName(t.BackupName, t.Id) ||
                    (t.StageName.Length != 0 && !SafeTransactionName(t.StageName, t.Id))) continue;
                var target = InstallDir(t.Id);
                var backup = CloudToolValidation.Under(_root, "Backups/" + t.BackupName);
                var old = ReadReceipt(backup, t.Id);
                if (!Directory.Exists(target) && old is not null)
                {
                    CloudToolValidation.CheckTree(backup);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.Move(backup, target);
                }
                if (t.StageName.Length != 0) TryDelete(CloudToolValidation.Under(_root, "Staging/" + t.StageName));
                // A valid target completes recovery; an old backup remains available
                // for data written at the very end of the previous version's life.
                if (ReadReceipt(target, t.Id) is not null) DeleteJournal(t.Id);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
        }
    }

    private static bool SafeTransactionName(string? name, string id) => name is not null && name.StartsWith(id + "-", StringComparison.Ordinal) &&
        name.Length == id.Length + 33 && name[(id.Length + 1)..].All(Uri.IsHexDigit);

    private CloudToolOperationResult Pending(string id, CloudToolDefinition tool, string message, string? owner = null)
    {
        var package = CloudToolValidation.SelectPackage(tool, _architecture);
        WriteJson(PendingPath(id), new { id, version = tool.Version, sha256 = package?.Sha256, owner,
            retryAfter = DateTimeOffset.UtcNow.AddMinutes(5) });
        _retryAfter[id] = DateTimeOffset.UtcNow.AddMinutes(5);
        Publish(id, tool, CloudToolStatus.PendingUpdate, 0, message, pending: true);
        return Result(id, false, message);
    }

    private bool PendingTargetMatches(string id, string version, string sha256, string? owner = null)
    {
        try
        {
            var json = ReadText(PendingPath(id), 32768);
            if (json is null) return false;
            using var data = JsonDocument.Parse(json);
            var value = data.RootElement;
            return value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("id", out var savedId) && savedId.ValueKind == JsonValueKind.String && savedId.GetString() == id &&
                value.TryGetProperty("version", out var savedVersion) && savedVersion.ValueKind == JsonValueKind.String && savedVersion.GetString() == version &&
                value.TryGetProperty("sha256", out var savedSha) && savedSha.ValueKind == JsonValueKind.String &&
                sha256.Equals(savedSha.GetString(), StringComparison.OrdinalIgnoreCase) &&
                (owner is null || value.TryGetProperty("owner", out var savedOwner) &&
                    savedOwner.ValueKind == JsonValueKind.String && savedOwner.GetString() == owner);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { return false; }
    }

    private static string? ReadPendingOwner(string json)
    {
        try
        {
            using var data = JsonDocument.Parse(json);
            return data.RootElement.ValueKind == JsonValueKind.Object &&
                data.RootElement.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.String &&
                owner.GetString() is { Length: 32 } value && value.All(Uri.IsHexDigit) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private CloudToolOperationResult Fail(string id, CloudToolDefinition? tool, string message,
        CloudToolStatus status = CloudToolStatus.Failed)
    {
        Publish(id, tool, status, 0, message);
        return Result(id, false, message);
    }

    private void Publish(string id, CloudToolDefinition? tool, CloudToolStatus status, double progress,
        string? error = null, bool pending = false)
    {
        var prior = ReadState(id, tool);
        _activity[id] = prior with { Status = status, Progress = progress, Error = error, PendingUpdate = pending };
        RaiseChanged();
    }

    private CloudToolOperationResult Result(string id, bool success, string message) =>
        new(success, message, GetStates().FirstOrDefault(s => s.Id == id));

    private CloudToolDefinition? FindTool(string id) => GetCatalog().FirstOrDefault(t => t.Id == id);
    private string InstallDir(string id) => CloudToolValidation.Under(_root, "Installed/" + id);
    private string JournalPath(string id) => CloudToolValidation.Under(_root, "Transactions/" + id + ".json");
    private string PendingPath(string id) => CloudToolValidation.Under(_root, "Pending/" + id + ".json");
    private string BlockedPath(string id) => CloudToolValidation.Under(_root, "BlockedUpdates/" + id + ".json");

    private bool IsUpdateBlocked(string id, string sha256)
    {
        if (_blockedUpdates.TryGetValue(id, out var blocked))
            return blocked.Equals(sha256, StringComparison.OrdinalIgnoreCase);
        try
        {
            var json = ReadText(BlockedPath(id), 1024);
            var record = json is null ? null : JsonSerializer.Deserialize<BlockedUpdate>(json, CloudToolValidation.JsonOptions);
            if (record is not null && CloudToolValidation.IsSha256(record.Sha256))
            {
                _blockedUpdates[id] = record.Sha256;
                return record.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
        return false;
    }

    private void BlockUpdate(string id, string sha256)
    {
        _blockedUpdates[id] = sha256;
        try { WriteJson(BlockedPath(id), new BlockedUpdate(sha256)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { /* The in-process block still prevents repeated automatic downloads. */ }
    }

    private void ClearBlockedUpdate(string id)
    {
        _blockedUpdates.TryRemove(id, out _);
        var path = BlockedPath(id);
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record BlockedUpdate(string Sha256);

    private void ClearPending(string id) { var path = PendingPath(id); if (File.Exists(path)) File.Delete(path); }
    private void DeleteJournal(string id) { var path = JournalPath(id); if (File.Exists(path)) File.Delete(path); }
    private static void TryDelete(string directory)
    {
        try { CloudToolValidation.DeleteTree(directory); }
        catch (IOException) { }
        catch (InvalidDataException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RaiseChanged()
    {
        // A failing UI subscriber must not interrupt a package transaction.
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
            try { ((EventHandler)subscriber)(this, EventArgs.Empty); } catch { }
    }

    private static string? ReadText(string path, int limit)
    {
        CloudToolValidation.CheckNoReparse(path);
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new InvalidDataException("工具管理文件超过大小限制。");
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    private static void WriteJson<T>(string path, T value)
    {
        CloudToolValidation.CheckNoReparse(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, CloudToolValidation.JsonOptions));
            CloudToolValidation.CheckNoReparse(path);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static async Task<string> ReadLimitedTextAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("工具目录超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > limit) throw new InvalidDataException("工具目录超过大小限制。");
            output.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static bool IsDirectoryInUse(string directory)
    {
        CloudToolValidation.CheckTree(directory);
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }
}
