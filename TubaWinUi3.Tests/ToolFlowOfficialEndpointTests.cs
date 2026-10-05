using System.Net;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>官方固定入口与旧设置迁移；所有发送均由假 HTTP handler 拦截。</summary>
public sealed class ToolFlowOfficialEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "zxai-official-upload-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousRoot;

    public ToolFlowOfficialEndpointTests()
    {
        _previousRoot = DataRoots.TestRootOverrideForTest;
        Directory.CreateDirectory(_root);
        DataRoots.TestRootOverrideForTest = _root;
        ConfigManager.SetConfigLocation(ConfigLocation.AppData);
        AppSettings.InvalidateCache();
        ToolFlowUploadLatch.ResetForTests();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://legacy.example/old-api/")]
    [InlineData("https://stats.zhenxingai.com/")]
    public async Task FactoriesIgnoreLegacyAddress_AndShareOfficialFlowEventMetricsRoutes(string? legacyAddress)
    {
        if (legacyAddress is null) AppSettings.Remove("ToolflowUploadEndpoint");
        else AppSettings.Set("ToolflowUploadEndpoint", legacyAddress);
        Assert.True(AppSettings.IsToolflowUploadEnabled);
        Assert.True(ToolFlowUploadClient.IsUploadEligibleNow());

        var itemId = Guid.NewGuid().ToString();
        var store = new ToolFlowSelectionStore(_root);
        var selected = store.SelectForInstall(new ToolFlowSelection
        {
            FlowId = Guid.NewGuid().ToString(),
            SubmissionId = Guid.NewGuid().ToString(),
            Origin = ToolFlowOrigin.User,
            SelectedAtUtc = DateTimeOffset.UtcNow,
            FlowName = "自选开发工作流",
            ProjectGoal = "制作一个本地应用",
            GoalDescription = "本地应用",
            FlowText = "使用选定的开发工具。",
            UploadEnabledAtSelection = ToolFlowUploadClient.IsUploadEligibleNow(),
            Conversation = [new() { Role = "user", Content = "我想制作本地应用", AtUtc = null }],
            Items = [new() { ItemId = itemId, Name = "Godot", Kind = "development" }],
        });
        store.AppendEvent(selected.SubmissionId, new ToolFlowExecutionEvent
        {
            Id = Guid.NewGuid().ToString(),
            ItemId = itemId,
            Kind = ToolFlowEventKind.InstallSucceeded,
            AtUtc = DateTimeOffset.UtcNow,
            Detail = "测试事件",
        });
        var metrics = new DownloadMetricsStore(_root);
        metrics.ReportButtonClicked("Godot");

        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var flowsUploader = ToolFlowUploadClient.TryCreateConfigured(http, store, _root)!;
        var metricsUploader = DownloadMetricsUploader.TryCreateConfigured(http, metrics)!;
        Assert.NotNull(flowsUploader);
        Assert.NotNull(metricsUploader);
        Assert.Equal(new ToolFlowUploadResult(2, 0, 0), await flowsUploader.UploadPendingAsync());
        Assert.True(await metricsUploader.UploadPendingAsync());

        Assert.Equal(new[]
        {
            "https://zhenxingai.com/api/toolflows/v1/toolflows",
            $"https://zhenxingai.com/api/toolflows/v1/toolflows/{selected.FlowId}/events",
            "https://zhenxingai.com/api/toolflows/v1/metrics",
        }, handler.Destinations);
        Assert.Equal("https://zhenxingai.com/api/toolflows/", ToolFlowUploadService.OfficialEndpoint.AbsoluteUri);
    }

    [Fact]
    public void PublicFactoriesCannotUseRealHttpTransportInIsolation()
    {
        var selections = new ToolFlowSelectionStore(_root);
        var metrics = new DownloadMetricsStore(_root);
        Assert.True(AppSettings.IsToolflowUploadEnabled);
        Assert.Null(ToolFlowUploadClient.TryCreateConfigured(selections, _root));
        Assert.Null(DownloadMetricsUploader.TryCreateConfigured(metrics));
    }

    public void Dispose()
    {
        // 先取消去抖保存再解除假根，测试设置不能写入真实用户目录。
        AppSettings.InvalidateCache();
        ConfigManager.SetConfigLocation(ConfigLocation.AppData);
        DataRoots.TestRootOverrideForTest = _previousRoot;
        ToolFlowUploadLatch.ResetForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Destinations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Destinations.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }
    }
}
