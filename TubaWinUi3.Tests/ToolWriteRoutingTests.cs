using System.IO.Compression;
using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【GUI 隔离 · R2 复核退回第 3 项】Tools 树写操作路由（2026-09-23）：
/// 隔离态下所有导入 / 安装 / 更新 / 清理 / 下载写操作必须落 ZXAI_DATA_ROOT\Tools（可写根），
/// 随包 Tools 恒为只读展示/启动来源。负控输入 = 临时假候选包 + 假隔离根；
/// 覆盖：隔离启动清理不删除包内空目录、导入只写隔离根、社区安装只写隔离根、非隔离行为不回归。
/// 全部用例只用一次性临时目录，不触碰任何真实目录。
/// </summary>
public class ToolWriteRoutingTests : IDisposable
{
    private readonly string _root;
    private readonly string _isoRoot;

    public ToolWriteRoutingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zxai-writerouting-" + Guid.NewGuid().ToString("N"));
        _isoRoot = Path.Combine(_root, "iso");
        Directory.CreateDirectory(_isoRoot);
        DataRoots.TestRootOverrideForTest = _isoRoot;
        ConfigManager.SetConfigLocation(ConfigLocation.AppData);   // 归一位置状态（清缓存，防御历史残留）
        Assert.Equal(_isoRoot, ConfigManager.GetDataDir());        // 数据根必须=隔离根（覆盖必须生效）
    }

    public void Dispose()
    {
        // 硬停：取消失败会写进真实目录的未落盘去抖写——AppSettings 的 500ms 持久化定时器在
        // 触发时才解析路径，若覆盖已释放则会解析到真实目录（2026-09-23 事故根因）。
        // InvalidateCache 置"未脏"→ 定时器触发时直接 no-op；再清 _cachedDataDir；最后才释放覆盖。
        try { AppSettings.InvalidateCache(); } catch { }
        try { ConfigManager.SetConfigLocation(ConfigLocation.AppData); } catch { }
        DataRoots.TestRootOverrideForTest = null;
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>假候选包：一个含"空分类目录"的 Tools 树（模拟随包 Tools 的结构，永不写入）。</summary>
    private string NewFakeCandidatePackage(string emptyCategory)
    {
        var tools = Path.Combine(_root, "candidate", "Tools");
        Directory.CreateDirectory(Path.Combine(tools, emptyCategory));
        return tools;
    }

    private static int CountEntries(string dir)
        => Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).Length;

    // ---------- 纯函数：可写根选择（生产语义不变 / 隔离态 = 隔离根\Tools） ----------

    [Fact]
    public void PickWritableToolsRoot_NotIsolated_ReturnsNormalRoot()
        => Assert.Equal(Path.Combine("X:", "normal", "tools"),
            DataRoots.PickWritableToolsRoot(null, Path.Combine("X:", "normal", "tools")));

    [Fact]
    public void PickWritableToolsRoot_Isolated_ReturnsIsolationTools()
        => Assert.Equal(Path.Combine("X:" + Path.DirectorySeparatorChar + "iso", "Tools"),
            DataRoots.PickWritableToolsRoot("X:" + Path.DirectorySeparatorChar + "iso", "X:" + Path.DirectorySeparatorChar + "normal" + Path.DirectorySeparatorChar + "tools"));

    [Fact]
    public void WritableToolsRoot_Isolated_UsesIsolationRoot()
        => Assert.Equal(Path.Combine(_isoRoot, "Tools"), ToolCatalog.WritableToolsRoot);

    [Fact]
    public void WritableToolsRoot_ConsistentWithEffectiveRoot()
    {
        DataRoots.TestRootOverrideForTest = null;
        try
        {
            if (DataRoots.EffectiveTestRoot is null)
                Assert.Equal(ToolCatalog.ToolsRoot, ToolCatalog.WritableToolsRoot);          // 未隔离：生产语义
            else
                Assert.Equal(Path.Combine(DataRoots.EffectiveTestRoot, "Tools"), ToolCatalog.WritableToolsRoot);
        }
        finally { DataRoots.TestRootOverrideForTest = _isoRoot; }
    }

    // ---------- 隔离启动清理：绝不删除包内空目录（假候选包 + 假隔离根） ----------

    [Fact]
    public void StartupCleanup_Isolation_NeverTouchesBundledToolDirs()
    {
        var candidateTools = NewFakeCandidatePackage("空分类_候选包");
        var candidateBefore = CountEntries(candidateTools);
        Assert.Equal(1, candidateBefore);

        // 隔离写根：一个空分类 + 一个非空分类
        var writableTools = Path.Combine(_isoRoot, "Tools");
        Directory.CreateDirectory(Path.Combine(writableTools, "空分类_隔离"));
        Directory.CreateDirectory(Path.Combine(writableTools, "非空分类_隔离"));
        File.WriteAllText(Path.Combine(writableTools, "非空分类_隔离", "keep.txt"), "x");

        // 启动清理序列（与 App.xaml.cs 同款调用顺序）：扫描 → 逐个清理
        var empties = ToolCatalog.FindEmptyCategories();
        Assert.Contains("空分类_隔离", empties);
        Assert.DoesNotContain("非空分类_隔离", empties);
        Assert.DoesNotContain("空分类_候选包", empties);   // 随包目录不在隔离扫描范围

        foreach (var name in empties)
            ToolCatalog.PruneCategoryIfEmpty(name);

        // 隔离写根：空的被清理、非空保留
        Assert.False(Directory.Exists(Path.Combine(writableTools, "空分类_隔离")));
        Assert.True(File.Exists(Path.Combine(writableTools, "非空分类_隔离", "keep.txt")));

        // 假候选包完全未动（目录仍在、条目数不变）——"隔离启动清理不删除包内空目录"
        Assert.True(Directory.Exists(Path.Combine(candidateTools, "空分类_候选包")));
        Assert.Equal(candidateBefore, CountEntries(candidateTools));

        // 即使直接对"与随包同名"的分类调用 Prune，也不会删除随包目录
        Assert.False(ToolCatalog.PruneCategoryIfEmpty("空分类_候选包"));
        Assert.True(Directory.Exists(Path.Combine(candidateTools, "空分类_候选包")));
    }

    // ---------- 隔离导入：只写隔离根（单文件与 ZIP 两条导入链） ----------

    [Fact]
    public async Task ImportSingleFile_Isolation_WritesOnlyToIsolationRoot()
    {
        var candidateTools = NewFakeCandidatePackage("空分类_候选包");
        var src = Path.Combine(_root, "src-tool.exe");
        File.WriteAllText(src, "fake-binary");

        var result = await CustomToolPackageService.ImportSingleFileAsync(
            src, "测试工具A", "分类X", "desc", "pub", ["tag1"]);

        var expectedRoot = Path.Combine(_isoRoot, "Tools");
        Assert.StartsWith(expectedRoot, result.ToolDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.PrimaryExecutablePath));
        Assert.StartsWith(expectedRoot, result.PrimaryExecutablePath, StringComparison.OrdinalIgnoreCase);

        // 随包目录（假候选包）未被写入
        Assert.False(Directory.Exists(Path.Combine(candidateTools, "分类X")));

        // 元数据同样只写隔离根（GetWritableMetadataDir → 隔离根\Metadata）
        Assert.True(File.Exists(Path.Combine(_isoRoot, "Metadata", "tools.json")));
    }

    [Fact]
    public async Task ImportZip_Isolation_WritesOnlyToIsolationRoot()
    {
        var candidateTools = NewFakeCandidatePackage("空分类_候选包");
        var zipPath = Path.Combine(_root, "tool.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("bin/tool.exe");
            using var s = entry.Open();
            s.WriteByte(0x4D);
        }

        var request = new CustomToolImportRequest(
            zipPath, "测试工具B", "分类Y", "bin/tool.exe", null, null, [], []);
        var result = await CustomToolPackageService.ImportAsync(request);

        var expectedRoot = Path.Combine(_isoRoot, "Tools");
        Assert.StartsWith(expectedRoot, result.ToolDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.PrimaryExecutablePath));
        Assert.False(Directory.Exists(Path.Combine(candidateTools, "分类Y")));
    }

    // ---------- 隔离"安装"（社区工具下载队列处理器）：只写隔离根 ----------

    [Fact]
    public async Task CommunityInstallProcessor_Isolation_ExtractsOnlyToIsolationRoot()
    {
        var candidateTools = NewFakeCandidatePackage("空分类_候选包");
        var zipPath = Path.Combine(_root, "community.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("run.exe");
            using var s = entry.Open();
            s.WriteByte(0x4D);
        }

        var processor = new CommunityToolInstallProcessor("commtool1", "分类Z", isArchive: true);
        await processor.ExecuteAsync(zipPath, Path.Combine(_root, "unused-dest"), null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_isoRoot, "Tools", "分类Z", "commtool1", "run.exe")));
        Assert.False(Directory.Exists(Path.Combine(candidateTools, "分类Z")));
    }

    // ---------- 生产语义不回归（纯函数与映射） ----------

    [Fact]
    public void ProductionSemantics_Unchanged_WhenNotIsolated()
    {
        DataRoots.TestRootOverrideForTest = null;
        try
        {
            var tools = ToolCatalog.ToolsRoot;
            if (DataRoots.EffectiveTestRoot is null)
            {
                // 未隔离进程（生产路径）：恒等映射，语义与修复前完全一致
                Assert.True(ToolCatalog.TryResolveWritableToolsPath(Path.Combine(tools, "其他工具"), out var resolved));
                Assert.Equal(Path.Combine(tools, "其他工具"), resolved);

                Assert.True(ToolCatalog.TryResolveWritableToolsPath(tools, out var rootItself));
                Assert.Equal(tools, rootItself);
            }
            else
            {
                // 进程级隔离根存在时（套件带 ZXAI_DATA_ROOT 运行）：Tools 树路径映射到隔离根下
                Assert.True(ToolCatalog.TryResolveWritableToolsPath(Path.Combine(tools, "其他工具"), out var resolved));
                Assert.Equal(Path.Combine(DataRoots.EffectiveTestRoot, "Tools", "其他工具"), resolved);
            }
        }
        finally { DataRoots.TestRootOverrideForTest = _isoRoot; }
    }

    [Fact]
    public void IsolationMapping_ToolsTreePath_MapsToWritableRoot()
    {
        var tools = ToolCatalog.ToolsRoot;

        Assert.True(ToolCatalog.TryResolveWritableToolsPath(
            Path.Combine(tools, "其他工具", "X", "x.exe"), out var resolved));
        Assert.Equal(Path.Combine(_isoRoot, "Tools", "其他工具", "X", "x.exe"), resolved);

        // 树外路径（如临时目录）→ 隔离态拒绝写/删
        Assert.False(ToolCatalog.TryResolveWritableToolsPath(Path.Combine(_root, "outside"), out _));

        // 路径 ∈ 随包 Tools 树本身 → 映射到可写根
        Assert.True(ToolCatalog.TryResolveWritableToolsPath(tools, out var rootMapped));
        Assert.Equal(Path.Combine(_isoRoot, "Tools"), rootMapped);
    }

    // ---------- 复核退回第 3 项：两种下载入口目标（假根负控，fail-closed） ----------

    [Fact]
    public void DownloadTargets_Isolation_MapToWritableRoot_AndFailClosed()
    {
        var tools = ToolCatalog.ToolsRoot;

        // 入口 1：首页下载对话框（HomePage.ShowDownloadDialogAsync 与 ToolDownloadDialog 共用同一解析）
        Assert.True(ToolCatalog.TryResolveDownloadTarget(Path.Combine(tools, "其他工具", "X"), out var mapped));
        Assert.Equal(Path.Combine(_isoRoot, "Tools", "其他工具", "X"), mapped);

        // 已在可写根内 → 原样放行
        Assert.True(ToolCatalog.TryResolveDownloadTarget(Path.Combine(_isoRoot, "Tools", "A", "B"), out var passthrough));
        Assert.Equal(Path.Combine(_isoRoot, "Tools", "A", "B"), passthrough);

        // 树外路径 → 隔离态 fail-closed（绝不回落原目录）
        Assert.False(ToolCatalog.TryResolveDownloadTarget(Path.Combine(_root, "outside"), out _));

        // 入口 2：优化鸭内置工具安装目标（交给下载队列的实际 destDir）
        Assert.Equal(Path.Combine(_isoRoot, "Tools", "系统工具", "优化鸭"), OptimizerDuckTool.ResolveInstallDir());

        // 生产态：恒等放行（语义不变）。注意 env 模式（套件带 ZXAI_DATA_ROOT 运行）下
        // override=null 只会回落到进程隔离根，并非生产态 → 按 EffectiveTestRoot 分支断言。
        DataRoots.TestRootOverrideForTest = null;
        try
        {
            if (DataRoots.EffectiveTestRoot is null)
            {
                Assert.True(ToolCatalog.TryResolveDownloadTarget(Path.Combine(_root, "outside"), out var prod));
                Assert.Equal(Path.Combine(_root, "outside"), prod);
                Assert.Equal(Path.Combine(ToolCatalog.ToolsRoot, "系统工具", "优化鸭"), OptimizerDuckTool.ResolveInstallDir());
            }
            else
            {
                // 进程级隔离根仍在：override=null 不得使其失效（树外路径仍 fail-closed；安装目标仍映射）
                Assert.False(ToolCatalog.TryResolveDownloadTarget(Path.Combine(_root, "outside"), out _));
                Assert.Equal(Path.Combine(DataRoots.EffectiveTestRoot, "Tools", "系统工具", "优化鸭"),
                    OptimizerDuckTool.ResolveInstallDir());
            }
        }
        finally { DataRoots.TestRootOverrideForTest = _isoRoot; }
    }

    // ---------- 复核退回第 3 项：社区安装态查询看可写根（隔离安装可发现/可启动） ----------

    [Fact]
    public void CommunityInstallStatus_Isolation_FoundInWritableRoot()
    {
        var tool = new CommunityTool { Id = "comm-detect-1", Name = "检测用社区工具", Category = "分类C" };
        Assert.Equal(CommunityToolInstallStatus.NotInstalled, CommunityToolService.CheckInstallStatus(tool));

        var exePath = Path.Combine(_isoRoot, "Tools", "分类C", "comm-detect-1", "run.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllText(exePath, "M");

        Assert.Equal(CommunityToolInstallStatus.Installed, CommunityToolService.CheckInstallStatus(tool));
        var local = CommunityToolService.GetLocalPath(tool);
        Assert.NotNull(local);
        Assert.StartsWith(Path.Combine(_isoRoot, "Tools"), local!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("run.exe", local!, StringComparison.OrdinalIgnoreCase);

        // 随包（真实仓库 Tools）不因此出现该工具目录
        Assert.False(Directory.Exists(Path.Combine(ToolCatalog.ToolsRoot, "分类C", "comm-detect-1")));
    }
}

// ---------- 复核退回第 3 项：安装后可发现/启动（假根负控，可写根优先 + 随包只读可见） ----------

[Collection("GlobalConfigTests")]
public class ToolDiscoveryIsolationTests : IDisposable
{
    private readonly string _root;
    private readonly string _isoRoot;
    private readonly string _fakeTools;
    private readonly string _fakeMeta;

    public ToolDiscoveryIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zxai-discovery-" + Guid.NewGuid().ToString("N"));
        _isoRoot = Path.Combine(_root, "iso");
        _fakeTools = Path.Combine(_root, "candidate", "Tools");
        _fakeMeta = Path.Combine(_root, "candidate", "Metadata");
        Directory.CreateDirectory(_isoRoot);

        // 随包（假候选包）：分类P 下有 工具P1 / 工具P3
        CreateExe(Path.Combine(_fakeTools, "分类P", "工具P1", "p1.exe"));
        CreateExe(Path.Combine(_fakeTools, "分类P", "工具P3", "p3.exe"));
        // 可写根（隔离态安装落点）：分类P 下新增 工具P2 与同名副本 工具P3；另有可写根独有 分类Q
        CreateExe(Path.Combine(_isoRoot, "Tools", "分类P", "工具P2", "p2.exe"));
        CreateExe(Path.Combine(_isoRoot, "Tools", "分类P", "工具P3", "p3.exe"));
        CreateExe(Path.Combine(_isoRoot, "Tools", "分类Q", "工具Q1", "q1.exe"));

        Directory.CreateDirectory(_fakeMeta);
        File.WriteAllText(Path.Combine(_fakeMeta, "tools.json"), """
        {
          "tools": [
            { "match": "工具P1", "category": "分类P" },
            { "match": "工具P2", "category": "分类P" },
            { "match": "工具P3", "category": "分类P" },
            { "match": "工具Q1", "category": "分类Q" }
          ]
        }
        """);

        DataRoots.TestRootOverrideForTest = _isoRoot;
        ToolCatalog.SetToolsRootForBuild(_fakeTools);
        ToolMetadataService.SetMetadataRootForTests(_fakeMeta);
        ToolCatalog.OnToolsChanged();
    }

    public void Dispose()
    {
        try { AppSettings.InvalidateCache(); } catch { }
        ToolMetadataService.SetMetadataRootForTests(null);
        ToolCatalog.SetToolsRootForBuild(null);
        ToolCatalog.OnToolsChanged();
        DataRoots.TestRootOverrideForTest = null;
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void CreateExe(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x00, 0x00]); // MZ 头占位
    }

    [Fact]
    public void InstalledTool_Isolation_DiscoverableAndLaunchable()
    {
        // 分类合并：随包分类 + 可写根独有分类（隔离安装）都必须出现
        var categories = ToolCatalog.GetCategories();
        Assert.Contains("分类P", categories);
        Assert.Contains("分类Q", categories);

        var pItems = ToolCatalog.GetTools("分类P");
        // 随包工具仍可见（只读展示/启动来源）
        Assert.Contains(pItems, t => t.Path!.EndsWith("p1.exe", StringComparison.OrdinalIgnoreCase));
        // 隔离安装的工具可发现，且路径指向可写根（可启动）
        var installed = pItems.Single(t => t.Path!.EndsWith("p2.exe", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith(Path.Combine(_isoRoot, "Tools"), installed.Path!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(installed.Path!));

        // 可写根独有分类的工具可发现/可启动
        var qItems = ToolCatalog.GetTools("分类Q");
        var q = Assert.Single(qItems);
        Assert.StartsWith(Path.Combine(_isoRoot, "Tools"), q.Path!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(q.Path!));

        // 同名副本去重：只出现一条，且优先可写根（隔离安装覆盖只读同名副本）
        var p3 = pItems.Where(t => t.Path!.EndsWith("p3.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        var only = Assert.Single(p3);
        Assert.StartsWith(Path.Combine(_isoRoot, "Tools"), only.Path!, StringComparison.OrdinalIgnoreCase);
    }
}
