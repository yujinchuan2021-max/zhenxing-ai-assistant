using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
namespace TubaWinUi3.Services.Agent;

/// <summary>
/// 创建对接 AiProviderStore 全局 AI 接口、模型与 Key
/// 的 IChatClient。使用官方 OpenAI SDK + M.E.AI 适配层（AsIChatClient），
/// 不再手写 SSE 流式解析与 JSON Schema。
/// </summary>
public static class AgentClientFactory
{
    public static IChatClient CreateClient()
    {
        var (endpoint, model, apiKey) = AiService.GetConfig();
        return CreateClient(endpoint, model, apiKey);
    }

    /// <summary>Use one captured configuration; callers can disable automatic retries for optional work.</summary>
    internal static IChatClient CreateClient(string endpoint, string model, string apiKey, bool noRetry = false)
    {

        if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = "https://" + endpoint;
        }

        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint.TrimEnd('/')) };
        if (noRetry) options.RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0);
        var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), options);

        // 外层包一层思考链回传装饰器：DeepSeek 系思考模型要求 reasoning_content 原样回传
        return new ReasoningEchoChatClient(openAiClient.GetChatClient(model).AsIChatClient());
    }
}
