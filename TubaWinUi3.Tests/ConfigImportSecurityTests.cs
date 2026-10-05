using System.IO.Compression;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【安全 · R2 复核退回第 4 项】配置 ZIP 导入的两阶段校验（2026-09-23）：
/// rooted / 盘符 / ".." 段 / 规范化后的严格根边界 / 已存在组件的重解析点 —— 全部 entry
/// 验证通过之前，不执行任何创建、覆盖或解压（防止写到一半才发现恶意条目）。
/// 全部用例只用临时假根与假外部目标，绝不制作指向真实用户目录的测试包；
/// 数据根 = ConfigManager.GetDataDir()（必须等于本测试隔离根，不等于真实目录）。
/// </summary>
public class ConfigImportSecurityTests : IDisposable
{
    private readonly string _root;
    private readonly string _isoRoot;
    private readonly string _external;
    private readonly string _dataRoot;

    public ConfigImportSecurityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zxai-configimport-" + Guid.NewGuid().ToString("N"));
        _isoRoot = Path.Combine(_root, "iso");
        _external = Path.Combine(_root, "external");
        Directory.CreateDirectory(_isoRoot);
        Directory.CreateDirectory(_external);
        DataRoots.TestRootOverrideForTest = _isoRoot;
        ConfigManager.SetConfigLocation(ConfigLocation.AppData);   // 归一位置状态（清缓存，防御历史残留）

        _dataRoot = ConfigManager.GetDataDir();
        // 数据根必须严格=隔离根：覆盖生效 + 绝不等于真实目录（2026-09-23 事故防回归断言）
        Assert.Equal(_isoRoot, _dataRoot);
        Assert.NotEqual(
            Path.GetFullPath(DataRoots.RealDataDir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(_dataRoot).TrimEnd(Path.DirectorySeparatorChar));
    }

