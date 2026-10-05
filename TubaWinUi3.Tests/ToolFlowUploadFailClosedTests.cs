using System.Net;
using System.Text.Json;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>
/// fail-closed 窄测试：关闭分享开关的内存级撤销门闩（ToolFlowUploadLatch）。
/// ① 磁盘清理失败时，旧选定/旧计数也不能被发送；② 快速关闭重开时，
/// 已被 UploadPendingAsync 取出的快照不得把撤销前的数据补传出去；
/// ③ 清理失败后重启：重新开启前必须先作废旧记录（拒绝开启直到作废成功）；
/// ④ 撤销恰好发生在发送核对处（锁内原子启动发送）时，旧请求不得跨出撤销边界。
/// 全部使用假数据根与假 HttpMessageHandler：不联网、不触真实数据目录、不读写真实设置。
/// </summary>
public sealed class ToolFlowUploadFailClosedTests : IDisposable
{
    private static readonly Uri Endpoint = new("https://example.test/api/");
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "zxai-toolflow-failclosed-" + Guid.NewGuid().ToString("N"));

    public ToolFlowUploadFailClosedTests() => ToolFlowUploadLatch.ResetForTests();

    [Fact]
    public async Task SelectionDiskCleanupFailureStillBlocksSending_AndReportsFailure()
    {
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(NewSelection());

        // 模拟磁盘清理失败：记录文件置为只读，作废写盘会抛 UnauthorizedAccessException。
        var recordPath = Path.Combine(_root, "ToolFlows", "Selections", selection.SubmissionId + ".json");
        File.SetAttributes(recordPath, FileAttributes.ReadOnly);
        var cleaned = store.InvalidatePendingUploads();

        Assert.False(cleaned);                                    // 设置页可以得知清理失败
        Assert.NotNull(ToolFlowUploadLatch.RevokedBeforeUtc);      // 内存门闩已立即生效
        Assert.True(store.GetBySubmissionId(selection.SubmissionId)!.UploadEnabledAtSelection);   // 磁盘标记未能清掉

        using (var handler = new FakeHandler(_ => HttpStatusCode.Created))
        using (var http = new HttpClient(handler))
        {
            var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);
            Assert.Equal(new ToolFlowUploadResult(0, 0, 0), await client.UploadPendingAsync());
            Assert.Empty(handler.Requests);                        // 门闩兜底：旧选定不得发送
        }

        File.SetAttributes(recordPath, FileAttributes.Normal);
        Assert.Single(store.ListAll());                            // 本地方案保留
    }

    [Fact]
    public async Task CountDiskCleanupFailureDoesNotResendOldCountsInTheNextBatch()
    {
        var store = new DownloadMetricsStore(_root, () => true);
        store.ReportButtonClicked("CPU-Z");                        // 旧计数

        var status = HttpStatusCode.ServiceUnavailable;
        using var handler = new FakeHandler(_ => status);
        using var http = new HttpClient(handler);
        var uploader = new DownloadMetricsUploader(http, Endpoint, () => true, store);

        Assert.False(await uploader.UploadPendingAsync());         // 冻结旧批次（发送失败，批次留在磁盘）
        Assert.Single(handler.Requests);
        Assert.Single(store.Snapshot());

        // 模拟磁盘清理失败：关闭时清理写盘失败，但内存门闩必须立即生效。
        var countersPath = Path.Combine(_root, "ToolFlows", "DownloadMetrics", "counters.json");
        File.SetAttributes(countersPath, FileAttributes.ReadOnly);
        Assert.False(store.ClearPending());
        Assert.NotNull(ToolFlowUploadLatch.RevokedBeforeUtc);
        File.SetAttributes(countersPath, FileAttributes.Normal);

        store.ReportButtonClicked("CPU-Z");                        // 重新开启后的新事件
        status = HttpStatusCode.Created;
        Assert.True(await uploader.UploadPendingAsync());

        // 只发了新批次：旧批次作废，且旧计数没有混进新批次。
        Assert.Equal(2, handler.Requests.Count);
        Assert.NotEqual(BatchIdOf(handler.Requests[0].Json), BatchIdOf(handler.Requests[1].Json));
        using (var document = JsonDocument.Parse(handler.Requests[1].Json))
        {
            var counts = document.RootElement.GetProperty("counts");
            Assert.Equal(1, counts.GetArrayLength());
            Assert.Equal(1, counts[0].GetProperty("count").GetInt32());
        }

        // 作废快照残留不会被再次发送。
        Assert.True(await uploader.UploadPendingAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RevocationMidUploadBlocksStaleSnapshot_ButFreshSelectionStillSends()
    {
        var store = new ToolFlowSelectionStore(_root);
        var first = store.SelectForInstall(NewSelection(selectedAt: DateTimeOffset.UtcNow.AddMinutes(-40)));
        store.AppendEvent(first.SubmissionId, NewEvent(first.Items[0].ItemId));
        var second = store.SelectForInstall(NewSelection(selectedAt: DateTimeOffset.UtcNow.AddMinutes(-30)));

        // 上传进行中（首个请求）用户关闭了开关——上传器持有的快照里 first/second 都还是"可发送"。
        var revokeOnFirstRequest = true;
        using var handler = new FakeHandler(_ =>
        {
            if (revokeOnFirstRequest)
            {
                revokeOnFirstRequest = false;
                ToolFlowUploadLatch.RevokeAll();
            }
            return HttpStatusCode.Created;
        });
        using var http = new HttpClient(handler);
        var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);

        // 只有撤销前已经发出的那一个请求成功；快照里的其余数据被门闩拦下。
        Assert.Equal(new ToolFlowUploadResult(1, 0, 0), await client.UploadPendingAsync());
        var request = Assert.Single(handler.Requests);
        Assert.Contains(second.SubmissionId, request.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(first.SubmissionId, request.Json, StringComparison.Ordinal);

        // 重开后新做的选定（晚于撤销时刻）可以正常发送；旧数据继续不发。
        var fresh = store.SelectForInstall(NewSelection(selectedAt: DateTimeOffset.UtcNow));
        Assert.Equal(new ToolFlowUploadResult(1, 0, 0), await client.UploadPendingAsync());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(fresh.SubmissionId, handler.Requests[1].Json, StringComparison.Ordinal);
        Assert.DoesNotContain(first.SubmissionId, handler.Requests[1].Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReopenIsRefusedUntilStalePendingIsVoided_AcrossRestart()
    {
        var store = new ToolFlowSelectionStore(_root);
        var stale = store.SelectForInstall(NewSelection());
        var metrics = new DownloadMetricsStore(_root, () => true);
        metrics.ReportButtonClicked("CPU-Z");

        // 上次运行：关闭开关时磁盘清理失败（记录与计数文件均只读），随后应用被重启
        //（内存门闩不跨进程，这里显式复位模拟）。
        var recordPath = Path.Combine(_root, "ToolFlows", "Selections", stale.SubmissionId + ".json");
        var countersPath = Path.Combine(_root, "ToolFlows", "DownloadMetrics", "counters.json");
        File.SetAttributes(recordPath, FileAttributes.ReadOnly);
        File.SetAttributes(countersPath, FileAttributes.ReadOnly);
        Assert.False(ToolFlowUploadSwitch.TryVoidPendingUploads(_root));
        ToolFlowUploadLatch.ResetForTests();                       // = 重启进程（门闩丢失）

        // 重启后用户重新开启：旧记录仍未作废 → 拒绝开启（保持关闭），且门闩重新生效兜底。
        Assert.False(ToolFlowUploadSwitch.TryVoidPendingUploads(_root));
        Assert.NotNull(ToolFlowUploadLatch.RevokedBeforeUtc);
        Assert.True(store.GetBySubmissionId(stale.SubmissionId)!.UploadEnabledAtSelection);

        using (var handler = new FakeHandler(_ => HttpStatusCode.Created))
        using (var http = new HttpClient(handler))
        {
            var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);
            Assert.Equal(new ToolFlowUploadResult(0, 0, 0), await client.UploadPendingAsync());
            Assert.Empty(handler.Requests);                        // 即使有人绕过开关，旧记录也不发送
        }

        // 磁盘恢复后再开启：先完成作废（磁盘标记清掉）才允许开启；旧数据不会再被补传。
        File.SetAttributes(recordPath, FileAttributes.Normal);
        File.SetAttributes(countersPath, FileAttributes.Normal);
        Assert.True(ToolFlowUploadSwitch.TryVoidPendingUploads(_root));
        Assert.False(store.GetBySubmissionId(stale.SubmissionId)!.UploadEnabledAtSelection);
        Assert.Empty(metrics.Snapshot());

        ToolFlowUploadLatch.ResetForTests();                       // 再"重启"一次：磁盘上已无可补传数据
        using (var handler = new FakeHandler(_ => HttpStatusCode.Created))
        using (var http = new HttpClient(handler))
        {
            var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);
            Assert.Equal(new ToolFlowUploadResult(0, 0, 0), await client.UploadPendingAsync());
            Assert.Empty(handler.Requests);                        // 跨进程：旧记录依然不发送
        }

        // 重开后新做的选定正常发送。
        var fresh = store.SelectForInstall(NewSelection(selectedAt: DateTimeOffset.UtcNow));
        using (var handler = new FakeHandler(_ => HttpStatusCode.Created))
        using (var http = new HttpClient(handler))
        {
            var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);
            Assert.Equal(new ToolFlowUploadResult(1, 0, 0), await client.UploadPendingAsync());
            var request = Assert.Single(handler.Requests);
            Assert.Contains(fresh.SubmissionId, request.Json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RevocationRacingTheSendChecksStillBlocksOldRequests()
    {
        var store = new ToolFlowSelectionStore(_root);
        var stale = store.SelectForInstall(NewSelection(selectedAt: DateTimeOffset.UtcNow.AddMinutes(-30)));

        // 模拟"快速关闭→重开"恰好落在发送前的资格核对处：第二次被询问开关状态时撤销已发生、
        // 开关已恢复为开（返回 true）——旧请求仍不得跨出撤销边界（锁内原子核对）。
        var probes = 0;
        using (var handler = new FakeHandler(_ => HttpStatusCode.Created))
        using (var http = new HttpClient(handler))
        {
            var client = new ToolFlowUploadClient(http, Endpoint, () =>
            {
                probes++;
                if (probes == 2) ToolFlowUploadLatch.RevokeAll();
                return true;
            }, store, _root);

            Assert.Equal(new ToolFlowUploadResult(0, 1, 0), await client.UploadPendingAsync());
            Assert.Empty(handler.Requests);
        }

        // 对照：晚于撤销时刻的新选定正常发送。
        var fresh = store.SelectForInstall(NewSelection(selectedAt: DateTimeOffset.UtcNow));
        using (var handler = new FakeHandler(_ => HttpStatusCode.Created))
        using (var http = new HttpClient(handler))
        {
            var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);
            Assert.Equal(new ToolFlowUploadResult(1, 0, 0), await client.UploadPendingAsync());
            var request = Assert.Single(handler.Requests);
            Assert.Contains(fresh.SubmissionId, request.Json, StringComparison.Ordinal);
        }

        // 计数批次同样不能跨撤销边界：撤销发生在发送核对处时，旧批次不得发出。
        var metricsStore = new DownloadMetricsStore(_root, () => true);
        metricsStore.ReportButtonClicked("CPU-Z");
        var metricsProbes = 0;
        using (var handler = new FakeHandler(_ => HttpStatusCode.Created))
        using (var http = new HttpClient(handler))
        {
            var uploader = new DownloadMetricsUploader(http, Endpoint, () =>
            {
                metricsProbes++;
                if (metricsProbes == 2) ToolFlowUploadLatch.RevokeAll();
                return true;
            }, metricsStore);

            Assert.False(await uploader.UploadPendingAsync());
            Assert.Empty(handler.Requests);
        }
    }

    private static string BatchIdOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("batchId").GetString()!;
    }

    private static ToolFlowSelection NewSelection(DateTimeOffset? selectedAt = null) => new()
    {
        FlowId = Guid.NewGuid().ToString(),
        SubmissionId = Guid.NewGuid().ToString(),
        Origin = ToolFlowOrigin.Assistant,
        SelectedAtUtc = selectedAt ?? DateTimeOffset.UtcNow.AddMinutes(-30),
        FlowName = "2D 游戏开发工作流",
        ProjectGoal = "我想做一款 2D 游戏",
        GoalDescription = "做一款 2D 游戏",
        FlowText = "先安装 Godot。",
        UploadEnabledAtSelection = true,
        Conversation = [new ToolFlowConversationMessage
        {
            Role = "user", Content = "我想做游戏", AtUtc = null,
        }],
        Items = [new ToolFlowItem
        {
            ItemId = Guid.NewGuid().ToString(),
            Name = "Godot", Kind = "development", Version = "4.x",
            SourceUrl = "https://example.test/",
            DownloadUrl = "https://example.test/tool.zip",
            InstallTargetKey = "godot",
        }],
    };

    private static ToolFlowExecutionEvent NewEvent(string itemId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = itemId,
        Kind = ToolFlowEventKind.Verified,
        AtUtc = new DateTimeOffset(2026, 9, 24, 9, 10, 0, TimeSpan.Zero),
        Detail = "本地步骤完成",
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        ToolFlowUploadLatch.ResetForTests();
    }

    private sealed class FakeHandler(Func<int, HttpStatusCode> statusForRequest) : HttpMessageHandler
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
