namespace TubaWinUi3.Services.Agent;

/// <summary>
/// 【ZXAI】Agent 会话统一门面（换核心 M2）：UI 只面向本接口，
/// 底层引擎可切换——自研 AgentRuntime（AgentSession）或 DeepSeek Harness（DshSession）。
/// 事件/方法与 AgentSession 完全对齐；dsh 引擎下少数自研专属成员为空实现（见各实现注释）。
/// </summary>
/// <summary>【A12】一轮发送的明确结果（页面按「成功才清空附件」消费，不依赖异步 Error 事件推断）。</summary>
public enum AgentSendOutcome
{
    /// <summary>正常完成（end_turn 等）。</summary>
    Completed,
    /// <summary>被取消（prompt 期取消 / 握手窗口停止 / OCE）。</summary>
    Cancelled,
    /// <summary>请求失败（异常）。</summary>
    Failed,
    /// <summary>被拒绝而未提交（会话忙 / 已关闭 / 能力不支持）。</summary>
    Rejected,
}

public interface IAgentSession : IDisposable
{
    string Id { get; }
    string Title { get; }
    bool IsRunning { get; }
    string PersonaId { get; }

    /// <summary>【A12】最近一轮 SendAsync 的明确结果（各引擎必须维护）。</summary>
    AgentSendOutcome LastSendOutcome { get; }

    int TotalPromptTokens { get; }
    int TotalCompletionTokens { get; }
    int TotalTokens => TotalPromptTokens + TotalCompletionTokens;

    /// <summary>缓存命中/未命中 token（提供商不返回时恒 0）。</summary>
    int TotalCacheHitTokens { get; }
    int TotalCacheMissTokens { get; }

    IReadOnlyCollection<string> ActiveSkillIds { get; }

    event Action<string>? TextChunk;
    event Action<string>? ReasoningChunk;
    event Action<AgentStep>? StepStarted;
    event Action<AgentStep>? StepCompleted;
    event Action<IReadOnlyList<AgentConfirmationRequest>>? ConfirmationsRequested;
    event Action<string>? Error;
    event Action? RunCompleted;
    event Action? RoundStarted;
    event Action<AgentStepGroupSummary>? StepGroupCompleted;

    Task SendAsync(string userText, IReadOnlyList<(byte[] Bytes, string MediaType)>? images = null);
    Task ResumeConfirmationsAsync(IReadOnlyList<AgentConfirmationDecision> decisions);
    void Cancel();
    void SetSkillEnabled(string id, bool enabled);
    void SetPersona(string personaId);
    void Rename(string title);
    void Save();
}
