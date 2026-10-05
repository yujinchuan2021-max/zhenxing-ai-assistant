using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>一次补发的请求数；待发送数只统计选定时允许上传的记录。</summary>
public sealed record ToolFlowUploadResult(int SuccessfulRequests, int FailedRequests, int PendingRequests);

/// <summary>
/// 上传已经由用户选定的工具流及其执行事件到官方服务器，不会自行启动后台上传。
/// 只有调用方显式调用 UploadPendingAsync 且当前开关开启时才发送请求。
/// </summary>
public sealed class ToolFlowUploadClient
{
    private static readonly HttpClient ConfiguredHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly Uri _flowsUri;
    private readonly Func<bool> _enabled;
    private readonly ToolFlowSelectionStore _selections;
    private readonly string _receiptDirectory;
    private readonly SemaphoreSlim _gate;

    /// <param name="endpoint">显式配置的 API 基地址；只允许 HTTPS 或 HTTP 环回地址。</param>
    /// <param name="dataRoot">回执数据根；测试可注入临时假根，省略时使用应用数据根。</param>
    public ToolFlowUploadClient(
        HttpClient httpClient,
        Uri endpoint,
        Func<bool> enabled,
        ToolFlowSelectionStore selections,
        string? dataRoot = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _selections = selections ?? throw new ArgumentNullException(nameof(selections));
        ValidateEndpoint(endpoint);

        // Uri(base, relative) replaces the last segment unless the base ends in '/'.
        var baseUri = endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);
        _flowsUri = new Uri(baseUri, "v1/toolflows");

