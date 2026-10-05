using System.Text;
using System.Text.Json;

namespace TubaWinUi3.Services;

/// <summary>
/// 【隔离测试机制 · 2026-09-23】测试专用固定 marker 门禁（可在候选复制品内启用；正式发行包不得携带 marker，
/// 官方候选校验器主动拒绝 marker 随包）。
/// <para>
/// marker 文件：<c>&lt;AppContext.BaseDirectory&gt;\.zxai-test-isolation.json</c>（非打包主程序
/// <c>src\TubaWinUi3.exe</c> 旁）。语义：
///  · <b>明确不存在</b> → 生产语义原样继续（唯一允许继续的"无 marker"路径；
///    "存在但不可读/含糊"一律 fail-closed，绝不当成无 marker）；
///  · <b>存在</b> → 必须通过全部校验：schemaVersion 匹配、expectedDataRoot/expectedTempRoot 为已规范化
///    本地绝对路径（拒绝别名形式）、原始 ZXAI_DATA_ROOT / TEMP / TMP / Path.GetTempPath() 与 marker
///    精确相等、根与 Temp 父链无重解析点。全部校验<b>只读</b>完成后才允许创建/写 attestation。
/// </para>
/// <para>
/// 失败 = 专用非零退出码（50-59）+ 不含配置内容的 stderr + <see cref="Environment.Exit"/>：
/// 不抛异常（不制造崩溃转储、不按预期路径写真实 TEMP），绝不回落生产模式。
/// 本机制是<b>应用层</b>测试约束，不构成 Windows/CLR 全进程零写盘沙箱。
/// </para>
/// </summary>
internal static class TestIsolationGuard
{
    internal const string MarkerFileName = ".zxai-test-isolation.json";
    internal const int SchemaVersion = 1;

    // 专用非零退出码（50-59；与 0xC000027B 崩溃码、30/40/41 自检脚本码不冲突）
    internal const int ExitMarkerUnreadable = 50;      // 存在但属性/内容读取失败（含共享冲突、权限拒绝）
    internal const int ExitMarkerBadJson = 51;         // 无法解析 / 根非对象 / 重复键
    internal const int ExitMarkerUnknownVersion = 52;  // schemaVersion 非本机制版本
    internal const int ExitMarkerInvalidField = 53;    // 缺字段 / 类型错 / 未知字段 / 路径别名 / runId 非法
    internal const int ExitDataRootMissing = 54;       // ZXAI_DATA_ROOT 整项丢失（marker 模式禁止回生产模式）
    internal const int ExitDataRootMismatch = 55;      // 数据根非空但 ≠ marker / 非完全限定 / 别名形式
    internal const int ExitTempMismatch = 56;          // TEMP/TMP/实际临时路径任一缺失或不符
    internal const int ExitReparse = 57;               // marker 自身或 数据根/Temp 父链存在重解析点
    internal const int ExitMarkerPathType = 58;        // marker 路径是目录等错误类型
    internal const int ExitGuardIoError = 59;          // 守卫自身意外错误（保守终止）

    internal sealed class MarkerInfo
    {
        public required string MarkerPath { get; init; }
        public required string MarkerSha256 { get; init; }
        public required string ExpectedDataRoot { get; init; }
        public required string ExpectedTempRoot { get; init; }
        public required string RunId { get; init; }
        public string? MainExeSha256 { get; init; }
    }

    internal sealed class CheckResult
    {
        /// <summary>true = marker 明确不存在 → 维持生产语义。</summary>
        public bool MarkerAbsent { get; init; }
        /// <summary>0 = 通过（Absent 或校验全部通过）；否则为专用退出码。</summary>
        public int ExitCode { get; init; }
        /// <summary>失败原因标签（只含字段名/序号，不含任何路径或配置值）。</summary>
        public string Reason { get; init; } = "ok";
        public MarkerInfo? Marker { get; init; }
    }

