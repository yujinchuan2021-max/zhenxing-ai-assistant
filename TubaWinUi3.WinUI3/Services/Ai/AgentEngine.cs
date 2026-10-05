using TubaWinUi3.Services.Agent;
using TubaWinUi3.Services.Ai.Dsh;

namespace TubaWinUi3.Services.Ai;

/// <summary>
/// 【ZXAI】Agent 引擎选择（换核心 M2）：读 engine.json（%LOCALAPPDATA%\TubaWinUi3\engine.json），
/// "dsh" = DeepSeek Harness（默认方向）；"builtin" = 自研 AgentRuntime。
/// dsh 不可用（未安装）时自动回退 builtin——切换零风险。
/// </summary>
public static class AgentEngine
{
    private static string? _cached;

    /// <summary>当前引擎："dsh" 或 "builtin"。</summary>
    public static string Current
    {
        get
        {
            if (_cached is not null) return _cached;
            _cached = ReadEngineSetting();
            return _cached;
        }
    }

    public static void SetCurrent(string engine)
    {
        engine = engine is "builtin" ? "builtin" : "dsh";
        _cached = engine;
        try
        {
            // 【A13 审计修复】统一走 ConfigManager（支持 AppRoot/自定义数据目录）
            var dir = ConfigManager.GetDataDir();
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "engine.json"),
                $"{{\"engine\": \"{engine}\"}}");
        }
        catch { /* 设置写入失败不阻断（内存生效） */ }
    }

    private static string ReadEngineSetting()
    {
        try
        {
            // 【A13】统一走 ConfigManager；旧版写在 AppData 的配置做一次性兼容迁移
            ConfigManager.MigrateLegacyFileIfMissing("engine.json");
            var path = Path.Combine(ConfigManager.GetDataDir(), "engine.json");
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("engine", out var e) &&
                    e.GetString() is { } s)
                    return s == "builtin" ? "builtin" : "dsh";
            }
        }
        catch { }
        return "dsh"; // 默认换核心方向
    }

    // ---------- dsh 可用性：缓存探测（【评审修复】不在 UI 状态更新里启动阻塞进程） ----------
    private static int _dshState;      // 0=unknown, 1=可用, 2=不可用
    private static int _probeStarted;

    /// <summary>
    /// 【评审修复】非阻塞查询：仅读缓存。未探测时返回 false（UI 显示中性）并触发一次后台探测。
    /// UI 状态更新/权限开关刷新专用——绝不在此路径启动子进程。
    /// </summary>
    public static bool IsDshAvailableCached()
    {
        var s = Volatile.Read(ref _dshState);
        if (s != 0) return s == 1;
        PrewarmDshProbe();
        return false;
    }

    /// <summary>【评审修复】后台预热探测（页面初始化调用一次；结果写入缓存）。</summary>
    public static void PrewarmDshProbe()
    {
        if (Interlocked.Exchange(ref _probeStarted, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try { Volatile.Write(ref _dshState, ProbeDshOnce() ? 1 : 2); }
            catch { Volatile.Write(ref _dshState, 2); }
        });
    }

    /// <summary>同步探测（仅工厂 CreateSession 使用——用户动作路径，非 UI 热路径）。结果写缓存。</summary>
    public static bool IsDshAvailable()
    {
        var s = Volatile.Read(ref _dshState);
        if (s == 1) return true;
        if (s == 2) return false;
        var ok = ProbeDshOnce();
        Volatile.Write(ref _dshState, ok ? 1 : 2);
        return ok;
    }

    /// <summary>【测试】运行时解析覆盖（验证"PATH 无 dsh + 私有 runtime 仍可用"）。</summary>
    internal static Func<TubaWinUi3.Services.Ai.Dsh.DshRuntimeResolver.Resolved>? RuntimeResolverOverrideForTest;

    /// <summary>【测试】重置可用性探测缓存。</summary>
    internal static void ResetDshProbeCacheForTest() => Volatile.Write(ref _dshState, 0);

    /// <summary>
    /// 【A16 返修】可用性探测与真实启动共用 DshRuntimeResolver：
    /// 应用私有 runtime（<appBaseDir>\runtime\node.exe + dsh\lib\bin.js）存在即视为可用，
    /// 不再只探测 PATH 的 dsh（旧实现会把装了私有 runtime 的干净机器判成不可用）。
    /// </summary>
    private static bool ProbeDshOnce()
    {
        try
        {
            var rt = RuntimeResolverOverrideForTest?.Invoke() ?? TubaWinUi3.Services.Ai.Dsh.DshRuntimeResolver.Resolve();
            if (rt.IsStandalone)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(rt.NodeExe!)
                {
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit(8_000);
                return p.ExitCode == 0;
            }

            var psi2 = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c dsh --version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p2 = System.Diagnostics.Process.Start(psi2)!;
            p2.WaitForExit(8_000);
            return p2.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>创建会话：按引擎选择实现；dsh 不可用自动回退 builtin。</summary>
    public static IAgentSession CreateSession()
    {
        if (Current == "dsh" && IsDshAvailable())
        {
            var workspace = ReadWorkspace();
            try
            {
                var (providerId, endpoint, model, key) = ReadSelectedCredentials();
                // 【A07 返修】按【所选 provider 自己】的 Key 独立解析：
                //   · 所选 provider 的 Key 缺失 → 明确回退 builtin（不再去别的 provider 凑 Key——
                //     旧实现会把 DeepSeek 的 Key 注入给自定义供应商的端点，等于把 A 的凭据发给 B）；
                //   · 仅配置了自定义 provider 的用户不再被"必须有 DeepSeek Key"挡住而回退。
                // 【R2 2026-09-25】地址与 Key 缺一（配置不完整）不启动 dsh：
                // 空地址时不得把用户 Key 交给可能回退默认端点的运行时（统一按未配置回退 builtin）。
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(endpoint))
                {
                    var launch = DshLaunchConfig.Build(providerId, model, endpoint, key, ConfigManager.GetDataDir());
                    return new DshSession(key, workspace, launch);
                }
            }
            catch { }   // 配置读取异常：回退 builtin
        }
        return AgentSession.CreateNew();
    }

    /// <summary>
    /// 【A06 返修】按【存档引擎】恢复 dsh 会话：显式走 dsh，不随全局 Current 切换——
    /// 旧实现用 CreateSession()（按全局 Current 决定），dsh 存档在全局 builtin 时会被错误载入 builtin 实例。
    /// 返回 null 表示当前不可恢复（dsh 不可用/构造失败），由调用方如实回退并告知。
    /// Key 允许为空：恢复浏览只需连接 dsh 本身，提供商凭据在发送时才需要。
    /// </summary>
    public static IAgentSession? TryCreateDshSessionForRestore()
    {
        if (!IsDshAvailable()) return null;
        try
        {
            var workspace = ReadWorkspace();
            var (providerId, endpoint, model, key) = ReadSelectedCredentials();
            var launch = DshLaunchConfig.Build(providerId, model, endpoint, key, ConfigManager.GetDataDir());
            return new DshSession(key, workspace, launch);
        }
        catch { return null; }
    }

    /// <summary>【A07】读取当前所选 provider 的独立凭据（endpoint/model/key 均为【该 provider 自己】的值）。</summary>
    internal static (string ProviderId, string Endpoint, string Model, string Key) ReadSelectedCredentials()
    {
        if (CredentialsOverrideForTest is { } ov) return ov();
        return AiProviderStore.GetSelectedSnapshot();
    }

    /// <summary>连接测试使用同一份当前配置生成请求和验证指纹，避免测试默认模型却验证了另一模型。</summary>
    internal static (string Endpoint, string Model, string Key, string Fingerprint) CaptureConnectionTestConfig()
    {
        var (providerId, endpoint, model, key) = ReadSelectedCredentials();
        return (endpoint, model, key,
            DshLaunchConfig.ComputeFingerprint(providerId, model, endpoint, key));
    }

    /// <summary>【A07】当前所选配置的启动指纹（与 DshSession.LaunchFingerprint 比较以检测切换）。</summary>
    public static string CurrentLaunchFingerprint()
    {
        var (pid, ep, model, key) = ReadSelectedCredentials();
        return DshLaunchConfig.ComputeFingerprint(pid, model, ep, key);
    }

    /// <summary>【R2 2026-09-25】当前所选配置是否已验证：AppSettings 中的已验证指纹与当前指纹一致。
    /// 设置页测试成功与 AI 页真实发送成功都写同一指纹；两页共用此判定，返回复用页面时读实时值。</summary>
    public static bool IsSelectedConfigVerified()
    {
        try
        {
            var saved = AppSettings.Get("AiVerifiedFingerprint");
            return !string.IsNullOrWhiteSpace(saved) && saved == CurrentLaunchFingerprint();
        }
        catch { return false; }
    }

    /// <summary>【A07】会话是否需要在下一轮（安全轮次边界）前重建：配置已切换。纯函数，可测试。</summary>
    public static bool NeedsSessionRebuild(string? sessionFingerprint, string currentFingerprint)
        => !string.IsNullOrEmpty(sessionFingerprint) && sessionFingerprint != currentFingerprint;

    /// <summary>【测试】读取所选凭据的注入点（假 provider store 场景）。</summary>
    internal static Func<(string ProviderId, string Endpoint, string Model, string Key)>? CredentialsOverrideForTest;

    /// <summary>workspace：用户主目录。本产品定位=排障助手，工作域就是用户环境本体。</summary>
    private static string ReadWorkspace()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
