using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace TubaWinUi3.Services.Agent;

/// <summary>
/// ZXAI 2026-09-21：《项目档案》+ 工作流阶段工具组（「AI Agent 工作流」技能的持久化底座）。
/// 目标：把技能从「一次性对话」升级为「有状态的流程」——
/// 问诊结果 / 方案 / 进度全部落盘，跨会话、断线、重启均可续接，不必从头重问。
/// ① save_project_profile(section, content) —— 分块存档（merge 语义，只覆盖该块）
/// ② get_project_profile() —— 读档（新会话/续聊时先读）
/// ③ update_workflow_stage(stage, note) —— 推进六阶段并记流水
/// 阶段：interview(访谈) → hardware(体检) → plan(方案) → install(安装) → teach(教学) → escort(陪跑) → done(成品)
/// 数据：%LocalAppData%\TubaWinUi3\project-profile.json（单档案）
/// </summary>
public static class ProjectProfileTool
{
    private static readonly string[] ValidSections =
        ["goal", "details", "network", "payment", "toolchain", "hardware", "milestones", "notes"];

    private static readonly string[] ValidStages =
        ["interview", "hardware", "plan", "install", "teach", "escort", "done"];

    private static readonly Dictionary<string, string> StageNames = new()
    {
        ["interview"] = "需求访谈",
        ["hardware"] = "硬件体检",
        ["plan"] = "方案生成",
        ["install"] = "自动安装",
        ["teach"] = "上手教学",
        ["escort"] = "陪跑到成品",
        ["done"] = "已出成品",
    };

    public static void Register()
    {
        AgentToolRegistry.Register(new AgentTool
        {
            Name = "save_project_profile",
            DisplayName = MiscTexts.T("保存项目档案"),
            Glyph = "\uE74E",
            RequiresConfirmation = false,
            Function = AIFunctionFactory.Create(
                (Func<string, string, string>)SaveProjectProfile,
                new AIFunctionFactoryOptions { Name = "save_project_profile" }),
        });

        AgentToolRegistry.Register(new AgentTool
        {
            Name = "get_project_profile",
            DisplayName = MiscTexts.T("读取项目档案"),
            Glyph = "\uE8A5",
            RequiresConfirmation = false,
            Function = AIFunctionFactory.Create(
                (Func<string>)GetProjectProfile,
                new AIFunctionFactoryOptions { Name = "get_project_profile" }),
        });

        AgentToolRegistry.Register(new AgentTool
        {
            Name = "update_workflow_stage",
            DisplayName = MiscTexts.T("更新工作流阶段"),
            Glyph = "\uE9D5",
            RequiresConfirmation = false,
            Function = AIFunctionFactory.Create(
                (Func<string, string, string>)UpdateWorkflowStage,
                new AIFunctionFactoryOptions { Name = "update_workflow_stage" }),
        });
    }

    /// <summary>测试注入（null=默认路径）。</summary>
    internal static string? ProfilePathOverride;

    private static string ProfilePath
    {
        get
        {
            if (ProfilePathOverride is not null) return ProfilePathOverride;
            // 【A13】旧版固定写 AppData：仅目标缺失时一次性兼容迁移（保留源）
            ConfigManager.MigrateLegacyFileIfMissing("project-profile.json");
            return Path.Combine(ConfigManager.GetDataDir(), "project-profile.json");
        }
    }

