using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TubaWinUi3.Models;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Services;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OwnedInstallerCatalog
{
    public int SchemaVersion { get; init; }
    public long Revision { get; init; }
    public string PublishedAt { get; init; } = "";
    public OwnedInstallerTool[] Tools { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OwnedInstallerTool
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public OwnedWindowsPackage[] Packages { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OwnedWindowsPackage
{
    public string Architecture { get; init; } = "";
    public string ExecutableArchitecture { get; init; } = "";
    public string Version { get; init; } = "";
    public string Type { get; init; } = "";
    public string Url { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    [JsonIgnore] public string FileName => Path.GetFileName(new Uri(Url).AbsolutePath);
}

/// <summary>Separate from portable ZIP tools: driver/desktop installers retain their actual install behaviour.</summary>
internal sealed class OwnedInstallerDownloadManager
{
    internal static readonly Uri Endpoint = new("https://zhenxingai.com/downloads/installers/catalog.json");
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 12 };
    private const int MaxCatalogBytes = 1024 * 1024;
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly string _architecture;
    private readonly bool _allowNetwork;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OwnedInstallerCatalog? _catalog;
    private DateTimeOffset _lastFetch;

    internal OwnedInstallerDownloadManager(string dataRoot, HttpClient http, string architecture, bool allowNetwork = true)
    {
        _root = Path.GetFullPath(Path.Combine(dataRoot, "downloads", "ManagedInstallers"));
        CloudToolValidation.CheckNoReparse(_root);
        _http = http;
        _architecture = architecture;
        _allowNetwork = allowNetwork;
        try
        {
            var cache = CloudToolValidation.Under(_root, "catalog.json");
            if (File.Exists(cache) && new FileInfo(cache).Length <= MaxCatalogBytes)
                _catalog = Parse(File.ReadAllText(cache));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
    }

    internal static bool IsOwnedUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("zhenxingai.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith("/downloads/installers/", StringComparison.Ordinal) ||
            !uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var prefix = "https://zhenxingai.com/";
        if (!value!.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var path = value[prefix.Length..];
        return CloudToolValidation.IsRelativePath(path) && path.Split('/').All(segment =>
            segment.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.'));
    }

    internal static void ValidatePackage(OwnedWindowsPackage package)
    {
        if (package is null || package.Architecture is not ("x86" or "x64" or "arm64" or "any") ||
            package.ExecutableArchitecture is not ("x86" or "x64" or "arm64") ||
            package.Type is not ("installer-exe" or "portable-exe") || string.IsNullOrWhiteSpace(package.Version) ||
            package.Version.Length > 80 || package.Version.Any(char.IsControl) || !IsOwnedUrl(package.Url) ||
            package.SizeBytes is < 64 or > WindowsDownloadValidation.MaxFileBytes || !CloudToolValidation.IsSha256(package.Sha256))
            throw new InvalidDataException("国内安装包的来源、大小、校验信息或架构无效，尚未运行安装程序。");
        if (package.Architecture == "x86" && package.ExecutableArchitecture != "x86")
            throw new InvalidDataException("32 位系统不能运行该安装包。");
        if (package.Architecture == "x64" && package.ExecutableArchitecture == "arm64")
            throw new InvalidDataException("安装包架构与目标系统不一致。");
        if (package.Architecture == "any" && package.ExecutableArchitecture != "x86")
            throw new InvalidDataException("通用安装包必须有可在受支持系统运行的入口。");
    }

    internal static OwnedInstallerCatalog Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxCatalogBytes) throw new InvalidDataException("国内安装包清单超过大小限制。");
        // Reject ambiguous duplicate keys before typed deserialization.
        using (var document = JsonDocument.Parse(json)) CheckDuplicateKeys(document.RootElement);
        var catalog = JsonSerializer.Deserialize<OwnedInstallerCatalog>(json, JsonOptions)
            ?? throw new InvalidDataException("国内安装包清单为空。");
        if (catalog.SchemaVersion != 1 || catalog.Revision <= 0 ||
            !DateTimeOffset.TryParse(catalog.PublishedAt, out var published) || published.Offset != TimeSpan.Zero ||
            catalog.Tools is null || catalog.Tools.Length > 256)
            throw new InvalidDataException("国内安装包清单版本或格式无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in catalog.Tools)
        {
            if (tool is null || !CloudToolValidation.IsId(tool.Id) || !ids.Add(tool.Id) ||
                string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > 160 || tool.Packages is null || tool.Packages.Length > 4)
                throw new InvalidDataException("国内安装包条目无效。");
            var architectures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var package in tool.Packages)
            {
                if (package is null || !architectures.Add(package.Architecture)) throw new InvalidDataException("安装包架构重复。");
                ValidatePackage(package);
            }
        }
        return catalog;
    }

    private static void CheckDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("国内安装包清单包含重复字段。");
                CheckDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicateKeys(item);
    }

    internal async Task<OwnedWindowsPackage?> ResolveAsync(string id, CancellationToken ct = default, string? architecture = null)
    {
        if (!CloudToolValidation.IsId(id)) throw new InvalidDataException("安装目标无效。");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_allowNetwork && (_catalog is null || DateTimeOffset.UtcNow - _lastFetch >= TimeSpan.FromMinutes(10)))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    using var response = await _http.GetAsync(Endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (response.RequestMessage?.RequestUri is { } final && final != Endpoint)
                        throw new InvalidDataException("国内安装包清单不接受重定向。");
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Only a genuinely absent catalog permits the explicit legacy official route.
                        if (_catalog is null) return null;
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength is > MaxCatalogBytes ||
                            response.Content.Headers.ContentType?.MediaType != "application/json")
                            throw new InvalidDataException("国内安装包清单返回了网页或无效内容。");
                        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                        using var buffer = new MemoryStream();
                        var block = new byte[8192];
                        int read;
                        while ((read = await input.ReadAsync(block, timeout.Token).ConfigureAwait(false)) > 0)
                        {
                            if (buffer.Length + read > MaxCatalogBytes) throw new InvalidDataException("国内安装包清单超过大小限制。");
                            buffer.Write(block, 0, read);
                        }
                        var incoming = Parse(new UTF8Encoding(false, true).GetString(buffer.ToArray()));
                        if (_catalog is not null && (incoming.Revision < _catalog.Revision ||
                            (incoming.Revision == _catalog.Revision && JsonSerializer.Serialize(incoming, JsonOptions) != JsonSerializer.Serialize(_catalog, JsonOptions))))
                            throw new InvalidDataException("国内安装包清单版本回退或同版本内容改变，已保留原清单。");
                        Directory.CreateDirectory(_root);
                        var cache = CloudToolValidation.Under(_root, "catalog.json");
                        var temporary = CloudToolValidation.Under(_root, "catalog-" + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(incoming, JsonOptions), timeout.Token).ConfigureAwait(false);
                            CloudToolValidation.CheckNoReparse(cache);
                            File.Move(temporary, cache, overwrite: true);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        _catalog = incoming;
                    }
                    _lastFetch = DateTimeOffset.UtcNow;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    if (_catalog is null) throw new InvalidDataException("国内安装包服务暂不可用，尚未运行安装程序；请稍后重试。", ex);
                    // Network outages can reuse the last validated immutable release. Invalid JSON never does.
                }
            }
            if (_catalog is null) throw new InvalidDataException("离线时没有可用的国内安装包清单。");
            var tool = _catalog.Tools.FirstOrDefault(item => item.Id == id)
                ?? throw new InvalidDataException("该工具尚未发布经过验证的国内安装包，尚未启动安装。");
            var requested = architecture ?? _architecture;
            return tool.Packages.FirstOrDefault(package => package.Architecture == requested)
                ?? tool.Packages.FirstOrDefault(package => package.Architecture == "any")
                ?? throw new InvalidDataException($"国内清单未提供适合 {requested} 系统的安装包，尚未启动安装。");
        }
        finally { _gate.Release(); }
    }

    internal string Destination(string id, OwnedWindowsPackage package)
    {
        if (!CloudToolValidation.IsId(id)) throw new InvalidDataException("安装目标无效。");
        ValidatePackage(package);
        return CloudToolValidation.Under(_root, id + "/" + package.Sha256.ToLowerInvariant());
    }

    internal OwnedWindowsPackage? GetCached(string id, string? architecture = null) =>
        _catalog?.Tools.FirstOrDefault(tool => tool.Id == id)?.Packages.FirstOrDefault(package => package.Architecture == (architecture ?? _architecture))
        ?? _catalog?.Tools.FirstOrDefault(tool => tool.Id == id)?.Packages.FirstOrDefault(package => package.Architecture == "any");

    internal async Task<string> DownloadAsync(string id, OwnedWindowsPackage package,
        IProgress<ToolDownloadProgress>? progress = null, CancellationToken ct = default) =>
        await StagedWindowsDownload.DownloadAsync(package.Url, Destination(id, package), package.FileName,
            progress, ct, package.SizeBytes, package.ExecutableArchitecture, _http, package.Sha256).ConfigureAwait(false);
}