        // Receipts are scoped to the endpoint. Changing servers must not reuse another
        // server's acknowledgements, even when submission IDs happen to be identical.
        var endpointKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(baseUri.AbsoluteUri)));
        var root = Path.GetFullPath(dataRoot ?? ConfigManager.GetDataDir());
        _receiptDirectory = Path.Combine(root, "ToolFlows", "UploadReceipts", endpointKey);
        _gate = Gates.GetOrAdd(_receiptDirectory, static _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// 按分享开关创建官方上传器，关闭时返回 null；不读取旧版自填地址。
    /// 调用此方法本身不会发送网络请求。
    /// </summary>
    public static ToolFlowUploadClient? TryCreateConfigured(
        ToolFlowSelectionStore selections,
        string? dataRoot = null)
    {
        // 假根/单测隔离中不得创建使用真实公网传输的上传器。
        if (DataRoots.EffectiveTestRoot is not null) return null;
        return TryCreateConfigured(ConfiguredHttpClient, selections, dataRoot);
    }

    // 测试只替换传输，保留生产入口、开关与固定路由；不会实际联网。
    internal static ToolFlowUploadClient? TryCreateConfigured(
        HttpClient httpClient, ToolFlowSelectionStore selections, string? dataRoot = null)
    {
        if (!AppSettings.IsToolflowUploadEnabled) return null;
        return new ToolFlowUploadClient(httpClient, ToolFlowUploadService.OfficialEndpoint,
            () => AppSettings.IsToolflowUploadEnabled, selections, dataRoot);
    }

    /// <summary>
    /// 逐条补发本地已选记录。2xx 才持久记为已送达；失败保留待发，下一次调用重试。
    /// 执行前及每次发请求前重新读取开关，关闭后不再发起下一请求。
    /// </summary>
    public async Task<ToolFlowUploadResult> UploadPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selections = _selections.ListAll()
                .Where(x => x.UploadEnabledAtSelection
                    && IsConversationUploadSupported(x.Conversation)
                    && !ToolFlowUploadLatch.IsRevoked(x.SelectedAtUtc))
                .ToArray();
            var successful = 0;
            var failed = 0;

            foreach (var selection in selections)
            {
                if (!_enabled()) break;
                // 发送前重新核对"开关世代"（fail-closed）：已被关闭开关撤销的选定不得发送或追认。
                if (ToolFlowUploadLatch.IsRevoked(selection.SelectedAtUtc)) continue;
                var receipt = ReadReceipt(selection);

                if (!receipt.FlowUploaded)
                {
                    if (ToolFlowUploadLatch.IsRevoked(selection.SelectedAtUtc)) continue;
                    var sent = await TryPostAsync(_flowsUri, FlowPayload(selection),
                        selection.SelectedAtUtc, cancellationToken)
                        .ConfigureAwait(false);
                    if (!sent)
                    {
                        failed++;
                        continue; // An event cannot precede its parent flow.
                    }

                    receipt.FlowUploaded = true;
                    WriteReceipt(selection, receipt);
                    successful++;
                }

                foreach (var executionEvent in selection.Events)
                {
                    if (receipt.EventIds.Contains(executionEvent.Id)) continue;
                    if (!_enabled()) break;
                    if (ToolFlowUploadLatch.IsRevoked(selection.SelectedAtUtc)) continue;

                    var eventUri = new Uri(_flowsUri.AbsoluteUri.TrimEnd('/') + "/" +
                        Uri.EscapeDataString(selection.FlowId) + "/events", UriKind.Absolute);
                    var sent = await TryPostAsync(eventUri, EventPayload(executionEvent),
                        selection.SelectedAtUtc, cancellationToken)
                        .ConfigureAwait(false);
                    if (!sent)
                    {
                        failed++;
                        continue;
                    }

                    receipt.EventIds.Add(executionEvent.Id);
                    WriteReceipt(selection, receipt);
                    successful++;
                }
            }

            var pending = 0;
            foreach (var selection in selections)
            {
                // 被撤销的记录不会发送，也不计入"待重试"。
                if (ToolFlowUploadLatch.IsRevoked(selection.SelectedAtUtc)) continue;
                var receipt = ReadReceipt(selection);
                if (!receipt.FlowUploaded) pending++;
                pending += selection.Events.Count(e => !receipt.EventIds.Contains(e.Id));
            }

            return new ToolFlowUploadResult(successful, failed, pending);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> TryPostAsync(Uri uri, object payload, DateTimeOffset selectedAtUtc,
        CancellationToken cancellationToken)
    {
        // 最佳努力预检（后面的核对为准，此处仅省掉无谓的请求构造）。
        if (!_enabled()) return false;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(SerializeRedacted(payload), Encoding.UTF8, "application/json"),
        };

        try
        {
            // 发送紧前以同一门闩核对资格（锁内原子启动发送）：快速"关闭→重开"或撤销
            // 恰好发生在核对处时，旧请求不得跨出撤销边界。
            var sendTask = ToolFlowUploadLatch.TryStartSend(selectedAtUtc, _enabled,
                () => _httpClient.SendAsync(request, cancellationToken));
            if (sendTask is null) return false;
            using var response = await sendTask.ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeout is retryable. Caller cancellation still propagates.
            return false;
        }
    }

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 序列化上传载荷，并对载荷中的<strong>全部字符串字段</strong>统一套用轻量凭据过滤：
    /// 覆盖项目/方案/对话与 items 的名称、类型、版本、来源/下载网址、固定安装目标代码，
    /// 以及执行事件的详情等所有嵌套字段；以后新增字段自动受同一保护。
    /// 过滤只影响上传载荷，不改本地记录。
    /// </summary>
    private static string SerializeRedacted(object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload)
            ?? throw new InvalidDataException(MiscTexts.T("上传载荷为空。"));
        RedactStrings(node);
        return node.ToJsonString(PayloadJsonOptions);
    }

    private static void RedactStrings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(x => x.Key).ToArray())
                {
                    if (obj[name] is JsonValue text && text.TryGetValue<string>(out var raw))
                        obj[name] = ToolFlowPayloadRedactor.Redact(raw);
                    else
                        RedactStrings(obj[name]);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue text && text.TryGetValue<string>(out var raw))
                        array[index] = ToolFlowPayloadRedactor.Redact(raw);
                    else
                        RedactStrings(array[index]);
                }
                break;
        }
    }

    private static object FlowPayload(ToolFlowSelection selection) => new
    {
        schemaVersion = 1,
        submissionId = selection.SubmissionId,
        flowId = selection.FlowId,
        origin = selection.Origin switch
        {
            ToolFlowOrigin.Assistant => "assistant",
            ToolFlowOrigin.User => "user",
            _ => throw new InvalidDataException("Unknown tool flow origin."),
        },
        selectedAt = selection.SelectedAtUtc,
        // 载荷在发送前由 SerializeRedacted 对全部字符串字段统一过滤；此处保留原始值。
        flowName = selection.FlowName,
        projectGoal = selection.ProjectGoal,
        goalDescription = selection.GoalDescription,
        flowText = selection.FlowText,
        conversation = selection.Conversation.Select(message => new
        {
            role = message.Role,
            content = message.Content,
            at = message.AtUtc,
        }).ToArray(),
        items = selection.Items.Select(item => new
        {
            itemId = item.ItemId,
            name = item.Name,
            kind = item.Kind,
            version = item.Version,
            sourceUrl = item.SourceUrl,
            downloadUrl = item.DownloadUrl,
            installTargetKey = item.InstallTargetKey,
        }).ToArray(),
    };

    private static object EventPayload(ToolFlowExecutionEvent executionEvent) => new
    {
        eventId = executionEvent.Id,
        itemId = executionEvent.ItemId,
        kind = executionEvent.Kind switch
        {
            ToolFlowEventKind.DownloadStarted => "download_started",
            ToolFlowEventKind.DownloadSucceeded => "download_succeeded",
            ToolFlowEventKind.DownloadFailed => "download_failed",
            ToolFlowEventKind.InstallSucceeded => "install_succeeded",
            ToolFlowEventKind.InstallFailed => "install_failed",
            ToolFlowEventKind.Verified => "verified",
            _ => throw new InvalidDataException("Unknown tool flow event kind."),
        },
        at = executionEvent.AtUtc,
        detail = executionEvent.Detail,
    };

    private UploadReceipt ReadReceipt(ToolFlowSelection selection)
    {
        var path = ReceiptPath(selection.SubmissionId);
        if (!File.Exists(path)) return NewReceipt(selection);
        try
        {
            var receipt = JsonSerializer.Deserialize<UploadReceipt>(File.ReadAllText(path));
            if (receipt is null || receipt.SchemaVersion != 1 ||
                receipt.SubmissionId != selection.SubmissionId ||
                receipt.FlowId != selection.FlowId || receipt.EventIds is null)
                throw new InvalidDataException("Upload receipt does not match its selection.");
            return receipt;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Upload receipt is damaged.", ex);
        }
    }

    private static UploadReceipt NewReceipt(ToolFlowSelection selection) => new()
    {
        SchemaVersion = 1,
        SubmissionId = selection.SubmissionId,
        FlowId = selection.FlowId,
        EventIds = [],
    };

    private void WriteReceipt(ToolFlowSelection selection, UploadReceipt receipt)
    {
        var path = ReceiptPath(selection.SubmissionId);
        Directory.CreateDirectory(_receiptDirectory);
        var temp = Path.Combine(_receiptDirectory, "." + selection.SubmissionId +
            "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(receipt));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private string ReceiptPath(string submissionId) =>
        Path.Combine(_receiptDirectory, submissionId + ".json");

    private static void ValidateEndpoint(Uri? endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || string.IsNullOrEmpty(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !(endpoint.Scheme == Uri.UriSchemeHttps ||
              (endpoint.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(endpoint.Host))))
            throw new ArgumentException("Endpoint must be HTTPS or HTTP loopback without credentials, query or fragment.",
                nameof(endpoint));
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    /// <summary>
    /// 校验构造函数注入的服务地址（供本地测试复用）。空字符串返回 true、endpoint 为 null；
    /// 非空但无效时返回 false。只允许 HTTPS 或本机 HTTP，不带用户名、查询或片段。
    /// 生产入口固定，不通过此方法解析用户设置。
    /// </summary>
    public static bool TryParseEndpoint(string? value, out Uri? endpoint, out string error)
    {
        endpoint = null;
        error = "";
        var text = value?.Trim() ?? "";
        if (text.Length == 0) return true;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = "Endpoint must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        try
        {
            ValidateEndpoint(uri);
        }
        catch (ArgumentException)
        {
            error = "Endpoint must be HTTPS or HTTP loopback without credentials, query or fragment.";
            return false;
        }

        endpoint = uri;
        return true;
    }

    /// <summary>
    /// 选定当时分享开关的判定；带对话的选定还须使用下方重载核对服务接收上限。
    /// 只应把"此刻"的判定值固定进选定快照；真正的发送资格由快照上的
    /// UploadEnabledAtSelection 决定，关闭开关会作废既有资格（见 ToolFlowSelectionStore）。
    /// </summary>
    public static bool IsUploadEligible(bool switchOn) => switchOn;

    /// <summary>
    /// 官方分析服务接收最多 200 条、每条最多 8192 字的完整对话。
    /// 超限仅影响分享，本机选定与准备流程仍保留全部原文。
    /// </summary>
    public static bool IsConversationUploadSupported(IReadOnlyCollection<ToolFlowConversationMessage> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return conversation.Count <= 200
            && conversation.All(message => message is not null && message.Content is not null
                && message.Content.Length <= 8192);
    }

    /// <summary>将开关与完整对话的接收资格一起固定进本次选定快照；不截断或拆分原文。</summary>
    public static bool IsUploadEligible(bool switchOn, IReadOnlyCollection<ToolFlowConversationMessage> conversation)
        => IsUploadEligible(switchOn) && IsConversationUploadSupported(conversation);

    /// <summary>按当前分享开关判定"此刻"的选定资格，不读取旧版自填地址。</summary>
    public static bool IsUploadEligibleNow()
        => IsUploadEligible(AppSettings.IsToolflowUploadEnabled);

    /// <summary>超限对话只在本机选定，不能因日后开关重开而补传。</summary>
    public static bool IsUploadEligibleNow(IReadOnlyCollection<ToolFlowConversationMessage> conversation)
        => IsUploadEligible(AppSettings.IsToolflowUploadEnabled, conversation);

    private sealed class UploadReceipt
    {
        public int SchemaVersion { get; set; }
        public string SubmissionId { get; set; } = "";
        public string FlowId { get; set; } = "";
        public bool FlowUploaded { get; set; }
        public HashSet<string> EventIds { get; set; } = [];
    }
}
