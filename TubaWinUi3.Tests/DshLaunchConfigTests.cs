using TubaWinUi3.Services.Ai.Dsh;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【A07】dsh 启动配置构建：用户在 AI 设置里选的 provider/model/baseURL/key
/// 必须真实进入启动参数（--patch 文件 + child env），且 **Key 绝不写入 patch 文件**。
/// 依据 zxai-docs/dsh-protocol-reference-2026-09-21.md（zero-option；--patch 覆盖 provider/model；
/// Key 仅放 child env；deepseek 原生 DEEPSEEK_BASE_URL/DEEPSEEK_API_KEY；自定义走 llm-pi-ai）。
/// </summary>
public class DshLaunchConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zxai-launch-" + Guid.NewGuid().ToString("N")[..8]);

    public DshLaunchConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void DeepSeek_ChosenModel_AlwaysPatched_KeyOnlyInEnv()
    {
        var cfg = DshLaunchConfig.Build("deepseek", "deepseek-flash", "https://api.deepseek.com", "sk-test-123", _dir);

        // 【A07 返修】任何【明确选择的】模型都必须显式 patch 覆盖：
        // dsh-acp-app 内置默认（deepseek-official/deepseek-v4-flash）未必等于用户所选框——
        // 旧实现把 deepseek-flash 当作"默认无需 patch"，首次启动即可能跑错模型。
        Assert.NotNull(cfg.PatchPath);
        Assert.Contains("\"deepseek-flash\"", File.ReadAllText(cfg.PatchPath!));
        Assert.Contains("provider: deepseek-official", File.ReadAllText(cfg.PatchPath!));
        Assert.Equal("sk-test-123", cfg.Env["DEEPSEEK_API_KEY"]);       // Key 进 env
        Assert.False(cfg.Env.ContainsKey("DEEPSEEK_BASE_URL"));         // 官方地址不写 BASE_URL
        Assert.Equal("deepseek-flash", cfg.Model);
    }

    [Fact]
    public void DeepSeek_NoModelSelection_StillProjectsBuiltinSkill()
    {
        // 未选模型只是不覆盖模型；仍必须投影产品技能。
        var cfg = DshLaunchConfig.Build("deepseek", "", "https://api.deepseek.com", "sk-test-123", _dir);
        Assert.NotNull(cfg.PatchPath);
        Assert.True(cfg.BuiltinGoalSkillProjected);
        var patch = File.ReadAllText(cfg.PatchPath!);
        Assert.Contains("id: skill-filesystem", patch);
        Assert.Contains("bundledSkillDir:", patch);
        Assert.DoesNotContain("id: acp", patch);
        Assert.DoesNotContain("sk-test-123", patch);

        var projected = Path.Combine(_dir, "dsh-home", "zxai-bundled-skills",
            AiAgentWorkflowSkill.DshName, "SKILL.md");
        Assert.True(File.Exists(projected));
        Assert.Equal(AiAgentWorkflowSkill.Document, File.ReadAllText(projected));
        // This product skill must not be shadowed by a same-name project/user skill.
        Assert.Contains("includeDefaultRoots: false", patch);
    }

    [Fact]
    public void DeepSeek_CustomEndpointAndModel_PatchCarriesModel_KeyNeverInPatchFile()
    {
        var cfg = DshLaunchConfig.Build("deepseek", "deepseek-v4-pro", "https://custom.example/v1", "sk-secret-XYZ", _dir);

        Assert.NotNull(cfg.PatchPath);
        Assert.Equal("https://custom.example/v1", cfg.Env["DEEPSEEK_BASE_URL"]);  // 自定义端点进 env
        Assert.Equal("sk-secret-XYZ", cfg.Env["DEEPSEEK_API_KEY"]);

        var text = File.ReadAllText(cfg.PatchPath!);
        Assert.Contains("id: acp", text);
        Assert.Contains("provider: deepseek-official", text);
        Assert.Contains("\"deepseek-v4-pro\"", text);                    // 模型名（YAML 安全标量）
        Assert.DoesNotContain("sk-secret-XYZ", text);                    // 【关键】Key 绝不落盘
        Assert.Contains("id: skill-filesystem", text);
    }

    [Fact]
    public void CustomProvider_RoutePatch_KeyEnvOnly()
    {
        var cfg = DshLaunchConfig.Build("custom", "my-model", "https://intra.example/v1", "sk-abc-999", _dir);

        Assert.NotNull(cfg.PatchPath);
        Assert.Equal("sk-abc-999", cfg.Env["ZXAI_DSH_API_KEY"]);         // 自定义供应商专用 env

        var text = File.ReadAllText(cfg.PatchPath!);
        Assert.Contains("llm-pi-ai", text);                              // 基础 profile 已挂载的路由
        Assert.Contains("api: openai-completions", text);
        Assert.Contains("https://intra.example/v1", text);
        Assert.Contains("apiKeyEnv: ZXAI_DSH_API_KEY", text);            // 只引用 env 名，不写值
        Assert.Contains("provider: zxai-selected", text);
        Assert.DoesNotContain("sk-abc-999", text);                       // 【关键】Key 绝不落盘
        Assert.Contains("id: skill-filesystem", text);
    }

    [Fact]
    public void YamlScalar_QuotesAndEscapes()
    {
        Assert.Equal("\"plain\"", DshLaunchConfig.YamlScalar("plain"));
        Assert.Equal("\"a\\\"b\\\\c\"", DshLaunchConfig.YamlScalar("a\"b\\c"));
        Assert.Equal("\"line1 line2\"", DshLaunchConfig.YamlScalar("line1\nline2")); // 换行折为空格
    }

    [Fact]
    public void IsOfficialDeepSeek_OnlyExactHost()
    {
        Assert.True(DshLaunchConfig.IsOfficialDeepSeek("https://api.deepseek.com"));
        Assert.True(DshLaunchConfig.IsOfficialDeepSeek("http://api.deepseek.com/v1"));
        Assert.False(DshLaunchConfig.IsOfficialDeepSeek("https://proxy.example.com"));
        Assert.False(DshLaunchConfig.IsOfficialDeepSeek("not a url"));
    }
}
