using System.Text;
using System.Text.Json;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>
/// 把下载按钮点击与真实文件下载请求的<strong>结构化计数汇总</strong>发送到官方分析服务
/// （与工具流上传共用同一分享开关与服务器地址；不含任何对话、URL 或个人内容）。
/// 一次调用最多一次网络尝试（无重试循环）；开关关闭时完全不发送；
/// 发送失败时计数保留在本地（批次 ID 已随冻结批次持久化），下次重试复用同一批次。
/// 站点级统计（Umami）不由这里承担。
/// </summary>
public sealed class DownloadMetricsUploader
{
    private static readonly HttpClient DefaultHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _metricsUri;
    private readonly Func<bool> _enabled;
    private readonly DownloadMetricsStore _store;

    /// <param name="endpoint">显式配置的 API 基地址；只允许 HTTPS 或 HTTP 环回地址。</param>
    public DownloadMetricsUploader(HttpClient httpClient, Uri endpoint, Func<bool> enabled,
        DownloadMetricsStore store)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        if (!ToolFlowUploadClient.TryParseEndpoint(endpoint.AbsoluteUri, out var parsed, out var error)
            || parsed is null)
            throw new ArgumentException(error, nameof(endpoint));

        var baseUri = parsed.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? parsed
            : new Uri(parsed.AbsoluteUri + "/", UriKind.Absolute);
        _metricsUri = new Uri(baseUri, "v1/metrics");
    }

    /// <summary>
    /// 按分享开关创建官方上传器；关闭时返回 null，创建本身不发送请求。
    /// </summary>
    public static DownloadMetricsUploader? TryCreateConfigured(DownloadMetricsStore? store = null)
    {
        if (DataRoots.EffectiveTestRoot is not null) return null;
        return TryCreateConfigured(DefaultHttpClient, store);
    }

    internal static DownloadMetricsUploader? TryCreateConfigured(
        HttpClient httpClient, DownloadMetricsStore? store = null)
    {
        if (!AppSettings.IsToolflowUploadEnabled) return null;
        return new DownloadMetricsUploader(httpClient, ToolFlowUploadService.OfficialEndpoint,
            () => AppSettings.IsToolflowUploadEnabled, store ?? DownloadMetricsStore.Default);
    }

    /// <summary>
    /// 取一次快照发送；2xx 才在本地扣除该快照。没有任何待发计数时不发请求。
    /// 返回是否成功（或本就无待发）。网络失败返回 false，不抛出。
    /// </summary>
    public async Task<bool> UploadPendingAsync(CancellationToken cancellationToken = default)
    {
        // 开关关闭时不创建、不冻结新批次（关闭期间不累计；重开从新事件计起）。
        if (!_enabled()) return false;

        // 已有冻结批次（上次失败）原样重试；否则把当前计数固化为新批次并持久化唯一 ID。
        var batch = _store.TryBeginBatch();
        if (batch is null) return true;
        // 发送前世代核对（fail-closed）：撤销时刻及之前冻结的批次不得发送。
        if (ToolFlowUploadLatch.IsRevoked(batch.CreatedAtUtc)) return false;

        var json = BuildPayload(batch);
        using var request = new HttpRequestMessage(HttpMethod.Post, _metricsUri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        try
        {
            // 发送紧前以同一门闩核对资格（锁内原子启动发送）：快速"关闭→重开"不能让旧批次跨出撤销边界。
            var sendTask = ToolFlowUploadLatch.TryStartSend(batch.CreatedAtUtc, _enabled,
                () => _httpClient.SendAsync(request, cancellationToken));
            if (sendTask is null) return false;
            using var response = await sendTask.ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            _store.CompleteBatch(batch);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient 超时可重试（下次调用）；调用方的取消照常传播。
            return false;
        }
    }

    /// <summary>
    /// 构造载荷：批次 ID 来自本地批次槽——每个<strong>新批次</strong>生成一次并持久化，
    /// 失败重试同一个冻结批次时复用同一 ID，服务端据此去重、不会重复计数；
    /// 两次内容相同但批次不同的快照也会得到不同 ID，各自计数。
    /// 工具名等字符串字段同样过一遍轻量凭据过滤。
    /// </summary>
    internal static string BuildPayload(DownloadMetricsStore.MetricsBatch batch)
    {
        var counts = batch.Counts
            .Select(x => new
            {
                kind = x.Kind,
                tool = ToolFlowPayloadRedactor.Redact(x.Tool),
                count = x.Count,
            })
            .ToArray();
        var countsJson = JsonSerializer.Serialize(counts,
            new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        return $"{{\"schemaVersion\":1,\"batchId\":\"{batch.BatchId}\",\"counts\":{countsJson}}}";
    }
}
