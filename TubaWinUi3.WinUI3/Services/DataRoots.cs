using System.Runtime.CompilerServices;

namespace TubaWinUi3.Services;

/// <summary>
/// 【GUI 隔离】开发预览 / 测试模式的数据根。
/// <para>
/// <b>未设置</b>（或全空白）环境变量 <c>ZXAI_DATA_ROOT</c> → 正常生产模式，所有路径行为与正式产品完全一致。
/// </para>
/// <para>
/// 设置为<b>绝对路径</b>且该路径可创建可写 → 进入"开发预览/测试模式"：所有持久化数据
/// （配置 / 技能 / MCP 队列 / WebView2 缓存 / 日志 / 使用统计 / 软件登记表 / 学习进度）
/// 统一落在该根目录下：
///  · <b>不读取、不迁移</b>真实 %LOCALAPPDATA%\TubaWinUi3 的任何文件（含 API Key 配置）——
///    旧数据迁移被显式禁用，避免空隔离目录被"自动补齐"成真实数据副本；
///  · App 启动时<b>不启动</b>与 UI 验收无关的自动服务：MCP 确认监听（避免与用户实例争用真实队列）、
///    游戏覆盖层轮询、静默更新检查；
///  · 仅影响本次进程。
/// </para>
/// <para>
/// 【fail-fast】设置了 ZXAI_DATA_ROOT 但值<b>无效</b>（相对路径 / 指向或包含真实数据目录 /
/// <b>抛异常终止启动</b>（见 <see cref="ValidateTestRoot"/>），绝不静默回退真实数据目录：
/// 静默回退会让"隔离实例"直接读写用户的真实配置与 API Key，是最危险的失败模式。
/// </para>
/// </summary>
public static class DataRoots
{
    private const string EnvVarName = "ZXAI_DATA_ROOT";

    /// <summary>进程启动时解析一次；无效值在这里抛异常（类型初始化异常 → 终止启动）。</summary>
    private static readonly string? _testRoot = ValidateTestRoot(Environment.GetEnvironmentVariable(EnvVarName));

    /// <summary>是否处于开发预览/测试模式。</summary>
    public static bool IsTestMode => _testRoot is not null;

    /// <summary>测试根（未设置时为 null；设置了无效值则进程已启动失败）。</summary>
    public static string? TestRoot => _testRoot;

    /// <summary>真实（正式产品）数据目录——测试模式下不得使用。</summary>
    public static string RealDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TubaWinUi3");

    /// <summary>测试模式下必须跳过旧数据迁移（防止真实 Key 配置被复制进隔离目录）。</summary>
    public static bool ShouldSkipLegacyMigration => IsTestMode;

    /// <summary>
    /// 【测试钩子】覆盖隔离根解析（仅测试程序集使用；null = 使用进程启动时解析出的 <see cref="TestRoot"/>）。
    /// 生产代码路径永远不写它；internal 以防止外部程序集误用。
    /// </summary>
    internal static string? TestRootOverrideForTest { get; set; }

    /// <summary>当前生效的隔离根（含测试钩子；生产路径下恒等于 <see cref="TestRoot"/>）。</summary>
    internal static string? EffectiveTestRoot => TestRootOverrideForTest ?? _testRoot;

    /// <summary>
    /// 【GUI 隔离 · 可测纯函数】"应用数据基目录"的唯一计算入口：
    /// 隔离模式 = 隔离根（整体替换 %LocalAppData%\TubaWinUi3 段）；否则 = {localAppDataRoot}\TubaWinUi3。
    /// 所有访问"应用数据区"的解析器（PathResolver / ToolCatalog / ToolMetadataService /
    /// ToolsBundleService 等）必须经此函数——运行时隔离才是应用级约束，而非仅启动器约束。
    /// </summary>
    internal static string PickBaseDir(string? testRoot, string localAppDataRoot)
        => testRoot ?? Path.Combine(localAppDataRoot, "TubaWinUi3");

    /// <summary>
    /// 【GUI 隔离 · 可测纯函数】"可写 Tools 根"的唯一计算入口：
    /// 隔离模式 = ZXAI_DATA_ROOT\Tools（随包 Tools 恒为只读展示/启动来源，导入/安装/更新/清理
    /// 一律写这里）；否则 = 正常 Tools 根（生产语义不变）。
    /// </summary>
    internal static string PickWritableToolsRoot(string? testRoot, string normalToolsRoot)
        => testRoot is null ? normalToolsRoot : Path.Combine(testRoot, "Tools");

