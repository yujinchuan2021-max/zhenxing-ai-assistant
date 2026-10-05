namespace TubaWinUi3.Services.ToolFlows;

/// <summary>客户端工具流、执行结果与下载计数共用的官方接收入口。</summary>
public static class ToolFlowUploadService
{
    // 前缀由官网反向代理剥离，服务端处理 /v1/toolflows、/v1/.../events 与 /v1/metrics。
    // 旧设置中的自填地址不再决定上传目的地。单纯创建 Uri 不访问网络。
    public static Uri OfficialEndpoint { get; } = new("https://zhenxingai.com/api/toolflows/");
}
