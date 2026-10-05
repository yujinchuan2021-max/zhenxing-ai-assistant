using TubaWinUi3.Services.Ai;
using TubaWinUi3.Services.Ai.Dsh;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【A07 返修】凭据独立解析与配置切换检测（Codex 第三次复核）：
///  · 所选 provider 的 Key 缺失时【绝不】跨供应商回退（不会把 DeepSeek Key 发到别的端点）；
///  · 两个不同 dummy Key 各自进入各自的子进程 env 槽（含真实子进程 env 转储断言）；
///  · 配置指纹切换检测（纯函数）与私有运行时可用性（不依赖 PATH 的 dsh）。
/// 全部使用 dummy Key，不读取/不使用任何真实 Key。
/// </summary>
public class AgentEngineCredentialTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zxai-cred-" + Guid.NewGuid().ToString("N")[..8]);

    public AgentEngineCredentialTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        AgentEngine.CredentialsOverrideForTest = null;
        AgentEngine.RuntimeResolverOverrideForTest = null;
        AgentEngine.ResetDshProbeCacheForTest();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void SelectedProviderKeyMissing_NoCrossProviderFallback()
    {
        // 场景：所选=自定义 provider、其 Key 为空（DeepSeek 另有 Key 也不得被借用）
        AgentEngine.CredentialsOverrideForTest = () => ("custom", "https://intra.example/v1", "m1", "");
        var (pid, ep, model, key) = AgentEngine.ReadSelectedCredentials();

        Assert.Equal("custom", pid);
        Assert.Equal("", key);   // 【核心】不跨供应商回退 Key
        Assert.Equal("https://intra.example/v1", ep);
    }

    [Fact]
    public void TwoDifferentDummyKeys_LandInOwnEnvSlots_NeverCross()
    {
        var ds = DshLaunchConfig.Build("deepseek", "deepseek-flash", "https://api.deepseek.com", "sk-dummy-A", _dir);
        var cs = DshLaunchConfig.Build("custom", "m1", "https://intra.example/v1", "sk-dummy-B", _dir);

        Assert.Equal("sk-dummy-A", ds.Env["DEEPSEEK_API_KEY"]);
        Assert.False(ds.Env.ContainsKey("ZXAI_DSH_API_KEY"));
        Assert.Equal("sk-dummy-B", cs.Env["ZXAI_DSH_API_KEY"]);
        Assert.False(cs.Env.ContainsKey("DEEPSEEK_API_KEY"));   // 【核心】互不注入
        Assert.DoesNotContain("sk-dummy-A", File.ReadAllText(ds.PatchPath!));
        Assert.DoesNotContain("sk-dummy-B", File.ReadAllText(cs.PatchPath!));
    }

    [Fact]
    public void DshLaunchConfig_EmptyEndpoint_DoesNotIntroduceDefaultAddress()
    {
        // 【R2】假配置：自定义 provider 只填了 Key、地址为空。
        // dsh 启动配置只按原样携带空地址，绝不自行换成内置默认地址；Key 只进 child env、不落 patch。
        var launch = DshLaunchConfig.Build("custom", "m1", "", "sk-dummy-EMPTY", _dir);

        Assert.Equal("", launch.Endpoint);
        var patch = File.ReadAllText(launch.PatchPath!);
        Assert.DoesNotContain("tubawinui3.cn", patch);
        Assert.Contains("baseURL: \"\"", patch);
        Assert.Equal("sk-dummy-EMPTY", launch.Env["ZXAI_DSH_API_KEY"]);
        Assert.DoesNotContain("sk-dummy-EMPTY", patch);
    }

    [Fact]
    public async Task SubprocessEnv_Dump_ContainsOwnDummyKeyOnly()
    {
        // 真正启动子进程验证凭据槽。脚本只输出本测试注入的 dummy Key，
        // 另一个供应商槽仅记存在标记，不转储继承环境或其中的值。
        var outFile = Path.Combine(_dir, "env-dump.txt");
        var cmdFile = Path.Combine(_dir, "dump-env.cmd");
        // cmd 按系统代码页读取批处理；ASCII 的 %~dp0 相对路径避免中文 TEMP 被误解码。
        File.WriteAllText(cmdFile,
            "@echo off\r\n" +
            "echo DEEPSEEK_API_KEY=%DEEPSEEK_API_KEY% > \"%~dp0env-dump.txt\"\r\n" +
            "if defined ZXAI_DSH_API_KEY echo UNEXPECTED:ZXAI_DSH_API_KEY >> \"%~dp0env-dump.txt\"\r\n",
            System.Text.Encoding.ASCII);

        var launch = DshLaunchConfig.Build("deepseek", "deepseek-flash", "https://api.deepseek.com", "sk-dummy-SUB", _dir);
        var stderr = new List<string>();
        await using (var client = DshAcpClient.Start(launch, _dir, cmdFile))
        {
            client.StderrLine += line => { lock (stderr) stderr.Add(line); };
            // cmd 执行完即退出；轮询等待转储文件出现
            for (var i = 0; i < 50 && !File.Exists(outFile) && client.IsRunning; i++)
                await Task.Delay(100);
        }
        Assert.True(File.Exists(outFile), "子进程未产出测试凭据槽转储：" + string.Join(" | ", stderr));
        var dump = File.ReadAllText(outFile);
        Assert.Contains("sk-dummy-SUB", dump);                   // 自己的 dummy Key 已注入
        Assert.DoesNotContain("ZXAI_DSH_API_KEY", dump);         // 不注入别的供应商槽
    }

    [Fact]
    public void NeedsSessionRebuild_OnlyWhenFingerprintDiffers()
    {
        var fp1 = DshLaunchConfig.ComputeFingerprint("deepseek", "deepseek-flash", "https://api.deepseek.com", "sk-x");
        var fpModel = DshLaunchConfig.ComputeFingerprint("deepseek", "deepseek-v4-pro", "https://api.deepseek.com", "sk-x");
        var fpKey = DshLaunchConfig.ComputeFingerprint("deepseek", "deepseek-flash", "https://api.deepseek.com", "sk-y");

        Assert.False(AgentEngine.NeedsSessionRebuild(fp1, fp1));       // 未变化
        Assert.True(AgentEngine.NeedsSessionRebuild(fp1, fpModel));    // 模型变化 → 重建
        Assert.True(AgentEngine.NeedsSessionRebuild(fp1, fpKey));      // Key 变化 → 重建
        Assert.False(AgentEngine.NeedsSessionRebuild(null, fpModel));  // 无指纹（非 dsh/旧路径）不触发
    }

    [Fact]
    public void CaptureConnectionTestConfig_SnapshotsRequestAndFingerprintTogether()
    {
        var calls = 0;
        AgentEngine.CredentialsOverrideForTest = () => ++calls == 1
            ? ("custom-a", "https://a.example/v1", "selected-model", "sk-dummy-a")
            : ("custom-b", "https://b.example/v1", "other-model", "sk-dummy-b");

        var captured = AgentEngine.CaptureConnectionTestConfig();

        Assert.Equal(1, calls);
        Assert.Equal("https://a.example/v1", captured.Endpoint);
        Assert.Equal("selected-model", captured.Model);
        Assert.Equal("sk-dummy-a", captured.Key);
        Assert.Equal(DshLaunchConfig.ComputeFingerprint("custom-a", captured.Model, captured.Endpoint, captured.Key), captured.Fingerprint);
        Assert.NotEqual(AgentEngine.CurrentLaunchFingerprint(), captured.Fingerprint);
        Assert.Equal("selected-model", captured.Model);
    }

    [Fact]
    public void IsDshAvailable_True_WithStandaloneRuntime_WithoutPathDsh()
    {
        // 【A16 返修】可用性与启动共用 Resolver：私有 runtime 分支独立于 PATH 的 dsh。
        // 【R3 修复】测试 Node 走统一依赖探测（TUBA_TEST_NODE → PATH → 应用运行时）：
        // 旧实现用 DshRuntimeResolver.Resolve().NodeExe——该 Resolver 需 Node+dsh 完整包才返回
        // 非空，CI 只有 setup-node（PATH 有 node、无 dsh）时整条测试被动态跳过（TRX 记 Failed）。
        // 缺依赖按门禁语义失败而不是跳过（FakeAcpDependencies 内部即 Assert.Fail）。
        var node = FakeAcpDependencies.RequireNode();

        AgentEngine.ResetDshProbeCacheForTest();
        AgentEngine.RuntimeResolverOverrideForTest = () => new DshRuntimeResolver.Resolved(
            DshRuntimeResolver.Source.AppRuntime, node, Path.Combine(_dir, "fake-bin.js"), "test");

        Assert.True(AgentEngine.IsDshAvailable());   // 只走独立运行时探测，不要求 PATH 有 dsh
    }
}
