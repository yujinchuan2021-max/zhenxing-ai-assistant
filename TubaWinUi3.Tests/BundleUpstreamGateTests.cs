using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【A15】独立发行（枕星图吧AI助手）上游内核包链路闸门回归。
///
/// 测试口径：离线假 HTTP 证明【零请求 / 零入队 / 零替换】，另有对照测试证明闸门打开时
/// 同一入口确实走检查/入队/解包/恢复路径——即测试钩子有效、入口本身没坏，
/// 闸门关闭时的「什么都没发生」是闸门生效而不是功能失效。
///
/// 覆盖四类入口（全部为生产真实调用路径）：
///   ①检查  ToolsBundleService.CheckForToolsUpdateAsync（启动静默检查 / 设置页 / 下载对话框共用）
///   ②入队  DownloadQueueService.EnqueueWithResolver + ToolsBundleService.CreateUrlResolver
///          （与 ToolsBundleDownloadDialog.StartDownloadAsync 完全相同的调用序列）
///   ③解包  ToolsBundleExtractProcessor.ExecuteAsync（解压并整体替换 Tools 目录）
///   ④恢复  DownloadQueueService.RestorePersistedQueue（跨会话持久化队列恢复 + 续传）
///
/// 钩子（internal，仅测试可见；生产恒为默认值）：
///   ToolsBundleService.HttpHandlerForTests —— 注入 HttpClient handler（离线假 HTTP）
///   ToolsBundleService._gateOpenForTests    —— 打开闸门（对照测试用；UpdateService.UpstreamUpdatesEnabled 恒 false）
/// 隔离原则：文件全部落在临时目录，绝不触碰真实 %LOCALAPPDATA%\\TubaWinUi3 队列/设置文件。
/// </summary>
public class BundleUpstreamGateTests : IDisposable
{
    /// <summary>上游内核包资产地址（与 ToolsBundleService 的源常量同源，仅作为字符串使用，测试中绝不真的请求）。</summary>
    private const string UpstreamAssetUrl =
        "https://api.gitcode.com/api/v5/repos/luolangaga/tubatool/releases/download/v9.9.9/Tools.zip";

    private const string ExtractMarker = "sentinel/marker.txt";

    private readonly string _dir;
    private readonly string? _previousTestRoot;