internal static class OwnedInstallerDownloads
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly object Sync = new();
    private static OwnedInstallerDownloadManager? _manager;
    private static string? _dataRoot;
    internal static OwnedInstallerDownloadManager? OverrideForTests { get; set; }
    internal static OwnedInstallerDownloadManager Manager
    {
        get
        {
            if (OverrideForTests is { } manager) return manager;
            var root = ConfigManager.GetDataDir();
            lock (Sync)
            {
                if (_manager is null || _dataRoot != root)
                {
                    _manager = new(root, Http, UpdateService.CurrentArchitecture, DataRoots.EffectiveTestRoot is null);
                    _dataRoot = root;
                }
                return _manager;
            }
        }
    }

    internal static async Task<bool> EnqueueAsync(string id, string name, string glyph,
        Action<string>? status = null, CancellationToken ct = default, string? architecture = null)
    {
        if (architecture is not null && architecture != UpdateService.CurrentArchitecture)
            throw new InvalidDataException($"当前系统为 {UpdateService.CurrentArchitecture}，请选择匹配的安装包；尚未运行其他架构的安装程序。");
        var package = await Manager.ResolveAsync(id, ct, architecture);
        if (package is null)
        {
            status?.Invoke("国内安装包目录暂未发布，将尝试官方来源；官方来源需要相应网络条件。");
            return false;
        }
        DownloadQueueService.EnqueueWithResolver(name,
            _ => Task.FromResult(new ResolvedDownloadUrl(package.Url, package.FileName, package.SizeBytes)),
            Manager.Destination(id, package), new OwnedDownloadPostProcessor(id, package),
            description: package.Type == "installer-exe" ? $"国内下载 · {package.Version} · 下载校验后启动安装程序，安装结果需在系统中确认。"
                : $"国内下载 · {package.Version} · 下载校验后打开工具。", glyph: glyph);
        status?.Invoke("已加入国内下载队列，校验通过后将打开工具或安装程序。");
        return true;
    }

    internal static async Task<bool> TryOpenPortableAsync(string id, Action<string>? status = null, CancellationToken ct = default)
    {
        var package = Manager.GetCached(id);
        if (package is not { Type: "portable-exe" }) return false;
        var directory = Manager.Destination(id, package);
        var path = Path.Combine(directory, package.FileName);
        if (!File.Exists(path)) return false;
        try
        {
            await new OwnedDownloadPostProcessor(id, package).ExecuteAsync(path, directory,
                status is null ? null : new Progress<string>(status), ct);
            return true;
        }
        catch (InvalidDataException)
        {
            status?.Invoke("已下载工具校验未通过，将重新获取有效版本。");
            return false;
        }
    }
}

