using System.Text.Json;
using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【GUI 隔离 · 补丁 B（复核退回：持久化队列恢复链）】2026-09-23：
/// 旧持久化条目里指向随包 Tools 的下载目标，在隔离态恢复时必须按现有隔离规则映射到
/// 可写根（ZXAI_DATA_ROOT\Tools）；无法安全解析（树外/空路径）→ 显式拒绝恢复（不入队）；
/// 生产态（无隔离根）路径原样、不拒绝任何条目（语义不变）。
/// 恢复出的「续传」是真实下载路径：目标目录的创建只能发生在映射后的可写根——
/// 假随包 Tools 目录必须始终零建、零写。
/// 全部用例只用一次性临时假根 + 假持久化文件，绝不触碰真实 %LOCALAPPDATA% 队列；
/// 恢复出的条目在 Dispose 中按 id 取消并清理，不给后续用例留下会续传的任务。
/// </summary>
[Collection("GlobalConfigTests")]
public class DownloadQueueRestoreIsolationTests : IDisposable
{
    private const string MappedId = "iso-restore-mapped";
    private const string OutsideId = "iso-restore-outside";
    private const string RefusedUrl = "http://127.0.0.1:9/never-download.zip";   // 本机保留端口：连接立即被拒，不触外网

    private readonly string _root;
    private readonly string _isoRoot;
    private readonly string _fakeTools;
    private readonly string _fakeBundledTarget;   // 假随包 Tools 子目录（旧持久化目标）
    private readonly string _outsideTarget;       // 树外目标（隔离态不合法 → 拒绝）
    private readonly string _queueFile;

    public DownloadQueueRestoreIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zxai-restore-" + Guid.NewGuid().ToString("N")[..8]);
        _isoRoot = Path.Combine(_root, "iso");
        _fakeTools = Path.Combine(_root, "candidate", "Tools");
        _fakeBundledTarget = Path.Combine(_fakeTools, "分类R", "工具R");
        _outsideTarget = Path.Combine(_root, "outside-target");
        Directory.CreateDirectory(_isoRoot);
        Directory.CreateDirectory(Path.Combine(_fakeTools, "分类R"));   // 假随包分类目录（空壳）
        PostProcessorRegistry.RegisterDefaults();

        DataRoots.TestRootOverrideForTest = _isoRoot;
        ToolCatalog.SetToolsRootForBuild(_fakeTools);
        ToolCatalog.OnToolsChanged();