    /// <summary>
    /// 【GUI 隔离 · 可测纯函数】校验 ZXAI_DATA_ROOT（只看入参 + 文件系统，不读环境变量，便于单测）。
    /// 校验顺序 = 先验证后写：所有字符串级拒绝均发生在任何文件系统操作之前。
    ///  · null / 空 / 全空白 → 返回 null（= 正常生产模式）；
    ///  · 非绝对路径（相对路径、盘符相对）→ 抛 <see cref="InvalidOperationException"/>；
    ///  · 与真实数据目录（%LocalAppData%\TubaWinUi3）重叠或互为包含（本身 / 其下 / 其父链）→ 抛；纯字符串比较，不触碰该路径；
    ///  · 路径链上任一已存在组件是 junction/symlink（重解析点）→ 抛；
    ///  · 无法创建目录 / 目录不可写（实测写一个探针文件）→ 抛。
    /// 异常消息统一以「ZXAI_DATA_ROOT 无效」开头并写明具体原因；调用方不得吞掉该异常回退真实目录。
    /// </summary>
    internal static string? ValidateTestRoot(string? raw)
        => ValidateTestRootCore(raw, [RealDataDir]);

    /// <summary>校验核心（禁止目录表可注入，供纯测试根负控使用；禁止目录仅作字符串比较，绝不触碰）。</summary>
    internal static string? ValidateTestRootCore(string? raw, IReadOnlyList<string> prohibitedRoots)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
            throw new InvalidOperationException(
                MiscTexts.TSub($"ZXAI_DATA_ROOT 无效：「{trimmed}」不是绝对路径。隔离模式要求绝对路径") +
                MiscTexts.TSub(@"（例如 D:\zxai-test-root），以免误读写真实数据目录。请修正或删除该环境变量。"));

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(MiscTexts.TSub($"ZXAI_DATA_ROOT 无效：「{trimmed}」不是合法路径：{ex.Message}"), ex);
        }

        // ① 字符串级拒绝：真实数据目录（或与其互为包含）——先于一切文件系统操作，绝不触碰该路径。
        foreach (var prohibited in prohibitedRoots)
        {
            if (string.IsNullOrWhiteSpace(prohibited)) continue;
            if (!Path.IsPathFullyQualified(prohibited)) continue;
            string p;
            try { p = Path.GetFullPath(prohibited); } catch { continue; }
            if (IsSameOrUnder(full, p) || IsSameOrUnder(p, full))
                throw new InvalidOperationException(
                    MiscTexts.TSub($"ZXAI_DATA_ROOT 无效：「{full}」与真实数据目录（{p}）重叠或互为包含。") +
                    MiscTexts.T("隔离根不得指向真实数据目录本身、其下或将其包含在内——真实用户数据绝不能被隔离实例使用。") +
                    MiscTexts.TSub(@"请改用独立目录（例如 D:\zxai-test-root）。"));
        }

        // ② 路径链上的重解析点（junction / symlink）→ 拒绝（先于创建/写入）。
        foreach (var component in EnumeratePathComponents(full))
        {
            FileAttributes attrs;
            try { attrs = File.GetAttributes(component); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    MiscTexts.TSub($"ZXAI_DATA_ROOT 无效：「{full}」路径链组件「{component}」无法读取属性：{ex.Message}"), ex);
            }
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    MiscTexts.TSub($"ZXAI_DATA_ROOT 无效：「{full}」路径链上存在 junction/symlink（{component}）。") +
                    MiscTexts.T("隔离根及其父链不得经过重解析点，以免数据被改写到他处。"));
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                MiscTexts.TSub($"ZXAI_DATA_ROOT 无效：「{full}」无法创建目录：{ex.Message}。请修正或删除该环境变量。"), ex);
        }

        try
        {
            // 探针写入：能创建目录 ≠ 能写文件（只读目录 / 权限不足 / 网络路径掉线都会在这拦下），
            // 宁可启动失败，也不能等运行到一半才发现隔离根不可写而回落到真实目录。
            var probe = Path.Combine(full, $".zxai-write-probe-{Guid.NewGuid():N}.tmp");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.WriteByte(0x5A);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                MiscTexts.TSub($"ZXAI_DATA_ROOT 无效：「{full}」不可写：{ex.Message}。请修正或删除该环境变量。"), ex);
        }

        return full;
    }

    /// <summary>
    /// 【GUI 隔离 · 物理边界（复核退回：Tools 子树 junction 越界）】隔离态：校验目标目录的物理链——
    /// 从隔离根到目标之间（含两者）每一段**已存在**组件都不得是 junction / 符号链接（重解析点）。
    /// 任何一段已存在组件无法读取属性（无法可靠验证）→ false（调用方必须关闭该操作，fail-closed）。
    /// 目标在隔离根之外（不属隔离映射面；补丁 B 既有设计保持调用方语义）或生产态（无隔离根）→ true（零额外行为）。
    /// 调用纪律：在**每个即将触及的目标目录**的操作执行前逐次调用（恢复入队 / 下载建目录与写入 / 清理 / 删除）——
    /// 只在启动时校验隔离根、或只在恢复时校验一次都不够：路径可能在恢复之后被换成 junction。
    /// </summary>
    internal static bool IsIsolationTargetPhysicallySafe(string? targetDir, out string reason)
    {
        reason = "";
        if (EffectiveTestRoot is null) return true;            // 生产态：语义与修复前完全一致
        if (string.IsNullOrWhiteSpace(targetDir)) { reason = "empty-target"; return false; }

        string rootFull, targetFull;
        try
        {
            rootFull = Path.GetFullPath(EffectiveTestRoot);
            targetFull = Path.GetFullPath(targetDir);
        }
        catch { reason = "unresolvable-path"; return false; }

        // 树外目标：不属隔离映射面（调用方语义，见补丁 B 设计），本检查不改变其行为。
        if (!IsSameOrUnder(targetFull, rootFull)) return true;

        // 逐段检查“已存在组件”：任何重解析点 → 拒绝；已存在但属性不可读 → fail-closed。
        // 某段不存在 → 其后更深段也不存在（无物可链接），到此为止视为通过。
        foreach (var component in EnumeratePathComponents(targetFull))
        {
            if (!IsSameOrUnder(component, rootFull)) continue;   // 隔离根之上由启动校验（ValidateTestRoot）负责
            FileAttributes attrs;
            try { attrs = File.GetAttributes(component); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            catch (Exception ex) { reason = $"component-unreadable:{component}:{ex.GetType().Name}"; return false; }
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                reason = $"reparse-component:{component}";
                return false;
            }
        }

        return true;
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return c.Equals(r, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumeratePathComponents(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) yield break;
        yield return root;
        var rest = fullPath.Substring(root.Length).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (rest.Length == 0) yield break;
        var acc = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var seg in rest.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (seg.Length == 0) continue;
            acc = acc + Path.DirectorySeparatorChar + seg;
            yield return acc;
        }
    }
}

