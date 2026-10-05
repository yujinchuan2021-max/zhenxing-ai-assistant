using System.Net.Http;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Services.CloudTools;

/// <summary>Application-owned lifecycle. Reading cards does not start background networking.</summary>
public static class CloudToolService
{
    public static Uri OwnEndpoint { get; } = new(ToolFlowUploadService.OfficialEndpoint, "v1/tools/catalog");
    public static Uri EventsEndpoint { get; } = new(ToolFlowUploadService.OfficialEndpoint, "v1/tools/events");
    private static readonly object Sync = new();
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    private static CloudToolManager? _manager;
    private static string? _managerKey;
    private static CancellationTokenSource? _lifetime;
    private static Task? _pollTask;
    private static Task? _eventsTask;

    public static event EventHandler? Changed;
    private static string? _initializationError;
    public static string? LastRefreshError => TryGetManager()?.LastRefreshError ?? _initializationError;
    internal static CloudToolManager? OverrideForTests { get; set; }

    private static CloudToolManager Manager
    {
        get
        {
            if (OverrideForTests is { } overridden) return overridden;
            var root = ConfigManager.GetDataDir();
            var metadata = ToolMetadataService.MetadataRootOverride ?? Path.Combine(AppContext.BaseDirectory, "Metadata");
            var seed = Path.Combine(metadata, "cloud-tools.json");
            var key = root + "|" + seed + "|" + ToolCatalog.ToolsRoot;
            lock (Sync)
            {
                if (_manager is not null && key == _managerKey) return _manager;
                if (_manager is not null) _manager.Changed -= OnChanged;
                _manager = new CloudToolManager(root, Http, OwnEndpoint, UpdateService.CurrentArchitecture,
                    UpdateService.CurrentVersion, seed, ToolCatalog.ToolsRoot,
                    allowNetwork: DataRoots.EffectiveTestRoot is null);
                _manager.Changed += OnChanged;
                _managerKey = key;
                return _manager;
            }
        }
    }

    private static CloudToolManager? TryGetManager()
    {
        try { var manager = Manager; _initializationError = null; return manager; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { _initializationError = ex.Message; return null; }
    }

    public static IReadOnlyList<CloudToolDefinition> GetCatalog() => TryGetManager()?.GetCatalog() ?? [];
    public static IReadOnlyList<CloudToolState> GetStates() => TryGetManager()?.GetStates() ?? [];
    public static string? GetInstalledEntryPath(string id) => TryGetManager()?.GetInstalledEntryPath(id);
    public static string? ResolveLegacyToolPath(string relativeToolsPath) => TryGetManager()?.ResolveLegacyToolPath(relativeToolsPath);
    public static bool IsManaged(string id) => TryGetManager()?.IsManaged(id) == true;
    public static Task<CloudToolOperationResult> InstallAsync(string id, CancellationToken ct = default) =>
        TryGetManager()?.InstallAsync(id, ct) ?? Unavailable();
    public static Task<CloudToolOperationResult> UpdateAsync(string id, CancellationToken ct = default) =>
        TryGetManager()?.UpdateAsync(id, ct) ?? Unavailable();
    public static Task<CloudToolOperationResult> RemoveAsync(string id, CancellationToken ct = default) =>
        TryGetManager()?.RemoveAsync(id, ct) ?? Unavailable();
    private static Task<CloudToolOperationResult> Unavailable() =>
        Task.FromResult(new CloudToolOperationResult(false, _initializationError ?? "云端工具服务暂不可用。", null));

    public static async Task RefreshAsync(CancellationToken ct = default)
    {
        var manager = TryGetManager();
        if (manager is null) return;
        await manager.RefreshAsync(ct).ConfigureAwait(false);
        if (manager.LastRefreshError is null) await manager.UpdateManagedAsync(ct).ConfigureAwait(false);
    }

    // Tests and isolated GUI probes cannot connect to the real catalog or SSE endpoint.
    public static void Start()
    {
        if (DataRoots.EffectiveTestRoot is not null) return;
        lock (Sync)
        {
            if (_lifetime is not null) return;
            _lifetime = new CancellationTokenSource();
            var token = _lifetime.Token;
            _pollTask = Task.Run(() => PollAsync(token), token);
            _eventsTask = Task.Run(() => SubscribeAsync(token), token);
        }
    }

    public static void Stop()
    {
        lock (Sync)
        {
            if (_lifetime is null) return;
            var source = _lifetime;
            _lifetime = null;
            source.Cancel();
            var tasks = new[] { _pollTask, _eventsTask }.Where(t => t is not null).Cast<Task>().ToArray();
            _pollTask = _eventsTask = null;
            _ = Task.WhenAll(tasks).ContinueWith(_ => source.Dispose(), TaskScheduler.Default);
        }
    }

    private static async Task PollAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
            do
            {
                try { await RefreshAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException) { }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static async Task SubscribeAsync(CancellationToken ct)
    {
        var retrySeconds = 5;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var request = new HttpRequestMessage(HttpMethod.Get, EventsEndpoint);
                request.Headers.Accept.ParseAdd("text/event-stream");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri is { } uri && uri != EventsEndpoint)
                    throw new InvalidDataException("工具事件不接受重定向。");
                if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                    throw new InvalidDataException("工具事件格式无效。");
                // Keep healthy heartbeat connections open; only connection setup and silent
                // reads time out. Full refreshes still recover missed/reordered events.
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var reader = new StreamReader(stream);
                var hasData = false;
                retrySeconds = 5;
                while (!timeout.IsCancellationRequested)
                {
                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    quiet.CancelAfter(TimeSpan.FromSeconds(90));
                    var line = await ReadEventLineAsync(reader, quiet.Token).ConfigureAwait(false);
                    if (line is null) break;
                    if (line.Length == 0)
                    {
                        if (hasData) await RefreshAsync(ct).ConfigureAwait(false);
                        hasData = false;
                    }
                    else if (line.StartsWith("data:", StringComparison.Ordinal)) hasData = true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(retrySeconds), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            retrySeconds = Math.Min(retrySeconds * 2, 60);
        }
    }

    // StreamReader.ReadLineAsync can allocate an unbounded line before a length check.
    private static async Task<string?> ReadEventLineAsync(StreamReader reader, CancellationToken ct)
    {
        var builder = new System.Text.StringBuilder();
        var character = new char[1];
        while (true)
        {
            var count = await reader.ReadAsync(character.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) return builder.Length == 0 ? null : builder.ToString();
            if (character[0] == '\n') return builder.ToString().TrimEnd('\r');
            if (builder.Length == 16384) throw new InvalidDataException("工具事件超过大小限制。");
            builder.Append(character[0]);
        }
    }

    private static void OnChanged(object? sender, EventArgs e)
    {
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
            try { ((EventHandler)subscriber)(sender, e); } catch { }
    }
}