        _queueFile = Path.Combine(_root, "download-queue.json");
        WriteQueueFile();
    }

    public void Dispose()
    {
        // 取消并清理本测试恢复出的条目（按 id 识别，不动其他测试/真实队列）
        foreach (var item in DownloadQueueService.Queue.Where(i =>
                     i.Id == MappedId || i.Id == OutsideId || i.Id.StartsWith("name-") || i.Id.StartsWith("p-") || i.Id.StartsWith("junc-") || i.DestinationPath.StartsWith(_isoRoot, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try { DownloadQueueService.Cancel(item.Id); } catch { }
        }
        foreach (var item in DownloadQueueService.Queue.Where(i =>
                     i.Id == MappedId || i.Id == OutsideId || i.Id.StartsWith("name-") || i.Id.StartsWith("p-") || i.Id.StartsWith("junc-") || i.DestinationPath.StartsWith(_isoRoot, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try { DownloadQueueService.Remove(item.Id); } catch { }
        }
        RemoveJunction(Path.Combine(_isoRoot, "Tools", "分类J"));
        RemoveJunction(Path.Combine(_isoRoot, "Tools", "分类M", "sub"));
        ToolCatalog.SetToolsRootForBuild(null);
        ToolCatalog.OnToolsChanged();
        DataRoots.TestRootOverrideForTest = null;
        try { Directory.Delete(_root, true); } catch { }
    }

    private static int CountEntries(string dir)
        => Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).Length;

    /// <summary>假持久化队列：一条目标=假随包 Tools 子目录（应被映射）、一条目标=树外（应被拒绝）。</summary>
    private void WriteQueueFile()
    {
        var entries = new List<DownloadQueueEntry>
        {
            new()
            {
                Id = MappedId,
                DisplayName = "旧持久化任务（目标=随包 Tools）",
                DestinationPath = _fakeBundledTarget,
                DirectUrl = RefusedUrl,
                State = DownloadItemState.Paused,
                BytesReceived = 512,
                TotalBytes = 4096
            },
            new()
            {
                Id = OutsideId,
                DisplayName = "旧持久化任务（目标=树外）",
                DestinationPath = _outsideTarget,
                DirectUrl = RefusedUrl,
                State = DownloadItemState.Paused,
                BytesReceived = 0,
                TotalBytes = 0
            }
        };
        File.WriteAllText(_queueFile, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    // ───────────────────────── 隔离态：映射 / 拒绝 / 续传零写随包 ----------

    [Fact]
    public async Task Restore_Isolation_MapsBundledTarget_RefusesUnsafe_ResumeNeverWritesBundled()
    {
        Assert.False(Directory.Exists(_fakeBundledTarget));   // 前提：假随包目标起初不存在
        var fakeToolsEntriesBefore = CountEntries(_fakeTools);
        var mappedTarget = Path.Combine(_isoRoot, "Tools", "分类R", "工具R");

        var restored = DownloadQueueService.RestorePersistedQueue(_queueFile);

        // ① 可解析（假随包 Tools 子目录）→ 恢复，且目标映射到假隔离根 Tools\...
        Assert.Equal(1, restored);
        var item = Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == MappedId).ToList());
        Assert.Equal(DownloadItemState.Paused, item.State);
        Assert.Equal(mappedTarget, item.DestinationPath);

        // ② 无法安全解析（树外）→ 显式拒绝：不入队 + 记录 + 不创建任何目录
        Assert.DoesNotContain(DownloadQueueService.Queue, i => i.Id == OutsideId);
        Assert.Contains(DownloadQueueService.LastRestoreRefusals, r => r.Contains(_outsideTarget));
        Assert.False(Directory.Exists(_outsideTarget));

        // ③ 继续任务（Resume）：下载路径上的目录创建只能落在映射后的可写根；
        //    假随包目录必须始终零建、零写（若旧目标被直接采用，这里会立刻出现目录/半成品）。
        DownloadQueueService.Resume(MappedId);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && item.State is DownloadItemState.Queued
                   or DownloadItemState.Resolving or DownloadItemState.Downloading)
        {
            await Task.Delay(50);
        }

        Assert.False(Directory.Exists(_fakeBundledTarget),
            "续传不得在随包 Tools 建目录/写入——目标必须已被映射到可写根");
        Assert.Equal(fakeToolsEntriesBefore, CountEntries(_fakeTools));   // 假随包树整体零变化
        Assert.False(File.Exists(Path.Combine(_fakeBundledTarget, "never-download.zip")));

        // 正面证据（防断言空转）：续传确实执行了下载流程——建目录点
        // （DownloadWithDownloaderAsync 开头）只使用 item.DestinationPath（= 映射后目标），
        // 因此映射后的可写根目录必须已经出现，且任务不可能「已完成」。
        Assert.True(Directory.Exists(mappedTarget),
            "续传应把目录创建在映射后的可写根（证明确实跑到建目录点，排除负控空转）");
        Assert.NotEqual(DownloadItemState.Completed, item.State);
    }

    // ───────────────────────── 对照：无隔离根时语义不变（env 模式则证明隔离仍生效） ----------

    [Fact]
    public void Restore_WithoutOverride_KeepsProductionSemantics_OrKeepsEnvIsolation()
    {
        DataRoots.TestRootOverrideForTest = null;
        try
        {
            var restored = DownloadQueueService.RestorePersistedQueue(_queueFile);
            var effective = DataRoots.EffectiveTestRoot;

            if (effective is null)
            {
                // 真生产态（无任何隔离根）：路径原样恢复——不映射、不拒绝（语义与修复前一致）
                Assert.Equal(2, restored);
                var mappedItem = Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == MappedId).ToList());
                Assert.Equal(_fakeBundledTarget, mappedItem.DestinationPath);
                var outsideItem = Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == OutsideId).ToList());
                Assert.Equal(_outsideTarget, outsideItem.DestinationPath);
                Assert.Empty(DownloadQueueService.LastRestoreRefusals);
            }
            else
            {
                // env 模式（套件带 ZXAI_DATA_ROOT 运行）：override=null 只是回落到进程隔离根，
                // 隔离语义必须继续生效——随包目标仍映射、树外仍拒绝（与生产态严格区分）。
                Assert.Equal(1, restored);
                var mappedItem = Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == MappedId).ToList());
                Assert.Equal(Path.Combine(effective, "Tools", "分类R", "工具R"), mappedItem.DestinationPath);
                Assert.DoesNotContain(DownloadQueueService.Queue, i => i.Id == OutsideId);
                Assert.Contains(DownloadQueueService.LastRestoreRefusals, r => r.Contains(_outsideTarget));
                Assert.False(Directory.Exists(_outsideTarget));
            }
        }
        finally
        {
            DataRoots.TestRootOverrideForTest = _isoRoot;
        }
    }

    // ───────────────────────── 文件名边界：绝对路径 / 相对穿越（恢复→续传→删除） ----------

    [Fact]
    public async Task Restore_UnsafePersistedFileName_CannotEscapeMappedDestination()
    {
        // 假随包 Tools 里放哨兵（.tubadl 与普通文件）：旧实现下会被越界删除/写入
        Directory.CreateDirectory(_fakeBundledTarget);
        var mappedTarget = Path.Combine(_isoRoot, "Tools", "分类R", "工具R");
        var absSentinel = Path.Combine(_fakeBundledTarget, "abs_sentinel");
        var absSentinelTubadl = absSentinel + ".tubadl";
        File.WriteAllText(absSentinelTubadl, "SENTINEL-ABS");
        var relSentinel = Path.Combine(_fakeTools, "分类R", "rel_sentinel");
        var relSentinelTubadl = relSentinel + ".tubadl";
        File.WriteAllText(relSentinelTubadl, "SENTINEL-REL");
        File.WriteAllText(Path.Combine(_fakeBundledTarget, "keep.bin"), "KEEP");
        var bundledBefore = CountEntries(_fakeTools);
        var relTraversal = Path.GetRelativePath(mappedTarget, relSentinel);   // 从映射后目录“穿越”到假随包树

        var queueFile = Path.Combine(_root, "restore-unsafe-names.json");
        WriteNameQueueFile(queueFile, "name-abs", "name-rel", "name-legit", relTraversal, absSentinel);

        var restored = DownloadQueueService.RestorePersistedQueue(queueFile);

        // ① 三条都恢复；越界文件名被拒绝采纳（回落派生），合法文件名保留
        Assert.Equal(3, restored);
        var absItem = Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == "name-abs").ToList());
        var relItem = Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == "name-rel").ToList());
        var legitItem = Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == "name-legit").ToList());
        Assert.Null(absItem.ResolvedFileName);
        Assert.Null(relItem.ResolvedFileName);
        Assert.Equal("tool.zip", legitItem.ResolvedFileName);

        // ② 恢复阶段（旧版会在 CleanupLegacyPartialFile 按该字段越界删哨兵）：随包树零变化
        Assert.True(File.Exists(absSentinelTubadl), "恢复/清理不得触碰随包哨兵（绝对文件名）");
        Assert.True(File.Exists(relSentinelTubadl), "恢复/清理不得触碰随包哨兵（相对穿越文件名）");
        Assert.Equal(bundledBefore, CountEntries(_fakeTools));

        // ③ 续传（ResolvedUrl 非空 → 跳过重解析，旧版会按不安全名越界写）：随包树仍零变化
        DownloadQueueService.Resume("name-abs");
        DownloadQueueService.Resume("name-rel");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               (absItem.State is DownloadItemState.Queued or DownloadItemState.Resolving or DownloadItemState.Downloading ||
                relItem.State is DownloadItemState.Queued or DownloadItemState.Resolving or DownloadItemState.Downloading))
        {
            await Task.Delay(50);
        }
        Assert.Equal(bundledBefore, CountEntries(_fakeTools));
        Assert.True(File.Exists(absSentinelTubadl));
        Assert.True(File.Exists(relSentinelTubadl));

        // ④ 删除（已完成项 DeleteFile 同样使用该值；旧版可越界删）：合法名只删映射目录内文件
        Directory.CreateDirectory(mappedTarget);
        var mappedLegit = Path.Combine(mappedTarget, "tool.zip");
        File.WriteAllText(mappedLegit, "DOWNLOADED");
        DownloadQueueService.DeleteFile("name-legit");
        Assert.False(File.Exists(mappedLegit), "合法文件名应正常删除映射目录内的文件");
        Assert.Equal(bundledBefore, CountEntries(_fakeTools));
        Assert.True(File.Exists(Path.Combine(_fakeBundledTarget, "keep.bin")));

        // ⑤ 生产态 / env 回落：名称安全校验与隔离无关（统一生效）；哨兵仍零触碰
        DataRoots.TestRootOverrideForTest = null;
        try
        {
            var prodFile = Path.Combine(_root, "restore-unsafe-names-prod.json");
            WriteNameQueueFile(prodFile, "p-abs", "p-rel", "p-legit", relTraversal, absSentinel);
            var restoredProd = DownloadQueueService.RestorePersistedQueue(prodFile);
            Assert.Equal(3, restoredProd);
            Assert.Null(Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == "p-abs").ToList()).ResolvedFileName);
            Assert.Null(Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == "p-rel").ToList()).ResolvedFileName);
            Assert.Equal("tool.zip", Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == "p-legit").ToList()).ResolvedFileName);
            Assert.True(File.Exists(absSentinelTubadl));
            Assert.True(File.Exists(relSentinelTubadl));
        }
        finally
        {
            DataRoots.TestRootOverrideForTest = _isoRoot;
        }
    }

    /// <summary>三条条目：绝对文件名 / 相对穿越文件名（均带非空 ResolvedUrl → 续传将跳过重解析）/ 合法文件名（对照，已完成态）。</summary>
    private void WriteNameQueueFile(string file, string absId, string relId, string legitId,
        string relTraversal, string absSentinel)
    {
        var entries = new List<DownloadQueueEntry>
        {
            new()
            {
                Id = absId, DisplayName = "绝对文件名条目",
                DestinationPath = _fakeBundledTarget, DirectUrl = RefusedUrl, ResolvedUrl = RefusedUrl,
                ResolvedFileName = absSentinel, State = DownloadItemState.Paused,
                BytesReceived = 1, TotalBytes = 2
            },
            new()
            {
                Id = relId, DisplayName = "穿越文件名条目",
                DestinationPath = _fakeBundledTarget, DirectUrl = RefusedUrl, ResolvedUrl = RefusedUrl,
                ResolvedFileName = relTraversal, State = DownloadItemState.Paused,
                BytesReceived = 1, TotalBytes = 2
            },
            new()
            {
                Id = legitId, DisplayName = "合法文件名条目",
                DestinationPath = _fakeBundledTarget, DirectUrl = RefusedUrl, ResolvedUrl = RefusedUrl,
                ResolvedFileName = "tool.zip", State = DownloadItemState.Completed,
                CompletedAt = DateTimeOffset.Now, ResolvedSize = 123
            }
        };
        File.WriteAllText(file, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ───────────────────────── 物理边界：隔离根 Tools 子树 junction（复核退回补丁 D） ----------

    [Fact]
    public async Task Restore_JunctionUnderIsolationTools_RefusedOrCannotBeReached_CleanPathStillWorks()
    {
        // 假随包分类（真目录）+ 哨兵（只在假随包内）；假隔离 Tools\分类J 将是指向它的 junction。
        var bundleCat = Path.Combine(_fakeTools, "分类J");
        Directory.CreateDirectory(bundleCat);
        var zip = Path.Combine(bundleCat, "safe.zip");
        var zipTubadl = zip + ".tubadl";
        File.WriteAllText(zip, "BUNDLED-ZIP");
        File.WriteAllText(zipTubadl, "BUNDLED-PARTIAL");
        var bytesBefore = File.ReadAllBytes(zipTubadl);
        var tsBefore = File.GetLastWriteTimeUtc(zipTubadl);
        var bundleCountBefore = CountEntries(bundleCat);

        var isoCat = Path.Combine(_isoRoot, "Tools", "分类J");
        Directory.CreateDirectory(Path.Combine(_isoRoot, "Tools"));
        Assert.True(CreateJunction(isoCat, bundleCat),
            "本负控依赖 mklink /J：junction 创建失败则负控失去意义（不得静默跳过）");

        // ① 恢复边界：映射目标链含 junction → 拒绝恢复（不入队）；哨兵字节与时间戳不变。
        var qf1 = Path.Combine(_root, "restore-junction-1.json");
        WriteJunctionQueueFile(qf1, "junc-1");
        Assert.Equal(0, DownloadQueueService.RestorePersistedQueue(qf1));
        Assert.DoesNotContain(DownloadQueueService.Queue, i => i.Id == "junc-1");
        Assert.Contains(DownloadQueueService.LastRestoreRefusals, r => r.Contains("reparse-component"));
        AssertJunctionSentinelsIntact(zip, zipTubadl, bytesBefore, tsBefore, bundleCat, bundleCountBefore);

        // ② 恢复后被换成 junction（只校验一次就会中招）：先无 junction 恢复（接受），再把映射目标换成 junction——
        //    续传（下载）与删除必须在操作执行前复查并拒绝，绝不穿透 junction。
        RemoveJunction(isoCat);
        var qf2 = Path.Combine(_root, "restore-junction-2.json");
        WriteJunctionQueueFile(qf2, "junc-2", completedId: "junc-3");
        Assert.Equal(2, DownloadQueueService.RestorePersistedQueue(qf2));
        Assert.True(CreateJunction(isoCat, bundleCat));

        var item2 = Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == "junc-2").ToList());
        DownloadQueueService.Resume("junc-2");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && item2.State is DownloadItemState.Queued
                   or DownloadItemState.Resolving or DownloadItemState.Downloading)
        {
            await Task.Delay(50);
        }
        AssertJunctionSentinelsIntact(zip, zipTubadl, bytesBefore, tsBefore, bundleCat, bundleCountBefore);

        DownloadQueueService.DeleteFile("junc-3");
        Assert.True(File.Exists(zip), "删除不得穿透 junction 触达随包文件");
        AssertJunctionSentinelsIntact(zip, zipTubadl, bytesBefore, tsBefore, bundleCat, bundleCountBefore);

        // ③ 无 junction 的干净假隔离目录：合法映射仍可用（不过度阻断），续传把目录建在真隔离根内。
        RemoveJunction(isoCat);
        var qf3 = Path.Combine(_root, "restore-junction-3.json");
        WriteJunctionQueueFile(qf3, "junc-4");
        Assert.Equal(1, DownloadQueueService.RestorePersistedQueue(qf3));
        var item4 = Assert.Single(DownloadQueueService.Queue.Where(i => i.Id == "junc-4").ToList());
        Assert.Equal(Path.Combine(_isoRoot, "Tools", "分类J"), item4.DestinationPath);
        DownloadQueueService.Resume("junc-4");
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && item4.State is DownloadItemState.Queued
                   or DownloadItemState.Resolving or DownloadItemState.Downloading)
        {
            await Task.Delay(50);
        }
        Assert.True(Directory.Exists(Path.Combine(_isoRoot, "Tools", "分类J")),
            "干净隔离目录下合法续传应把目标目录创建在真隔离根内（正面证据）");
        AssertJunctionSentinelsIntact(zip, zipTubadl, bytesBefore, tsBefore, bundleCat, bundleCountBefore);
    }

    // ───────────────────────── 物理边界：多文件嵌套落点（复核退回·补丁 E） ----------

    [Fact]
    public async Task MultiFile_NestedJunctionUnderIsolationRoot_RefusedBeforeAnyTouch_LegitNestedStillWorks()
    {
        // 布局：干净任务目标 <iso>\Tools\分类M；其下 sub 是指向假随包 <bundle>\Tools\分类M\sub 的 junction（含哨兵）。
        var isoCatM = Path.Combine(_isoRoot, "Tools", "分类M");
        Directory.CreateDirectory(isoCatM);
        var bundleCatM = Path.Combine(_fakeTools, "分类M");
        var bundleSub = Path.Combine(bundleCatM, "sub");
        Directory.CreateDirectory(bundleSub);
        var sentinel = Path.Combine(bundleSub, "safe.zip.tubadl");
        File.WriteAllText(sentinel, "NESTED-SENTINEL");
        var bytesBefore = File.ReadAllBytes(sentinel);
        var tsBefore = File.GetLastWriteTimeUtc(sentinel);
        var bundleCountBefore = CountEntries(bundleCatM);

        var subLink = Path.Combine(isoCatM, "sub");
        Assert.True(CreateJunction(subLink, bundleSub), "mklink /J 不可用则本负控失去意义");

        // ① 多文件 FileName="sub/safe.zip"（无效 URL）→ 必须在任何 CreateDirectory / File.Exists /
        //    DeleteLegacyPartial / 下载之前拒绝；修复前 DeleteLegacyPartial 会穿过 junction 删除哨兵。
        var item = DownloadQueueService.EnqueueMultiFile(
            "嵌套 junction 负控",
            _ => Task.FromResult(new List<ResolvedDownloadUrl> { new(RefusedUrl, "sub/safe.zip", 0) }),
            Path.Combine(_fakeTools, "分类M"));
        var deadline = DateTime.UtcNow.AddSeconds(90);   // 多文件自动重试上限 6 次 × 3s 延迟
        while (DateTime.UtcNow < deadline && item.State is DownloadItemState.Queued
                   or DownloadItemState.Resolving or DownloadItemState.Downloading or DownloadItemState.Processing)
        {
            await Task.Delay(100);
        }
        Assert.Equal(DownloadItemState.Failed, item.State);
        Assert.Contains("已拒绝", item.ErrorMessage ?? "");
        Assert.True(File.Exists(sentinel), "任何阶段不得穿过 sub junction 触达哨兵");
        Assert.Equal(bytesBefore, File.ReadAllBytes(sentinel));
        Assert.Equal(tsBefore, File.GetLastWriteTimeUtc(sentinel));
        Assert.Equal(bundleCountBefore, CountEntries(bundleCatM));

        // ② 无 junction 的合法嵌套路径仍可用：拆链后同一 FileName 应放行，实际父目录被创建在真隔离根内。
        RemoveJunction(subLink);
        var item2 = DownloadQueueService.EnqueueMultiFile(
            "嵌套合法路径对照",
            _ => Task.FromResult(new List<ResolvedDownloadUrl> { new(RefusedUrl, "sub/safe.zip", 0) }),
            Path.Combine(_fakeTools, "分类M"));
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !Directory.Exists(subLink)
                   && item2.State is not DownloadItemState.Failed)
        {
            await Task.Delay(100);
        }
        Assert.True(Directory.Exists(subLink), "无 junction 时合法嵌套落点应可创建实际父目录");
        Assert.False(File.GetAttributes(subLink).HasFlag(FileAttributes.ReparsePoint));
        Assert.True(File.Exists(sentinel));
        Assert.Equal(bytesBefore, File.ReadAllBytes(sentinel));
        Assert.Equal(tsBefore, File.GetLastWriteTimeUtc(sentinel));
        Assert.Equal(bundleCountBefore, CountEntries(bundleCatM));
    }

    /// <summary>假持久化队列条目：目标 = 旧假随包分类（将映射到假隔离 Tools\分类J）；可选再带一条已完成条目（删除负控）。</summary>
    private void WriteJunctionQueueFile(string file, string pausedId, string? completedId = null)
    {
        var entries = new List<DownloadQueueEntry>
        {
            new()
            {
                Id = pausedId, DisplayName = "junction 负控（暂停）",
                DestinationPath = Path.Combine(_fakeTools, "分类J"),
                DirectUrl = RefusedUrl, ResolvedUrl = RefusedUrl, ResolvedFileName = "safe.zip",
                State = DownloadItemState.Paused, BytesReceived = 1, TotalBytes = 2
            }
        };
        if (completedId is not null)
        {
            entries.Add(new()
            {
                Id = completedId, DisplayName = "junction 负控（已完成）",
                DestinationPath = Path.Combine(_fakeTools, "分类J"),
                DirectUrl = RefusedUrl, ResolvedUrl = RefusedUrl, ResolvedFileName = "safe.zip",
                State = DownloadItemState.Completed, CompletedAt = DateTimeOffset.Now, ResolvedSize = 11
            });
        }
        File.WriteAllText(file, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AssertJunctionSentinelsIntact(string zip, string zipTubadl, byte[] bytesBefore,
        DateTime tsBefore, string bundleCat, int bundleCountBefore)
    {
        Assert.True(File.Exists(zipTubadl), "随包哨兵（safe.zip.tubadl）不得被触碰");
        Assert.True(File.Exists(zip), "随包哨兵（safe.zip）不得被触碰");
        Assert.Equal(bytesBefore, File.ReadAllBytes(zipTubadl));
        Assert.Equal(tsBefore, File.GetLastWriteTimeUtc(zipTubadl));
        Assert.Equal(bundleCountBefore, CountEntries(bundleCat));
    }

    /// <summary>创建目录 junction（mklink /J 不需要管理员）；返回是否成功且属性确为重解析点。</summary>
    private static bool CreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(cmd,
                $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(15000);
            return p.ExitCode == 0 && Directory.Exists(linkPath)
                && File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }

    /// <summary>仅拆除 junction 本身（rmdir 不递归进目标）；非重解析点/不存在则不动。</summary>
    private static void RemoveJunction(string linkPath)
    {
        try
        {
            if (!Directory.Exists(linkPath)) return;
            if (!File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint)) return;
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(cmd, $"/c rmdir \"{linkPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(15000);
        }
        catch { }
    }
}
