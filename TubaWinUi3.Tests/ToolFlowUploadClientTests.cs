using System.Net;
using System.Text.Json;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowUploadClientTests : IDisposable
{
    private static readonly Uri Endpoint = new("https://example.test/api/");
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "zxai-toolflow-upload-" + Guid.NewGuid().ToString("N"));

    public ToolFlowUploadClientTests() => ToolFlowUploadLatch.ResetForTests();

    [Fact]
    public async Task UploadsExactFlowAndEventPayload_ThenUsesPersistedReceipts()
    {
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(NewSelection() with { ConversationId = "local-chat-not-for-upload" });
        var executionEvent = NewEvent(selection.Items[0].ItemId, ToolFlowEventKind.DownloadSucceeded);
        store.AppendEvent(selection.SubmissionId, executionEvent);
        using var handler = new FakeHandler(_ => HttpStatusCode.Created);
        using var http = new HttpClient(handler);
        var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);

        Assert.Equal(new ToolFlowUploadResult(2, 0, 0), await client.UploadPendingAsync());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/v1/toolflows", handler.Requests[0].Path);
        Assert.Equal($"/api/v1/toolflows/{selection.FlowId}/events", handler.Requests[1].Path);

        using (var document = JsonDocument.Parse(handler.Requests[0].Json))
        {
            var root = document.RootElement;
            Assert.Equal(new[] { "conversation", "flowId", "flowName", "flowText", "goalDescription", "items", "origin",
                "projectGoal", "schemaVersion", "selectedAt", "submissionId" },
                root.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(selection.SubmissionId, root.GetProperty("submissionId").GetString());
            Assert.Equal(selection.FlowId, root.GetProperty("flowId").GetString());
            Assert.Equal("assistant", root.GetProperty("origin").GetString());
            Assert.Equal(selection.SelectedAtUtc, root.GetProperty("selectedAt").GetDateTimeOffset());
            Assert.Equal(selection.FlowName, root.GetProperty("flowName").GetString());
            Assert.Equal(selection.ProjectGoal, root.GetProperty("projectGoal").GetString());
            Assert.Equal(selection.GoalDescription, root.GetProperty("goalDescription").GetString());
            Assert.Equal(selection.FlowText, root.GetProperty("flowText").GetString());
            var conversation = root.GetProperty("conversation")[0];
            Assert.Equal(new[] { "at", "content", "role" },
                conversation.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
            Assert.Equal(JsonValueKind.Null, conversation.GetProperty("at").ValueKind);
            var item = root.GetProperty("items")[0];
            Assert.Equal(new[] { "downloadUrl", "installTargetKey", "itemId", "kind", "name", "sourceUrl", "version" },
                item.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
            Assert.Equal("https://example.test/tool.zip", item.GetProperty("downloadUrl").GetString());
        }

        using (var document = JsonDocument.Parse(handler.Requests[1].Json))
        {
            var root = document.RootElement;
            Assert.Equal(new[] { "at", "detail", "eventId", "itemId", "kind" },
                root.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
            Assert.Equal(executionEvent.Id, root.GetProperty("eventId").GetString());
            Assert.Equal("download_succeeded", root.GetProperty("kind").GetString());
            Assert.Equal(executionEvent.AtUtc, root.GetProperty("at").GetDateTimeOffset());
        }

        var restarted = new ToolFlowUploadClient(http, Endpoint, () => true,
            new ToolFlowSelectionStore(_root), _root);
        Assert.Equal(new ToolFlowUploadResult(0, 0, 0), await restarted.UploadPendingAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CurrentOptOutMakesNoRequests_SelectionTimeOptOutIsNeverBackfilled()
    {
        var store = new ToolFlowSelectionStore(_root);
        var eligible = store.SelectForInstall(NewSelection());
        store.AppendEvent(eligible.SubmissionId, NewEvent(eligible.Items[0].ItemId,
            ToolFlowEventKind.InstallSucceeded));
        var optedOut = store.SelectForInstall(NewSelection(uploadEnabled: false));
        store.AppendEvent(optedOut.SubmissionId, NewEvent(optedOut.Items[0].ItemId,
            ToolFlowEventKind.Verified));
        var enabled = false;
        using var handler = new FakeHandler(_ => HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new ToolFlowUploadClient(http, Endpoint, () => enabled, store, _root);

        Assert.Equal(new ToolFlowUploadResult(0, 0, 2), await client.UploadPendingAsync());
        Assert.Empty(handler.Requests);

        enabled = true;
        Assert.Equal(new ToolFlowUploadResult(2, 0, 0), await client.UploadPendingAsync());
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, r => r.Json.Contains(optedOut.SubmissionId,
            StringComparison.Ordinal) || r.Path.Contains(optedOut.FlowId, StringComparison.Ordinal));

        enabled = false;
        Assert.Equal(new ToolFlowUploadResult(0, 0, 0), await client.UploadPendingAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FailedFlowAndEventStayPending_AndRetryWithoutRepeatingAcknowledgedFlow()
    {
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(NewSelection());
        store.AppendEvent(selection.SubmissionId, NewEvent(selection.Items[0].ItemId,
            ToolFlowEventKind.DownloadFailed));
        using var handler = new FakeHandler(number => number switch
        {
            1 => HttpStatusCode.ServiceUnavailable,
            3 => HttpStatusCode.InternalServerError,
            _ => HttpStatusCode.OK,
        });
        using var http = new HttpClient(handler);
        var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);

        Assert.Equal(new ToolFlowUploadResult(0, 1, 2), await client.UploadPendingAsync());
        Assert.Single(handler.Requests); // Event waits until flow is accepted.

        Assert.Equal(new ToolFlowUploadResult(1, 1, 1), await client.UploadPendingAsync());
        Assert.Equal(3, handler.Requests.Count);

        var restarted = new ToolFlowUploadClient(http, Endpoint, () => true,
            new ToolFlowSelectionStore(_root), _root);
        Assert.Equal(new ToolFlowUploadResult(1, 0, 0), await restarted.UploadPendingAsync());
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(2, handler.Requests.Count(r => r.Path == "/api/v1/toolflows"));
        Assert.Equal(2, handler.Requests.Count(r => r.Path.EndsWith("/events", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TurningOffAfterFlowResponseStopsEventUntilReenabled()
    {
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(NewSelection());
        store.AppendEvent(selection.SubmissionId, NewEvent(selection.Items[0].ItemId,
            ToolFlowEventKind.Verified));
        var enabled = true;
        using var handler = new FakeHandler(_ =>
        {
            enabled = false;
            return HttpStatusCode.Created;
        });
        using var http = new HttpClient(handler);
        var client = new ToolFlowUploadClient(http, Endpoint, () => enabled, store, _root);

        Assert.Equal(new ToolFlowUploadResult(1, 0, 1), await client.UploadPendingAsync());
        Assert.Single(handler.Requests);

        enabled = true;
        Assert.Equal(new ToolFlowUploadResult(1, 0, 0), await client.UploadPendingAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void RequiresExplicitSafeEndpoint()
    {
        var store = new ToolFlowSelectionStore(_root);
        using var http = new HttpClient(new FakeHandler(_ => HttpStatusCode.OK));
        foreach (var address in new[]
        {
            "http://example.test/", "http://192.0.2.1/", "ftp://localhost/",
            "https://user:password@example.test/", "https://example.test/?token=secret",
            "https://example.test/#fragment",
        })
        {
            Assert.Throws<ArgumentException>(() =>
                new ToolFlowUploadClient(http, new Uri(address), () => true, store, _root));
        }

        _ = new ToolFlowUploadClient(http, new Uri("http://127.0.0.1:8768/"),
            () => true, store, _root);
        _ = new ToolFlowUploadClient(http, new Uri("http://[::1]:8768/"),
            () => true, store, _root);
    }

    [Fact]
    public async Task UploadPayloadRedactsEveryStringFieldIncludingItemsAndEventDetail()
    {
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(NewSelection() with
        {
            FlowName = "工作流 apiKey=abcdef1234567890",
            Items =
            [
                new ToolFlowItem
                {
                    ItemId = Guid.NewGuid().ToString(),
                    Name = "工具 sk-abcdef1234567890",
                    Kind = "software",
                    Version = "token=abcdef1234567890xyz",
                    SourceUrl = "https://example.test/ghp_abcdefghijklmnopqrstuvwxyz123",
                    DownloadUrl = "https://example.test/tool.zip",
                    InstallTargetKey = "Bearer abcdef1234567890",
                },
            ],
        });
        var executionEvent = NewEvent(selection.Items[0].ItemId, ToolFlowEventKind.Verified) with
        {
            Detail = "安装完成，凭据 apiKey=abcdef1234567890 已写入",
        };
        store.AppendEvent(selection.SubmissionId, executionEvent);
        using var handler = new FakeHandler(_ => HttpStatusCode.Created);
        using var http = new HttpClient(handler);
        var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);

        Assert.Equal(new ToolFlowUploadResult(2, 0, 0), await client.UploadPendingAsync());

        var flowJson = handler.Requests[0].Json;
        Assert.DoesNotContain("abcdef1234567890", flowJson);
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz123", flowJson);
        using (var document = JsonDocument.Parse(flowJson))
        {
            var root = document.RootElement;
            Assert.Equal("工作流 apiKey=[已过滤]", root.GetProperty("flowName").GetString());
            var item = root.GetProperty("items")[0];
            Assert.Equal("工具 [已过滤]", item.GetProperty("name").GetString());
            Assert.Equal("token=[已过滤]", item.GetProperty("version").GetString());
            Assert.Contains("[已过滤]", item.GetProperty("sourceUrl").GetString());
            Assert.Equal("Bearer [已过滤]", item.GetProperty("installTargetKey").GetString());
            Assert.Equal("https://example.test/tool.zip", item.GetProperty("downloadUrl").GetString());
        }

        using (var document = JsonDocument.Parse(handler.Requests[1].Json))
        {
            Assert.Equal("安装完成，凭据 apiKey=[已过滤] 已写入",
                document.RootElement.GetProperty("detail").GetString());
        }
    }

    private static ToolFlowSelection NewSelection(bool uploadEnabled = true) => new()
    {
        FlowId = Guid.NewGuid().ToString(),
        SubmissionId = Guid.NewGuid().ToString(),
        Origin = ToolFlowOrigin.Assistant,
        SelectedAtUtc = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero),
        FlowName = "2D 游戏开发工作流",
        ProjectGoal = "我想做一款 2D 游戏",
        GoalDescription = "做一款 2D 游戏",
        FlowText = "先安装 Godot。\n再验证运行。",
        UploadEnabledAtSelection = uploadEnabled,
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

    private static ToolFlowExecutionEvent NewEvent(string itemId, ToolFlowEventKind kind) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = itemId,
        Kind = kind,
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
