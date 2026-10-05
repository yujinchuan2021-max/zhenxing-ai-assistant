using System.Diagnostics;
using System.Text.Json;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【隔离测试机制 · 2026-09-23】marker 门禁单测（<see cref="TestIsolationGuard.Evaluate"/> 纯函数面）：
/// 覆盖 Codex 点名负控——marker 缺失（生产语义）/ 损坏 / 未知版本 / 不可读 / 重解析点 / 目录 /
/// 字段缺失 / 路径别名；环境整项丢失 / 数据根不符 / TEMP·TMP·实际临时路径不符；只读纪律与
/// attestation 字段白名单。
/// <para>
/// 全部用例只用临时假根（<see cref="Path.GetTempPath"/> 下 GUID 目录），绝不读改真实用户目录；
/// 不启动 GUI/Launcher——进程级负控由独立探针脚本在候选复制品上执行（见交付报告）。
/// </para>
/// </summary>
public class TestIsolationGuardTests : IDisposable
{
    private readonly string _root;       // 每用例独立假根
    private readonly string _baseDir;    // 模拟 exe 所在目录（marker 所在）
    private readonly string _dataRoot;   // marker 期望数据根（规范形式）
    private readonly string _tempRoot;   // marker 期望 temp 根（规范形式）
    private readonly string _markerPath;

