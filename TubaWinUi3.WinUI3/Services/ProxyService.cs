namespace TubaWinUi3.Services;

/// <summary>共享 HTTP 连接池；沿用系统默认网络设置，不读取应用自定义代理或凭据。</summary>
public static class ProxyService
{
    private static readonly HttpClientHandler SharedHandler = CreateHandler();

    // 保持 UseProxy/Proxy 的默认值，由 .NET 按系统设置解析，不强制禁用系统代理。
    internal static HttpClientHandler CreateHandler() => new();

    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        // disposeHandler: false —— 调用方 using 释放 client 时不会连带释放共享连接池
        var client = new HttpClient(SharedHandler, disposeHandler: false);

        client.Timeout = timeout ?? TimeSpan.FromSeconds(30);
        return client;
    }

    public static HttpClient CreateClientWithHeaders(TimeSpan? timeout = null, params (string name, string value)[] headers)
    {
        var client = CreateClient(timeout);
        foreach (var (name, value) in headers)
        {
            if (!client.DefaultRequestHeaders.Contains(name))
                client.DefaultRequestHeaders.Add(name, value);
        }
        return client;
    }

}
