// ZXAI A07/A16：dsh 启动配置构建器 —— 把用户在 AI 设置里选的 provider/model/baseURL/key
// 映射为 dsh 的启动参数（--patch 文件 + child env）。
// 依据 zxai-docs/dsh-protocol-reference-2026-09-21.md：
//   · ACP app 是 zero-option 命令（不接受 --model 之类参数）；覆盖 provider/model 用 --patch。
//   · Key 仅放 child env，绝不写入 patch 文件、绝不落盘、绝不入日志。
//   · deepseek 供应商原生支持 DEEPSEEK_BASE_URL / DEEPSEEK_API_KEY；
//     其它供应商走基础 profile 已挂载的 llm-pi-ai（api: openai-completions）。
//   · 环境凭据在启动时快照冻结：换 Key 需要重启会话（dispose 重建）。

using System.Text;
using TubaWinUi3.Services.Agent;
using TubaWinUi3.Services.Ai;

namespace TubaWinUi3.Services.Ai.Dsh;

public sealed class DshLaunchConfig
{
    /// <summary>生成的 patch 文件路径（无需覆盖时为 null）。</summary>
    public string? PatchPath { get; init; }

    /// <summary>本次启动补丁已投影枕星内置技能，发送时可显式调用。</summary>
    public bool BuiltinGoalSkillProjected { get; init; }

    /// <summary>注入子进程的环境变量（含 Key —— 只进内存/child env，绝不落盘）。</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>本次启动实际生效的模型（供诊断/会话元数据记录；空=用 dsh 默认）。</summary>
    public string? Model { get; init; }

    /// <summary>本次启动实际生效的供应商 id。</summary>
    public string ProviderId { get; init; } = AiProviderStore.DeepSeekProviderId;

    /// <summary>应用数据目录（用于推导 DSH_HOME=dsh-home；null 则不设）。</summary>
    public string? DataDir { get; init; }

    /// <summary>【测试】dsh 启动路径覆盖（假 ACP 回归用；生产为 null → 走 DshRuntimeResolver）。</summary>
    public string? DshPathOverride { get; init; }

    /// <summary>本次启动使用的端点的规范化值（指纹与诊断用）。</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>【A07】启动配置指纹（provider|model|endpoint|key 哈希前 8 位）——切换检测用，不泄漏 Key。</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>【A07】与实例同口径的静态指纹计算（供"当前所选配置"侧比较）。</summary>
    public static string ComputeFingerprint(string providerId, string model, string endpoint, string apiKey)
        => string.Join("|", providerId ?? "", model ?? "", (endpoint ?? "").Trim(), ShortHash(apiKey ?? ""));

