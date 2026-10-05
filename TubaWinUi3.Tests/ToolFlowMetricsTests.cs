using System.Net;
using System.Text.Json;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>
/// 结构化计数（下载按钮点击 / 真实下载请求）、上报门控与轻量凭据过滤的关键测试。
/// 全部使用注入的假数据根与假 HttpMessageHandler：不联网、不触真实数据目录。
/// </summary>
public sealed class ToolFlowMetricsTests : IDisposable
{
    private static readonly Uri Endpoint = new("https://example.test/api/");
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "zxai-toolflow-metrics-" + Guid.NewGuid().ToString("N"));

    public ToolFlowMetricsTests() => ToolFlowUploadLatch.ResetForTests();

    [Fact]
    public void CountersAggregatePersistAndSubtractOnlyTheConfirmedSnapshot()
    {
        var store = new DownloadMetricsStore(_root, () => true);
        store.ReportButtonClicked("CPU-Z");
        store.ReportButtonClicked("CPU-Z");
        store.ReportRequestStarted("CPU-Z");
        store.ReportRequestSucceeded("CPU-Z");
        store.ReportRequestFailed("GPU-Z\nbad");

        var reopened = new DownloadMetricsStore(_root, () => true);
        var snapshot = reopened.Snapshot();
        Assert.Equal(new[]
        {
            (DownloadMetricsStore.ClickedKind, "CPU-Z", 2),
            (DownloadMetricsStore.FailedKind, "GPU-Z bad", 1),
            (DownloadMetricsStore.RequestedKind, "CPU-Z", 1),
            (DownloadMetricsStore.SucceededKind, "CPU-Z", 1),
        }, snapshot.Select(x => (x.Kind, x.Tool, x.Count)).ToArray());

        // 快照发送期间新增的计数不得被扣除。
        reopened.ReportButtonClicked("GPU-Z");
        reopened.ConfirmSent(snapshot);
        var after = reopened.Snapshot();
        Assert.DoesNotContain(after, x => x.Tool == "CPU-Z");
        Assert.Contains(after, x => x.Tool == "GPU-Z" &&
            x.Kind == DownloadMetricsStore.ClickedKind && x.Count == 1);
    }

    [Fact]
    public async Task DisabledSwitchSendsNothingAndKeepsLocalCounts()
    {
        var store = new DownloadMetricsStore(_root, () => true);
        store.ReportButtonClicked("CPU-Z");
        using var handler = new MetricsHandler(_ => HttpStatusCode.Created);
        using var http = new HttpClient(handler);
        var uploader = new DownloadMetricsUploader(http, Endpoint, () => false, store);

        Assert.False(await uploader.UploadPendingAsync());
        Assert.Empty(handler.Requests);
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public async Task UploadsStructuredCountsOnceAndClearsThemOnSuccess()
    {
        var store = new DownloadMetricsStore(_root, () => true);
        store.ReportButtonClicked("CPU-Z");
        store.ReportButtonClicked("CPU-Z");
        store.ReportRequestStarted("CPU-Z");
        using var handler = new MetricsHandler(_ => HttpStatusCode.Created);
        using var http = new HttpClient(handler);
        var uploader = new DownloadMetricsUploader(http, Endpoint, () => true, store);

        Assert.True(await uploader.UploadPendingAsync());
        Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/metrics", handler.Requests[0].Path);

        using (var document = JsonDocument.Parse(handler.Requests[0].Json))
        {
            var root = document.RootElement;
            Assert.Equal(new[] { "batchId", "counts", "schemaVersion" },
                root.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.True(Guid.TryParseExact(root.GetProperty("batchId").GetString(), "D", out _));
            var counts = root.GetProperty("counts");
            Assert.Equal(2, counts.GetArrayLength());
            Assert.Equal("download_clicked", counts[0].GetProperty("kind").GetString());
            Assert.Equal("CPU-Z", counts[0].GetProperty("tool").GetString());
            Assert.Equal(2, counts[0].GetProperty("count").GetInt32());
            Assert.Equal("download_requested", counts[1].GetProperty("kind").GetString());
        }

        Assert.Empty(store.Snapshot());
        Assert.True(await uploader.UploadPendingAsync());   // 已无待发：不发请求
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FailedBatchRetriesWithPersistedId_ThenIdenticalSnapshotCountsAsANewBatch()
    {
        var store = new DownloadMetricsStore(_root, () => true);
        store.ReportButtonClicked("CPU-Z");
        var status = HttpStatusCode.ServiceUnavailable;
        using var handler = new MetricsHandler(_ => status);
        using var http = new HttpClient(handler);
        var uploader = new DownloadMetricsUploader(http, Endpoint, () => true, store);

        Assert.False(await uploader.UploadPendingAsync());
        Assert.Single(store.Snapshot());

        // 重启（新存储实例 + 新上传器）后重试：复用同一冻结批次的持久化 ID，服务端可据此去重、不重复计数。
        var restartedStore = new DownloadMetricsStore(_root, () => true);
        var restartedUploader = new DownloadMetricsUploader(http, Endpoint, () => true, restartedStore);
        status = HttpStatusCode.Created;
        Assert.True(await restartedUploader.UploadPendingAsync());
        Assert.Equal(2, handler.Requests.Count);
        var retryId = BatchIdOf(handler.Requests[1].Json);
        Assert.Equal(BatchIdOf(handler.Requests[0].Json), retryId);
        Assert.Empty(restartedStore.Snapshot());

        // 之后出现与第一批完全相同的新计数快照：必须是新的批次 ID（两次都被服务端分别计数）。
        restartedStore.ReportButtonClicked("CPU-Z");
        Assert.True(await restartedUploader.UploadPendingAsync());
        Assert.Equal(3, handler.Requests.Count);
        var secondId = BatchIdOf(handler.Requests[2].Json);
        Assert.NotEqual(retryId, secondId);
        Assert.Equal(CountsOf(handler.Requests[0].Json), CountsOf(handler.Requests[2].Json));   // 快照内容相同
        Assert.Empty(restartedStore.Snapshot());
    }

    [Fact]
    public void SwitchOffStopsCountingAndVoidsUnsentCounts_SoReopenStartsFromNewEvents()
    {
        var enabled = true;
        var store = new DownloadMetricsStore(_root, () => enabled);
        store.ReportButtonClicked("CPU-Z");          // 关闭前：进入待发
        enabled = false;
        store.ReportButtonClicked("GPU-Z");          // 关闭期间：不累计待发
        store.ReportButtonClicked("GPU-Z");
        store.ClearPending();                        // 设置页关闭分享开关时的动作：作废未发送计数
        enabled = true;
        store.ReportButtonClicked("CPU-Z");          // 重开后的新事件

        var snapshot = store.Snapshot();
        Assert.Equal(new[] { (DownloadMetricsStore.ClickedKind, "CPU-Z", 1) },
            snapshot.Select(x => (x.Kind, x.Tool, x.Count)).ToArray());
    }

    [Fact]
    public async Task SharingOffCountsAreNotKeptForLaterUpload_DespiteLegacyEndpointChanges()
    {
        // 使用生产默认分享开关与隔离设置；旧地址不影响资格，关闭期间不保留计数。
        var isoRoot = Path.Combine(_root, "settings");
        Directory.CreateDirectory(isoRoot);
        var previous = DataRoots.TestRootOverrideForTest;
        DataRoots.TestRootOverrideForTest = isoRoot;
        try
        {
            ConfigManager.SetConfigLocation(ConfigLocation.AppData);
            AppSettings.InvalidateCache();

            Assert.True(AppSettings.IsToolflowUploadEnabled);
            Assert.True(ToolFlowUploadClient.IsUploadEligibleNow());
            AppSettings.Set(AppSettings.ToolflowUploadEnabledKey, false);
            Assert.False(ToolFlowUploadClient.IsUploadEligibleNow());
            var store = new DownloadMetricsStore();                 // 真实默认门
            store.ReportButtonClicked("Godot");
            store.ReportRequestStarted("Godot");
            Assert.Empty(store.Snapshot());

            AppSettings.Set("ToolflowUploadEndpoint", "https://example.test/");
            Assert.False(ToolFlowUploadClient.IsUploadEligibleNow());
            AppSettings.Set(AppSettings.ToolflowUploadEnabledKey, true);
            Assert.True(ToolFlowUploadClient.IsUploadEligibleNow());

            using var handler = new MetricsHandler(_ => HttpStatusCode.Created);
            using var http = new HttpClient(handler);
            // 此工厂只替换HTTP传输；目的地与资格仍走生产固定入口。
            var uploader = DownloadMetricsUploader.TryCreateConfigured(http, store)!;
            Assert.NotNull(uploader);
            Assert.True(await uploader.UploadPendingAsync());
            Assert.Empty(handler.Requests);
            Assert.Null(store.TryBeginBatch());

            // 重开后只累计新点击。
            store.ReportButtonClicked("Godot");
            Assert.Equal(new[] { (DownloadMetricsStore.ClickedKind, "Godot", 1) },
                store.Snapshot().Select(x => (x.Kind, x.Tool, x.Count)).ToArray());
        }
        finally
        {
            // 去抖写盘窗口内先清脏缓存再释放隔离根：测试数据绝不落进真实目录（2026-09-23 事故根因）。
            AppSettings.InvalidateCache();
            ConfigManager.SetConfigLocation(ConfigLocation.AppData);
            DataRoots.TestRootOverrideForTest = previous;
        }
    }

    [Fact]
    public async Task LegacyV1CountersFileIsVoidedInsteadOfBeingUploadedLater()
    {
        // 迁移负控：旧预览版（schema v1）文件里遗留的计数与冻结批次，
        // 即使后来补填了地址、资格开启，也不得被上传。
        var dir = Path.Combine(_root, "ToolFlows", "DownloadMetrics");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "counters.json"), """
            {
              "schemaVersion": 1,
              "entries": [
                { "kind": "download_clicked", "tool": "CPU-Z", "count": 3 }
              ],
              "pendingBatch": {
                "batchId": "00000000-0000-0000-0000-000000000001",
                "createdAtUtc": "2026-09-23T00:00:00+00:00",
                "counts": [ { "kind": "download_clicked", "tool": "CPU-Z", "count": 3 } ]
              }
            }
            """);

        var store = new DownloadMetricsStore(_root, () => true);   // 模拟"后来补填了地址，资格开启"

        Assert.Empty(store.Snapshot());                            // 旧计数整体作废
        Assert.Null(store.TryBeginBatch());                        // 冻结批次也不可发送

        using var handler = new MetricsHandler(_ => HttpStatusCode.Created);
        using var http = new HttpClient(handler);
        var uploader = new DownloadMetricsUploader(http, Endpoint, () => true, store);
        Assert.True(await uploader.UploadPendingAsync());
        Assert.Empty(handler.Requests);                            // 没有任何上传请求

        // 作废后从新事件计起；磁盘文件已升级为 v2，旧计数与冻结批次不再存在。
        store.ReportButtonClicked("CPU-Z");
        Assert.Equal(new[] { (DownloadMetricsStore.ClickedKind, "CPU-Z", 1) },
            store.Snapshot().Select(x => (x.Kind, x.Tool, x.Count)).ToArray());
        var text = File.ReadAllText(Path.Combine(dir, "counters.json"));
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("pendingBatch").ValueKind);
        Assert.DoesNotContain("2026-09-23", text);
    }

    [Fact]
    public void RedactorStripsObviousCredentialShapesButKeepsNormalText()
    {
        Assert.Equal("我的 key 是 [已过滤]",
            ToolFlowPayloadRedactor.Redact("我的 key 是 sk-abc123def456ghi789"));
        var assignment = ToolFlowPayloadRedactor.Redact("apiKey=abcdef1234567890");
        Assert.DoesNotContain("abcdef1234567890", assignment);
        Assert.Contains("apiKey=", assignment);
        var bearer = ToolFlowPayloadRedactor.Redact(
            "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", bearer);
        Assert.Equal("普通对话内容，含 sk 两个字母",
            ToolFlowPayloadRedactor.Redact("普通对话内容，含 sk 两个字母"));
        Assert.Equal("", ToolFlowPayloadRedactor.Redact(null));
    }

    [Fact]
    public void EndpointParserAcceptsHttpsAndLoopbackOnly()
    {
        Assert.True(ToolFlowUploadClient.TryParseEndpoint("", out var empty, out _));
        Assert.Null(empty);
        Assert.True(ToolFlowUploadClient.TryParseEndpoint("   ", out empty, out _));
        Assert.Null(empty);
        Assert.True(ToolFlowUploadClient.TryParseEndpoint("https://example.test", out var https, out _));
        Assert.Equal("https://example.test/", https!.AbsoluteUri);
        Assert.True(ToolFlowUploadClient.TryParseEndpoint("http://127.0.0.1:8768", out var loopback, out _));
        Assert.Equal("http://127.0.0.1:8768/", loopback!.AbsoluteUri);

        foreach (var invalid in new[]
        {
            "http://example.test/", "http://192.0.2.1/", "not a url",
            "https://user:pass@example.test/", "ftp://localhost/",
        })
        {
            Assert.False(ToolFlowUploadClient.TryParseEndpoint(invalid, out _, out var error));
            Assert.NotEqual("", error);
        }
    }

    private static string BatchIdOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("batchId").GetString()!;
    }

    private static string CountsOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("counts").GetRawText();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        ToolFlowUploadLatch.ResetForTests();
    }

    private sealed class MetricsHandler(Func<int, HttpStatusCode> statusForRequest) : HttpMessageHandler
    {
        public List<(string Path, string Json)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, json));
            return new HttpResponseMessage(statusForRequest(Requests.Count));
        }
    }
}