    /// <summary>
    /// 【可测纯逻辑】只读校验全部完成；本函数绝不创建目录、绝不写文件。
    /// 注入 <paramref name="getEnv"/> / <paramref name="getTempPath"/> 以便负控单测。
    /// </summary>
    internal static CheckResult Evaluate(
        string baseDirectory,
        Func<string, string?> getEnv,
        Func<string> getTempPath)
    {
        var markerPath = Path.Combine(baseDirectory, MarkerFileName);

        // ① 建立存在性：只有"明确不存在"才维持生产语义；其余含糊/错误一律 fail-closed。
        //    注意：属性查询在某些遮蔽场景（独占共享锁等）下可能失败——失败时用目录枚举复核：
        //    枚举看到名字 = 存在但被遮蔽 → fail-closed；枚举也看不到 = 明确不存在 → 生产语义。
        FileAttributes attrs;
        try { attrs = File.GetAttributes(markerPath); }
        catch (FileNotFoundException) { return AbsentOrOccluded(baseDirectory, "marker-attrs-notfound"); }
        catch (DirectoryNotFoundException) { return AbsentOrOccluded(baseDirectory, "marker-attrs-dirnotfound"); }
        catch (Exception ex) { return Fail(ExitMarkerUnreadable, "marker-attrs:" + ex.GetType().Name); }

        if ((attrs & FileAttributes.ReparsePoint) != 0) return Fail(ExitReparse, "marker-is-reparse-point");
        if ((attrs & FileAttributes.Directory) != 0) return Fail(ExitMarkerPathType, "marker-is-directory");

        // ② 读取（纯只读）。
        byte[] bytes;
        try { bytes = File.ReadAllBytes(markerPath); }
        catch (FileNotFoundException) { return AbsentOrOccluded(baseDirectory, "marker-read-notfound"); }
        catch (DirectoryNotFoundException) { return AbsentOrOccluded(baseDirectory, "marker-read-dirnotfound"); }
        catch (Exception ex) { return Fail(ExitMarkerUnreadable, "marker-read:" + ex.GetType().Name); }

        string markerSha;
        try
        {
            markerSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        }
        catch (Exception ex) { return Fail(ExitMarkerUnreadable, "marker-hash:" + ex.GetType().Name); }

        // ③ 严格解析与字段校验。
        if (!TryParseMarker(bytes, markerPath, markerSha, out var marker, out var parseExit, out var parseReason))
            return Fail(parseExit, parseReason);
        marker = marker!;

        // ④ 原始环境根校验（marker 模式：整项丢失必须安全退出，不得回生产模式）。
        //    "精确相等"按**原始**字符串判定——不做任何修剪，带空白/别名形式一律拒绝。
        var rawData = getEnv("ZXAI_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(rawData)) return Fail(ExitDataRootMissing, "zxai-data-root-missing");
        if (!TryCanonicalLocalPath(rawData, out var dAlias)) return Fail(ExitDataRootMismatch, "zxai-data-root-" + dAlias);
        if (!string.Equals(rawData, marker.ExpectedDataRoot, StringComparison.OrdinalIgnoreCase))
            return Fail(ExitDataRootMismatch, "zxai-data-root-not-equal-marker");

        // ⑤ TEMP / TMP / 实际临时路径三方一致（同样按原始字符串）。
        var tempV = getEnv("TEMP");
        if (string.IsNullOrWhiteSpace(tempV)) return Fail(ExitTempMismatch, "temp-env-missing");
        var tmpV = getEnv("TMP");
        if (string.IsNullOrWhiteSpace(tmpV)) return Fail(ExitTempMismatch, "tmp-env-missing");
        if (!TryCanonicalLocalPath(tempV, out var tAlias)) return Fail(ExitTempMismatch, "temp-env-" + tAlias);
        if (!TryCanonicalLocalPath(tmpV, out var mAlias)) return Fail(ExitTempMismatch, "tmp-env-" + mAlias);
        if (!string.Equals(tempV, marker.ExpectedTempRoot, StringComparison.OrdinalIgnoreCase))
            return Fail(ExitTempMismatch, "temp-env-not-equal-marker");
        if (!string.Equals(tmpV, marker.ExpectedTempRoot, StringComparison.OrdinalIgnoreCase))
            return Fail(ExitTempMismatch, "tmp-env-not-equal-marker");

        string actualTemp;
        try { actualTemp = getTempPath() ?? ""; }
        catch (Exception ex) { return Fail(ExitTempMismatch, "temp-path:" + ex.GetType().Name); }
        actualTemp = actualTemp.TrimEnd('\\', '/');
        if (!string.Equals(actualTemp, marker.ExpectedTempRoot, StringComparison.OrdinalIgnoreCase))
            return Fail(ExitTempMismatch, "temp-path-not-equal-marker");

        // ⑥ 数据根与 Temp 父链重解析点检查（先于任何创建/写；已存在组件逐段检查，读不到 → fail-closed）。
        if (!CheckNoReparseComponents(marker.ExpectedDataRoot, out var bad1)) return Fail(ExitReparse, "data-root-" + bad1);
        if (!CheckNoReparseComponents(marker.ExpectedTempRoot, out var bad2)) return Fail(ExitReparse, "temp-root-" + bad2);

        return new CheckResult { MarkerAbsent = false, ExitCode = 0, Reason = "ok", Marker = marker };
    }

    /// <summary>
    /// 应用启动门禁（由 <c>DataRootsStartupGuard.Init</c> 在模块初始化时最先调用）：
    /// marker 不存在 → 直接返回；校验失败 → stderr + <see cref="Environment.Exit"/>；通过 → 写 attestation。
    /// </summary>
    internal static void RunStartupGate()
    {
        CheckResult result;
        try
        {
            result = Evaluate(AppContext.BaseDirectory, Environment.GetEnvironmentVariable, Path.GetTempPath);
        }
        catch (Exception ex)
        {
            // 守卫自身意外异常：无法证明处于安全状态 → 保守终止（专用码，不抛异常）。
            SafeStderr($"[ZXAI-TEST-ISOLATION] guard-error {ex.GetType().Name}; exit {ExitGuardIoError}");
            Environment.Exit(ExitGuardIoError);
            return;
        }

        if (result.MarkerAbsent) return;

        if (result.ExitCode != 0)
        {
            // 专用非零退出码 + 不含配置内容的 stderr；不抛异常（不制造崩溃转储、不写真实 TEMP）。
            SafeStderr($"[ZXAI-TEST-ISOLATION] rejected reason={result.Reason}; exit {result.ExitCode}");
            Environment.Exit(result.ExitCode);
            return;
        }

        // 全部校验通过（且此前未发生任何创建/写）：写主 exe 自身 attestation。
        try
        {
            WriteAttestation(result.Marker!);
        }
        catch (Exception ex)
        {
            SafeStderr($"[ZXAI-TEST-ISOLATION] attestation-failed {ex.GetType().Name}; exit {ExitGuardIoError}");
            Environment.Exit(ExitGuardIoError);
        }
    }

    /// <summary>
    /// 在已验证隔离根写"主 exe 自身"attestation（runId / PID / 进程创建时间 / 映像绝对路径 / BaseDirectory /
    /// 实际数据根与 Temp / marker 哈希）。只写下列字段——不写全环境、不写任何密钥。
    /// </summary>
    internal static string WriteAttestation(MarkerInfo marker)
    {
        var dataRoot = marker.ExpectedDataRoot;
        Directory.CreateDirectory(dataRoot);

        string startedAt;
        try { startedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o"); }
        catch { startedAt = DateTime.UtcNow.ToString("o"); }

        var att = new Dictionary<string, object?>
        {
            ["schemaVersion"] = SchemaVersion,
            ["runId"] = marker.RunId,
            ["pid"] = Environment.ProcessId,
            ["startedAtUtc"] = startedAt,
            ["imagePath"] = Environment.ProcessPath ?? "",
            ["baseDirectory"] = AppContext.BaseDirectory,
            ["dataRoot"] = dataRoot,
            ["tempRoot"] = marker.ExpectedTempRoot,
            ["markerPath"] = marker.MarkerPath,
            ["markerSha256"] = marker.MarkerSha256,
        };
        var json = JsonSerializer.Serialize(att, new JsonSerializerOptions { WriteIndented = true });

        var target = Path.Combine(dataRoot, ".zxai-attestation.json");
        var tmp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, target, overwrite: true);
        return target;
    }

    // ── 内部实现 ──────────────────────────────────────────────────────────────

    private static void SafeStderr(string line)
    {
        try { Console.Error.WriteLine(line); } catch { }
    }

    private static CheckResult Absent() => new() { MarkerAbsent = true, ExitCode = 0, Reason = "no-marker" };

    private static CheckResult Fail(int exit, string reason) => new() { MarkerAbsent = false, ExitCode = exit, Reason = reason };

    /// <summary>
    /// 属性/读取报告"不存在"时的复核：用目录枚举（FindFirstFile，不经文件句柄，不受共享锁影响）再确认一次——
    /// 枚举看不到名字 = 明确不存在（返回 Absent，生产语义）；枚举能看到 = 存在但被遮蔽（fail-closed）；
    /// 枚举本身失败 = 无法确认（fail-closed）。
    /// 这就是"不得用 File.Exists 的 false 把不可读当无 marker"的落实方式。
    /// </summary>
    private static CheckResult AbsentOrOccluded(string baseDirectory, string label)
    {
        bool visible;
        try
        {
            visible = false;
            foreach (var _ in new DirectoryInfo(baseDirectory).EnumerateFiles(MarkerFileName))
            {
                visible = true;
                break;
            }
        }
        catch (Exception ex) { return Fail(ExitMarkerUnreadable, label + "-enum:" + ex.GetType().Name); }

        return visible ? Fail(ExitMarkerUnreadable, label + "-occluded") : Absent();
    }

    private static bool TryParseMarker(byte[] bytes, string markerPath, string markerSha,
        out MarkerInfo? info, out int exit, out string reason)
    {
        info = null; exit = 0; reason = "ok";

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch { exit = ExitMarkerBadJson; reason = "marker-json-parse"; return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { exit = ExitMarkerBadJson; reason = "marker-root-not-object"; return false; }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
            {
                if (!seen.Add(prop.Name)) { exit = ExitMarkerBadJson; reason = "marker-duplicate-key"; return false; }
                if (prop.Name is not ("schemaVersion" or "expectedDataRoot" or "expectedTempRoot" or "runId" or "mainExeSha256"))
                { exit = ExitMarkerInvalidField; reason = "marker-unknown-field"; return false; }
            }

            if (!root.TryGetProperty("schemaVersion", out var sv)
                || sv.ValueKind != JsonValueKind.Number
                || !sv.TryGetInt32(out var schema))
            { exit = ExitMarkerInvalidField; reason = "marker-schemaVersion"; return false; }
            if (schema != SchemaVersion) { exit = ExitMarkerUnknownVersion; reason = "marker-schemaVersion-unsupported"; return false; }

            if (!TryGetStringField(root, "expectedDataRoot", out var dr, out var drReason)) { exit = ExitMarkerInvalidField; reason = drReason; return false; }
            if (!TryCanonicalLocalPath(dr, out var drAlias)) { exit = ExitMarkerInvalidField; reason = "marker-expectedDataRoot-" + drAlias; return false; }
            if (!TryGetStringField(root, "expectedTempRoot", out var tr, out var trReason)) { exit = ExitMarkerInvalidField; reason = trReason; return false; }
            if (!TryCanonicalLocalPath(tr, out var trAlias)) { exit = ExitMarkerInvalidField; reason = "marker-expectedTempRoot-" + trAlias; return false; }

            if (!TryGetStringField(root, "runId", out var runId, out var runReason)) { exit = ExitMarkerInvalidField; reason = runReason; return false; }
            if (!IsValidRunId(runId)) { exit = ExitMarkerInvalidField; reason = "marker-runId"; return false; }

            string? exeSha = null;
            if (root.TryGetProperty("mainExeSha256", out var ms))
            {
                if (ms.ValueKind != JsonValueKind.String) { exit = ExitMarkerInvalidField; reason = "marker-mainExeSha256"; return false; }
                exeSha = ms.GetString();
                if (exeSha is null || exeSha.Length != 64 || !exeSha.All(Uri.IsHexDigit))
                { exit = ExitMarkerInvalidField; reason = "marker-mainExeSha256"; return false; }
            }

            info = new MarkerInfo
            {
                MarkerPath = markerPath,
                MarkerSha256 = markerSha,
                ExpectedDataRoot = dr,
                ExpectedTempRoot = tr,
                RunId = runId,
                MainExeSha256 = exeSha,
            };
            return true;
        }
    }

    private static bool TryGetStringField(JsonElement root, string name, out string value, out string reason)
    {
        value = ""; reason = "marker-" + name;
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return false;
        var s = el.GetString();
        if (string.IsNullOrEmpty(s)) return false;
        value = s;
        reason = "ok";
        return true;
    }

    private static bool IsValidRunId(string runId)
        => runId.Length is >= 8 and <= 128
           && runId.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-' || c == '_');

    /// <summary>
    /// 路径必须已是"唯一规范形式"的本地绝对路径——路径别名（相对形式、正斜杠、`.`/`..` 段、`\\?\`/UNC、
    /// 尾部分隔符、大小写/规范不一致形式）一律拒绝。纯字符串 + 只读，不触碰文件系统。
    /// </summary>
    private static bool TryCanonicalLocalPath(string value, out string reason)
    {
        reason = "ok";
        if (string.IsNullOrEmpty(value)) { reason = "empty"; return false; }
        if (value.Length > 512) { reason = "too-long"; return false; }
        if (value.Contains('"') || value.Contains('\0')) { reason = "invalid-char"; return false; }
        if (!Path.IsPathFullyQualified(value)) { reason = "not-fully-qualified"; return false; }
        if (value.StartsWith(@"\\") || value.StartsWith(@"//")) { reason = "unc-or-device-alias"; return false; }
        if (!(value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/')))
        { reason = "no-drive-root"; return false; }

        string full;
        try { full = Path.GetFullPath(value); }
        catch { reason = "unresolvable"; return false; }
        if (!string.Equals(full, value, StringComparison.OrdinalIgnoreCase)) { reason = "alias-form"; return false; }
        if (value.Length > 3 && (value[^1] == '\\' || value[^1] == '/')) { reason = "trailing-separator"; return false; }
        return true;
    }

    /// <summary>
    /// 逐段检查路径链上"已存在组件"是否重解析点（junction/symlink）——先于任何创建/写执行。
    /// 某段不存在 → 其后更深段必不存在，视为通过（与 DataRoots 启动校验同一语义）。
    /// 已存在但属性读不到 → 无法可靠验证 → fail-closed。
    /// </summary>
    private static bool CheckNoReparseComponents(string fullPath, out string detail)
    {
        detail = "ok";
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) { detail = "no-root"; return false; }

        var acc = root.TrimEnd('\\', '/');
        var rest = fullPath.Substring(root.Length).Trim('\\', '/');
        var idx = 0;
        foreach (var seg in rest.Split('\\', '/'))
        {
            if (seg.Length == 0) continue;
            idx++;
            acc = acc + "\\" + seg;
            FileAttributes a;
            try { a = File.GetAttributes(acc); }
            catch (FileNotFoundException) { return true; }        // 不存在 → 更深处也不存在
            catch (DirectoryNotFoundException) { return true; }
            catch { detail = "unreadable-#" + idx; return false; }  // fail-closed
            if ((a & FileAttributes.ReparsePoint) != 0) { detail = "reparse-#" + idx; return false; }
        }
        return true;
    }
}
