using Xunit;
using System.Text;
using System.Text.Json.Nodes;
using TubaWinUi3.Services.Ai.Dsh;

namespace TubaWinUi3.Tests;

/// <summary>
/// 离线假 ACP 服务依赖解析（Node / fake-dsh.cjs）。
///
/// 【门禁语义】离线 fake ACP 测试是**必要门禁**：缺少依赖必须 **失败（Assert.Fail）**，
/// 绝不能静默 Skip —— 静默跳过会让 CI「绿」得毫无意义。
///
/// Node 探测顺序（与 CI 的 actions/setup-node + TUBA_TEST_NODE 对齐）：
///   ① 环境变量 TUBA_TEST_NODE（显式指定 = 权威；指向不存在/不可执行的路径 → 直接失败，不再回退）
///   ② PATH 上的 node（CI 由 actions/setup-node 提供，固定 Node 22）
///   ③ 应用私有运行时（DshRuntimeResolver：app runtime/node.exe，其次开发期 Hermes 目录）
///   ④ 都没有 → 失败，并列出每个候选与失败原因
///
/// 公开此类型供其它 fake 测试复用（如 DshSessionFakeServerTests），避免各自再写会静默 Skip 的探测。
/// </summary>
public static class FakeAcpDependencies
{
    public const string NodeExeEnvVar = "TUBA_TEST_NODE";

    /// <summary>fake-dsh.cjs 绝对路径（测试程序集输出目录 → 源码目录上溯兜底）；找不到即失败。</summary>
    public static string RequireFakeJs()
    {
        var p = FindFakeJs();
        if (p is null)
            Assert.Fail("缺少测试依赖 fake-dsh.cjs：TestAssets 未随测试程序集输出（检查 TubaWinUi3.Tests.csproj 的 " +
                        "TestAssets\\**\\* CopyToOutputDirectory 项），在上溯目录中也未找到。离线 fake ACP 测试是不可跳过的门禁。");
        return p!;
    }

    /// <summary>可用的 node.exe 绝对路径；按 TUBA_TEST_NODE → PATH → 应用运行时 顺序探测，全不可用即失败。</summary>
    public static string RequireNode()
    {
        var tried = new List<string>();

        var overridden = Environment.GetEnvironmentVariable(NodeExeEnvVar);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            // 显式指定即权威：坏了就报错，不回退（避免"以为在测指定 Node，其实测了别的"）
            if (!File.Exists(overridden))
            {
                tried.Add($"① {NodeExeEnvVar} = \"{overridden}\" → 文件不存在");
                Fail(tried);
            }
            if (!TryNodeVersion(overridden!, out var v, out var why))
            {
                tried.Add($"① {NodeExeEnvVar} = \"{overridden}\" → 无法执行：{why}");
                Fail(tried);
            }
            return overridden!;
        }
        tried.Add($"① {NodeExeEnvVar}：未设置");

        // ② PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var cand = Path.Combine(dir.Trim(), "node.exe");
            if (!File.Exists(cand)) continue;
            if (TryNodeVersion(cand, out var v2, out var why2)) return cand;
            tried.Add($"② PATH 候选 \"{cand}\" → 无法执行：{why2}");
        }
        tried.Add("② PATH 上未找到可执行的 node.exe");

        // ③ 应用私有运行时（DshRuntimeResolver：app runtime/node.exe，其次开发期 Hermes 目录）
        var resolved = DshRuntimeResolver.Resolve();
        if (resolved.NodeExe is { } rn)
        {
            if (TryNodeVersion(rn, out var v3, out var why3)) return rn;
            tried.Add($"③ 运行时解析（{resolved.Origin}）\"{rn}\" → 无法执行：{why3}");
        }
        else
        {
            tried.Add($"③ 运行时解析（{resolved.Origin}）：未解析到 node（应用目录下无 runtime\\node.exe，Hermes 目录也不可用）");
        }

        Fail(tried);
        return null!;   // 不可达（Fail 抛异常）
    }

    private static void Fail(List<string> tried)
    {
        Assert.Fail(
            "缺少测试依赖 node：离线 fake ACP 测试是必要门禁，缺依赖必须失败而不是跳过。\n" +
            "探测顺序：① TUBA_TEST_NODE → ② PATH → ③ 应用运行时/Hermes。\n" +
            string.Join("\n", tried) + "\n" +
            "修复：设 TUBA_TEST_NODE=<node.exe 绝对路径>，把 node 放进 PATH，或安装应用私有运行时；CI 由 actions/setup-node（Node 22）提供。");
    }

    private static string? FindFakeJs()
    {
        var here = AppContext.BaseDirectory;
        var p = Path.Combine(here, "TestAssets", "fake-dsh.cjs");
        if (File.Exists(p)) return p;
        var dir = new DirectoryInfo(here);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "TestAssets", "fake-dsh.cjs");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>`<node> --version` 探活（有界等待，避免坏二进制挂死测试）。</summary>
    private static bool TryNodeVersion(string nodeExe, out string version, out string why)
    {
        version = "";
        why = "";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(nodeExe, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) { why = "Process.Start 返回 null"; return false; }
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            if (!proc.WaitForExit(15_000)) { why = "15s 内未退出"; try { proc.Kill(); } catch { } return false; }
            if (proc.ExitCode != 0) { why = $"退出码 {proc.ExitCode}"; return false; }
            if (!stdout.StartsWith("v", StringComparison.Ordinal)) { why = $"输出异常：{stdout}"; return false; }
            version = stdout;
            return true;
        }
        catch (Exception ex) { why = ex.Message; return false; }
    }
}

