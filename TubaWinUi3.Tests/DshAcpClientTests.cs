using Xunit;
using System.Diagnostics;
using System.Text;
using TubaWinUi3.Services.Ai.Dsh;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【ZXAI】【Q01】真实 dsh 集成测试：**默认不执行**（离线优先）。
/// 显式启用：ZXAI_RUN_REAL_DSH_TESTS=1、专用测试 Key 环境变量 ZXAI_REAL_DSH_API_KEY、可用 dsh。
/// 不读取用户的 ai_providers.json；每个用例使用独立 DSH_HOME，避免修改真实会话。
/// 条件不满足时用 Xunit.SkipException 真正 Skip（xUnit 正确计为跳过而非通过）；会消耗极少量 token。
/// </summary>
[Trait("Category", "Integration")]
public class DshAcpClientTests
{
    /// <summary>【Q01】显式 opt-in + 依赖检查：不满足即真 Skip（不再裸 return 伪造通过）。</summary>
    private static string RequireRealEnvironment()
    {
        if (Environment.GetEnvironmentVariable("ZXAI_RUN_REAL_DSH_TESTS") != "1")
            Skip.If(true, "真实 dsh 集成测试需显式启用：设置 ZXAI_RUN_REAL_DSH_TESTS=1");
        var key = Environment.GetEnvironmentVariable("ZXAI_REAL_DSH_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            Skip.If(true, "缺少专用测试 Key 环境变量 ZXAI_REAL_DSH_API_KEY");
        if (!DshAvailable())
            Skip.If(true, "本机 dsh 不可用（PATH/运行时解析均失败）");
        return key!;
    }

    private static bool DshAvailable()
    {
        if (DshRuntimeResolver.Resolve().IsStandalone) return true;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c dsh --version")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [SkippableFact]
    public async Task Acp_FullFlow_Handshake_Session_Prompt()
    {
        var key = RequireRealEnvironment();
        var workspace = Path.Combine(Path.GetTempPath(), "TubaRealDshTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var launch = DshLaunchConfig.Build("deepseek", "deepseek-flash", "https://api.deepseek.com", key, workspace);

        var updates = new List<DshAcpUpdate>();
        await using var client = DshAcpClient.Start(launch, workspace);
        client.SessionUpdate += u => { lock (updates) updates.Add(u); };

        // ① 握手
        var info = await client.InitializeAsync();
        Assert.True(info["agentInfo"] is not null, "initialize 应返回 agentInfo");

        // ② 建会话
        var sid = await client.NewSessionAsync(workspace);
        Assert.False(string.IsNullOrWhiteSpace(sid), "session/new 应返回 sessionId");

        // ③ 一轮 prompt
        var stop = await client.PromptAsync(sid, "回复两个字：你好");
        Assert.Equal("end_turn", stop);

        // ④ 流式 update 里应有 agent_message_chunk 文本
        var texts = updates.Where(u => u.Kind == "agent_message_chunk" && u.Text is not null)
            .Select(u => u.Text!).ToList();
        Assert.True(texts.Count > 0, "应收到 agent_message_chunk 更新");
        Assert.Contains("你好", string.Concat(texts));
    }

    [SkippableFact]
    public async Task DshSession_MultiTurn_KeepsContext()
    {
        var key = RequireRealEnvironment();
        var workspace = Path.Combine(Path.GetTempPath(), "TubaRealDshTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var launch = DshLaunchConfig.Build("deepseek", "deepseek-flash", "https://api.deepseek.com", key, workspace);

        using var session = new DshSession(key, workspace, launch);
        var texts = new StringBuilder();
        var errors = new StringBuilder();
        session.TextChunk += t => { lock (texts) texts.Append(t); };
        session.Error += e => { lock (errors) errors.Append(e); };

        // 第一轮：存信息
        await session.SendAsync("请记住这个数字：42。只回复\"好的\"。");
        Assert.True(errors.Length == 0, $"第一轮不应出错：{errors}");

        // 第二轮：取信息（验证 dsh 侧会话上下文跨轮保持）
        lock (texts) texts.Clear();
        await session.SendAsync("我刚才让你记的数字是几？只回复数字。");
        Assert.True(errors.Length == 0, $"第二轮不应出错：{errors}");
        Assert.Contains("42", texts.ToString());

        Assert.True(session.TotalPromptTokens > 0, "应有用量统计");
    }
}
