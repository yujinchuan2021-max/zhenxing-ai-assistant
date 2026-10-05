using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Services.Ai;

/// <summary>
/// Agent 工具循环护栏（会话级）：
/// - 重复调用检测：同一会话中「工具 + 参数」完全相同的第二次调用直接拦截、不真正执行，
///   阻止模型对同一操作反复调用（反复 launch_tool 同一工具 / web_search 同一关键词）空转烧轮次。
/// - 无进展检测：连续 N 轮纯工具调用（无用户可见文本）视为疑似死循环，
///   达到阈值后由内置引擎 AgentRuntime 直接终止工具循环。
/// - web_search 技能拦截计数：被拦第二次起直接强硬终止，防弱模型反复尝试同一被禁工具。
/// - 空参数调用（查询类，如实时温度）豁免去重——允许重复取最新值。
/// - 状态按异步工具上下文中的 AgentSession 分开保存，新会话从空状态开始。
/// - 没有会话上下文的旧调用和纯测试使用独立兼容状态，不混入任意会话。
/// 线程安全：AI 工具调用与流式请求可能并发，每个会话状态的访问分别加锁。
/// </summary>
internal static class AgentToolLoopGuard
{
    /// <summary>连续纯工具轮阈值：达到该轮数仍未产出用户可见文本，注入终止指令。
    /// 10 轮（原 6）：查价/盘点类任务天然需要多轮检索，6 太激进会误杀正常长查询。</summary>
    internal const int NoProgressThreshold = 10;

    /// <summary>工具结果最大长度（字符）：超长结果截断保留开头，控制上下文体积
    /// （由内置引擎 AgentRuntime 使用）。</summary>
    internal const int MaxToolResultChars = 6000;

    private sealed class GuardState
    {
        internal readonly object Sync = new();
        internal readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        internal int ConsecutiveToolRounds;
        internal int WebSearchBlocked;
    }

    private static readonly ConditionalWeakTable<AgentSession, GuardState> SessionStates = new();
    private static readonly GuardState FallbackState = new();
    private static GuardState CurrentState => AgentToolContext.Current is { } session
        ? SessionStates.GetValue(session, static _ => new GuardState())
        : FallbackState;

    /// <summary>
    /// 登记一次工具调用；返回 true 表示重复（应拦截），false 表示首次（可正常执行）。
    /// 签名 = toolName + '\u0000' + 规范化参数（递归按键排序序列化）。
    /// </summary>
    public static bool IsDuplicate(string toolName, string normalizedArgs)
    {
        var state = CurrentState;
        lock (state.Sync)
            return !state.Seen.Add(toolName + "\u0000" + normalizedArgs);
    }

    /// <summary>
    /// 一轮流式请求结束后的进展上报：
    /// 模型产出过用户可见文本（TextDelta）→ 有进展，计数清零；
    /// 纯工具轮（无文本但有工具调用）→ 无进展，计数递增。
    /// </summary>
    public static void ReportRound(bool hadUserText, bool hadToolCalls)
    {
        var state = CurrentState;
        lock (state.Sync)
            state.ConsecutiveToolRounds = !hadUserText && hadToolCalls ? state.ConsecutiveToolRounds + 1 : 0;
    }

    /// <summary>当前连续无进展（纯工具）轮数。</summary>
    public static int ConsecutiveToolRounds
    {
        get
        {
            var state = CurrentState;
            lock (state.Sync) return state.ConsecutiveToolRounds;
        }
    }

    /// <summary>是否应注入终止指令（连续纯工具轮达到阈值，疑似死循环）。</summary>
    public static bool ShouldInjectStopDirective => ConsecutiveToolRounds >= NoProgressThreshold;

    /// <summary>登记一次 web_search 技能拦截，返回包含本次在内的累计拦截次数。</summary>
    public static int RegisterWebSearchBlocked()
    {
        var state = CurrentState;
        lock (state.Sync)
            return ++state.WebSearchBlocked;
    }

    /// <summary>用户发来新消息：仅重置无进展计数。
    /// 关键：上一次任务触达阈值后计数会停在 >= 阈值——若不重置，
    /// 用户重发/说"继续"时首轮就命中终止判定，表现为"永远回复错误、重试也没用"。
    /// 重复调用登记（Seen）保留，去重防护继续有效。</summary>
    public static void OnUserMessage()
    {
        var state = CurrentState;
        lock (state.Sync) state.ConsecutiveToolRounds = 0;
    }

    /// <summary>只清空当前异步会话上下文的登记，不影响其他后台会话。</summary>
    public static void Reset()
    {
        var state = CurrentState;
        lock (state.Sync)
        {
            state.Seen.Clear();
            state.ConsecutiveToolRounds = 0;
            state.WebSearchBlocked = 0;
        }
    }

    /// <summary>
    /// 参数规范化：对象递归按键排序序列化（{ "b":1,"a":2 } 与 { "a":2,"b":1 } 视为相同签名）。
    /// 空对象/非对象/非法 JSON 返回 null，表示不做去重（查询类工具允许重复取最新值）。
    /// 内置引擎 AgentRuntime 使用此规范化结果登记调用签名。
    /// </summary>
    public static string? NormalizeArgs(string? jsonArgs)
    {
        if (string.IsNullOrWhiteSpace(jsonArgs)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonArgs);
            return NormalizeArgsCore(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeArgsCore(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.EnumerateObject().Any())
            return null;

        var sb = new StringBuilder();
        AppendNormalized(el, sb);
        return sb.ToString();
    }

    private static void AppendNormalized(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
            {
                sb.Append('{');
                var first = true;
                foreach (var prop in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(prop.Name)).Append(':');
                    AppendNormalized(prop.Value, sb);
                }
                sb.Append('}');
                break;
            }
            case JsonValueKind.Array:
            {
                sb.Append('[');
                var first = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendNormalized(item, sb);
                }
                sb.Append(']');
                break;
            }
            default:
                sb.Append(el.GetRawText()); // 字符串/数字/布尔/null 原样（含引号，保真）
                break;
        }
    }
}
