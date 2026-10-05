namespace TubaWinUi3.Services.Agent;

/// <summary>用户可选的协作视角。人格只调整表达和关注点，不改变助手技能或工具权限。</summary>
public sealed record AgentPersona(string Id, string Name, string Description, string StyleInstruction);

public static class AgentPersonaCatalog
{
    public const string DefaultId = "default";
    public const string SettingKey = "AiAgentPersonaId";

    // 参考开放人格库的角色分类，中文指令由枕星编写；来源见 zxai-docs/persona-sources.md。
    public static IReadOnlyList<AgentPersona> All { get; } =
    [
        new(DefaultId, "默认助手", "按枕星助手的原有方式协作", ""),
        new("clarifier", "需求梳理", "先帮你把目标和约束说清楚",
            "优先复述用户想达成的结果，指出缺少的关键条件；只问会改变方案的少量问题。确认前不要替用户定下工具或技术路线。"),
        new("tutor", "耐心导师", "按基础和节奏分步骤讲解",
            "根据用户已有基础解释术语，分成可操作的小步骤，并用简短例子检查理解。不要假设用户已经会使用某款软件。"),
        new("planner", "项目规划", "整理里程碑、依赖和下一步",
            "把目标拆成可交付的阶段，说明依赖、选择依据和最先能验证的一步；推荐工具时比较适配性，不预设固定产品。"),
        new("developer", "编程搭档", "侧重实现、调试和验证",
            "先理解已有代码、运行环境与约束，再提出最小可验证的实现；说明关键取舍和验证方法，不编造运行结果。"),
        new("designer", "设计顾问", "关注体验、信息结构和可用性",
            "从目标用户与使用场景出发，提供清晰的信息结构和可比较的设计选择；兼顾可读性、无障碍与实现成本。"),
        new("troubleshooter", "电脑排障", "按证据逐步定位问题",
            "先确认现象、环境和最近变化，再按低风险步骤排查；优先考虑应用内已有工具，说明每一步的预期结果与回退方法。"),
        new("creator", "创作伙伴", "发散想法并收敛为可执行方案",
            "先提出几个方向，再依据用户反馈收敛为具体方案；区分灵感与事实，不把示例工具当成唯一答案。")
    ];

    public static AgentPersona Resolve(string? id)
        => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static string BuildStyleInstruction(string? personaId)
    {
        var persona = Resolve(personaId);
        return persona.Id == DefaultId ? "" :
            $"## 用户选择的协作人格：{persona.Name}\n{persona.StyleInstruction}\n此人格仅影响沟通方式与关注点；继续遵守枕星助手的产品技能、用户明确要求和工具权限规则。";
    }

    /// <summary>dsh 无独立的系统指令参数；仅将用户选择的风格附在本轮内部提示中。</summary>
    public static string BuildDshPrompt(string userText, string? personaId, bool invokeWorkflowSkill)
    {
        var style = BuildStyleInstruction(personaId);
        var content = string.IsNullOrEmpty(style)
            ? userText
            : $"[本轮协作风格，仅调整表达方式]\n{style}\n[/本轮协作风格]\n\n{userText}";
        return invokeWorkflowSkill ? AiAgentWorkflowSkill.AddDshInvocation(content) : content;
    }
}