/// <summary>
/// 【Q01】离线假 ACP 服务测试：模拟 dsh ACP 协议面（TestAssets/fake-dsh.cjs），
/// 默认执行、无网络、无真实 Key、无真实 dsh —— 覆盖正常/取消/恢复/权限/进程退出分支。
/// 依赖（node + fake-dsh.cjs）缺失时 **Assert.Fail**（见 FakeAcpDependencies）：
/// 离线 fake ACP 测试是必要门禁，不提供任何 Skip 通道。
/// </summary>
[Trait("Category", "Offline")]
public class DshAcpFakeServerTests : IDisposable
{
    private readonly string _dir;

    public DshAcpFakeServerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zxai-fake-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ───────────────────────── 工具 ─────────────────────────

    /// <summary>生成 fake-dsh.cmd：设置环境变量后以绝对 node 路径启动 fake-dsh.cjs。</summary>
    private static string MakeFakeCmd(string dir)
    {
        // 【假 ACP 中文路径修复】shim 只包含 ASCII：node 与脚本路径经子进程环境变量传入
        // （TUBA_FAKE_NODE / TUBA_FAKE_JS）。File.WriteAllText 默认 UTF-8 的 .cmd 在
        // CreateNoWindow 隐藏进程下按系统代码页解析——内嵌非 ASCII 路径会 MODULE_NOT_FOUND
        // （已用小复现确认，见 review-patches/fake-shim-unicode-fix.md）。
        var cmd = Path.Combine(dir, "fake-dsh.cmd");
        File.WriteAllText(cmd, "@echo off\r\n\"%TUBA_FAKE_NODE%\" \"%TUBA_FAKE_JS%\" %*\r\n", Encoding.ASCII);
        return cmd;
    }

    /// <summary>把一个指向 fake ACP 的客户端起起来（Key 为无效占位串，绝不读真实 Key）。
    /// 【中文路径修复】node/脚本路径经子进程环境变量传入；fakeJsOverride 用于"脚本位于含中文目录"回归。</summary>
    private DshAcpClient StartFake(string? dirOverride = null, IReadOnlyDictionary<string, string>? env = null, string? fakeJsOverride = null)
    {
        var js = fakeJsOverride ?? FakeAcpDependencies.RequireFakeJs();
        var node = FakeAcpDependencies.RequireNode();
        var d = dirOverride ?? _dir;
        var childEnv = env is null ? new Dictionary<string, string>() : new Dictionary<string, string>(env);
        childEnv["DEEPSEEK_API_KEY"] = "fake-key";
        childEnv["TUBA_FAKE_NODE"] = node;
        childEnv["TUBA_FAKE_JS"] = js;
        return DshAcpClient.Start(new DshLaunchConfig
        {
            Env = childEnv,
            DataDir = d,
        }, d, MakeFakeCmd(d));
    }

