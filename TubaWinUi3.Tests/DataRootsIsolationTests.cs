using System.Diagnostics;
using System.Security.Principal;
using TubaWinUi3.Services;
using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【GUI 隔离】ZXAI_DATA_ROOT 校验（fail-fast，2026-09-22）：
/// 无效值（相对路径 / 无法创建 / 不可写）必须抛异常终止启动，绝不允许静默回退真实数据目录——
/// 静默回退会让"隔离实例"直接读写用户的真实配置与 API Key。
///
/// 说明：<see cref="DataRoots.TestRoot"/> 是静态只读字段（进程内无法变换环境变量），因此这里只测
/// 可测纯函数 <see cref="DataRoots.ValidateTestRoot"/>——它只看入参 + 文件系统，不读环境变量。
/// 另附一条与进程环境无关的一致性断言，验证静态入口 TestRoot / IsTestMode 自洽。
/// </summary>
public class DataRootsIsolationTests
{
    private static string NewTempComponent() => "zxai-dataroots-" + Guid.NewGuid().ToString("N");

    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), NewTempComponent());

    // ---------- 分支①：未设置 / 空白 → null（正常生产模式，行为与正式产品一致） ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void ValidateTestRoot_NotSetOrBlank_ReturnsNull(string? raw)
        => Assert.Null(DataRoots.ValidateTestRoot(raw));

    // ---------- 分支②：相对路径 → 抛（不得按当前工作目录"猜"一个目录出来） ----------

    [Theory]
    [InlineData("relative-root")]
    [InlineData(@"..\zxai-root")]
    [InlineData(@"sub\dir\root")]
    [InlineData("zxai/root")]
    public void ValidateTestRoot_RelativePath_Throws(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DataRoots.ValidateTestRoot(raw));
        Assert.Contains("ZXAI_DATA_ROOT 无效", ex.Message);
        Assert.Contains("绝对路径", ex.Message);
    }

    // ---------- 分支③：绝对路径但无法创建 → 抛 ----------

    [Fact]
    public void ValidateTestRoot_AbsoluteButNotCreatable_Throws()
    {
        // 用一个"文件"当父目录：绝对路径成立，但 CreateDirectory/写入必然失败。
        // （不依赖 ACL / 管理员身份，任何机器上都确定失败。）
        var filePath = Path.Combine(Path.GetTempPath(), NewTempComponent() + ".file");
        File.WriteAllText(filePath, "not a directory");
        try
        {
            var bogusRoot = Path.Combine(filePath, "child-root");
            Assert.True(Path.IsPathFullyQualified(bogusRoot));

            var ex = Assert.Throws<InvalidOperationException>(() => DataRoots.ValidateTestRoot(bogusRoot));
            Assert.Contains("ZXAI_DATA_ROOT 无效", ex.Message);
        }
        finally
        {
            try { File.Delete(filePath); } catch { }
        }
    }

    // ---------- 分支④：绝对路径可创建但不可写 → 抛（探针写入分支） ----------

    [SkippableFact]
    public void ValidateTestRoot_UnwritableDirectory_Throws()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) { Skip.If(true, "无法获取当前用户 SID"); return; }
        try
        {
            // 对当前用户在该目录上拒绝写入（W = 含 FILE_ADD_FILE）
            if (!TryIcacls($"{Quote(dir)} /deny \"*{sid}\":(W)"))
            {
                Skip.If(true, "icacls 不可用或无权限（无法构造不可写目录）");
                return;
            }

            // 自检：确认 ACL 拒绝真的生效（提权/特权环境可能绕过 ACL）——否则本分支无从验证，跳过而不是伪造通过
            if (CanWriteProbe(dir))
            {
                Skip.If(true, "当前进程可绕过 ACL（拒绝项未生效），跳过不可写分支");
                return;
            }

            var ex = Assert.Throws<InvalidOperationException>(() => DataRoots.ValidateTestRoot(dir));
            Assert.Contains("ZXAI_DATA_ROOT 无效", ex.Message);
            Assert.Contains("不可写", ex.Message);
        }
        finally
        {
            TryIcacls($"{Quote(dir)} /remove:d \"*{sid}\"");   // 先撤销拒绝项，否则临时目录删不掉
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------- 分支⑤：绝对路径且可创建可写 → 通过并返回规范化路径 ----------

    [Fact]
    public void ValidateTestRoot_AbsoluteWritable_CreatesDir_ReturnsFullPath_CleansProbe()
    {
        var raw = NewTempDir();
        try
        {
            var got = DataRoots.ValidateTestRoot(raw);

            Assert.Equal(Path.GetFullPath(raw), got);
            Assert.True(Directory.Exists(got));
            // 探针文件必须清理干净（隔离根里不能留垃圾）
            Assert.Empty(Directory.GetFileSystemEntries(got!, ".zxai-write-probe-*"));
        }
        finally
        {
            try { Directory.Delete(Path.GetFullPath(raw), true); } catch { }
        }
    }

    [Fact]
    public void ValidateTestRoot_TrimsSurroundingWhitespace()
    {
        var raw = NewTempDir();
        try
        {
            Assert.Equal(Path.GetFullPath(raw), DataRoots.ValidateTestRoot("  " + raw + "  "));
        }
        finally
        {
            try { Directory.Delete(Path.GetFullPath(raw), true); } catch { }
        }
    }

    [Fact]
    public void ValidateTestRoot_ExistingDirectory_IsIdempotent()
    {
        var raw = NewTempDir();
        Directory.CreateDirectory(raw);
        try
        {
            Assert.Equal(Path.GetFullPath(raw), DataRoots.ValidateTestRoot(raw));
            Assert.Equal(Path.GetFullPath(raw), DataRoots.ValidateTestRoot(raw));   // 可重复校验
        }
        finally
        {
            try { Directory.Delete(Path.GetFullPath(raw), true); } catch { }
        }
    }

    /// <summary>绝对路径（含已存在目录）不得因"目录已存在"被误判为无效。</summary>
    [Fact]
    public void ValidateTestRoot_SystemTempPath_IsAccepted()
    {
        var got = DataRoots.ValidateTestRoot(Path.GetTempPath());
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), got);
    }

    // ---------- 静态入口自洽（不依赖环境变量取值，任何机器都能跑） ----------

    [Fact]
    public void TestRoot_IsConsistentWithIsTestMode()
    {
        Assert.Equal(DataRoots.TestRoot is not null, DataRoots.IsTestMode);
        Assert.Equal(DataRoots.IsTestMode, DataRoots.ShouldSkipLegacyMigration);
        if (DataRoots.TestRoot is { } root)
        {
            // 测试模式：根必须是绝对路径且真实存在（无效值在启动时已终止进程，走不到这里）
            Assert.True(Path.IsPathFullyQualified(root));
            Assert.True(Directory.Exists(root));
        }
    }

    /// <summary>真实数据目录必须始终指向 %LocalAppData%\TubaWinUi3（测试模式也不许被改写）。</summary>
    [Fact]
    public void RealDataDir_AlwaysPointsToRealLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TubaWinUi3");
        Assert.Equal(expected, DataRoots.RealDataDir);
    }

    // ---------- ⑥ 数据基目录（PickBaseDir 纯函数 + 隔离路由活体验证） ----------

    [Fact]
    public void PickBaseDir_NoTestRoot_ReturnsRealBase()
    {
        var local = @"C:\Users\Example\AppData\Local";
        Assert.Equal(Path.Combine(local, "TubaWinUi3"), DataRoots.PickBaseDir(null, local));
    }

    [Fact]
    public void PickBaseDir_TestRoot_ReplacesWholeBaseSegment()
    {
        // 隔离根整体替换 %LocalAppData%\TubaWinUi3 段（不拼在真实路径下面）
        var baseDir = DataRoots.PickBaseDir(@"D:\zxai-iso-root", @"C:\Users\Example\AppData\Local");
        Assert.Equal(Path.Combine(@"D:\zxai-iso-root", "sub"), Path.Combine(baseDir, "sub"));
    }

    /// <summary>
    /// 【活体路由】隔离根覆盖下：{AppDataDir} / 内核可写目录 / 沙箱环境根必须全部改道隔离根。
    /// 短暂设置 <see cref="DataRoots.TestRootOverrideForTest"/>（仅测试钩子；本程序集已禁用集合并行，
    /// 不会与其它测试并发），finally 必须复位 null。
    /// </summary>
    [Fact]
    public void RoutedResolvers_FollowTestRootOverride()
    {
        var isoRoot = Path.Combine(Path.GetTempPath(), NewTempComponent());
        try
        {
            DataRoots.TestRootOverrideForTest = isoRoot;

            Assert.Equal(Path.Combine(isoRoot, "x", "y.json"),
                PathResolver.ExpandPath(@"{AppDataDir}\x\y.json"));
            Assert.Equal(Path.Combine(isoRoot, "Tools"), ToolsBundleService.GetToolsBundleDir());
            Assert.Equal(Path.Combine(isoRoot, "ToolboxCore", "Environments"), AppCenterService.EnvironmentsRoot);
        }
        finally
        {
            DataRoots.TestRootOverrideForTest = null;
        }
    }

    /// <summary>覆盖复位后必须回到真实基目录语义（仅进程未以隔离模式启动时可断言具体路径）。</summary>
    [Fact]
    public void RoutedResolvers_WithoutOverride_ReturnRealBase()
    {
        try
        {
            DataRoots.TestRootOverrideForTest = null;
            if (DataRoots.TestRoot is not null) return;   // 本进程本身在隔离模式下运行：不断言真实路径

            var real = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TubaWinUi3");
            Assert.Equal(Path.Combine(real, "Tools"), ToolsBundleService.GetToolsBundleDir());
            // 生产语义：沙箱根 = %LocalAppData%\ToolboxCore\Environments（不经 TubaWinUi3 段）
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToolboxCore", "Environments"),
                AppCenterService.EnvironmentsRoot);
            Assert.StartsWith(real, PathResolver.ExpandPath("{AppDataDir}"), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DataRoots.TestRootOverrideForTest = null;
        }
    }

    // ---------- ⑦ 源码审计：%LocalAppData% 直引清单（新直引必须接隔离或登记理由） ----------

    /// <summary>
    /// 允许清单：逐条列明"未经 DataRoots.PickBaseDir / EffectiveTestRoot 路由"仍可直引
    /// %LocalAppData%（或 RuntimeHelper.GetLocalAppDataRoot）的位置与理由。
    /// 规则：扫描 TubaWinUi3.WinUI3 / TubaWinUi3.Compatible 全部 .cs（排除 obj/bin）——
    ///  · 命中行 ±5 行内出现隔离路由 token（TestRoot / PickBaseDir / IsTestMode / ShouldSkipLegacyMigration）
    ///    → 视为已接隔离，自动通过；
    ///  · 否则必须与某条（文件后缀 + 行内片段）精确匹配；未匹配 → 失败（新直引须先接隔离）；
    ///  · 清单条目若不再命中任何扫描命中行 → 同样失败（防清单漂移/失效）。
    /// 除直引 %LocalAppData% 外，扫描 token 还含派生数据目录字段 <c>AppDataDir</c> 的**使用点**——
    /// ConfigManager 迁移链曾漏审的根因 = 只登记了定义行、写路径引用派生字段未被覆盖；现在使用点逐一受审。
    /// </summary>
    private static readonly (string FileSuffix, string LineSnippet, string Reason)[] ApprovedLocalAppDataSites =
    new[]
    {
        ("TubaWinUi3.Compatible/Services/ConfigManager.cs", "SpecialFolder.LocalApplicationData",
            "兼容版（WinForms）无隔离支持——隔离运行不启动兼容版 exe；已知限制，报告如实披露"),
        ("Pages/AiAgentPage.xaml.cs", "SpecialFolder.LocalApplicationData",
            "ffmpeg 定位（优先 PATH，回退 LocalAppData\\Microsoft\\WinGet\\Links；只读外部工具查找）"),
        ("Services/Agent/Skills/UserSkillLoader.cs", "SpecialFolder.LocalApplicationData",
            "已接隔离（TestRoot 优先分支在前）；此处为生产回退路径（SkillsDirOverride 或无隔离进程）"),
        ("Services/Ai/Dsh/DshRuntimeResolver.cs", "SpecialFolder.LocalApplicationData",
            "开发模式回退：定位 hermes 自带 node 运行时（只读外部运行时查找，不写应用数据）"),
        ("Services/AppManagement/SystemInstaller.cs", "SpecialFolder.LocalApplicationData",
            "winget 包目录探测（%LocalAppData%\\Microsoft\\WinGet\\Packages；只读外部安装位置）"),
        ("Services/BuiltinTools/OptimizerDuckTool.cs", "SpecialFolder.LocalApplicationData",
            "安装位置扫描（只读探测已安装软件目录，非应用数据读写）"),
        ("Services/DotnetCompletionService.cs", "SpecialFolder.LocalApplicationData",
            "dotnet 目录探测（只读探测运行时位置）"),
        ("Services/JunkCleaner/PathExpander.cs", "Add(\"LocalAppData\"",
            "垃圾清理功能域根（功能语义 = 系统 LocalAppData 域，非应用数据）"),
        ("Services/JunkCleaner/PathExpander.cs", "Add(\"LocalLowAppData\"",
            "垃圾清理功能域根（LocalLow 域；同一功能语义）"),
        ("Services/RogueCleaner/ContextMenuAdvancedModules.cs", "WinX",
            "Windows 系统 WinX 菜单目录（系统集成路径，非应用数据）"),
        ("Services/ConfigManager.cs", "_ => AppDataDir",
            "GetDataDir() 生产分支枚举值（隔离模式在其前方 TestRoot 分支直接返回隔离根，此处仅生产可达）"),
        ("Services/ConfigManager.cs", "return AppDataDir;",
            "ResolveCustomDataDir 生产回退（隔离进程 GetDataDir 提前返回、不可达；已配 MigrateData 迁移链负控）"),
        ("Services/PathResolver.cs", "AppData 目录 (%LocalAppData%",
            "占位符人类可读描述串（仅展示用途，不涉及读写）"),
        ("Services/PathResolver.cs", "=> \"{AppDataDir}",
            "占位符展开示例串（仅展示用途，不涉及读写）"),
        ("TubaWinUi3.Compatible/Services/ConfigManager.cs", "static readonly string AppDataDir",
            "兼容版（WinForms）无隔离支持——隔离运行不启动兼容版 exe；已知限制，报告如实披露"),
        ("TubaWinUi3.Compatible/Services/ConfigManager.cs", "AppRootDir : AppDataDir",
            "兼容版数据目录选择（同上：无隔离支持，隔离运行不启动兼容版 exe）"),
        ("Services/RuntimeHelper.cs", "public static string GetLocalAppDataRoot()",
            "LocalAppData 访问器本体（隔离替换发生在调用点，经 DataRoots.PickBaseDir）"),
        ("Services/RuntimeHelper.cs", "_localAppDataRoot = Environment.GetFolderPath",
            "同上（解析实现本体）"),
        ("Services/WebView2EnvironmentService.cs", "SpecialFolder.LocalApplicationData",
            "已接隔离（ResolveUserDataFolder 的 testRoot 优先分支在前：隔离模式 = <隔离根>\\WebView2）；此处仅无隔离进程的生产回退可达"),
    };

    [Fact]
    public void Sources_NoUnguardedLocalAppDataRoots()
    {
        var repoRoot = FindRepositoryRoot();
        var roots = new[] { "TubaWinUi3.WinUI3", "TubaWinUi3.Compatible" };
        var scanTokens = new[] { "GetLocalAppDataRoot(", "SpecialFolder.LocalApplicationData", "AppDataDir" };
        var routedTokens = new[] { "TestRoot", "PickBaseDir", "IsTestMode", "ShouldSkipLegacyMigration" };

        var hits = 0;
        var violations = new List<string>();
        var used = new bool[ApprovedLocalAppDataSites.Length];

        foreach (var root in roots)
        {
            var rootDir = Path.Combine(repoRoot, root);
            if (!Directory.Exists(rootDir)) continue;
            foreach (var file in Directory.EnumerateFiles(rootDir, "*.cs", SearchOption.AllDirectories))
            {
                var norm = file.Replace('\\', '/');
                if (norm.Contains("/obj/") || norm.Contains("/bin/")) continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!scanTokens.Any(t => lines[i].Contains(t, StringComparison.Ordinal))) continue;
                    hits++;

                    var lo = Math.Max(0, i - 5);
                    var hi = Math.Min(lines.Length - 1, i + 5);
                    var routed = false;
                    for (var j = lo; j <= hi && !routed; j++)
                        routed = routedTokens.Any(t => lines[j].Contains(t, StringComparison.Ordinal));
                    if (routed) continue;

                    var matched = false;
                    for (var k = 0; k < ApprovedLocalAppDataSites.Length; k++)
                    {
                        var entry = ApprovedLocalAppDataSites[k];
                        if (norm.EndsWith(entry.FileSuffix, StringComparison.Ordinal) &&
                            lines[i].Contains(entry.LineSnippet, StringComparison.Ordinal))
                        {
                            used[k] = true;
                            matched = true;
                            break;
                        }
                    }

                    if (!matched)
                        violations.Add(norm + ":" + (i + 1) + ": " + lines[i].Trim());
                }
            }
        }

        Assert.True(hits > 0, "扫描未命中任何 %LocalAppData% 直引——扫描条件失效，审计形同虚设");
        Assert.True(violations.Count == 0,
            "发现未接隔离且未登记的 %LocalAppData% 直引（须接 DataRoots.PickBaseDir/EffectiveTestRoot 或登记允许清单）：\n" +
            string.Join("\n", violations));

        var stale = new List<string>();
        for (var k = 0; k < ApprovedLocalAppDataSites.Length; k++)
            if (!used[k])
                stale.Add(ApprovedLocalAppDataSites[k].FileSuffix + " :: " + ApprovedLocalAppDataSites[k].LineSnippet);
        Assert.True(stale.Count == 0,
            "允许清单存在失效条目（源码中已找不到对应命中，须同步更新清单）：\n" + string.Join("\n", stale));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TubaWinUi3.WinUI3", "TubaWinUi3.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到仓库根（缺少 TubaWinUi3.WinUI3/TubaWinUi3.csproj）");
    }

    // ---------- ⑧ ConfigManager 迁移/切换：纯测试根负控（真实根绝不创建/覆盖） ----------

    /// <summary>
    /// 【纯函数负控】隔离态 targetLocation=AppData 必须解析到隔离根本身（= 迁移源 → 无操作成功路径），
    /// 绝不解析到真实 %LocalAppData%\TubaWinUi3；并回归生产态语义。
    /// </summary>
    [Fact]
    public void MigrationTarget_AppData_InTestMode_ResolvesToTestRoot_NotRealRoot()
    {
        var isoRoot = Path.Combine(Path.GetTempPath(), NewTempComponent());
        var appDir = @"D:\fake-app";
        var appRootDir = Path.Combine(appDir, "Data");

        var target = ConfigManager.ResolveMigrationTarget(
            ConfigLocation.AppData, null, isoRoot, appDir, appRootDir, isoRoot);

        Assert.Equal(isoRoot, target);
        Assert.NotEqual(DataRoots.RealDataDir, target);   // 纯字符串断言：未触碰真实路径

        // 生产态（testRoot=null）语义回归：目标 = 传入的生产 AppData 基准
        var prod = @"C:\Users\Example\AppData\Local\TubaWinUi3";
        Assert.Equal(prod, ConfigManager.ResolveMigrationTarget(
            ConfigLocation.AppData, null, null, appDir, appRootDir, prod));
    }

    /// <summary>【纯函数负控】隔离态 AppRoot 一律拒绝（应用目录不是隔离态落点）；生产态照旧。</summary>
    [Fact]
    public void MigrationTarget_AppRoot_InTestMode_IsRefused()
    {
        var isoRoot = Path.Combine(Path.GetTempPath(), NewTempComponent());
        var appDir = @"D:\fake-app";
        var appRootDir = Path.Combine(appDir, "Data");

        Assert.Null(ConfigManager.ResolveMigrationTarget(
            ConfigLocation.AppRoot, null, isoRoot, appDir, appRootDir, isoRoot));

        Assert.Equal(appRootDir, ConfigManager.ResolveMigrationTarget(
            ConfigLocation.AppRoot, null, null, appDir, appRootDir, @"C:\x"));
    }

    /// <summary>【纯函数负控】隔离态 Custom：隔离根外/空路径一律拒绝；隔离根内允许。</summary>
    [Fact]
    public void MigrationTarget_Custom_InTestMode_RefusesOutside_AllowsInside()
    {
        var isoRoot = Path.Combine(Path.GetTempPath(), NewTempComponent());
        var outside = Path.Combine(Path.GetTempPath(), NewTempComponent());   // 隔离根外的任意外部路径（临时假根）
        var appDir = @"D:\fake-app";
        var appRootDir = Path.Combine(appDir, "Data");

        Assert.Null(ConfigManager.ResolveMigrationTarget(ConfigLocation.Custom, outside, isoRoot, appDir, appRootDir, isoRoot));
        Assert.Null(ConfigManager.ResolveMigrationTarget(ConfigLocation.Custom, @"C:\anywhere\else", isoRoot, appDir, appRootDir, isoRoot));
        Assert.Null(ConfigManager.ResolveMigrationTarget(ConfigLocation.Custom, null, isoRoot, appDir, appRootDir, isoRoot));
        Assert.Null(ConfigManager.ResolveMigrationTarget(ConfigLocation.Custom, "  ", isoRoot, appDir, appRootDir, isoRoot));

        var inside = Path.Combine(isoRoot, "backup");
        Assert.Equal(inside, ConfigManager.ResolveMigrationTarget(ConfigLocation.Custom, inside, isoRoot, appDir, appRootDir, isoRoot));

        // 【点名用例】Custom = {AppDataDir}\backup：隔离态 {AppDataDir} 展开 = 隔离根 → 允许；
        // 该展开经 PathResolver 全局解析，故用测试钩子把 EffectiveTestRoot 固定为 isoRoot 后再断言。
        try
        {
            DataRoots.TestRootOverrideForTest = isoRoot;
            Assert.Equal(inside,
                ConfigManager.ResolveMigrationTarget(ConfigLocation.Custom, @"{AppDataDir}\backup", isoRoot, appDir, appRootDir, isoRoot));
        }
        finally { DataRoots.TestRootOverrideForTest = null; }
    }

    /// <summary>
    /// 【活体负控】隔离模式下 MigrateData 全链：目标=AppData 为无操作成功；外部 Custom / 嵌套 Custom /
    /// AppRoot 一律拒绝且零文件系统效果——隔离源数据保持原样、外部路径绝不创建；仅切换（migrate=false）留档于隔离根。
    /// </summary>
    [Fact]
    public void MigrateData_InTestMode_RefusesUnsafeTargets_ZeroFsEffect()
    {
        var isoRoot = Path.Combine(Path.GetTempPath(), NewTempComponent());
        var outside = Path.Combine(Path.GetTempPath(), NewTempComponent());
        Directory.CreateDirectory(isoRoot);
        File.WriteAllText(Path.Combine(isoRoot, "settings.json"), "{\"marker\":\"iso\"}");
        try
        {
            DataRoots.TestRootOverrideForTest = isoRoot;
            ConfigManager.SetConfigLocation(ConfigLocation.AppData);   // 清缓存，确保 GetDataDir 在覆盖下重新解析（= isoRoot）
            Assert.Equal(isoRoot, ConfigManager.GetDataDir());          // 隔离态数据根 = 隔离根
            Assert.NotEqual(DataRoots.RealDataDir, ConfigManager.GetDataDir());   // 绝非真实数据目录

            // ① targetLocation=AppData：解析到隔离根本身 → 无操作成功；隔离源保持
            Assert.True(ConfigManager.MigrateData(ConfigLocation.AppData, true));
            Assert.True(File.Exists(Path.Combine(isoRoot, "settings.json")));

            // ② 外部 Custom：拒绝；外部路径绝不被创建（真实根/外部根负控）
            Assert.False(ConfigManager.MigrateData(ConfigLocation.Custom, true, outside));
            Assert.False(Directory.Exists(outside));

            // ③ 隔离根内嵌套 Custom + 迁移：拒绝（复制后删源 = 自毁）；隔离源保持、目标未创建
            Assert.False(ConfigManager.MigrateData(ConfigLocation.Custom, true, Path.Combine(isoRoot, "backup")));
            Assert.True(File.Exists(Path.Combine(isoRoot, "settings.json")));
            Assert.False(Directory.Exists(Path.Combine(isoRoot, "backup")));

            // ④ AppRoot：拒绝；零效果
            Assert.False(ConfigManager.MigrateData(ConfigLocation.AppRoot, true));
            Assert.True(File.Exists(Path.Combine(isoRoot, "settings.json")));

            // ⑤ 仅切换（migrate=false）+ 隔离根内 Custom（点名用例 {AppDataDir}）：允许（不动数据）；标记写入隔离根
            Assert.True(ConfigManager.MigrateData(ConfigLocation.Custom, false, @"{AppDataDir}\cfg2"));
            Assert.True(File.Exists(Path.Combine(isoRoot, ".config_location")));
            Assert.True(File.Exists(Path.Combine(isoRoot, "settings.json")));
        }
        finally
        {
            try { ConfigManager.SetConfigLocation(ConfigLocation.AppData); } catch { }
            DataRoots.TestRootOverrideForTest = null;
            try { Directory.Delete(isoRoot, true); } catch { }
        }
    }

    /// <summary>【活体负控】隔离模式 SetConfigLocation 绝不写应用目录：标记必须落隔离根；读写自洽且可撤销。</summary>
    [Fact]
    public void SetConfigLocation_InTestMode_WritesMarkerUnderTestRoot_NotAppDir()
    {
        var isoRoot = Path.Combine(Path.GetTempPath(), NewTempComponent());
        Directory.CreateDirectory(isoRoot);
        try
        {
            DataRoots.TestRootOverrideForTest = isoRoot;

            Assert.True(ConfigManager.SetConfigLocation(ConfigLocation.Custom, @"{AppDataDir}\cfg"));
            var marker = Path.Combine(isoRoot, ".config_location");
            Assert.True(File.Exists(marker), "标记必须写在隔离根下");
            Assert.Equal(ConfigLocation.Custom, ConfigManager.GetConfigLocation());
            Assert.Equal(@"{AppDataDir}\cfg", ConfigManager.GetCustomPath());

            Assert.True(ConfigManager.SetConfigLocation(ConfigLocation.AppData));
            Assert.False(File.Exists(marker));
            Assert.Equal(ConfigLocation.AppData, ConfigManager.GetConfigLocation());
        }
        finally
        {
            try { ConfigManager.SetConfigLocation(ConfigLocation.AppData); } catch { }
            DataRoots.TestRootOverrideForTest = null;
            try { Directory.Delete(isoRoot, true); } catch { }
        }
    }

    // ---------- ⑨ 内核包解压目标 / 可写 Metadata：隔离态一律落隔离根 ----------

    [Fact]
    public void ToolsBundleInstallTarget_PureFunction_LiteVsIsolation()
    {
        var appTools = @"D:\app\Tools";
        var bundle = @"D:\data\Tools";
        // 生产 Lite + 内置 Tools：就地升级（应用目录）
        Assert.Equal(appTools, ToolsBundleService.PickInstallTargetDir(isLite: true, appToolsExists: true, appTools, bundle, isolated: false));
        // 隔离态：即使 Lite + 内置 Tools 存在，也必须走统一数据根（隔离根），绝不写应用目录
        Assert.Equal(bundle, ToolsBundleService.PickInstallTargetDir(true, true, appTools, bundle, isolated: true));
        // 生产非 Lite / Lite 无内置：统一数据根
        Assert.Equal(bundle, ToolsBundleService.PickInstallTargetDir(false, true, appTools, bundle, isolated: false));
        Assert.Equal(bundle, ToolsBundleService.PickInstallTargetDir(true, false, appTools, bundle, isolated: false));
    }

    [Fact]
    public void ToolsBundleInstallTarget_InIsolation_StaysUnderTestRoot()
    {
        var isoRoot = Path.Combine(Path.GetTempPath(), NewTempComponent());
        try
        {
            DataRoots.TestRootOverrideForTest = isoRoot;
            Assert.Equal(Path.Combine(isoRoot, "Tools"), ToolsBundleService.GetInstallTargetDir());
        }
        finally { DataRoots.TestRootOverrideForTest = null; }
    }

    [Fact]
    public void WritableMetadataDir_InIsolation_StaysUnderTestRoot()
    {
        var isoRoot = Path.Combine(Path.GetTempPath(), NewTempComponent());
        try
        {
            DataRoots.TestRootOverrideForTest = isoRoot;
            var dir = ToolMetadataService.GetWritableMetadataDir();
            Assert.Equal(Path.Combine(isoRoot, "Metadata"), dir);
            Assert.StartsWith(Path.GetFullPath(isoRoot), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DataRoots.TestRootOverrideForTest = null;
            try { Directory.Delete(isoRoot, true); } catch { }
        }
    }

    // ---------- ⑩ 隔离根自我防护：禁止与真实数据目录重叠 / 拒绝 reparse 链（纯假根负控） ----------

    /// <summary>
    /// 【纯假根负控】禁止目录表中与真实数据目录同构的"假真实根"：本身 / 其下 / 其父链（互为包含）
    /// 均必须被字符串级拒绝——且不得对禁止路径产生任何文件系统效果（被拒绝的根不会被创建）。
    /// </summary>
    [Fact]
    public void ValidateTestRootCore_ProhibitedOverlap_Throws_NoFsEffect()
    {
        var fakeLocal = Path.Combine(Path.GetTempPath(), NewTempComponent());
        var fakeReal = Path.Combine(fakeLocal, "TubaWinUi3");                 // 假"真实数据目录"
        var fakeSub = Path.Combine(fakeReal, "sub");                          // 其下
        try
        {
            var ex1 = Assert.Throws<InvalidOperationException>(() => DataRoots.ValidateTestRootCore(fakeReal, [fakeReal]));
            Assert.Contains("重叠", ex1.Message);

            Assert.Throws<InvalidOperationException>(() => DataRoots.ValidateTestRootCore(fakeSub, [fakeReal]));
            Assert.Throws<InvalidOperationException>(() => DataRoots.ValidateTestRootCore(fakeLocal, [fakeReal]));   // 父链（包含真实根）

            // 零文件系统效果：被拒绝的路径（及其父）绝未被创建
            Assert.False(Directory.Exists(fakeLocal));
            Assert.False(Directory.Exists(fakeReal));
        }
        finally
        {
            try { if (Directory.Exists(fakeLocal)) Directory.Delete(fakeLocal, true); } catch { }
        }
    }

    /// <summary>生产入口以真实数据目录为禁止根：字符串级拒绝（不读/不写真实目录；代码顺序保证，见实现①②）。</summary>
    [Fact]
    public void ValidateTestRoot_RealDataDirOverlap_IsRefused_StringOnly()
    {
        var real = DataRoots.RealDataDir;
        var exSelf = Assert.Throws<InvalidOperationException>(() => DataRoots.ValidateTestRoot(real));
        Assert.Contains("重叠", exSelf.Message);
        var exSub = Assert.Throws<InvalidOperationException>(() => DataRoots.ValidateTestRoot(Path.Combine(real, "zxai-should-never-exist")));
        Assert.Contains("重叠", exSub.Message);
    }

    /// <summary>路径链上的 junction/symlink 必须被拒绝（junction 仅在临时假根内创建，绝不涉及真实目录）。</summary>
    [SkippableFact]
    public void ValidateTestRoot_ReparseChain_Throws()
    {
        var target = Path.Combine(Path.GetTempPath(), NewTempComponent());
        var link = Path.Combine(Path.GetTempPath(), NewTempComponent());
        Directory.CreateDirectory(target);
        try
        {
            if (!TryCreateJunction(link, target))
            {
                Skip.If(true, "无法创建 junction（mklink/New-Item 均被系统拒绝）");
                return;
            }
            var attrs = File.GetAttributes(link);
            Assert.True((attrs & FileAttributes.ReparsePoint) != 0, "前置自检：链接确为重解析点");

            var ex = Assert.Throws<InvalidOperationException>(() => DataRoots.ValidateTestRoot(link + @"\child"));
            Assert.Contains("junction/symlink", ex.Message);
        }
        finally
        {
            try
            {
                var di = new DirectoryInfo(link);
                if (di.Exists)
                {
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) di.Delete();   // 只删链接，不触目标
                    else di.Delete(true);
                }
            }
            catch { }
            try { Directory.Delete(target, true); } catch { }
        }
    }

    /// <summary>在临时假根内创建 junction（cmd mklink 优先，被拒回退 PowerShell New-Item；失败返回 false 由调用方 Skip）。</summary>
    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            proc?.WaitForExit(15_000);
            if (Directory.Exists(linkPath) &&
                (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0) return true;
        }
        catch { }
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"New-Item -ItemType Junction -Path '{linkPath}' -Target '{targetPath}' -Force | Out-Null\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            proc?.WaitForExit(20_000);
            return Directory.Exists(linkPath) &&
                (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    // ---------- 辅助 ----------

    private static string Quote(string path) => $"\"{path}\"";

    /// <summary>在目录内试写一个临时文件（用于判断 ACL 拒绝是否真的生效）。</summary>
    private static bool CanWriteProbe(string dir)
    {
        var probe = Path.Combine(dir, "acl-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>调用 icacls（不可用 / 无权限 / 失败一律返回 false，由调用方 Skip）。</summary>
    private static bool TryIcacls(string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (proc is null) return false;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(20_000))
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
