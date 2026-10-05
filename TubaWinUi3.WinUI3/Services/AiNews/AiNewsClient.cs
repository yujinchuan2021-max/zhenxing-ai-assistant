using System.Text;
using System.Text.Json;

namespace TubaWinUi3.Services.AiNews;

internal interface IAiNewsReader
{
    Task<AiNewsBatch> ReadAsync(AiNewsQuery query, bool force, CancellationToken token);
}

internal sealed class AiNewsClient : IAiNewsReader
{
    internal const string OfficialApiRoot = "https://zhenxingai.com/api/ai-news/v1/";
    private static readonly HttpClient Shared = CreateHttp();
    private readonly HttpClient _http;
    private readonly Uri _root;
    private readonly string? _cachePath;
    private readonly Func<DateTimeOffset> _now;
    private sealed record Cache(string Endpoint, DateTimeOffset At, string Body);

    internal AiNewsClient(HttpClient http, Uri root, string? cachePath = null,
        Func<DateTimeOffset>? now = null)
    { _http = http; _root = root; _cachePath = cachePath; _now = now ?? (() => DateTimeOffset.UtcNow); }

    internal static AiNewsClient CreateDefault() => new(Shared, new(OfficialApiRoot),
        Path.Combine(ConfigManager.GetDataDir(), "AiAssistant", "AiNews", "recent.json"));

    private static HttpClient CreateHttp()
    {
        var handler = HttpClientFactory.CreateIpv4PreferredHandler();
        handler.AllowAutoRedirect = false;
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZhenxingAI/0.1");
        return client;
    }

    public async Task<AiNewsBatch> ReadAsync(AiNewsQuery query, bool force, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool cacheable = query == new AiNewsQuery();
        var cached = cacheable ? ReadCache() : null;
        if (!force && cached is not null && _now() - cached.RetrievedAt < TimeSpan.FromMinutes(2)) return cached;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_root, "items?" + query.QueryString));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > AiNewsDocument.MaxBytes) throw new InvalidDataException("News response too large.");
            using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, budget.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > AiNewsDocument.MaxBytes) throw new InvalidDataException("News response too large.");
                buffer.Write(chunk, 0, count);
            }
            var body = new UTF8Encoding(false, true).GetString(buffer.ToArray());
            var batch = AiNewsDocument.Parse(body, _now());
            token.ThrowIfCancellationRequested();
            if (cacheable) WriteCache(body, batch.RetrievedAt);
            return batch;
        }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is HttpRequestException or IOException or InvalidDataException or JsonException or DecoderFallbackException or OperationCanceledException)
        {
            if (cached is not null) return cached with { FromCache = true };
            throw new AiNewsUnavailableException();
        }
    }

    private AiNewsBatch? ReadCache()
    {
        try
        {
            if (_cachePath is null || !File.Exists(_cachePath) || new FileInfo(_cachePath).Length > AiNewsDocument.MaxBytes * 4) return null;
            var cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(_cachePath));
            if (cache is null || cache.Endpoint != _root.AbsoluteUri || cache.At > _now()
                || _now() - cache.At > TimeSpan.FromHours(1) || Encoding.UTF8.GetByteCount(cache.Body) > AiNewsDocument.MaxBytes) return null;
            return AiNewsDocument.Parse(cache.Body, cache.At) with { FromCache = true };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
    private void WriteCache(string body, DateTimeOffset at)
    {
        if (_cachePath is null) return;
        string temporary = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Cache(_root.AbsoluteUri, at, body)), new UTF8Encoding(false));
            File.Move(temporary, _cachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}

internal sealed class AiNewsUnavailableException : Exception;