    /// <summary>轮询等待文件出现并读取（fake 在应答 prompt 前写盘，这里只做小保险）。</summary>
    private static async Task<string> WaitForFile(string path, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    var text = await sr.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                catch (IOException) { }
            }
            await Task.Delay(50);
        }
        Assert.Fail($"等待文件超时（{timeoutMs}ms）：{path}");
        return "";
    }

    // ───────────────────────── 正常链路 ─────────────────────────

    [Fact]
    public async Task Fake_Handshake_Session_Prompt_Flow()
    {
        var updates = new List<DshAcpUpdate>();
        await using var client = StartFake();
        client.SessionUpdate += u => { lock (updates) updates.Add(u); };

        var init = await client.InitializeAsync();
        Assert.True(init["agentInfo"] is not null);

        var sid = await client.NewSessionAsync(_dir);
        Assert.Equal("fake-sess-1", sid);

        var stop = await client.PromptAsync(sid, "随便说点什么");
        Assert.Equal("end_turn", stop);

        var text = string.Concat(updates.Where(u => u.Kind == "agent_message_chunk").Select(u => u.Text));
        Assert.Contains("你好", text);
    }

    [Fact]
    public async Task Fake_Handshake_NonAsciiPaths_HiddenProcess_Works()
    {
        // 【假 ACP 中文路径回归】fake 脚本与工作目录都位于含中文的路径，隐藏进程（CreateNoWindow）仍能握手：
        // 验证 shim 纯 ASCII + 环境变量传路径的方案（不依赖系统代码页）。
        var unicodeDir = Path.Combine(Path.GetTempPath(), "zxai-假ACP-中文路径-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(unicodeDir);
        try
        {
            var fakeJsDst = Path.Combine(unicodeDir, "假-fake-dsh.cjs");
            File.Copy(FakeAcpDependencies.RequireFakeJs(), fakeJsDst, overwrite: true);

            await using var client = StartFake(dirOverride: unicodeDir, fakeJsOverride: fakeJsDst);
            var init = await client.InitializeAsync();
            Assert.True(init["agentInfo"] is not null);
            var sid = await client.NewSessionAsync(unicodeDir);
            Assert.Equal("fake-sess-1", sid);
        }
        finally
        {
            try { Directory.Delete(unicodeDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Fake_ImageCapability_Reflected_In_Handshake()
    {
        await using var client = StartFake(env: new Dictionary<string, string> { ["FAKE_DSH_IMAGE"] = "1" });
        var init = await client.InitializeAsync();
        Assert.True(init["agentCapabilities"]?["promptCapabilities"]?["image"]?.GetValue<bool>());
    }

    // ───────────────────────── 取消分支 ─────────────────────────

    [Fact]
    public async Task Fake_Cancel_During_Prompt_Returns_Cancelled()
    {
        await using var client = StartFake(env: new Dictionary<string, string> { ["FAKE_DSH_PROMPT_DELAY_MS"] = "150" });

        await client.InitializeAsync();
        var sid = await client.NewSessionAsync(_dir);

        var promptTask = client.PromptAsync(sid, "慢速回复");   // 每 chunk 150ms，及时取消
        await Task.Delay(80);
        await client.CancelAsync(sid);

        var stop = await promptTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cancelled", stop);
    }

    [Fact]
    public async Task Fake_Cancel_During_Handshake_Reclaims_Process()
    {
        // initialize 延迟 5 秒：在握手期内取消 → 进程被回收（不再可用）
        var client = StartFake(env: new Dictionary<string, string> { ["FAKE_DSH_INIT_DELAY_MS"] = "5000" });

        using var cts = new CancellationTokenSource();
        var initTask = client.InitializeAsync(cts.Token);
        await Task.Delay(200);
        cts.Cancel();   // 取消握手

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initTask);
        // 进程应被回收/回收中：此处断言不会卡住后续 Disposal
        await client.DisposeAsync();
    }

    // ───────────────────────── 恢复分支 ─────────────────────────

    [Fact]
    public async Task Fake_Resume_Success_And_Reject()
    {
        // ① 成功 resume
        await using (var ok = StartFake())
        {
            await ok.InitializeAsync();
            var sid = await ok.ResumeSessionAsync("fake-sess-1", _dir);
            Assert.Equal("fake-sess-1", sid);
        }

        // ② 拒绝 resume → 抛错（调用方按设计重建新会话）
        await using (var bad = StartFake(env: new Dictionary<string, string> { ["FAKE_DSH_RESUME_FAIL"] = "1" }))
        {
            await bad.InitializeAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => bad.ResumeSessionAsync("fake-sess-1", _dir));
        }
    }

    // ───────────────────────── 进程退出分支 ─────────────────────────

    /// <summary>
    /// 子进程在 prompt 中途自杀（exit 7）：
    /// ① 在期限内【以异常结束】——超时=静默挂起，必须判失败（不允许把挂起当通过）；
    /// ② 进程确已退出（IsRunning=false）；
    /// ③ 之后**真重建新会话并成功发一条消息**（新进程 + 新 session + 完整往返），
    ///    证明失败面不是死路（不是只断言 IsRunning=false 就算完）。
    /// </summary>
    [Fact]
    public async Task Fake_Exit_During_Prompt_Surfaces_Error_Then_Usable()
    {
        const int deadlineSeconds = 15;

        await using (var client = StartFake(env: new Dictionary<string, string> { ["FAKE_DSH_EXIT_MID_PROMPT"] = "1" }))
        {
            await client.InitializeAsync();
            var sid = await client.NewSessionAsync(_dir);

            var promptTask = client.PromptAsync(sid, "触发退出");
            var winner = await Task.WhenAny(promptTask, Task.Delay(TimeSpan.FromSeconds(deadlineSeconds)));

            Assert.True(winner == promptTask,
                $"子进程退出后 prompt 必须在 {deadlineSeconds}s 内结束（报错或返回）；超时=静默挂起，判失败");
            await Assert.ThrowsAnyAsync<Exception>(() => promptTask);

            Assert.False(client.IsRunning, "子进程退出后 IsRunning 应为 false");
        }

        // ── 真重建：新客户端（新进程）→ initialize → 新会话 → 成功发一条消息 ──
        var updates = new List<DshAcpUpdate>();
        await using var rebuilt = StartFake();
        rebuilt.SessionUpdate += u => { lock (updates) updates.Add(u); };

        await rebuilt.InitializeAsync();
        var newSid = await rebuilt.NewSessionAsync(_dir);
        Assert.Equal("fake-sess-1", newSid);

        var stop = await rebuilt.PromptAsync(newSid, "重建后能收到吗").WaitAsync(TimeSpan.FromSeconds(deadlineSeconds));
        Assert.Equal("end_turn", stop);

        var text = string.Concat(updates.Where(u => u.Kind == "agent_message_chunk").Select(u => u.Text));
        Assert.Contains("你好", text);
        Assert.True(rebuilt.IsRunning, "重建后的客户端应仍在运行");
    }

    // ───────────────────────── 权限分支（真实 ACP options 结构） ─────────────────────────

    /// <summary>起一个会发权限请求的 fake，跑完一轮 prompt，返回 permission log（服务端视角的请求/响应记录）。</summary>
    private async Task<JsonNode> RunPermissionRoundTrip(bool denyOnly, List<string> stderr, StringBuilder streamed)
    {
        var logPath = Path.Combine(_dir, "permission-log.json");
        var env = new Dictionary<string, string>
        {
            ["FAKE_DSH_PERMISSION"] = "1",
            ["FAKE_DSH_PERMISSION_LOG"] = logPath,
        };
        if (denyOnly) env["FAKE_DSH_PERMISSION_DENY"] = "1";

        await using var client = StartFake(env: env);
        client.StderrLine += l => { lock (stderr) stderr.Add(l); };
        client.SessionUpdate += u =>
        {
            if (u.Kind == "agent_message_chunk" && u.Text is not null) lock (streamed) streamed.Append(u.Text);
        };

        await client.InitializeAsync();
        var sid = await client.NewSessionAsync(_dir);

        // 服务端在 prompt 期间发 session/request_permission：
        // 客户端应答若不符合 ACP schema（扁平 outcome / 超时 / 不在 options 内）→ fake 以 JSON-RPC error 结束本轮，
        // 这里就会抛异常 —— 即"应答结构错误会被门禁抓住"，不会被静默放行。
        var stop = await client.PromptAsync(sid, "触发权限请求").WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("end_turn", stop);
        Assert.True(client.IsRunning, "权限请求往返后客户端不应崩溃/退出");

        var json = await WaitForFile(logPath);
        return JsonNode.Parse(json)!;
    }

    [Fact]
    public async Task Fake_Permission_Allow_AutoApproves_WithRealOptionsStructure()
    {
        var stderr = new List<string>();
        var streamed = new StringBuilder();
        var log = await RunPermissionRoundTrip(denyOnly: false, stderr, streamed);

        // ① 服务端按 ACP RequestPermissionRequest 真实结构发请求（options[{optionId,name,kind}]）
        Assert.Equal(9001, log["requestId"]!.GetValue<int>());
        Assert.Equal("allow_and_deny", log["offer"]!.GetValue<string>());
        var options = log["options"]!.AsArray();
        Assert.Equal(3, options.Count);
        var kinds = options.Select(o => o!["kind"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "allow_once", "allow_always", "reject_once" }, kinds);
        Assert.All(options, o =>
        {
            Assert.False(string.IsNullOrWhiteSpace(o!["optionId"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(o!["name"]!.GetValue<string>()));
        });

        // ② 客户端应答字段正确（嵌套 outcome；id 原样回显；选中 allow_*）
        Assert.True(log["valid"]!.GetValue<bool>(), "权限应答应通过 ACP schema 校验：" + log["errors"]);
        Assert.Equal("allow_once", log["selectedOptionId"]!.GetValue<string>());
        var received = log["received"]!.AsObject();
        Assert.Equal("2.0", received["jsonrpc"]!.GetValue<string>());
        Assert.Equal(9001, received["id"]!.GetValue<int>());
        Assert.Null(received["error"]);
        var outcome = received["result"]!["outcome"]!.AsObject();
        Assert.Equal("selected", outcome["outcome"]!.GetValue<string>());
        Assert.Equal("allow_once", outcome["optionId"]!.GetValue<string>());

        // ③ 客户端侧处理路径留痕（收到请求 → 按全权限策略自动允许）
        lock (stderr)
        {
            Assert.Contains(stderr, l => l.Contains("session/request_permission"));
            Assert.Contains(stderr, l => l.Contains("自动允许"));
        }
        // ④ 权限往返之后会话仍可用（继续流式输出）
        lock (streamed) Assert.Contains("你好（fake）", streamed.ToString());
    }

    [Fact]
    public async Task Fake_Permission_DenyOnly_FallsBackToFirstOfferedOption()
    {
        var stderr = new List<string>();
        var streamed = new StringBuilder();
        var log = await RunPermissionRoundTrip(denyOnly: true, stderr, streamed);

        // 服务端只提供 reject_*（无 allow 可选）
        Assert.Equal("deny_only", log["offer"]!.GetValue<string>());
        var options = log["options"]!.AsArray();
        Assert.Equal(2, options.Count);
        Assert.All(options, o => Assert.StartsWith("reject", o!["kind"]!.GetValue<string>()));

        // 客户端不得凭空造一个 allow：只能选服务端给出的选项（源码语义=无 allow 时退回首项）
        Assert.True(log["valid"]!.GetValue<bool>(), "权限应答应通过 ACP schema 校验：" + log["errors"]);
        var selected = log["selectedOptionId"]!.GetValue<string>();
        Assert.Equal("reject_once", selected);
        Assert.Contains(options.Select(o => o!["optionId"]!.GetValue<string>()), id => id == selected);

        var outcome = log["received"]!["result"]!["outcome"]!.AsObject();
        Assert.Equal("selected", outcome["outcome"]!.GetValue<string>());
        Assert.Equal("reject_once", outcome["optionId"]!.GetValue<string>());

        // 收到"拒绝"仍不崩：prompt 正常 end_turn，后续文本照常流出
        lock (streamed) Assert.Contains("你好（fake）", streamed.ToString());
        lock (stderr) Assert.Contains(stderr, l => l.Contains("session/request_permission"));
    }

    // ───────────────────────── 门禁自检：缺依赖必须失败而不是跳过 ─────────────────────────

    /// <summary>
    /// 负控：把 TUBA_TEST_NODE 指向不存在的路径 → 依赖解析必须**抛错（失败）**，
    /// 而不是回退到别的 node 或静默 Skip。这条保证"缺依赖=红"而非"缺依赖=绿"。
    /// </summary>
    [Fact]
    public void NodeProbe_BrokenOverride_FailsInsteadOfSkipping()
    {
        var saved = Environment.GetEnvironmentVariable(FakeAcpDependencies.NodeExeEnvVar);
        var bogus = Path.Combine(_dir, "no-such-node.exe");
        try
        {
            Environment.SetEnvironmentVariable(FakeAcpDependencies.NodeExeEnvVar, bogus);

            var ex = Assert.ThrowsAny<Exception>(() => FakeAcpDependencies.RequireNode());

            Assert.Contains(FakeAcpDependencies.NodeExeEnvVar, ex.Message);
            Assert.Contains(bogus, ex.Message);
            Assert.Contains("必要门禁", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeAcpDependencies.NodeExeEnvVar, saved);
        }
    }

    /// <summary>正控：TUBA_TEST_NODE 指向真实 node → 解析成功，且版本输出可读。</summary>
    [Fact]
    public void NodeProbe_RealNode_Resolves()
    {
        var node = FakeAcpDependencies.RequireNode();
        Assert.True(File.Exists(node), $"探测到的 node 必须存在：{node}");
        var js = FakeAcpDependencies.RequireFakeJs();
        Assert.EndsWith("fake-dsh.cjs", js);
    }
}