    [Description("保存/更新《项目档案》的分块内容（「AI Agent 工作流」专用）。" +
        "section 取值：goal=项目目标 / details=细分需求 / network=网络环境 / payment=付费方式 / " +
        "toolchain=选定的工具链方案 / hardware=硬件摘要 / milestones=里程碑清单 / notes=其他备注。" +
        "用法：问诊每问清一块就存一块（如 save_project_profile(\"goal\",\"做2D小游戏\")），" +
        "方案定稿后 save_project_profile(\"toolchain\",\"Godot 4.x + Git + Codex(DeepSeek直连)\")。" +
        "只覆盖传入的 section，其余保留。")]
    public static string SaveProjectProfile(string section, string content)
    {
        try
        {
            section = (section ?? "").Trim().ToLowerInvariant();
            if (!ValidSections.Contains(section))
                return $"未知 section「{section}」。可用：{string.Join(" / ", ValidSections)}";
            if (string.IsNullOrWhiteSpace(content)) return "content 为空，未保存。";

            var root = LoadRoot();
            root[section] = content.Trim();
            root["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            SaveRoot(root);
            return $"已存档 [{section}]：{Truncate(content.Trim(), 80)}";
        }
        catch (Exception ex) { return "存档失败：" + ex.Message; }
    }

    [Description("读取《项目档案》全文（「AI Agent 工作流」专用）。" +
        "用法：①用户提到继续之前的项目 / 说\"继续\"/\"做到哪了\"时先读它续接 ②每轮工作流推进前读一次确认当前阶段。" +
        "返回各分块内容 + 当前阶段 + 阶段流水；空档案会提示从访谈开始。")]
    public static string GetProjectProfile()
    {
        try
        {
            if (!File.Exists(ProfilePath)) return "（尚无项目档案——这是新项目，从需求访谈开始。）";
            var root = LoadRoot();
            var sb = new StringBuilder("《项目档案》\n");
            var labels = new Dictionary<string, string>
            {
                ["goal"] = "项目目标", ["details"] = "细分需求", ["network"] = "网络环境",
                ["payment"] = "付费方式", ["toolchain"] = "工具链方案", ["hardware"] = "硬件摘要",
                ["milestones"] = "里程碑", ["notes"] = "备注",
            };
            var any = false;
            foreach (var kv in labels)
            {
                if (root[kv.Key] is JsonNode n && !string.IsNullOrWhiteSpace(n.ToString()))
                {
                    sb.AppendLine($"- {kv.Value}：{n}");
                    any = true;
                }
            }
            if (!any) return "（档案为空——从需求访谈开始。）";

            var stage = root["stage"]?.ToString() ?? "interview";
            sb.AppendLine($"- 当前阶段：{StageNames.GetValueOrDefault(stage, stage)}（{stage}）");
            if (root["updatedAt"] is JsonNode ua) sb.AppendLine($"- 更新于：{ua}");

            if (root["stageHistory"] is JsonArray hist && hist.Count > 0)
            {
                sb.AppendLine("- 阶段流水：");
                foreach (var h in hist) sb.AppendLine($"  · {h}");
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "读取失败：" + ex.Message; }
    }

    [Description("更新工作流阶段（「AI Agent 工作流」专用，每推进一步就调）。" +
        "stage 取值：interview=需求访谈 / hardware=硬件体检 / plan=方案生成 / install=自动安装 / " +
        "teach=上手教学 / escort=陪跑到成品 / done=已出成品。note=本次进展一两句话。" +
        "用法：如环境装完验证通过 → update_workflow_stage(\"teach\",\"Godot 4.3 装好，跑通第一个场景\")。")]
    public static string UpdateWorkflowStage(string stage, string note)
    {
        try
        {
            stage = (stage ?? "").Trim().ToLowerInvariant();
            if (!ValidStages.Contains(stage))
                return $"未知阶段「{stage}」。可用：{string.Join(" / ", ValidStages)}";

            var root = LoadRoot();
            root["stage"] = stage;
            root["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            if (!string.IsNullOrWhiteSpace(note))
            {
                if (root["stageHistory"] is not JsonArray arr)
                {
                    arr = new JsonArray();
                    root["stageHistory"] = arr;
                }
                arr.Add($"{DateTime.Now:MM-dd HH:mm} [{StageNames.GetValueOrDefault(stage, stage)}] {note.Trim()}");
                while (arr.Count > 50) arr.RemoveAt(0); // 流水限长
            }
            SaveRoot(root);
            return $"阶段已更新：{StageNames.GetValueOrDefault(stage, stage)}。{ (string.IsNullOrWhiteSpace(note) ? "" : "记录：" + Truncate(note.Trim(), 60)) }";
        }
        catch (Exception ex) { return "更新失败：" + ex.Message; }
    }

    private static JsonObject LoadRoot()
    {
        try
        {
            if (File.Exists(ProfilePath))
                return JsonNode.Parse(File.ReadAllText(ProfilePath, Encoding.UTF8))?.AsObject() ?? new JsonObject();
        }
        catch { }
        return new JsonObject();
    }

    private static void SaveRoot(JsonObject root)
    {
        var dir = Path.GetDirectoryName(ProfilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ProfilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
