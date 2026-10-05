using System.Net;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>
/// 反证测试：选定资格在选定当时由分享开关与完整对话接收上限判定，官方接收入口固定；
/// 关闭开关会作废既有待发记录，重开不补传。
/// 全部使用假数据根与假 HttpMessageHandler：不联网、不触真实数据目录、不读写真实设置。
/// </summary>
public sealed class ToolFlowUploadEligibilityTests : IDisposable
{
    private static readonly Uri Endpoint = new("https://example.test/api/");
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "zxai-toolflow-eligibility-" + Guid.NewGuid().ToString("N"));

    public ToolFlowUploadEligibilityTests() => ToolFlowUploadLatch.ResetForTests();

    [Theory]
    [InlineData(200, 8192, true)]
    [InlineData(201, 8192, false)]
    [InlineData(200, 8193, false)]
    public void FullConversationLimitsAffectSharingOnly(int messages, int messageLength, bool supported)
    {
        var conversation = Enumerable.Range(0, messages)
            .Select(_ => new ToolFlowConversationMessage
            {
                Role = "assistant", Content = new string('文', messageLength),
            }).ToList();

        Assert.Equal(supported, ToolFlowUploadClient.IsConversationUploadSupported(conversation));
        Assert.Equal(supported, ToolFlowUploadClient.IsUploadEligible(switchOn: true, conversation));
        Assert.False(ToolFlowUploadClient.IsUploadEligible(switchOn: false, conversation));
    }

    [Theory]
    [InlineData(2, 10375)]
    [InlineData(201, 20)]
    public async Task OversizedSelectionRetainsFullConversationLocallyAndNeverBackfills(
        int messages, int messageLength)
    {
        var conversation = Enumerable.Range(0, messages)
            .Select(index => new ToolFlowConversationMessage
            {
                Role = index == 0 ? "user" : "assistant",
                Content = index + "：" + new string('文', messageLength),
            }).ToList();
        var store = new ToolFlowSelectionStore(_root);
        var localOnly = store.SelectForInstall(NewSelection(
            uploadEnabled: ToolFlowUploadClient.IsUploadEligible(switchOn: true, conversation)) with
        {
            Conversation = conversation,
        });
        store.AppendEvent(localOnly.SubmissionId, NewEvent(localOnly.Items[0].ItemId));

        var reopened = new ToolFlowSelectionStore(_root).GetBySubmissionId(localOnly.SubmissionId)!;
        Assert.False(reopened.UploadEnabledAtSelection);
        Assert.Equal(conversation, reopened.Conversation);
        Assert.Single(reopened.Events);

        using var handler = new FakeHandler(_ => HttpStatusCode.Created);
        using var http = new HttpClient(handler);
        var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);
        Assert.Equal(new ToolFlowUploadResult(0, 0, 0), await client.UploadPendingAsync());
        Assert.Empty(handler.Requests);
        Assert.Equal(conversation, store.GetBySubmissionId(localOnly.SubmissionId)!.Conversation);
    }

    [Fact]
    public async Task OversizedLegacyEligibleRecordDoesNotSendOrStayPending()
    {
        var store = new ToolFlowSelectionStore(_root);
        var legacy = store.SelectForInstall(NewSelection(uploadEnabled: true) with
        {
            Conversation = [new ToolFlowConversationMessage
            {
                Role = "assistant", Content = new string('文', 8193),
            }],
        });
        store.AppendEvent(legacy.SubmissionId, NewEvent(legacy.Items[0].ItemId));
        using var handler = new FakeHandler(_ => HttpStatusCode.Created);
        using var http = new HttpClient(handler);
        var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);

        Assert.Equal(new ToolFlowUploadResult(0, 0, 0), await client.UploadPendingAsync());
        Assert.Empty(handler.Requests);
        Assert.Equal(8193, store.GetBySubmissionId(legacy.SubmissionId)!.Conversation[0].Content.Length);
    }

    [Fact]
    public async Task SelectionMadeWhileSharingOffIsNeverUploaded_AfterSwitchReopens()
    {
        Assert.False(ToolFlowUploadClient.IsUploadEligible(switchOn: false));
        Assert.True(ToolFlowUploadClient.IsUploadEligible(switchOn: true));

        var store = new ToolFlowSelectionStore(_root);
        var localOnly = store.SelectForInstall(NewSelection(
            uploadEnabled: ToolFlowUploadClient.IsUploadEligible(switchOn: false)));

        // 之后用户开启分享：关闭期间选定的旧记录仍不得上传。
        using var handler = new FakeHandler(_ => HttpStatusCode.Created);
        using var http = new HttpClient(handler);
        var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);

        Assert.Equal(new ToolFlowUploadResult(0, 0, 0), await client.UploadPendingAsync());
        Assert.Empty(handler.Requests);

        // 重开后新做的选定（具备资格）正常发送；旧记录不随行。
        var fresh = store.SelectForInstall(NewSelection(
            uploadEnabled: ToolFlowUploadClient.IsUploadEligible(switchOn: true)));
        Assert.Equal(new ToolFlowUploadResult(1, 0, 0), await client.UploadPendingAsync());
        var request = Assert.Single(handler.Requests);
        Assert.Contains(fresh.SubmissionId, request.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(localOnly.SubmissionId, request.Json, StringComparison.Ordinal);

        Assert.Equal(2, store.ListAll().Count);   // 旧记录仍保存在本机，只是不再具备上报资格
        Assert.False(store.GetBySubmissionId(localOnly.SubmissionId)!.UploadEnabledAtSelection);
    }

    [Fact]
    public async Task TurningSharingOffVoidsPendingSelections_SoReopenNeverBackfillsThem()
    {
        var store = new ToolFlowSelectionStore(_root);
        var eligible = store.SelectForInstall(NewSelection());
        store.AppendEvent(eligible.SubmissionId, NewEvent(eligible.Items[0].ItemId));

        // 设置页关闭分享开关时执行的动作。
        store.InvalidatePendingUploads();

        var retained = store.GetBySubmissionId(eligible.SubmissionId)!;
        Assert.False(retained.UploadEnabledAtSelection);
        Assert.Single(retained.Events);   // 本地方案与事件原样保留

        using var handler = new FakeHandler(_ => HttpStatusCode.Created);
        using var http = new HttpClient(handler);
        var client = new ToolFlowUploadClient(http, Endpoint, () => true, store, _root);
        Assert.Equal(new ToolFlowUploadResult(0, 0, 0), await client.UploadPendingAsync());
        Assert.Empty(handler.Requests);

        // 重开后的新选定正常发送；被作废的旧记录继续不发（新选定时刻晚于撤销门闩）。
        var fresh = store.SelectForInstall(NewSelection(selectedAt: DateTimeOffset.UtcNow));
        Assert.Equal(new ToolFlowUploadResult(1, 0, 0), await client.UploadPendingAsync());
        var request = Assert.Single(handler.Requests);
        Assert.Contains(fresh.SubmissionId, request.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(eligible.SubmissionId, request.Json, StringComparison.Ordinal);
    }

    private static ToolFlowSelection NewSelection(bool uploadEnabled = true,
        DateTimeOffset? selectedAt = null) => new()
    {
        FlowId = Guid.NewGuid().ToString(),
        SubmissionId = Guid.NewGuid().ToString(),
        Origin = ToolFlowOrigin.Assistant,
        SelectedAtUtc = selectedAt ?? new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero),
        FlowName = "2D 游戏开发工作流",
        ProjectGoal = "我想做一款 2D 游戏",
        GoalDescription = "做一款 2D 游戏",
        FlowText = "先安装 Godot。",
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
