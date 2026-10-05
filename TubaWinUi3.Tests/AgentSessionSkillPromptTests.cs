using System.Reflection;
using Microsoft.Extensions.AI;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

/// <summary>
/// 验证技能注入链路：注册技能 → 创建会话 → 系统提示词必须包含「已加载技能」索引段
/// （名称 + 简介）。完整技能指令按需加载，不常驻系统提示词——只在命中触发词时随「系统指令」注入。
/// 防止"技能没生效"类回归（模型看不到技能索引）。
/// </summary>
[Collection("AgentSkillRegistry")]
public class AgentSessionSkillPromptTests
{
    private static readonly FieldInfo SkillsField =
        typeof(AgentSkillRegistry).GetField("_skills", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo HistoryField =
        typeof(AgentSession).GetField("_history", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void ClearRegistry()
        => ((List<AgentSkill>)SkillsField.GetValue(null)!).Clear();

    [Fact]
    public void CreateNew_SystemPrompt_IncludesActiveSkillContext()
    {
        ClearRegistry();
        AgentSkillRegistry.RegisterDefaults();

        var session = AgentSession.CreateNew();
        var history = (List<ChatMessage>)HistoryField.GetValue(session)!;

        Assert.True(history.Count > 0 && history[0].Role == ChatRole.System, "系统提示词应为首条消息");
        var system = history[0].Text ?? "";

        // 技能索引段已注入（名称 + 简介）
        Assert.Contains("## 已加载技能", system);
        Assert.Contains("枕星目标助手", system);
        // 按需加载：索引存在，完整产品技能不常驻基础系统提示词。
        Assert.DoesNotContain(AiAgentWorkflowSkill.Body, system);
        // 技能段紧跟主提示词（位置在工具箱 CLI 索引标题之前，保证模型优先注意）
        var skillsIdx = system.IndexOf("## 已加载技能", StringComparison.Ordinal);
        // The detailed catalog heading can change its guidance or report missing documentation.
        // The main prompt also mentions this section, so locate the final catalog heading.
        var indexIdx = system.LastIndexOf("## 工具箱命令行工具", StringComparison.Ordinal);
        Assert.True(skillsIdx < indexIdx,
            $"技能段位置异常：skills@{skillsIdx} index@{indexIdx}\n系统提示词开头：\n{system[..Math.Min(600, system.Length)]}");

        // 缓存优化：系统提示词不得含当前时间（分钟级变化会导致服务端前缀缓存整段失效；
        // 时间已改为追加到用户消息末尾，由 AiAssistantService.WithCurrentTime 负责）
        Assert.DoesNotContain("当前时间", system);
        session.Dispose();
    }

    [Fact]
    public void SetSkillEnabled_Off_RemovesSkillFromSystemPrompt()
    {
        ClearRegistry();
        AgentSkillRegistry.RegisterDefaults();

        var session = AgentSession.CreateNew();
        session.SetSkillEnabled(AiAgentWorkflowSkill.Id, false);

        var history = (List<ChatMessage>)HistoryField.GetValue(session)!;
        var system = history[0].Text ?? "";

        Assert.DoesNotContain("## 已加载技能", system);
        Assert.DoesNotContain("枕星目标助手", system);
        session.Dispose();
    }
}
