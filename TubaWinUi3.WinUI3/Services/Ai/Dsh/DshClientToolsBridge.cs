using System.Text.Json.Nodes;

namespace TubaWinUi3.Services.Ai.Dsh;

internal static class DshClientToolsBridge
{
    internal const string Prompt = "本客户端有可查询的工具目录和枕星AI资讯。需要现有工具时先调用 mcp__zhenxing-client__list_client_tools；" +
        "需要命令行用法时调用 mcp__zhenxing-client__get_cli_tool_usage；涉及枕星AI资讯时先调用 mcp__zhenxing-client__read_ai_news，" +
        "不要先要求用户粘贴或截图。资讯返回摘要和来源链接，不是完整文章；按返回的发布时间和缓存标记说明时效，查询失败时如实说明。";

    internal static JsonArray Build(string appExecutable)
    {
        if (!Path.IsPathFullyQualified(appExecutable)) throw new ArgumentException("Client bridge requires an absolute apphost path.");
        // ACP stdio transport has no 'type' field; env is a name/value array.
        // Inherit the process isolation environment; no model key is added here.
        return new JsonArray(new JsonObject
        {
            ["name"] = "zhenxing-client", ["command"] = appExecutable,
            ["args"] = new JsonArray("--mcp-stdio", "--mcp-readonly"), ["env"] = new JsonArray(),
        });
    }

    internal static JsonArray BuildDefault() => Build(Path.Combine(AppContext.BaseDirectory, "TubaWinUi3.exe"));
}