/// <summary>
/// 【GUI 隔离 · fail-fast】模块初始化守卫：在 Main 之前 ① 校验测试隔离 marker（<see cref="TestIsolationGuard"/>，
/// 只读；失败=专用退出码终止）② 强制解析一次 <see cref="DataRoots.TestRoot"/>。
/// <para>
/// 为什么需要：启动路径上大量 try/catch（AppSettings.Load / UsageStats.Load / AgentDebugLog 等都会
/// 吞掉异常）会把类型初始化异常静默吞掉，进程会带着"半个隔离实例"继续跑；模块初始化异常无法被
/// 应用内任何 try/catch 捕获，因此无效的 ZXAI_DATA_ROOT 必然在启动阶段终止进程，
/// 而不是等到某个调用方把异常吞了、再回落到真实 %LOCALAPPDATA%\TubaWinUi3。
/// </para>
/// <para>
/// 【隔离测试机制 · 2026-09-23】marker 校验必须先于 DataRoots.TestRoot 的任何建目录/写探针执行：
/// marker 存在时数据根/Temp 必须与 marker 精确相等（整项丢失=安全退出而非回生产模式），
/// 且全部校验只读完成后才允许创建/写（attestation 与后续探针）。marker 明确不存在 → 生产语义不变。
/// </para>
/// </summary>
internal static class DataRootsStartupGuard
{
    [ModuleInitializer]
    internal static void Init()
    {
        // ① 测试隔离 marker 门禁（无 marker 时为零行为；校验失败以专用退出码终止，绝不继续）。
        TestIsolationGuard.RunStartupGate();

        // ② fail-fast：无效的 ZXAI_DATA_ROOT 必须在启动阶段终止（细节见类型注释）。
        try
        {
            _ = DataRoots.TestRoot;
        }
        catch (Exception ex)
        {
            // GUI 进程无控制台，但重定向/调试器下可直接看到原因；随后 rethrow 终止启动。
            try { Console.Error.WriteLine(MiscTexts.TSub($"[ZXAI] 启动终止：{ex.GetBaseException().Message}")); } catch { }
            throw;
        }
    }
}
