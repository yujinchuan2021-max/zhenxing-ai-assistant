using System.Text.Json.Nodes;

namespace TubaWinUi3.Services.Ai.Dsh;

/// <summary>
/// ACP session/update 语义更新（dsh → 客户端流式通知）的强类型视图。
/// 【ZXAI】实测样例：agent_message_chunk{content:{type:text,text}} / usage_update{used,size}；
/// 工具生命周期为 tool_call / tool_call_update（title/kind/status/rawInput/rawOutput）。
/// Raw 保留原文兜底——未知 update 类型不丢（升级兼容）。
/// </summary>
public sealed class DshAcpUpdate
{
    /// <summary>更新类型：agent_message_chunk / agent_thought_chunk / tool_call / tool_call_update / usage_update / plan / ...（未知类型原样保留）。</summary>
    public string Kind { get; init; } = "";

    public string SessionId { get; init; } = "";

    // ---- 文本类 ----
    /// <summary>增量文本（message_chunk / thought_chunk）。</summary>
    public string? Text { get; init; }

    // ---- 工具类 ----
    public string? ToolCallId { get; init; }
    public string? ToolTitle { get; init; }
    /// <summary>工具类别（ACP kind：read/edit/execute/search/other...）。</summary>
    public string? ToolKind { get; init; }
    /// <summary>状态（pending/in_progress/completed/failed）。</summary>
    public string? ToolStatus { get; init; }
    public string? RawInput { get; init; }
    public string? RawOutput { get; init; }

    // ---- 用量类 ----
    public long? UsedTokens { get; init; }
    public long? ContextSize { get; init; }

    /// <summary>原文（全字段，兜底）。</summary>
    public JsonObject Raw { get; init; } = new();

    internal static DshAcpUpdate? Parse(JsonObject? prms)
    {
        if (prms is null) return null;
        var update = prms["update"] as JsonObject ?? prms;

        var kind = update["sessionUpdate"]?.GetValue<string>() ?? "";
        var sessionId = prms["sessionId"]?.GetValue<string>() ?? "";

        string? text = null;
        var content = update["content"];
        if (content is JsonObject co)
            text = co["text"]?.GetValue<string>();
        else if (content is JsonArray ca && ca.Count > 0)
            text = ca[0]?["text"]?.GetValue<string>();

        return new DshAcpUpdate
        {
            Kind = kind,
            SessionId = sessionId,
            Text = text,
            ToolCallId = update["toolCallId"]?.GetValue<string>(),
            ToolTitle = update["title"]?.GetValue<string>(),
            ToolKind = update["kind"]?.GetValue<string>(),
            ToolStatus = update["status"]?.GetValue<string>(),
            RawInput = update["rawInput"]?.ToJsonString(),
            RawOutput = update["rawOutput"]?.ToJsonString(),
            UsedTokens = TryLong(update["used"]),
            ContextSize = TryLong(update["size"]),
            Raw = update,
        };
    }

    private static long? TryLong(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<long>(); }
        catch { return null; }
    }
}
