using Microsoft.Extensions.AI;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

/// <summary>
/// AgentRuntime 历史裁剪与工具配对兜底测试：
/// 块粒度丢弃不拆散 assistant(tool_calls)+tool 配对；孤儿 tool 丢弃；缺失响应补桩。
/// （网关 400 "tool must be response to preceding tool_calls" 的回归防线）
/// </summary>
public class AgentRuntimeHistoryTests
{
    private static ChatMessage Sys(string t) => new(ChatRole.System, t);
    private static ChatMessage User(string t) => new(ChatRole.User, t);
    private static ChatMessage Asst(string t) => new(ChatRole.Assistant, t);

    private static ChatMessage AsstCalls(params (string Id, string Name)[] calls)
    {
        var contents = new List<AIContent>();
        foreach (var c in calls)
            contents.Add(new FunctionCallContent(c.Id, c.Name, new Dictionary<string, object?>()));
        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static ChatMessage ToolResult(string id, string result)
        => new(ChatRole.Tool, [new FunctionResultContent(id, result)]);

    [Fact]
    public void Sanitize_OrphanTool_Dropped()
    {
        var h = new List<ChatMessage>
        {
            Sys("s"), User("u"),
            ToolResult("orphan1", "no parent"),          // 孤儿：前面没有 assistant(tool_calls)
            Asst("done"),
        };
        AgentRuntime.SanitizeToolPairs(h);
        Assert.Equal(3, h.Count);
        Assert.DoesNotContain(h, m => m.Role == ChatRole.Tool);
    }

    [Fact]
    public void Sanitize_MissingResponse_Filled()
    {
        var h = new List<ChatMessage>
        {
            Sys("s"), User("u"),
            AsstCalls(("c1", "a"), ("c2", "b")),
            ToolResult("c1", "r1"),                       // c2 的响应缺失
            Asst("done"),
        };
        AgentRuntime.SanitizeToolPairs(h);
        var tools = h.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Equal(2, tools.Count);                    // c1 原有 + c2 补桩
        var ids = tools.Select(t => t.Contents.OfType<FunctionResultContent>().First().CallId).ToList();
        Assert.Contains("c1", ids);
        Assert.Contains("c2", ids);
    }

    [Fact]
    public void Sanitize_NormalPairs_Untouched()
    {
        var h = new List<ChatMessage>
        {
            Sys("s"), User("u"),
            AsstCalls(("c1", "a")),
            ToolResult("c1", "r1"),
            Asst("done"),
        };
        var before = h.Count;
        AgentRuntime.SanitizeToolPairs(h);
        Assert.Equal(before, h.Count);
    }

    [Fact]
    public void TrimHistory_OverBudget_DropsWholeBlocks_NoOrphans()
    {
        // 构造超预算历史：大块工具轮次 + 尾部最新 user
        var h = new List<ChatMessage> { Sys("s"), User("早期需求") };
        for (var i = 0; i < 40; i++)
        {
            h.Add(AsstCalls(($"c{i}", $"tool{i}")));
            h.Add(ToolResult($"c{i}", new string('x', 3000)));  // 每块 ~3KB
        }
        h.Add(User("最新需求"));

        AgentRuntime.TrimHistory(h);

        // 结果：无孤儿 tool——每个 tool 前面必有其 assistant(tool_calls)
        for (var i = 0; i < h.Count; i++)
        {
            if (h[i].Role != ChatRole.Tool) continue;
            Assert.True(i > 0, "tool 出现在首条");
            var prev = h[i - 1];
            var ok = prev.Role == ChatRole.Tool ||
                     (prev.Role == ChatRole.Assistant && prev.Contents.OfType<FunctionCallContent>().Any());
            Assert.True(ok, $"位置 {i} 的 tool 是孤儿");
        }
        // 首条 system 与最后一条 user 保留
        Assert.Equal(ChatRole.System, h[0].Role);
        Assert.Contains(h, m => m.Role == ChatRole.User);
    }

    [Fact]
    public void TrimHistory_UnderBudget_NoChange()
    {
        var h = new List<ChatMessage> { Sys("s"), User("u"), Asst("a") };
        var before = h.Count;
        AgentRuntime.TrimHistory(h);
        Assert.Equal(before, h.Count);
    }
}
