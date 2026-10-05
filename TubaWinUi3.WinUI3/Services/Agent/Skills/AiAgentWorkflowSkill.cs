using System.Text;

namespace TubaWinUi3.Services.Agent;

/// <summary>
/// 枕星的产品技能。SKILL.md 是单一事实来源：内置引擎读取其正文，
/// dsh 引擎使用同一文件的私有投影并通过 /zhenxing-assistant 调用。
/// </summary>
public static class AiAgentWorkflowSkill
{
    public const string Id = "ai_agent_workflow"; // 保留既有会话技能开关的身份
    public const string DshName = "zhenxing-assistant";
    private const string ResourceName = "TubaWinUi3.Skills.ZhenxingAssistant";

    public static void Register()
        => AgentSkillRegistry.Register(new AgentSkill
        {
            Id = Id,
            DisplayName = "枕星目标助手",
            Glyph = "\uE99A",
            Description = "理解目标，复用已有工具，确认方案后准备环境，接续使用与排障",
            TriggerKeywords =
            [
                "我想", "我要", "帮我", "工具流", "工作流", "AI agent", "AI智能体", "智能体", "AI编程", "编程助手",
                "写代码", "帮我写程序", "做个游戏", "做一款游戏", "开发游戏", "开发一个",
                "做个小工具", "做网站", "做网页", "建站", "学编程", "想做游戏",
                "想做个", "做一个", "学做网站", "做一款", "怎么做", "帮我装",
                "电脑", "蓝屏", "风扇", "剪辑", "视频", "软件", "安装", "推荐",
                "生成音乐", "生成歌曲", "AI生成", "AI 生成",
                "排障", "故障", "无法启动", "打不开", "教程", "课程", "学AI", "学习AI",
                "学习人工智能", "学机器学习", "学习机器学习", "学习大模型",
                "继续", "下一步", "停止", "暂停", "进度", "不要", "不用", "改成", "换成",
                "已装", "装过", "装好了", "安装好了", "安装失败", "能用了", "完成了",
                "workflow", "install", "set up", "troubleshoot", "I want", "continue", "next step",
                "stop", "pause", "progress", "switch to", "already installed", "it works"
            ],
            SystemPromptFragment = Body
        });

    /// <summary>从随程序嵌入的原始 SKILL.md 读取；构建缺资源即失败，不悄悄退回旧提示词。</summary>
    public static string Document
    {
        get
        {
            using var stream = typeof(AiAgentWorkflowSkill).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"缺少内置技能资源：{ResourceName}");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    public static string Body
    {
        get
        {
            var document = Document.Replace("\r\n", "\n", StringComparison.Ordinal);
            var end = document.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (!document.StartsWith("---\n", StringComparison.Ordinal) || end < 0)
                throw new InvalidOperationException("内置技能缺少 YAML frontmatter");
            return document[(end + 5)..].Trim();
        }
    }

    public static string EffectiveDocument
    {
        get
        {
            try { return new SkillRevisionStore().ReadEffectiveDocument(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            { return Document; }
        }
    }

    public static string EffectiveBody => SkillRevisionDocument.Split(EffectiveDocument).Body.Trim();

    /// <summary>
    /// 投影到应用管理的数据根，供 dsh skill-filesystem 的 bundledSkillDir 扫描。
    /// 官方版本来自嵌入资源；显式本机试用覆写正文，保持同一技能身份与路径。
    /// </summary>
    public static string WriteDshProjection(string dataDir)
    {
        var root = Path.Combine(dataDir, "dsh-home", "zxai-bundled-skills");
        var directory = Path.Combine(root, DshName);
        Directory.CreateDirectory(directory);
        string effective;
        try { effective = new SkillRevisionStore(dataDir).ReadEffectiveDocument(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { effective = Document; }
        SkillRevisionStore.AtomicWrite(Path.Combine(directory, "SKILL.md"), effective);
        return root;
    }

    /// <summary>dsh 只扫描直接 user 消息中的 /name；保留原文供 UI 与历史展示。</summary>
    public static string AddDshInvocation(string userText)
        => $"/{DshName}\n{userText}";
}
