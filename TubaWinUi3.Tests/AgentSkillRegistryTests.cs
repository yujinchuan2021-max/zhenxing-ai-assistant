using System.Reflection;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

// 技能注册表是独立静态状态，与工具注册表互不影响，单独集合串行
[CollectionDefinition("AgentSkillRegistry")]
public sealed class AgentSkillRegistryCollection
{
}

/// <summary>技能注册表 + 技能提示词生成逻辑测试。</summary>
[Collection("AgentSkillRegistry")]
public class AgentSkillRegistryTests
{
    private static readonly FieldInfo SkillsField =
        typeof(AgentSkillRegistry).GetField("_skills", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void ClearRegistry()
        => ((List<AgentSkill>)SkillsField.GetValue(null)!).Clear();

    private static AgentSkill MakeSkill(string id) => new()
    {
        Id = id,
        DisplayName = $"技能{id}",
        Glyph = "\uE721",
        Description = "测试技能",
        SystemPromptFragment = $"这是 {id} 的指导片段"
    };

    [Fact]
    public void RegisterDefaults_RegistersAiAgentWorkflowSkill()
    {
        ClearRegistry();
        AgentSkillRegistry.RegisterDefaults();

        var skill = AgentSkillRegistry.Find(AiAgentWorkflowSkill.Id);
        Assert.NotNull(skill);
        Assert.Equal("枕星目标助手", skill!.DisplayName);

        // 同一份技能同时描述 AI 主项、内置工具优先及设备执行边界。
        Assert.Contains("AI 助手是主产品", skill.SystemPromptFragment);
        Assert.Contains("组工具流和电脑排障", skill.SystemPromptFragment);
        Assert.Contains("不授予设备权限", skill.SystemPromptFragment);
        Assert.Contains("可复制给所选 AI Agent", skill.SystemPromptFragment);
        Assert.DoesNotContain("DeepSeek 几块钱", skill.SystemPromptFragment);

        // 触发词至少覆盖一个核心场景
        Assert.Contains("写代码", skill.TriggerKeywords);
    }

    [Fact]
    public void Register_DuplicateId_Throws()
    {
        ClearRegistry();
        var skill = MakeSkill("dup_skill");
        AgentSkillRegistry.Register(skill);
        Assert.Throws<InvalidOperationException>(() => AgentSkillRegistry.Register(skill));
    }

    [Fact]
    public void Register_BlankId_Throws()
    {
        ClearRegistry();
        var skill = MakeSkill("");
        Assert.Throws<InvalidOperationException>(() => AgentSkillRegistry.Register(skill));
    }

    [Fact]
    public void Find_ReturnsRegisteredSkill()
    {
        ClearRegistry();
        AgentSkillRegistry.Register(MakeSkill("skill_a"));
        var skill = AgentSkillRegistry.Find("skill_a");
        Assert.NotNull(skill);
        Assert.Equal("skill_a", skill!.Id);
        Assert.Null(AgentSkillRegistry.Find("missing"));
    }

    [Fact]
    public void BuildActiveSkillsContext_OnlyContainsActiveSkillIndex()
    {
        ClearRegistry();
        AgentSkillRegistry.Register(MakeSkill("skill_a"));
        AgentSkillRegistry.Register(MakeSkill("skill_b"));

        var active = new[] { AgentSkillRegistry.Find("skill_a")! };
        var context = AgentSkillRegistry.BuildActiveSkillsContext(active);

        // 索引：包含激活技能的名称与简介，不注入完整指导片段（按需加载）
        Assert.Contains("## 已加载技能", context);
        Assert.Contains("技能skill_a", context);
        Assert.Contains("测试技能", context);
        Assert.DoesNotContain("这是 skill_a 的指导片段", context);
        Assert.DoesNotContain("skill_b", context);
    }

    [Fact]
    public void BuildActiveSkillsContext_NoActiveSkills_ReturnsEmpty()
    {
        var context = AgentSkillRegistry.BuildActiveSkillsContext([]);
        Assert.Equal("", context);
    }

    [Fact]
    public void BuildTriggerFor_HitsKeyword_ReturnsForceInstruction()
    {
        ClearRegistry();
        AgentSkillRegistry.RegisterDefaults();

        var trigger = AgentSkillRegistry.BuildTriggerFor("我想做个网站，帮我写程序", ["ai_agent_workflow"]);

        Assert.Contains("枕星目标助手", trigger);
        Assert.Contains("技能触发", trigger);
    }

    [Fact]
    public void BuildTriggerFor_NoKeyword_ReturnsEmpty()
    {
        ClearRegistry();
        AgentSkillRegistry.RegisterDefaults();

        Assert.Equal("", AgentSkillRegistry.BuildTriggerFor("今天天气怎么样", ["ai_agent_workflow"]));
    }

    [Fact]
    public void BuildTriggerFor_SkillDisabled_ReturnsEmpty()
    {
        ClearRegistry();
        AgentSkillRegistry.RegisterDefaults();

        // 技能未激活：即使命中关键词也不触发
        Assert.Equal("", AgentSkillRegistry.BuildTriggerFor("我想做个游戏", []));
    }

    [Fact]
    public void BuildTriggerFor_KeywordMatchIsSubstring()
    {
        ClearRegistry();
        AgentSkillRegistry.RegisterDefaults();

        // "想做个" 作为子串命中
        Assert.NotEqual("", AgentSkillRegistry.BuildTriggerFor("我想做个贪吃蛇小游戏", ["ai_agent_workflow"]));
    }

    [Fact]
    public void ProductSkill_EmbeddedDocumentMatchesSourceAsset()
    {
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "TestAssets", "builtin-goal-skill", "SKILL.md"));
        Assert.Equal(expected, AiAgentWorkflowSkill.Document);
        Assert.DoesNotContain("description:", AiAgentWorkflowSkill.Body);
    }

    [Theory]
    [InlineData("不要音频，只做电脑版")]
    [InlineData("Git已经装过了")]
    [InlineData("它安装好了，下一步")]
    [InlineData("继续昨天的方案")]
    [InlineData("停止，明天再继续")]
    [InlineData("I want to make a Windows game")]
    [InlineData("Please install Git")]
    [InlineData("Switch to the second option")]
    [InlineData("用AI生成音乐")]
    [InlineData("AI生成一张图片")]
    public void ProductSkill_TriggersForGoalChangesAndContinuation(string message)
    {
        ClearRegistry();
        AgentSkillRegistry.RegisterDefaults();

        Assert.Contains("枕星目标助手", AgentSkillRegistry.BuildTriggerFor(message,
            [AiAgentWorkflowSkill.Id]));
        Assert.Equal("", AgentSkillRegistry.BuildTriggerFor(message, []));
    }
}