    public TestIsolationGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zxai-tig-" + Guid.NewGuid().ToString("N"));
        _baseDir = Path.Combine(_root, "base");
        _dataRoot = Path.Combine(_root, "data");
        _tempRoot = Path.Combine(_root, "temp");
        Directory.CreateDirectory(_baseDir);
        _markerPath = Path.Combine(_baseDir, TestIsolationGuard.MarkerFileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ── 构造助手 ─────────────────────────────────────────────────────────────

    private static string J(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private string MarkerJson(
        int schema = 1,
        string? dataRoot = null,
        string? tempRoot = null,
        string? runId = "r-0123456789abcdef",
        bool includeSchema = true,
        bool includeDataRoot = true,
        bool includeTempRoot = true,
        bool includeRunId = true,
        string? mainExeSha = null,
        string? extra = null)
    {
        var parts = new List<string>();
        if (includeSchema) parts.Add("\"schemaVersion\": " + schema);
        if (includeDataRoot) parts.Add("\"expectedDataRoot\": " + J(dataRoot ?? _dataRoot));
        if (includeTempRoot) parts.Add("\"expectedTempRoot\": " + J(tempRoot ?? _tempRoot));
        if (includeRunId) parts.Add("\"runId\": " + J(runId ?? "r-0123456789abcdef"));
        if (mainExeSha is not null) parts.Add("\"mainExeSha256\": " + J(mainExeSha));
        if (extra is not null) parts.Add(extra);
        return "{" + string.Join(",", parts) + "}";
    }

    private void WriteMarker(string json) => File.WriteAllText(_markerPath, json);

    private static Func<string, string?> EnvMap(Dictionary<string, string?> map)
        => name => map.TryGetValue(name, out var v) ? v : null;

    private Dictionary<string, string?> HappyEnv() => new()
    {
        ["ZXAI_DATA_ROOT"] = _dataRoot,
        ["TEMP"] = _tempRoot,
        ["TMP"] = _tempRoot,
    };

    private TestIsolationGuard.CheckResult Eval(Func<string, string?> env, Func<string>? tempPath = null)
        => TestIsolationGuard.Evaluate(_baseDir, env, tempPath ?? (() => _tempRoot));

    // ── ① marker 缺失 = 生产语义（唯一允许继续的路径） ─────────────────────────

    [Fact]
    public void MarkerAbsent_ProductionSemantics_NoSideEffects()
    {
        var r = Eval(EnvMap(new Dictionary<string, string?>()));
        Assert.True(r.MarkerAbsent);
        Assert.Equal(0, r.ExitCode);
        Assert.Null(r.Marker);
        // 只读：base 目录除 marker 外零文件（本用例 marker 也不存在）
        Assert.Empty(Directory.GetFileSystemEntries(_baseDir));
    }

    [Fact]
    public void RunStartupGate_NoMarker_InTestProcess_IsNoOp()
    {
        // 测试进程的 BaseDirectory 没有 marker：门禁必须零行为返回（测试进程存活性即断言）。
        TestIsolationGuard.RunStartupGate();
        Assert.True(true);
    }

    // ── ② marker 形态类负控 ──────────────────────────────────────────────────

    [Fact]
    public void MarkerIsDirectory_Rejected58()
    {
        Directory.CreateDirectory(_markerPath);
        var r = Eval(EnvMap(HappyEnv()));
        Assert.False(r.MarkerAbsent);
        Assert.Equal(TestIsolationGuard.ExitMarkerPathType, r.ExitCode);
    }

    [Fact]
    public void MarkerUnreadable_SharingViolation_Rejected50()
    {
        WriteMarker(MarkerJson());
        using (new FileStream(_markerPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = Eval(EnvMap(HappyEnv()));
            Assert.Equal(TestIsolationGuard.ExitMarkerUnreadable, r.ExitCode);
        }
    }

    [Theory]
    [InlineData("")]                          // 空文件
    [InlineData("not-json-at-all")]          // 非 JSON
    [InlineData("[1,2,3]")]                  // 根非对象
    [InlineData("{\"schemaVersion\":1,}")]   // 尾逗号（严格拒绝）
    public void MarkerBadJson_Rejected51(string content)
    {
        WriteMarker(content);
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(TestIsolationGuard.ExitMarkerBadJson, r.ExitCode);
    }

    [Fact]
    public void MarkerDuplicateKey_Rejected51()
    {
        WriteMarker("{\"schemaVersion\":1,\"schemaVersion\":1,\"expectedDataRoot\":" + J(_dataRoot)
            + ",\"expectedTempRoot\":" + J(_tempRoot) + ",\"runId\":\"r-0123456789abcdef\"}");
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(TestIsolationGuard.ExitMarkerBadJson, r.ExitCode);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    [InlineData(99)]
    public void MarkerUnknownVersion_Rejected52(int schema)
    {
        WriteMarker(MarkerJson(schema: schema));
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(TestIsolationGuard.ExitMarkerUnknownVersion, r.ExitCode);
    }

    [Fact]
    public void MarkerSchemaWrongType_Rejected53()
    {
        WriteMarker("{\"schemaVersion\": \"1\", \"expectedDataRoot\":" + J(_dataRoot)
            + ",\"expectedTempRoot\":" + J(_tempRoot) + ",\"runId\":\"r-0123456789abcdef\"}");
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(TestIsolationGuard.ExitMarkerInvalidField, r.ExitCode);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void MarkerMissingFields_Rejected53(bool incSchema, bool incData, bool incTemp, bool incRun)
    {
        if (incSchema && incData && incTemp && incRun) return;
        WriteMarker(MarkerJson(
            includeSchema: incSchema, includeDataRoot: incData,
            includeTempRoot: incTemp, includeRunId: incRun));
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(TestIsolationGuard.ExitMarkerInvalidField, r.ExitCode);
    }

    [Fact]
    public void MarkerUnknownField_Rejected53()
    {
        WriteMarker(MarkerJson(extra: "\"unknownField\": 1"));
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(TestIsolationGuard.ExitMarkerInvalidField, r.ExitCode);
    }

    [Theory]
    [InlineData("ab")]                       // 过短
    [InlineData("has space 1234")]           // 非法字符
    public void MarkerRunIdInvalid_Rejected53(string runId)
    {
        WriteMarker(MarkerJson(runId: runId));
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(TestIsolationGuard.ExitMarkerInvalidField, r.ExitCode);
    }

    [Fact]
    public void MarkerRunIdTooLong_Rejected53()
    {
        WriteMarker(MarkerJson(runId: new string('a', 129)));
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(TestIsolationGuard.ExitMarkerInvalidField, r.ExitCode);
    }

    [Theory]
    [InlineData("xyz")]                      // 非 64 hex
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]  // 64 个非 hex
    public void MarkerMainExeShaInvalid_Rejected53(string sha)
    {
        WriteMarker(MarkerJson(mainExeSha: sha));
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(TestIsolationGuard.ExitMarkerInvalidField, r.ExitCode);
    }

    // ── ③ marker 路径别名类负控（全部只读拒绝，先于任何文件系统写） ────────────

    [Theory]
    [InlineData("relative-root")]            // 相对形式
    [InlineData(@"\\server\share\data")]     // UNC
    [InlineData(@"\\?\D:\data")]             // 设备别名前缀
    public void MarkerPathAliasForm_Rejected53(string raw)
    {
        WriteMarker(MarkerJson(dataRoot: raw));
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(TestIsolationGuard.ExitMarkerInvalidField, r.ExitCode);
    }

    [Fact]
    public void MarkerPathAliasForms_Rejected53()
    {
        // 正斜杠 / ".." 段 / 尾分隔符 / 混合形式 —— 均为别名
        var aliases = new[]
        {
            _dataRoot.Replace('\\', '/'),
            _dataRoot + @"\",
            Path.Combine(_root, "sub", "..", "data"),
            _dataRoot.Replace("\\", @"\\"),   // 双反斜杠中段（非 UNC 头）
        };
        foreach (var alias in aliases)
        {
            WriteMarker(MarkerJson(dataRoot: alias));
            var r = Eval(EnvMap(HappyEnv()));
            Assert.Equal(TestIsolationGuard.ExitMarkerInvalidField, r.ExitCode);
        }
    }

    // ── ④ 环境类负控 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnvDataRootMissing_Rejected54(string? value)
    {
        WriteMarker(MarkerJson());
        var env = HappyEnv();
        env["ZXAI_DATA_ROOT"] = value;
        var r = Eval(EnvMap(env));
        Assert.Equal(TestIsolationGuard.ExitDataRootMissing, r.ExitCode);
    }

    [Fact]
    public void EnvDataRootNotSetAtAll_Rejected54()
    {
        WriteMarker(MarkerJson());
        var env = HappyEnv();
        env.Remove("ZXAI_DATA_ROOT");
        var r = Eval(EnvMap(env));
        Assert.Equal(TestIsolationGuard.ExitDataRootMissing, r.ExitCode);
    }

    [Theory]
    [InlineData(@"D:\somewhere-else", "not-equal")]     // 值不符
    [InlineData("relative", "alias")]                    // 相对 → 55（DataRootMismatch，先于相等比较）
    public void EnvDataRootMismatch_Rejected55(string value, string _)
    {
        WriteMarker(MarkerJson());
        var env = HappyEnv();
        env["ZXAI_DATA_ROOT"] = value;
        var r = Eval(EnvMap(env));
        Assert.Equal(TestIsolationGuard.ExitDataRootMismatch, r.ExitCode);
    }

    [Fact]
    public void EnvDataRootTrailingSeparatorAlias_Rejected55()
    {
        WriteMarker(MarkerJson());
        var env = HappyEnv();
        env["ZXAI_DATA_ROOT"] = _dataRoot + @"\";
        var r = Eval(EnvMap(env));
        Assert.Equal(TestIsolationGuard.ExitDataRootMismatch, r.ExitCode);
    }

    [Fact]
    public void EnvTempMissing_Rejected56()
    {
        WriteMarker(MarkerJson());
        foreach (var which in new[] { "TEMP", "TMP" })
        {
            var env = HappyEnv();
            env.Remove(which);
            var r = Eval(EnvMap(env));
            Assert.Equal(TestIsolationGuard.ExitTempMismatch, r.ExitCode);
        }
    }

    [Fact]
    public void EnvTempMismatch_Rejected56()
    {
        WriteMarker(MarkerJson());
        foreach (var which in new[] { "TEMP", "TMP" })
        {
            var env = HappyEnv();
            env[which] = Path.Combine(_root, "other-temp");
            var r = Eval(EnvMap(env));
            Assert.Equal(TestIsolationGuard.ExitTempMismatch, r.ExitCode);
        }
    }

    [Fact]
    public void ActualTempPathMismatch_Rejected56()
    {
        // TEMP/TMP 两变量都等于 marker，但进程实际临时路径（注入）不同 → 拒绝。
        WriteMarker(MarkerJson());
        var r = Eval(EnvMap(HappyEnv()), () => Path.Combine(_root, "actual-other"));
        Assert.Equal(TestIsolationGuard.ExitTempMismatch, r.ExitCode);
    }

    // ── ⑤ 重解析点负控（junction 仅在临时假根内创建） ────────────────────────

    [SkippableFact]
    public void MarkerIsReparsePoint_Rejected57()
    {
        var targetDir = Path.Combine(_root, "junction-target");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "payload.json"), MarkerJson());
        if (!TryCreateJunction(_markerPath, targetDir))
        {
            Skip.If(true, "无法创建 junction（mklink/New-Item 均被系统拒绝）");
            return;
        }
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(TestIsolationGuard.ExitReparse, r.ExitCode);
    }

    [SkippableFact]
    public void DataRootReparseChain_Rejected57()
    {
        var realTarget = Path.Combine(_root, "real-data");
        var link = Path.Combine(_root, "jn");
        Directory.CreateDirectory(realTarget);
        if (!TryCreateJunction(link, realTarget))
        {
            Skip.If(true, "无法创建 junction（mklink/New-Item 均被系统拒绝）");
            return;
        }
        var aliasedDataRoot = Path.Combine(link, "data");
        WriteMarker(MarkerJson(dataRoot: aliasedDataRoot));
        var env = HappyEnv();
        env["ZXAI_DATA_ROOT"] = aliasedDataRoot;
        var r = Eval(EnvMap(env));
        Assert.Equal(TestIsolationGuard.ExitReparse, r.ExitCode);
    }

    // ── ⑥ 通过路径：只读纪律 + marker 哈希 + attestation 字段白名单 ──────────

    [Fact]
    public void HappyPath_Ok_ReadOnly_And_MarkerHash()
    {
        WriteMarker(MarkerJson());
        var r = Eval(EnvMap(HappyEnv()));
        Assert.False(r.MarkerAbsent);
        Assert.Equal(0, r.ExitCode);
        Assert.NotNull(r.Marker);
        Assert.Equal("r-0123456789abcdef", r.Marker!.RunId);
        Assert.Equal(64, r.Marker.MarkerSha256.Length);
        // 只读纪律：Evaluate 绝不创建 marker 根/Temp 根（创建只在校验通过后的 attestation 写阶段）
        Assert.False(Directory.Exists(_dataRoot));
        Assert.False(Directory.Exists(_tempRoot));
        Assert.Single(Directory.GetFileSystemEntries(_baseDir));   // 仅 marker
    }

    [Fact]
    public void HappyPath_MainExeShaOptional_Present_Ok()
    {
        WriteMarker(MarkerJson(mainExeSha: new string('a', 64)));
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(new string('a', 64), r.Marker!.MainExeSha256);
    }

    [Fact]
    public void Attestation_WrittenAfterValidation_FieldWhitelist()
    {
        WriteMarker(MarkerJson());
        var r = Eval(EnvMap(HappyEnv()));
        Assert.Equal(0, r.ExitCode);

        var path = TestIsolationGuard.WriteAttestation(r.Marker!);
        Assert.True(File.Exists(path));
        Assert.Equal(Path.Combine(_dataRoot, ".zxai-attestation.json"), path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var allowed = new HashSet<string> { "schemaVersion", "runId", "pid", "startedAtUtc", "imagePath", "baseDirectory", "dataRoot", "tempRoot", "markerPath", "markerSha256" };
        var fields = root.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.True(fields.SetEquals(allowed), "attestation 字段必须恰好为白名单（不写全环境/密钥）");

        Assert.Equal("r-0123456789abcdef", root.GetProperty("runId").GetString());
        Assert.Equal(Environment.ProcessId, root.GetProperty("pid").GetInt32());
        Assert.Equal(_dataRoot, root.GetProperty("dataRoot").GetString());
        Assert.Equal(_tempRoot, root.GetProperty("tempRoot").GetString());
        Assert.Equal(r.Marker!.MarkerSha256, root.GetProperty("markerSha256").GetString());
    }

    // ── junction 助手（镜像 DataRootsIsolationTests 模式；失败由调用方 Skip） ──

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
            if (proc is { ExitCode: 0 }) return true;
        }
        catch { }

        try
        {
            using var ps = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"New-Item -ItemType Junction -Path '{linkPath}' -Target '{targetPath}' | Out-Null\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            ps?.WaitForExit(20_000);
            return ps is { ExitCode: 0 } && Directory.Exists(linkPath)
                && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }
}