/// <summary>Every task keeps its original manifest binding, including after a queue restore.</summary>
internal sealed class OwnedDownloadPostProcessor : IDownloadPostProcessor
{
    private const string Prefix = "owned-windows-v1:";
    private readonly string _id;
    internal OwnedWindowsPackage Package { get; }
    public string DisplayName => Package.Type == "installer-exe" ? "校验后运行安装程序" : "校验后打开工具";
    internal string PersistenceKey => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        new Binding(_id, Package), OwnedInstallerDownloadManager.JsonOptions)));
    private sealed record Binding(string Id, OwnedWindowsPackage Package);

    internal OwnedDownloadPostProcessor(string id, OwnedWindowsPackage package)
    {
        if (!CloudToolValidation.IsId(id)) throw new InvalidDataException("安装目标无效。");
        OwnedInstallerDownloadManager.ValidatePackage(package);
        _id = id;
        Package = package;
    }

    internal static IDownloadPostProcessor? Restore(string key)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal) || key.Length > 8192) return null;
        try
        {
            var binding = JsonSerializer.Deserialize<Binding>(Convert.FromBase64String(key[Prefix.Length..]), OwnedInstallerDownloadManager.JsonOptions);
            return binding?.Package is null ? null : new OwnedDownloadPostProcessor(binding.Id, binding.Package);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException or ArgumentException) { return null; }
    }

    internal static async Task VerifyAsync(string path, OwnedWindowsPackage package, CancellationToken ct = default)
    {
        OwnedInstallerDownloadManager.ValidatePackage(package);
        if (!Path.GetFileName(path).Equals(package.FileName, StringComparison.Ordinal))
            throw new InvalidDataException("下载文件与当前安装任务不匹配，尚未运行安装程序。");
        WindowsDownloadValidation.Validate(path, package.SizeBytes, package.ExecutableArchitecture);
        await using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
        if (!actual.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("国内安装包 SHA-256 校验失败，尚未运行安装程序，请重新下载。");
    }

    public async Task ExecuteAsync(string downloadedFilePath, string destinationPath,
        IProgress<string>? statusProgress, CancellationToken ct)
    {
        statusProgress?.Report("正在核对安装包大小、SHA-256 与程序架构...");
        await VerifyAsync(downloadedFilePath, Package, ct).ConfigureAwait(false);
        await new InstallerLaunchProcessor().ExecuteAsync(downloadedFilePath, destinationPath, statusProgress, ct).ConfigureAwait(false);
        statusProgress?.Report(Package.Type == "installer-exe" ? "已交给安装程序，等待系统安装结果。" : "已校验并打开工具。");
    }
}