    public BundleUpstreamGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zxai-a15-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _previousTestRoot = DataRoots.TestRootOverrideForTest;
        DataRoots.TestRootOverrideForTest = _dir;
        // 与生产一致：启动时注册默认后处理器（闸门判定依赖「解压工具包」这一条目可被解析）
        PostProcessorRegistry.RegisterDefaults();
    }

    public void Dispose()
    {
        ResetHooks();
        // 清理本测试放入队列的条目（按目标目录归属识别，不动其他测试/真实队列）
        foreach (var item in DownloadQueueService.Queue.Where(i =>
                     i.DestinationPath.StartsWith(_dir, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            DownloadQueueService.Remove(item.Id);
        }
        DataRoots.TestRootOverrideForTest = _previousTestRoot;
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ───────────────────────── 闸门本身 ─────────────────────────

    [Fact]
    public void Gate_IsClosed_ForIndependentDistribution()
    {
        Assert.False(UpdateService.UpstreamUpdatesEnabled,
            "独立发行不得放开上游程序更新闸门（A15，见 UpdateService 注释）。");
        Assert.False(ToolsBundleService.UpstreamBundleEnabled,
            "独立发行不得放开上游内核包闸门（A15）。App 启动链路据此跳过下载引导与静默检查。");
        Assert.False(ToolsBundleService.IsBlockedUpstreamBundleTask(null),
            "闸门判定只作用于上游内核包任务，不得误伤普通下载。");
    }

    [Theory]
    [InlineData("script")]      // 普通下载：后处理器是别的类型
    [InlineData("bundle")]      // 上游内核包：按后处理器识别
    [InlineData("asset-url")]   // 上游内核包：后处理器键丢失时按资产名兜底识别
    public void BlockPredicate_OnlyBlocksUpstreamBundleTasks(string kind)
    {
        var processor = kind == "bundle"
            ? (IDownloadPostProcessor)new ToolsBundleExtractProcessor("9.9.9", ToolsBundleService.KindFull)
            : new ArchiveExtractProcessor();
        var url = kind == "asset-url" ? UpstreamAssetUrl : "https://example.com/tool.zip";

        var blocked = ToolsBundleService.IsBlockedUpstreamBundleTask(processor, url);

        Assert.Equal(kind != "script", blocked);
    }

    // ───────────────────────── ① 检查入口 ─────────────────────────

    [Fact]
    public async Task Check_WhenLocked_ReturnsNull_AndSendsZeroRequests()
    {
        var handler = new ExplodingHandler();
        ToolsBundleService.HttpHandlerForTests = handler;

        var info = await ToolsBundleService.CheckForToolsUpdateAsync();

        Assert.Null(info);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Check_Control_WhenGateOpen_ReachesFakeHttp_AndParsesRelease()
    {
        // 对照：闸门打开 + 假 HTTP（200/JSON）时必须真的发出请求并解析出发行版信息。
        // 这条证明「闸门关闭时零请求」不是入口失效，而是闸门把请求挡住了。
        var handler = new FakeReleaseHandler();
        ToolsBundleService.HttpHandlerForTests = handler;
        ToolsBundleService._gateOpenForTests = true;

        var info = await ToolsBundleService.CheckForToolsUpdateAsync();

        Assert.NotNull(info);
        Assert.Equal("9.9.9", info!.Version);
        Assert.True(info.GitCodeUrl is not null && info.GitCodeUrl.Contains("Tools.zip"),
            $"对照测试必须解析出 Tools.zip 资产链接，实际：{info.GitCodeUrl}");
        Assert.True(handler.Calls > 0, "对照：闸门打开时检查入口必须真的走 HTTP 路径。");
    }

    // ───────────────────────── ② 入队入口 ─────────────────────────

    [Fact]
    public void Enqueue_WhenLocked_DoesNotEnterQueue_AndSendsZeroRequests()
    {
        var handler = new ExplodingHandler();
        ToolsBundleService.HttpHandlerForTests = handler;
        var pendingBefore = DownloadQueueService.PendingCount;
        var toolsDir = Path.Combine(_dir, "Tools");

        var info = new ToolsBundleUpdateInfo(true, "9.9.9", UpstreamAssetUrl, null, 1024);

        // 与 ToolsBundleDownloadDialog.StartDownloadAsync 完全相同的调用序列（真实入队路径）
        var resolver = ToolsBundleService.CreateUrlResolver(info, preferGitCode: true, lite: false);
        var item = DownloadQueueService.EnqueueWithResolver(
            displayName: "完整版内核 9.9.9",
            urlResolver: resolver,
            destinationPath: toolsDir,
            postProcessor: new ToolsBundleExtractProcessor("9.9.9", ToolsBundleService.KindFull),
            fallbackUrl: info.FallbackUrl(false));

        Assert.DoesNotContain(DownloadQueueService.Queue, i => i.Id == item.Id);
        Assert.DoesNotContain(DownloadQueueService.Queue,
            i => i.PostProcessor is ToolsBundleExtractProcessor);
        // 待办计数不得因这次入队而增加（其他用例的后台收尾只会让它减少，故用 <=）
        Assert.True(DownloadQueueService.PendingCount <= pendingBefore,
            $"闸门关闭时入队不得增加待办计数：之前 {pendingBefore}，现在 {DownloadQueueService.PendingCount}");
        Assert.Equal(0, handler.Calls);
        // 任务被明确标记为已取消并给出停用原因（不静默失败）：
        Assert.Equal(DownloadItemState.Cancelled, item.State);
        Assert.Contains("已停用", item.ErrorMessage ?? "");
        // 零落盘：不入队自然不会创建目标目录
        Assert.False(Directory.Exists(toolsDir));
    }

    [Fact]
    public async Task UrlResolver_WhenLocked_RefusesToProduceUrl()
    {
        var info = new ToolsBundleUpdateInfo(true, "9.9.9", UpstreamAssetUrl, null, 1024);
        var resolver = ToolsBundleService.CreateUrlResolver(info, preferGitCode: true, lite: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver(default));
        Assert.Contains("已停用", ex.Message);
    }

    [Fact]
    public async Task UrlResolver_Control_WhenGateOpen_ReturnsUpstreamUrl()
    {
        var info = new ToolsBundleUpdateInfo(true, "9.9.9", UpstreamAssetUrl, null, 1024);
        var resolver = ToolsBundleService.CreateUrlResolver(info, preferGitCode: true, lite: false);

        ToolsBundleService._gateOpenForTests = true;
        var resolved = await resolver(default);

        Assert.Equal(UpstreamAssetUrl, resolved.Url);
        Assert.Equal("Tools.zip", resolved.FileName);
    }

    [Fact]
    public async Task Enqueue_Control_WhenGateOpen_EntersQueue()
    {
        // 对照：闸门打开时同一条入队调用必须真的进队列（证明入队入口本身可用，不是入口坏掉）。
        // 解析器由测试注入「立即失败」，因此本对照不触网、不等待网络：进队即为证据。
        ToolsBundleService._gateOpenForTests = true;
        var toolsDir = Path.Combine(_dir, "ToolsControl");

        var item = DownloadQueueService.EnqueueWithResolver(
            displayName: "完整版内核 9.9.9（对照）",
            urlResolver: _ => throw new InvalidOperationException("control-resolver"),
            destinationPath: toolsDir,
            postProcessor: new ToolsBundleExtractProcessor("9.9.9", ToolsBundleService.KindFull));

        Assert.Contains(DownloadQueueService.Queue, i => i.Id == item.Id);

        // 等它落定为终态（测试注入的失败立即发生），避免给后续用例留下未结算的待办计数。
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && item.State is DownloadItemState.Queued
                   or DownloadItemState.Resolving or DownloadItemState.Downloading)
        {
            await Task.Delay(20);
        }
        Assert.Equal(DownloadItemState.Failed, item.State);
    }

    // ───────────────────────── ③ 解包（替换）入口 ─────────────────────────

    [Fact]
    public async Task Extract_WhenLocked_DoesNotExtractOrReplace()
    {
        var zip = CreateToolsZip(Path.Combine(_dir, "Tools.zip"));
        var dest = Path.Combine(_dir, "Tools");
        Directory.CreateDirectory(dest);
        var keep = Path.Combine(dest, "keep.txt");
        File.WriteAllText(keep, "ORIGINAL");

        var handler = new ExplodingHandler();
        ToolsBundleService.HttpHandlerForTests = handler;
        var statuses = new RecordingProgress();

        await new ToolsBundleExtractProcessor("9.9.9", ToolsBundleService.KindFull)
            .ExecuteAsync(zip, dest, statuses, CancellationToken.None);

        Assert.Equal("ORIGINAL", File.ReadAllText(keep));                       // 零替换：既有内容原样
        Assert.False(File.Exists(Path.Combine(dest, "sentinel", "marker.txt"))); // 零解包
        Assert.False(Directory.Exists(Path.Combine(dest, "sentinel")));
        Assert.True(File.Exists(zip), "闸门关闭时不得消费/删除下载到的内核包");
        Assert.Equal(0, handler.Calls);
        Assert.Contains(statuses.Messages, m => m.Contains("已停用"));
    }

    [Fact]
    public async Task Extract_Control_WhenGateOpen_ReplacesDestination()
    {
        // 对照：闸门打开时同一解包入口必须真的解压并整体替换目标目录。
        // 版本/变种传 null —— 对照测试不写真实设置文件；只断言文件系统结果。
        var zip = CreateToolsZip(Path.Combine(_dir, "ToolsControl.zip"));
        var dest = Path.Combine(_dir, "ToolsControl");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "ORIGINAL");

        ToolsBundleService._gateOpenForTests = true;

        await new ToolsBundleExtractProcessor()
            .ExecuteAsync(zip, dest, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(dest, "sentinel", "marker.txt")),
            "对照：闸门打开时解包入口必须真的把包内容落到目标目录。");
    }

    // ───────────────────────── ④ 持久下载恢复入口 ─────────────────────────

    [Fact]
    public void Restore_WhenLocked_DoesNotRestoreUpstreamBundleTask()
    {
        var queueFile = WritePersistedQueueFile();

        var handler = new ExplodingHandler();
        ToolsBundleService.HttpHandlerForTests = handler;

        var restored = DownloadQueueService.RestorePersistedQueue(queueFile);

        Assert.Equal(0, restored);
        Assert.DoesNotContain(DownloadQueueService.Queue, i => i.Id == PersistedId);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Restore_Control_WhenGateOpen_RestoresEntry_WithoutAnyRequest()
    {
        // 对照：闸门打开时同一恢复入口必须真的把持久化条目回队（证明恢复入口本身可用）。
        var queueFile = WritePersistedQueueFile();
        var handler = new ExplodingHandler();
        ToolsBundleService.HttpHandlerForTests = handler;
        ToolsBundleService._gateOpenForTests = true;

        var restored = DownloadQueueService.RestorePersistedQueue(queueFile);

        var item = Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == PersistedId).ToList());
        Assert.Equal(1, restored);
        Assert.Equal(DownloadItemState.Paused, item.State);   // 恢复为暂停态，等待用户续传
        Assert.Equal(0, handler.Calls);                       // 恢复本身不发请求（续传才发）
    }

    [Fact]
    public void Resume_WhenLocked_DoesNotStartAnyDownload()
    {
        // 跨会话恢复后的「续传」是最深的真实路径：即使条目已在队列里，闸门关闭也不得开工。
        var queueFile = WritePersistedQueueFile();
        ToolsBundleService._gateOpenForTests = true;
        Assert.Equal(1, DownloadQueueService.RestorePersistedQueue(queueFile));

        ToolsBundleService._gateOpenForTests = false;     // 闸门重新关闭（= 独立发行常态）
        var handler = new ExplodingHandler();
        ToolsBundleService.HttpHandlerForTests = handler;

        DownloadQueueService.Resume(PersistedId);

        var item = Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == PersistedId).ToList());
        Assert.Equal(DownloadItemState.Cancelled, item.State);
        Assert.Contains("已停用", item.ErrorMessage ?? "");
        Assert.Equal(0, handler.Calls);
        Assert.False(File.Exists(Path.Combine(_dir, "Tools", "Restore", "Tools.zip")));
        Assert.False(File.Exists(Path.Combine(_dir, "ToolsRestore", "Tools.zip.download")));
    }

    // ───────────────────────── 离线假 HTTP / 测试脚手架 ─────────────────────────

    private const string PersistedId = "a15-bundle-restored";

    /// <summary>写一份「上游内核包任务正在下载中」的持久化队列文件（重启后应被恢复的那条）。</summary>
    private string WritePersistedQueueFile()
    {
        var queueFile = Path.Combine(_dir, "download-queue.json");
        var entries = new List<DownloadQueueEntry>
        {
            new()
            {
                Id = PersistedId,
                DisplayName = "完整版内核 9.9.9",
                DestinationPath = Path.Combine(_dir, "Tools", "Restore"),
                DirectUrl = UpstreamAssetUrl,
                State = DownloadItemState.Downloading,
                PostProcessorKey = new ToolsBundleExtractProcessor().DisplayName,
                BytesReceived = 512,
                TotalBytes = 1024
            }
        };
        File.WriteAllText(queueFile, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions { WriteIndented = true }));
        return queueFile;
    }

    private static string CreateToolsZip(string path)
    {
        using var fs = File.Create(path);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(ExtractMarker).Open());
        writer.Write("TOOLS-CORE");
        return path;
    }

    private static void ResetHooks()
    {
        ToolsBundleService.HttpHandlerForTests = null;
        ToolsBundleService._gateOpenForTests = false;
    }

    /// <summary>同步记录进度的 IProgress（避免 xunit 同步上下文把回调推迟到断言之后）。</summary>
    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }

    /// <summary>离线假 HTTP：任何请求都失败。闸门关闭时一个请求都不该打到这里。</summary>
    private sealed class ExplodingHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException(
                $"A15 离线测试：闸门关闭时不得发起任何 HTTP 请求，实际请求 {request.RequestUri}");
        }
    }

    /// <summary>离线假 HTTP：返回带 Tools.zip 资产的发行版 JSON（对照测试用，不触网）。</summary>
    private sealed class FakeReleaseHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        private const string Json = """
        [
          {
            "tag_name": "v9.9.9",
            "draft": false,
            "prerelease": false,
            "published_at": "2030-01-01T00:00:00Z",
            "assets": [
              {
                "name": "Tools.zip",
                "size": 1024,
                "browser_download_url": "https://api.gitcode.com/api/v5/repos/luolangaga/tubatool/releases/download/v9.9.9/Tools.zip"
              },
              {
                "name": "Tools_Lite.zip",
                "size": 512,
                "browser_download_url": "https://api.gitcode.com/api/v5/repos/luolangaga/tubatool/releases/download/v9.9.9/Tools_Lite.zip"
              }
            ]
          }
        ]
        """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(Json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
