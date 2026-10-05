using System.Text;

namespace TubaWinUi3.Services.Agent;

/// <summary>
/// Agent 技能注册表。启动时注册一次；重复 Id 抛异常。
/// 测试通过反射清理私有静态列表（与 AgentToolRegistryTests 同模式）。
/// </summary>
public static class AgentSkillRegistry
{
    private static readonly List<AgentSkill> _skills = [];
    internal static readonly object Sync = new();

    public static IReadOnlyList<AgentSkill> All { get { lock (Sync) return _skills.ToArray(); } }

    public static void Register(AgentSkill skill)
    {
        lock (Sync)
        {
        if (string.IsNullOrWhiteSpace(skill.Id))
            throw new InvalidOperationException("Agent 技能 Id 不能为空");
        if (_skills.Any(s => s.Id == skill.Id))
            throw new InvalidOperationException($"Agent 技能 '{skill.Id}' 重复注册");
        _skills.Add(skill);
        }
    }

    public static AgentSkill? Find(string id)
    { lock (Sync) return _skills.FirstOrDefault(s => s.Id == id); }

    /// <summary>注册内置技能 + 加载用户自定义技能（启动时调用一次）。</summary>
    public static void RegisterDefaults()
    {
        AiAgentWorkflowSkill.Register();
        // ZXAI：加载用户自定义技能（%LocalAppData%\TubaWinUi3\Skills\*.skill.json，含子目录）
        UserSkillLoader.LoadAll();
    }

    /// <summary>
    /// 技能触发检测（纯逻辑，可单测）：用户消息命中某个激活技能的触发关键词时，
    /// 返回要注入系统提示词末尾的强指令；未命中返回空串。
    /// </summary>
    public static string BuildTriggerFor(string userText, IEnumerable<string> activeSkillIds)
    {
        if (string.IsNullOrWhiteSpace(userText)) return "";
        var active = activeSkillIds.ToHashSet();

        foreach (var skill in All.Where(s => active.Contains(s.Id)))
        {
            if (skill.TriggerKeywords.Length == 0) continue;
            if (!skill.TriggerKeywords.Any(k => userText.Contains(k, StringComparison.OrdinalIgnoreCase)))
                continue;

            return $"【技能触发】用户消息命中了已加载技能「{skill.DisplayName}」的触发场景。" +
                   "请读取随后提供的完整技能内容，结合当前任务执行相关步骤；" +
                   "用户的明确要求、事实核验和宿主实际权限边界仍须遵守。";
        }

        return "";
    }

    /// <summary>
    /// 生成「已加载技能」系统提示词段落（索引，仅含名称与简介），仅包含激活的技能。
    /// 完整技能指令（SystemPromptFragment）按需加载：只在命中触发词时随「系统指令」注入，
    /// 避免每轮请求都携带完整片段浪费 token。未激活任何技能时返回空串。
    /// </summary>
    public static string BuildActiveSkillsContext(IEnumerable<AgentSkill> activeSkills)
    {
        var list = activeSkills.ToList();
        if (list.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("## 已加载技能");
        sb.AppendLine();
        sb.AppendLine("以下技能已激活：用户咨询对应场景时，将提供完整技能内容；仍须遵守用户要求和宿主权限边界：");
        sb.AppendLine();
        foreach (var skill in list)
            sb.AppendLine($"- {skill.DisplayName}：{skill.Description}");
        return sb.ToString().TrimEnd();
    }
}
