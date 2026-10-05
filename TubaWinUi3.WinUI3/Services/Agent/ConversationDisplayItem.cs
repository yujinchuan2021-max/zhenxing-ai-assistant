using System.Text.Json.Serialization;

namespace TubaWinUi3.Services.Agent;

/// <summary>
/// 会话展示记录（磁盘持久化 {id}.display.json）：按时间顺序保存每条
/// 文本气泡与步骤链节点，保证历史对话按原始顺序完整恢复。
/// 与协议历史 messages.json 独立 —— 展示层与 LLM 续聊上下文互不干扰。
/// </summary>
public sealed class ConversationDisplayItem
{
    /// <summary>条目类型：text（文本气泡）/ steps（步骤链）。</summary>
    public string Type { get; set; } = "text";

    /// <summary>text 项：user / assistant。</summary>
    public string Role { get; set; } = "";

    /// <summary>text 项：消息内容。</summary>
    public string Content { get; set; } = "";

    /// <summary>assistant text 项：与最终回答分开展示的模型思维过程。</summary>
    public string ReasoningContent { get; set; } = "";

    /// <summary>
    /// Explicit completion evidence for assistant replies. Null = legacy/unfinished;
    /// false = failed, cancelled or awaiting confirmation. Only true may restore a plan action.
    /// Display metadata only; the original message and upload conversation are unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReplyCompletedSuccessfully { get; set; }

    internal bool CanRestoreToolFlowAction =>
        Type == "text" && Role == "assistant" && ReplyCompletedSuccessfully == true;

    /// <summary>Stamp this send/confirmation segment without rewriting earlier history.</summary>
    internal static void RecordReplyCompletion(IReadOnlyList<ConversationDisplayItem> items,
        int firstItem, AgentSendOutcome outcome, bool hadError = false, bool awaitingConfirmation = false)
    {
        var completed = outcome == AgentSendOutcome.Completed && !hadError && !awaitingConfirmation;
        for (var i = Math.Max(0, firstItem); i < items.Count; i++)
            if (items[i].Type == "text" && items[i].Role == "assistant")
                items[i].ReplyCompletedSuccessfully = completed;
    }

    /// <summary>notice 项：error / cancelled / warning。</summary>
    public string NoticeKind { get; set; } = "";

    /// <summary>稳定展示项标识，便于增量更新与后续格式演进。</summary>
    public string ItemId { get; set; } = "";

    /// <summary>steps 项：步骤行快照（按执行顺序）。</summary>
    public List<AgentStepSnapshot> Steps { get; set; } = [];

    /// <summary>steps 项：折叠后的摘要文字（含耗时/token 统计）。</summary>
    public string SummaryText { get; set; } = "";

    public double? DurationSeconds { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    /// <summary>meta 条目：会话级累计缓存命中/未命中 token（用于恢复展示）。</summary>
    public int CacheHitTokens { get; set; }
    public int CacheMissTokens { get; set; }
}

/// <summary>单个 Agent 步骤的持久化快照（历史恢复时重建步骤链行）。</summary>
public sealed class AgentStepSnapshot
{
    public string DisplayName { get; set; } = "";
    public string Glyph { get; set; } = "";
    public string Summary { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentStepStatus Status { get; set; }

    public string? Result { get; set; }
    public string? Error { get; set; }
    public double? DurationSeconds { get; set; }

    public static AgentStepSnapshot From(AgentStep s) => new()
    {
        DisplayName = s.DisplayName,
        Glyph = s.Glyph,
        Summary = s.Summary,
        Status = s.Status,
        Result = s.Result,
        Error = s.Error,
        DurationSeconds = s.Duration?.TotalSeconds
    };

    /// <summary>重建为可绑定步骤行数据的 AgentStep（仅用于展示）。</summary>
    public AgentStep ToAgentStep() => new()
    {
        DisplayName = DisplayName,
        Glyph = Glyph,
        Summary = Summary,
        Status = Status,
        Result = Result,
        Error = Error,
        Duration = DurationSeconds is double d ? TimeSpan.FromSeconds(d) : null
    };
}
