using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

/// <summary>
/// 内置工具入口「AI 助手」：承载新的智能代理页面（AiAgentPage）。
/// 完整版 Agent 架构：多轮工具调用循环 + 步骤链可视化 + 确认卡片。
/// </summary>
public sealed class AiAssistantTool : IBuiltinTool
{
    public string Id => "ai-assistant";
    public string Name => "枕星图吧AI助手";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "说出想做的项目，先理解目标和约束，再组合可选工具流（优先应用内现成工具）并把能自动的装好配好；需要注册、登录或付费的步骤会逐步引导，完成后继续帮你排障。图吧工具箱是附加的现成工具与排障底座。";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE946";
    public string Category => "实用工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        // 【主审修复·2026-09-22】走主窗口统一卡片入口：主窗口模式复用唯一 owner（与左主页
        // 同一会话、同一实例）；独立窗口模式开独立新实例（关闭释放自己，不影响主窗口会话）。
        App.MainWindow?.OpenAiAssistantFromCard();
        return Task.CompletedTask;
    }
}