    public void Dispose()
    {
        // 硬停：AppSettings 去抖落盘在触发时才解析路径——必须"取消未落盘写 + 清缓存"后才释放覆盖，
        // 防止 500ms 窗口在覆盖释放后把测试数据写进真实目录（2026-09-23 事故根因）。
        try { AppSettings.InvalidateCache(); } catch { }
        try { ConfigManager.SetConfigLocation(ConfigLocation.AppData); } catch { }
        DataRoots.TestRootOverrideForTest = null;
        RemoveTopLevelJunctions();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static int CountEntries(string dir)
        => Directory.GetFileSystemEntries(dir).Length;

    private static void RunCmd(string arguments)
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(15000);
        }
        catch { }
    }

    /// <summary>防御性清理：删除数据根顶层遗留的 junction（即使用例中途失败也不留悬挂链接）。</summary>
    private void RemoveTopLevelJunctions()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(_dataRoot))
            {
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                        RunCmd($"/c rmdir \"{dir}\"");
                }
                catch { }
            }
        }
        catch { }
    }

    private string MakeZip(params (string Name, string Content)[] entries)
    {
        var zipPath = Path.Combine(_root, "cfg-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            if (name.EndsWith('/'))
                continue;   // 目录条目
            using var s = entry.Open();
            using var w = new StreamWriter(s);
            w.Write(content);
        }
        return zipPath;
    }

    // ---------- 恶意条目：整体拒绝 + 外部零创建零改动（两阶段，合法条目也不落盘） ----------

    [Theory]
    [InlineData("../evil.txt")]          // 父目录逃逸
    [InlineData("..\\evil.txt")]         // 反斜杠变体
    [InlineData("a/../../evil.txt")]     // 嵌套逃逸
    [InlineData("/rooted.txt")]          // rooted（绝对）
    [InlineData("C:/colon.txt")]         // 盘符 / 冒号
    [InlineData("sub/../inside.txt")]    // ".." 即使规范化后仍在根内也严格拒绝（语义简单严格）
    public async Task Import_MaliciousEntries_Rejected_NoWritesAnywhere(string evilName)
    {
        var zipPath = MakeZip((evilName, "PWNED"), ("benign.txt", "benign"));
        var externalBefore = CountEntries(_external);
        var dataBefore = CountEntries(_dataRoot);

        var ok = await ConfigManager.ImportConfigAsync(zipPath);

        Assert.False(ok);

        // 两阶段：拒绝发生在任何写入之前——连合法条目 benign.txt 也不落盘
        Assert.False(File.Exists(Path.Combine(_dataRoot, "benign.txt")));
        Assert.Equal(dataBefore, CountEntries(_dataRoot));

        // 外部（假目标）零创建零改动；逃逸名没有落在任何父级
        Assert.Equal(externalBefore, CountEntries(_external));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_external, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_dataRoot, "inside.txt")));
    }

    // ---------- 合法条目：正常导入（数据根下），目录条目也支持 ----------

    [Fact]
    public async Task Import_BenignEntries_WrittenUnderIsolationRoot()
    {
        var zipPath = MakeZip(
            ("sub/", ""),
            ("sub/a.txt", "A"),
            ("b.txt", "B"));

        var ok = await ConfigManager.ImportConfigAsync(zipPath);

        Assert.True(ok);
        Assert.True(File.Exists(Path.Combine(_dataRoot, "sub", "a.txt")));
        Assert.True(File.Exists(Path.Combine(_dataRoot, "b.txt")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(_dataRoot, "sub", "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(_dataRoot, "b.txt")));
    }

    // ---------- 重解析点：目标链上已存在的组件是 junction → 拒绝（临时假根内，不碰真实目录） ----------

    // ---------- 复核退回第 4 项：数据根起初不存在 + 合法条目在前 + 恶意条目在后 ----------

    [Fact]
    public async Task Import_RootAbsent_BenignFirst_MaliciousLater_RejectsAndRootStaysAbsent()
    {
        // 数据根改用全新不存在的路径（本用例专用；期望整包拒绝且根目录本身不被创建——
        // 旧实现在遍历验证条目前先 Directory.CreateDirectory(dataDir)，此组合必现目录）。
        var absentRoot = Path.Combine(_root, "iso-absent");
        DataRoots.TestRootOverrideForTest = absentRoot;
        ConfigManager.SetConfigLocation(ConfigLocation.AppData);
        try
        {
            // SetConfigLocation 会在隔离根写位置标记目录（可能创建该根）→ 先清零，
            // 保证「导入开始时数据根确实不存在」这一前置条件（本用例专项负控）。
            try { if (Directory.Exists(absentRoot)) Directory.Delete(absentRoot, true); } catch { }
            Assert.False(Directory.Exists(absentRoot));   // 前置断言：根起初必须不存在

            var zipPath = MakeZip(("ok/sub/a.txt", "A"), ("../evil.txt", "PWNED"));   // 合法在前、恶意在后
            var externalBefore = CountEntries(_external);

            var ok = await ConfigManager.ImportConfigAsync(zipPath);

            Assert.False(ok);                              // 整包拒绝
            Assert.False(Directory.Exists(absentRoot));    // 数据根仍不存在（零写入含根目录本身）
            Assert.Equal(externalBefore, CountEntries(_external));
            Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));

            // 对照：同一不存在根 + 全合法包 → 导入成功，根在验证通过后才被创建（功能不回退）
            try { if (Directory.Exists(absentRoot)) Directory.Delete(absentRoot, true); } catch { }
            Assert.False(Directory.Exists(absentRoot));
            var benignOnly = Path.Combine(_root, "benign-only.zip");
            using (var zip = ZipFile.Open(benignOnly, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("ok/only.txt");
                using var s = entry.Open();
                using var w = new StreamWriter(s);
                w.Write("hi");
            }
            Assert.True(await ConfigManager.ImportConfigAsync(benignOnly));
            Assert.True(File.Exists(Path.Combine(absentRoot, "ok", "only.txt")));
        }
        finally
        {
            DataRoots.TestRootOverrideForTest = _isoRoot;
            ConfigManager.SetConfigLocation(ConfigLocation.AppData);
        }
    }

    [Fact]
    public async Task Import_RootAbsent_AllMalicious_RejectsAndRootStaysAbsent()
    {
        var absentRoot = Path.Combine(_root, "iso-absent2");
        DataRoots.TestRootOverrideForTest = absentRoot;
        ConfigManager.SetConfigLocation(ConfigLocation.AppData);
        try
        {
            try { if (Directory.Exists(absentRoot)) Directory.Delete(absentRoot, true); } catch { }
            Assert.False(Directory.Exists(absentRoot));
            var zipPath = MakeZip(("/rooted.txt", "PWNED"));

            Assert.False(await ConfigManager.ImportConfigAsync(zipPath));
            Assert.False(Directory.Exists(absentRoot));
        }
        finally
        {
            DataRoots.TestRootOverrideForTest = _isoRoot;
            ConfigManager.SetConfigLocation(ConfigLocation.AppData);
        }
    }

    [Fact]
    public async Task Import_ExistingReparseComponent_Rejected()
    {
        // 在数据根内造一个 junction：<dataRoot>\jnk-xxxx -> external\target（外部假目标）
        var jnkName = "jnk-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var jnkPath = Path.Combine(_dataRoot, jnkName);
        var externalTarget = Path.Combine(_external, "target");
        Directory.CreateDirectory(externalTarget);

        RunCmd($"/c mklink /J \"{jnkPath}\" \"{externalTarget}\"");

        var created = Directory.Exists(jnkPath)
            && (File.GetAttributes(jnkPath) & FileAttributes.ReparsePoint) != 0;
        Assert.True(created, "junction 创建失败，重解析点负控无法执行");

        try
        {
            var zipPath = MakeZip((jnkName + "/tool.txt", "X"));
            var externalBefore = CountEntries(externalTarget);

            var ok = await ConfigManager.ImportConfigAsync(zipPath);

            Assert.False(ok);   // 目标链上有重解析点 → 拒绝
            Assert.Equal(externalBefore, CountEntries(externalTarget));   // 外部零改动
        }
        finally
        {
            RunCmd($"/c rmdir \"{jnkPath}\"");
        }
    }
}