    internal static string ShortHash(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)))[..8];
    }

    public const string DshHomeDirName = "dsh-home";
    private const string PatchFileName = "zxai-launch.patch.yml";
    /// <summary>自定义供应商在 patch 里的内部别名（不暴露用户 id，避免特殊字符）。</summary>
    private const string CustomRouteId = "zxai-selected";

    /// <summary>【第二轮复核】本次启动配置在 dsh 侧【实际产生的 ACP route provider id】。
    /// 应用的 ProviderId（deepseek / custom / custom-N …）不等于 ACP 路由身份：DeepSeek 走原生插件
    /// → deepseek-official；自定义（OpenAI 兼容）→ 本类的 CustomRouteId。模型切换匹配必须用它，
    /// 不能用应用 id、供应商展示名或前缀。</summary>
    public string RouteProviderId => IsDeepSeek(ProviderId) ? "deepseek-official" : CustomRouteId;

    /// <summary>
    /// 构建启动配置。endpoint/model/apiKey 来自 AiProviderStore.GetSelectedConfig()（用户所选）。
    /// providerId 为 deepseek 时走原生插件；其它值视为 OpenAI 兼容自定义供应商（llm-pi-ai）。
    /// </summary>
    public static DshLaunchConfig Build(string providerId, string model, string endpoint, string apiKey, string dataDir)
    {
        var env = new Dictionary<string, string>();
        string? providerPatch = null;
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

        if (IsDeepSeek(providerId))
        {
            env["DEEPSEEK_API_KEY"] = apiKey;
            if (!string.IsNullOrWhiteSpace(endpoint) && !IsOfficialDeepSeek(endpoint))
                env["DEEPSEEK_BASE_URL"] = endpoint.Trim();
            // 【A07 返修】任何【明确选择的】模型都要通过 patch 显式覆盖：
            // dsh-acp-app 的内置默认是 deepseek-official/deepseek-v4-flash，与用户所选框未必一致——
            // 不能把 deepseek-flash 当作"默认无需覆盖"（首次启动就可能跑错模型）。
            if (effectiveModel is not null)
                providerPatch = DeepSeekModelPatch(effectiveModel);
        }
        else
        {
            // 自定义供应商：Key 进专用 env（apiKeyEnv 指向它），patch 里只写地址与模型
            env["ZXAI_DSH_API_KEY"] = apiKey;
            providerPatch = CustomProviderPatch(effectiveModel ?? "default", endpoint);
        }

        // dsh ACP 不提供单独的 system/skills 参数；使用已挂载的 skill-filesystem
        // 投影产品内置 SKILL.md。即使用户未指定模型，也必须生成技能补丁。
        var bundledSkillDir = AiAgentWorkflowSkill.WriteDshProjection(dataDir);
        var patch = WritePatch(dataDir,
            (providerPatch ?? "") + BundledGoalSkillPatch(bundledSkillDir));

        return new DshLaunchConfig
        {
            PatchPath = patch,
            BuiltinGoalSkillProjected = true,
            Env = env,
            Model = effectiveModel,
            ProviderId = providerId,
            DataDir = dataDir,
            Endpoint = (endpoint ?? "").Trim(),
            Fingerprint = ComputeFingerprint(providerId, model ?? "", endpoint ?? "", apiKey ?? ""),
        };
    }

    private static bool IsDeepSeek(string providerId)
        => string.IsNullOrWhiteSpace(providerId) ||
           string.Equals(providerId, AiProviderStore.DeepSeekProviderId, StringComparison.OrdinalIgnoreCase);

    /// <summary>官方地址判定：api.deepseek.com（含 https/http）时不写 DEEPSEEK_BASE_URL。</summary>
    internal static bool IsOfficialDeepSeek(string endpoint)
    {
        try
        {
            var host = new Uri(endpoint.Trim()).Host;
            return host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>deepseek + 指定模型的 patch。dsh 按 id 替换整份 config，必须保留 provider。</summary>
    internal static string DeepSeekModelPatch(string model)
        => "# ZXAI 自动生成：覆盖 acp app 的默认模型（每次启动重写，请勿手改）\n" +
           "- id: acp\n" +
           "  config:\n" +
           "    provider: deepseek-official\n" +
           $"    model: {YamlScalar(model)}\n";

    /// <summary>自定义（OpenAI 兼容）供应商 patch：llm-pi-ai 路由 + acp 指向它。Key 不进这里。</summary>
    internal static string CustomProviderPatch(string model, string endpoint)
        => "# ZXAI 自动生成：自定义 OpenAI 兼容供应商路由（每次启动重写，请勿手改）\n" +
           "# Key 通过 child env ZXAI_DSH_API_KEY 注入，绝不写入本文件。\n" +
           "- id: llm-pi-ai\n" +
           "  config:\n" +
           "    providers:\n" +
           $"      {CustomRouteId}:\n" +
           "        api: openai-completions\n" +
           $"        baseURL: {YamlScalar(endpoint)}\n" +
           "        apiKeyEnv: ZXAI_DSH_API_KEY\n" +
           "        models:\n" +
           $"          - id: {YamlScalar(model)}\n" +
           "- id: acp\n" +
           "  config:\n" +
           $"    provider: {CustomRouteId}\n" +
           $"    model: {YamlScalar(model)}\n";

    internal static string BundledGoalSkillPatch(string root)
        => "- id: skill-filesystem\n" +
           "  config:\n" +
           "    includeDefaultRoots: false\n" +
           $"    bundledSkillDir: {YamlScalar(root)}\n";

    /// <summary>YAML 标量安全序列化（字符串/号码一律带双引号转义；不产生可执行 !!js 结构）。</summary>
    internal static string YamlScalar(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '"' or '\\') sb.Append('\\');
            sb.Append(c is '\r' or '\n' ? ' ' : c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string WritePatch(string dataDir, string content)
    {
        var dir = Path.Combine(dataDir, DshHomeDirName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, PatchFileName);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
