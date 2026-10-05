using System.Text;
using TubaWinUi3.Services.Agent;
using TubaWinUi3.Services.Ai.Dsh;
using DshSess = TubaWinUi3.Services.Ai.Dsh.DshSession;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【Q01 任务4 / 第二轮复核】DshSession 适配层离线测试：用假 ACP 服务（TestAssets/fake-dsh.cjs）
/// 从 IAgentSession 门面层验收——不经 DshAcpClient 低层断言替代。
/// 覆盖：生命周期（连接/流式文本/用量/完成）、恢复（resume 成功 / 失败自动重建）、
/// 失败结果（图片能力未协商时明确报错且不提交 prompt；关闭后发送被拒）、
/// 【第二轮复核】resume 后模型切换的可见结果与发送阻断（精确唯一匹配、歧义拒绝、前缀不误配）。
/// 全部离线：无网络、无真实 Key（仅无效占位串）、不向任何真实供应商发请求。
/// 依赖（node + fake-dsh.cjs）缺失 = **失败**（FakeAcpDependencies → Assert.Fail），不静默 Skip：
/// 离线 fake 测试是必要门禁（独立 Node 探测链：TUBA_TEST_NODE &gt; CI setup-node 的 PATH &gt;
/// 应用私有运行时/Hermes）。
/// </summary>
[Trait("Category", "Offline")]
public class DshSessionFakeServerTests : IDisposable
{
    private readonly string _dir;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    public DshSessionFakeServerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zxai-dshsess-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ───────────── 工具 ─────────────

    /// <summary>起一个指向假 ACP 的 DshSession（Key 为无效占位串，绝不读真实 Key）。
    /// model = 【A07】本次启动所选模型（resume 后需显式切到它的场景用）；
    /// providerId = 【第二轮复核】所选供应商（同名跨 provider 时消歧用）。</summary>
    private DshSess NewSession(IReadOnlyDictionary<string, string>? env = null, string? model = null,
        string? providerId = null, bool builtinGoalSkillProjected = false)
    {
        // 缺依赖必须失败而不是跳过（不 Skip.If、不 TryXxx 回退）——离线 fake 测试是必要门禁
        var js = FakeAcpDependencies.RequireFakeJs();
        var node = FakeAcpDependencies.RequireNode();

        var cmd = Path.Combine(_dir, "fake-dsh.cmd");
        // 【假 ACP 中文路径修复】shim 只包含 ASCII；路径经子进程环境变量传入（TUBA_FAKE_NODE/TUBA_FAKE_JS）。
        File.WriteAllText(cmd, "@echo off\r\n\"%TUBA_FAKE_NODE%\" \"%TUBA_FAKE_JS%\" %*\r\n", Encoding.ASCII);

        var envCopy = env is null ? new Dictionary<string, string>() : new Dictionary<string, string>(env);
        envCopy.TryAdd("FAKE_DSH_PROMPT_LOG", PromptLogPath);   // 每条 session/prompt 落一行（阻断证据）
        envCopy["TUBA_FAKE_NODE"] = node;
        envCopy["TUBA_FAKE_JS"] = js;

        var launch = new DshLaunchConfig
        {
            DshPathOverride = cmd,
            Env = envCopy,
            DataDir = _dir,
            Model = model,
            ProviderId = providerId ?? "deepseek",   // 不走真实 ProviderStore：显式给应用侧 provider id
            BuiltinGoalSkillProjected = builtinGoalSkillProjected,
        };
        return new DshSess("sk-invalid-placeholder-not-a-real-key", _dir, launch);
    }

    /// <summary>fake 记录的 session/set_config_option 日志路径。</summary>
    private string SetConfigLogPath => Path.Combine(_dir, "set-config-log.json");

    /// <summary>fake 记录的 session/prompt 日志（JSONL，一行一次）。</summary>
    private string PromptLogPath => Path.Combine(_dir, "prompt-log.jsonl");

    /// <summary>fake 实际收到的 prompt 条数——"没有悄悄发出去"必须是可验证的事实，不是自述。</summary>
    private int PromptCount()
        => File.Exists(PromptLogPath) ? File.ReadAllLines(PromptLogPath).Count(l => !string.IsNullOrWhiteSpace(l)) : 0;

    /// <summary>读取 fake 记录的 set_config_option 原文（缺失 → 断言失败）。</summary>
    private System.Text.Json.Nodes.JsonObject ReadSetConfigLog()
    {
        Assert.True(File.Exists(SetConfigLogPath), "fake 未收到 session/set_config_option（本应收到恰好一次）");
        return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SetConfigLogPath))!.AsObject();
    }

    /// <summary>发一轮并收集流式文本（用于"这一轮确实成功了"的用例）。</summary>
    private static async Task<string> SendAndWaitText(DshSess session, string text)
    {
        var sb = new StringBuilder();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnText(string t) { lock (sb) sb.Append(t); }
        void OnDone() => done.TrySetResult();
        session.TextChunk += OnText;
        session.RunCompleted += OnDone;
        try
        {
            await session.SendAsync(text);
            await done.Task.WaitAsync(Wait);
        }
        finally
        {
            session.TextChunk -= OnText;
            session.RunCompleted -= OnDone;
        }
        lock (sb) return sb.ToString();
    }

    /// <summary>发一轮并同时收流式文本 + Error 文案（用于"被阻断/被拒"的用例：
    /// 这类用例的本轮以 RunCompleted 收尾，必须靠"文本为空 + 明确错误"来区分）。</summary>
    private static async Task<(string Text, List<string> Errors)> SendAndCaptureAsync(DshSess session, string text)
    {
        var sb = new StringBuilder();
        var errors = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnText(string t) { lock (sb) sb.Append(t); }
        void OnError(string e) { lock (errors) errors.Add(e); }
        void OnDone() => done.TrySetResult();
        session.TextChunk += OnText;
        session.Error += OnError;
        session.RunCompleted += OnDone;
        try
        {
            await session.SendAsync(text);
            await done.Task.WaitAsync(Wait);
        }
        finally
        {
            session.TextChunk -= OnText;
            session.Error -= OnError;
            session.RunCompleted -= OnDone;
        }
        lock (sb) lock (errors) return (sb.ToString(), errors.ToList());
    }

    // ───────────── 生命周期 ─────────────

    [Fact]
    public async Task Session_SendStreamsText_UsageAndCompletion()
    {
        using var session = NewSession();

        var text = await SendAndWaitText(session, "你好");

        Assert.Contains("你好（fake）", text);
        Assert.True(session.TotalPromptTokens > 0, "usage_update 应计入用量");
        Assert.NotEqual("新会话", session.Title);   // 标题按首条消息生成
        Assert.Equal(TubaWinUi3.Services.Agent.AgentSendOutcome.Completed, session.LastSendOutcome);
        // 【第二轮复核】未请求模型切换（未走 resume）→ 无阻断、正常提交
        Assert.Equal(DshSess.ResumeModelSelectionKind.None, session.ResumeModelSelection);
        Assert.False(session.IsModelSwitchBlocked);
        Assert.Equal(1, PromptCount());
    }

    [Fact]
    public async Task ProductSkill_DshPromptInvokesFullSkill_WithoutChangingDisplayedUserText()
    {
        using var session = NewSession(builtinGoalSkillProjected: true);
        session.SetResumeSessionId("fake-old-session"); // 恢复路径也必须带同一技能调用

        await SendAndWaitText(session, "我想做一款 2D 游戏");

        Assert.Contains(AiAgentWorkflowSkill.Id, session.ActiveSkillIds);
        Assert.Equal("fake-old-session", session.DshSessionId);
        Assert.Equal("我想做一款 2D 游戏", session.Title);
        var line = Assert.Single(File.ReadAllLines(PromptLogPath));
        var logged = System.Text.Json.Nodes.JsonNode.Parse(line)!;
        var prompt = logged["prompt"]?[0]?["text"]?.GetValue<string>();
        Assert.NotNull(prompt);
        Assert.StartsWith(AiAgentWorkflowSkill.AddDshInvocation("我想做一款 2D 游戏") + "\n\n", prompt);
        Assert.EndsWith(TubaWinUi3.Services.ToolFlows.ToolFlowProposalParser.BuildOutputInstructions(
            TubaWinUi3.Services.AppManagement.SystemInstaller.KnownTargets,
            TubaWinUi3.Services.AppManagement.SystemInstaller.GetTargetDescriptions()), prompt);
    }

    // ───────────── 恢复 ─────────────

    [Fact]
    public async Task Resume_Success_KeepsHistoricalSessionId()
    {
        using var session = NewSession();
        session.SetResumeSessionId("fake-old-session");

        await SendAndWaitText(session, "继续吧");

        Assert.Equal("fake-old-session", session.DshSessionId);   // resume 路径生效（cwd 匹配、非活动）
        Assert.Equal(TubaWinUi3.Services.Ai.Dsh.DshSession.ResumeOutcomeKind.Resumed, session.ResumeOutcome);
    }

    [Fact]
    public async Task Resume_Rejected_FallsBackToNewSession_WithoutBreaking()
    {
        using var session = NewSession(new Dictionary<string, string> { ["FAKE_DSH_RESUME_FAIL"] = "1" });
        // 【A06 返修】历史会话的原始 cwd 与本次 workspace【不同】：重建后不得残留
        var historicalCwd = Path.Combine(Path.GetTempPath(), "zxai-historical-workspace");
        session.SetResumeContext("fake-old-session", historicalCwd);

        var text = await SendAndWaitText(session, "继续吧");

        Assert.Equal("fake-sess-1", session.DshSessionId);      // 失败 → 重建新会话（fake 的 new 返回值）
        Assert.Contains("你好（fake）", text);                   // 会话仍完全可用
        // 【A06 返修】不再"无感"：明确记录重建结果（页面据此向用户告知上下文不再延续）
        Assert.Equal(TubaWinUi3.Services.Ai.Dsh.DshSession.ResumeOutcomeKind.Recreated, session.ResumeOutcome);
        // 【A06 返修·本轮】重建用的是【当前 workspace】：EffectiveWorkspace（持久化/显示用）必须指向
        // 新建会话实际所用目录——旧实现保留 _resumeCwd，会一直指向历史目录（再次 resume 会拿错 cwd）
        Assert.NotEqual(historicalCwd, session.EffectiveWorkspace);
        Assert.Equal(_dir, session.EffectiveWorkspace);
        // 【第二轮复核】新会话直接用启动配置的模型 → 无需切换、不阻断
        Assert.Equal(DshSess.ResumeModelSelectionKind.None, session.ResumeModelSelection);
        Assert.False(session.IsModelSwitchBlocked);
    }

    // ───────────── 失败结果（附件场景的适配层出口） ─────────────

    [Fact]
    public async Task Image_Unsupported_ReportsErrorResult_And_DoesNotSubmitPrompt()
    {
        using var session = NewSession();   // fake 默认 image=false

        var errors = new List<string>();
        var gotText = new StringBuilder();
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Error += e => { lock (errors) errors.Add(e); };
        session.TextChunk += t => { lock (gotText) gotText.Append(t); };
        session.RunCompleted += () => ran.TrySetResult();

        // 纯图片发送：连接建立后能力协商为 false → 明确报错、不提交 prompt
        await session.SendAsync("", [(new byte[] { 1, 2, 3 }, "image/png")]);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(15));

        lock (errors) Assert.Contains(errors, e => e.Contains("图片"));
        lock (gotText) Assert.Equal("", gotText.ToString());   // 未提交 → 无任何流式文本
        Assert.Equal(TubaWinUi3.Services.Agent.AgentSendOutcome.Rejected, session.LastSendOutcome);
        Assert.Equal(0, PromptCount());   // 服务侧证据：一条 prompt 都没收到

        // 会话未被破坏：紧接着发文本仍正常
        var text = await SendAndWaitText(session, "你好");
        Assert.Contains("你好（fake）", text);
    }

    [Fact]
    public async Task Outcome_Cancelled_WhenPromptCancelled()
    {
        using var session = NewSession(new Dictionary<string, string> { ["FAKE_DSH_PROMPT_DELAY_MS"] = "150" });

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RunCompleted += () => done.TrySetResult();

        var send = session.SendAsync("慢速回复");
        await Task.Delay(120);   // 进入流式阶段
        session.Cancel();        // 取消（通知 + 远端终结）
        await send.WaitAsync(TimeSpan.FromSeconds(15));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 【A12 返修】取消必须是【明确结果】（不再只记 Diagnostic 后按正常返回）——
        // 页面据此保留附件而不是当作成功清空。
        Assert.Equal(TubaWinUi3.Services.Agent.AgentSendOutcome.Cancelled, session.LastSendOutcome);
    }

    [Fact]
    public async Task TOCTOU_ReclaimTimer_AfterOldTurnEnded_DoesNotReclaimLiveClient()
    {
        var oldTimeout = TubaWinUi3.Services.Ai.Dsh.DshSession.CancelReclaimTimeout;
        TubaWinUi3.Services.Ai.Dsh.DshSession.CancelReclaimTimeout = TimeSpan.FromMilliseconds(250);
        try
        {
            using var session = NewSession();   // prompt 快（默认 20ms/chunk）
            var diags = new List<string>();
            session.Diagnostic += d => { lock (diags) diags.Add(d); };

            // 【R3 修复】先把连接预热到 Ready 再发：否则 SendAsync 后 15ms 的早取消会落进
            // 「握手未完成」分支（Cancel 只掐连接令牌、不安排回收定时器），回收屏障永远进不去——
            // 旧版在全量跑中因此稳定 15s 超时。取消必须在 Ready 会话上进行才会安排回收。
            await session.PrewarmAsync();

            // T1：起一发，确认轮次已活跃（activeTurn 已登记）再取消 →
            // 旧轮结束后，回收定时器（250ms）仍在倒计时
            var t1 = session.SendAsync("第一轮");
            var swActive = System.Diagnostics.Stopwatch.StartNew();
            while (session.ActiveTurnForTest == 0 && swActive.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(5);
            Assert.True(session.ActiveTurnForTest != 0, "第一轮未进入活跃态，取消将不会走回收定时器分支");
            session.Cancel();
            await t1.WaitAsync(TimeSpan.FromSeconds(15));   // T1 已结束（activeTurn 已清）

            // barrier：在回收定时器【进锁核对之前】把"新轮已启动并完成"这一 TOCTOU 窗口人为拉长——
            // 新轮复用同一连接，旧实现会在锁内无条件抓走这个仍被新轮使用的 client。
            var barrierDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TubaWinUi3.Services.Ai.Dsh.DshSession.ReclaimBarrierForTest = async () =>
            {
                TubaWinUi3.Services.Ai.Dsh.DshSession.ReclaimBarrierForTest = null;
                var text = await SendAndWaitText(session, "第二轮");
                Assert.Contains("你好（fake）", text);
                barrierDone.TrySetResult();
            };

            await barrierDone.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(300);   // 让回收回调（核对后）彻底走完

            // 【核心断言】新轮已复用连接：身份核对必须拦住回收——不得出现"已回收连接"；
            // 且会话继续完全可用（第三轮正常）。
            lock (diags) Assert.DoesNotContain(diags, d => d.Contains("已回收连接"));
            var t3 = await SendAndWaitText(session, "第三轮");
            Assert.Contains("你好（fake）", t3);
        }
        finally
        {
            TubaWinUi3.Services.Ai.Dsh.DshSession.CancelReclaimTimeout = oldTimeout;
            TubaWinUi3.Services.Ai.Dsh.DshSession.ReclaimBarrierForTest = null;
        }
    }

    [Fact]
    public async Task Outcome_Failed_WhenChildProcessDied_DuringPrompt()
    {
        // 子进程在第 3 个请求（prompt）收到时自杀：发送必须以【明确失败结果】告终（写失败/连接断开路径）
        using var session = NewSession(new Dictionary<string, string> { ["FAKE_DSH_EXIT_AFTER_N"] = "3" });

        var errors = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Error += e => { lock (errors) errors.Add(e); };
        session.RunCompleted += () => done.TrySetResult();

        await session.SendAsync("触发进程退出");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(TubaWinUi3.Services.Agent.AgentSendOutcome.Failed, session.LastSendOutcome);
        lock (errors) Assert.NotEmpty(errors);   // 明确报错（不是静默挂起）
    }

    [Fact]
    public void AdoptIdentity_And_ResumeContext_AreExposed()
    {
        using var session = NewSession();

        session.AdoptIdentity("app-id-abc123", "历史会话标题");
        session.SetResumeContext(null, @"D:\saved-cwd");

        Assert.Equal("app-id-abc123", session.Id);            // 【A06 返修】同一应用会话身份（恢复不产生新副本）
        Assert.Equal("历史会话标题", session.Title);
        Assert.Equal(@"D:\saved-cwd", session.EffectiveWorkspace);   // 保存的原始 cwd（resume 要求匹配）
    }

    [Fact]
    public async Task Send_AfterDispose_IsRejected_WithClearResult()
    {
        var session = NewSession();
        await SendAndWaitText(session, "建立连接");
        session.Dispose();

        var errors = new List<string>();
        session.Error += e => errors.Add(e);
        await session.SendAsync("迟到的消息");

        Assert.Contains(errors, e => e.Contains("已关闭"));
    }

    // ───────────── 【A07 返修】resume 后按真实 configOptions schema 切换模型 ─────────────

    /// <summary>【A07】真实 dsh-acp schema（<c>id/options</c> + provider 分组）：
    /// 必须命中服务给出的 option 并【原样取用它的 value】（不透明编码，非客户端拼造），
    /// 且只在响应核对通过后才声称已切换。</summary>
    [Fact]
    public async Task Resume_GroupedConfigOptionsSchema_SetsModelUsingServerOptionValue()
    {
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "1",
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "deepseek-v4-pro");
        var diags = new List<string>();
        session.Diagnostic += d => { lock (diags) diags.Add(d); };
        session.SetResumeContext("fake-old-session", Path.Combine(Path.GetTempPath(), "zxai-old-workspace"));

        await SendAndWaitText(session, "继续吧");

        Assert.Equal(TubaWinUi3.Services.Ai.Dsh.DshSession.ResumeOutcomeKind.Resumed, session.ResumeOutcome);

        var log = ReadSetConfigLog();
        Assert.Equal("session/set_config_option", log["method"]!.GetValue<string>());
        Assert.Equal("model", log["configId"]!.GetValue<string>());
        Assert.Equal("fake-old-session", log["sessionId"]!.GetValue<string>());
        // 【关键】发出的是【服务返回的 option.value】（"["deepseek-official","deepseek-v4-pro"]"），
        // 不是当前选择的原始串（"deepseek-v4-pro"）——旧实现按 configId/values 解析拿不到这个编码。
        Assert.Equal("[\"deepseek-official\",\"deepseek-v4-pro\"]", log["value"]!.GetValue<string>());
        Assert.True(log["applied"]!.GetValue<bool>());
        lock (diags) Assert.Contains(diags, d => d.Contains("已按当前选择设置历史会话模型"));

        // 【第二轮复核】切换成立 → 明确状态 + 不阻断 + 本轮确实提交了（阻断只针对未成立的切换）
        Assert.Equal(DshSess.ResumeModelSelectionKind.Applied, session.ResumeModelSelection);
        Assert.Null(session.ResumeModelSelectionMessage);
        Assert.False(session.IsModelSwitchBlocked);
        Assert.Null(session.ModelSwitchBlockMessage);
        Assert.Equal(1, PromptCount());
    }

    /// <summary>【A07】同 schema 的【扁平】options 形态（<c>[{value,name}]</c>）同样要能解析
    /// （扁平无需分组：value 自带完整路由）；本用例选中的是 API model ID（展示名不是身份）。</summary>
    [Fact]
    public async Task Resume_FlatConfigOptionsSchema_AlsoResolvesModelOption()
    {
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "flat",
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "deepseek-v4-pro");
        var diags = new List<string>();
        session.Diagnostic += d => { lock (diags) diags.Add(d); };
        session.SetResumeSessionId("fake-old-session");

        await SendAndWaitText(session, "继续吧");

        Assert.Equal("[\"deepseek-official\",\"deepseek-v4-pro\"]", ReadSetConfigLog()["value"]!.GetValue<string>());
        lock (diags) Assert.Contains(diags, d => d.Contains("已按当前选择设置历史会话模型"));
        // 【第二轮复核】展示名精确匹配成立 → 不阻断
        Assert.Equal(DshSess.ResumeModelSelectionKind.Applied, session.ResumeModelSelection);
        Assert.False(session.IsModelSwitchBlocked);
    }

    /// <summary>【A07 / 第二轮复核】服务拒绝 set_config_option（真实 dsh-acp = -32602 unknown model option）：
    /// 必须如实记"未确认"、不声称已切换；【第二轮复核】结果要可读（Rejected）且本轮 prompt 被阻断
    /// （不得用历史模型悄悄发），用户确认后会话仍完全可用。</summary>
    [Fact]
    public async Task Resume_SetConfigOptionRejected_ReportsRejectedAndBlocksPrompt()
    {
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "1",
            ["FAKE_DSH_SET_CONFIG_FAIL"] = "1",
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "deepseek-v4-pro");
        var diags = new List<string>();
        session.Diagnostic += d => { lock (diags) diags.Add(d); };
        session.SetResumeContext("fake-old-session", _dir);

        var (text, errors) = await SendAndCaptureAsync(session, "继续吧");

        Assert.Equal(DshSess.ResumeOutcomeKind.Resumed, session.ResumeOutcome);
        Assert.Equal("", text);                                  // 阻断：没有任何流式文本
        Assert.Equal(0, PromptCount());                          // 服务侧证据：prompt 未被提交
        Assert.Equal(DshSess.ResumeModelSelectionKind.Rejected, session.ResumeModelSelection);
        Assert.True(session.IsModelSwitchBlocked);
        Assert.Equal(TubaWinUi3.Services.Agent.AgentSendOutcome.Rejected, session.LastSendOutcome);
        Assert.False(string.IsNullOrWhiteSpace(session.ResumeModelSelectionMessage));
        Assert.Contains(errors, e => e.Contains("模型切换未成立"));
        Assert.NotNull(session.ModelSwitchBlockMessage);         // 页面可直接展示的明确状态
        lock (diags)
        {
            Assert.Contains(diags, d => d.Contains("模型切换未确认"));
            Assert.DoesNotContain(diags, d => d.Contains("已按当前选择设置历史会话模型"));
        }
        var log = ReadSetConfigLog();
        Assert.NotNull(log["value"]);                            // 请求确实发出过（不是"没试"）

        // 用户知情确认 → 放行，会话可用（切换失败不影响会话可用性）
        session.AcknowledgeResumeModelFallback();
        Assert.False(session.IsModelSwitchBlocked);
        var after = await SendAndWaitText(session, "继续吧");
        Assert.Contains("你好（fake）", after);
        Assert.Equal(1, PromptCount());
    }

    // ───────────── 【第二轮复核】按实际 ACP route provider + API model ID 精确匹配 ─────────────

    /// <summary>构造 provider 分组候选 JSON（value = JSON.stringify([provider, model])，
    /// 与真实 dsh-acp 的 AcpModelControl 一致）；供应商上下文由 group 承载。</summary>
    private static string GroupsJson(params (string Provider, string Model)[] routes)
        => System.Text.Json.JsonSerializer.Serialize(routes.GroupBy(r => r.Provider)
            .Select(g => new
            {
                group = g.Key,
                name = g.Key,
                options = g.Select(r => new
                {
                    value = System.Text.Json.JsonSerializer.Serialize(new[] { r.Provider, r.Model }),
                    name = r.Model,
                }).ToArray(),
            }).ToArray());

    /// <summary>【第二轮复核·同名跨 provider】服务在两个 provider 分组下各给同名模型
    /// （deepseek-v4-pro，错误路由故意排在前面）：只接受【本次启动真实路由
    /// （DeepSeek → deepseek-official）】下的那个候选——同名不足以入选，也不许"取第一个"。</summary>
    [Fact]
    public async Task Resume_SameModelAcrossProviders_OnlyLaunchRouteAccepted()
    {
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "1",
            ["FAKE_DSH_MODEL_GROUPS_JSON"] = GroupsJson(
                ("local-lmstudio", "deepseek-v4-pro"),        // 错误路由，故意排在前
                ("deepseek-official", "deepseek-v4-pro")),
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "deepseek-v4-pro", providerId: "deepseek");
        session.SetResumeSessionId("fake-old-session");

        await SendAndWaitText(session, "继续吧");

        Assert.Equal("[\"deepseek-official\",\"deepseek-v4-pro\"]", ReadSetConfigLog()["value"]!.GetValue<string>());
        Assert.Equal(DshSess.ResumeModelSelectionKind.Applied, session.ResumeModelSelection);
        Assert.False(session.IsModelSwitchBlocked);
        Assert.Equal(1, PromptCount());
    }

    /// <summary>【第二轮复核·唯一但供应商不符】服务只有一个候选，但它的路由不是本次启动的真实路由
    /// （自定义供应商 → zxai-selected）：必须拒绝（NoMatch）——"唯一"不能代替供应商校验；
    /// 结果可读、本轮零 prompt（fake 侧接收记录）。</summary>
    [Fact]
    public async Task Resume_UniqueWrongProvider_IsRejectedBeforePrompt()
    {
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "1",
            ["FAKE_DSH_MODEL_GROUPS_JSON"] = GroupsJson(("other-provider", "gpt-4")),
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "gpt-4", providerId: "my-custom-provider");   // 真实路由 = zxai-selected
        session.SetResumeSessionId("fake-old-session");

        var (text, errors) = await SendAndCaptureAsync(session, "不要发给错误供应商");

        Assert.Equal("", text);
        Assert.Equal(DshSess.ResumeModelSelectionKind.NoMatch, session.ResumeModelSelection);
        Assert.True(session.IsModelSwitchBlocked);
        Assert.Contains(errors, e => e.Contains("模型切换未成立"));
        Assert.False(string.IsNullOrWhiteSpace(session.ResumeModelSelectionMessage));
        Assert.Equal(TubaWinUi3.Services.Agent.AgentSendOutcome.Rejected, session.LastSendOutcome);
        Assert.False(File.Exists(SetConfigLogPath));
        Assert.Equal(0, PromptCount());
    }

    /// <summary>【第二轮复核·自定义供应商路由】应用的 ProviderId（my-custom-provider）≠ ACP 路由：
    /// 自定义配置的真实路由是 zxai-selected —— 只有该路由下的候选可被采用。</summary>
    [Fact]
    public async Task Resume_CustomProviderId_UsesActualZxaiSelectedRoute()
    {
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "1",
            ["FAKE_DSH_MODEL_GROUPS_JSON"] = GroupsJson(
                ("other-provider", "gpt-4"), ("zxai-selected", "gpt-4")),
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "gpt-4", providerId: "my-custom-provider");
        session.SetResumeSessionId("fake-old-session");

        var text = await SendAndWaitText(session, "使用明确选择的路由");

        Assert.Equal(DshSess.ResumeModelSelectionKind.Applied, session.ResumeModelSelection);
        Assert.False(session.IsModelSwitchBlocked);
        Assert.Contains("你好（fake）", text);
        Assert.Equal("[\"zxai-selected\",\"gpt-4\"]", ReadSetConfigLog()["value"]!.GetValue<string>());
        Assert.Equal(1, PromptCount());
    }

    /// <summary>【第二轮复核·供应商前缀≠身份】模型挂在 deepseek-proxy（前缀形似 official）：
    /// 本次启动真实路由是 deepseek-official —— 前缀不得当作身份，必须拒绝。</summary>
    [Fact]
    public async Task Resume_DeepSeekPrefixProvider_IsNotOfficialRoute()
    {
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "1",
            ["FAKE_DSH_MODEL_GROUPS_JSON"] = GroupsJson(("deepseek-proxy", "deepseek-v4-pro")),
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "deepseek-v4-pro", providerId: "deepseek");
        session.SetResumeSessionId("fake-old-session");

        var (text, _) = await SendAndCaptureAsync(session, "不接受供应商前缀代替身份");

        Assert.Equal("", text);
        Assert.Equal(DshSess.ResumeModelSelectionKind.NoMatch, session.ResumeModelSelection);
        Assert.True(session.IsModelSwitchBlocked);
        Assert.False(File.Exists(SetConfigLogPath));
        Assert.Equal(0, PromptCount());
    }

    /// <summary>【第二轮复核·模型前缀≠身份】候选是 gpt-4o（用户选 gpt-4，前缀近似）：
    /// 必须 NoMatch 且零 prompt（旧实现的 Contains 匹配会误配；本用例用扁平 options 形态）。</summary>
    [Fact]
    public async Task Resume_ModelPrefix_IsRejectedBeforePrompt()
    {
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "flat",
            ["FAKE_DSH_MODEL_GROUPS_JSON"] = GroupsJson(("zxai-selected", "gpt-4o")),
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "gpt-4", providerId: "my-custom-provider");
        session.SetResumeSessionId("fake-old-session");

        var (text, errors) = await SendAndCaptureAsync(session, "只允许精确模型");

        Assert.Equal("", text);
        Assert.Equal(DshSess.ResumeModelSelectionKind.NoMatch, session.ResumeModelSelection);
        Assert.True(session.IsModelSwitchBlocked);
        Assert.Contains(errors, e => e.Contains("模型切换未成立"));
        Assert.False(File.Exists(SetConfigLogPath));
        Assert.Equal(0, PromptCount());
    }

    /// <summary>【第二轮复核·展示名≠模型 ID】候选的 route model 是 gpt-4o、展示名被故意写成 gpt-4：
    /// 用户选择 gpt-4 必须 NoMatch（不得按展示名匹配），且零 prompt。</summary>
    [Fact]
    public async Task Resume_DisplayNameIsNotModelIdentity()
    {
        var groups = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                group = "zxai-selected",
                name = "zxai-selected",
                options = new[]
                {
                    new
                    {
                        value = System.Text.Json.JsonSerializer.Serialize(new[] { "zxai-selected", "gpt-4o" }),
                        name = "gpt-4",
                    },
                },
            },
        });
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "1",
            ["FAKE_DSH_MODEL_GROUPS_JSON"] = groups,
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "gpt-4", providerId: "my-custom-provider");
        session.SetResumeSessionId("fake-old-session");

        var (text, _) = await SendAndCaptureAsync(session, "展示名不算身份");

        Assert.Equal("", text);
        Assert.Equal(DshSess.ResumeModelSelectionKind.NoMatch, session.ResumeModelSelection);
        Assert.True(session.IsModelSwitchBlocked);
        Assert.False(File.Exists(SetConfigLogPath));
        Assert.Equal(0, PromptCount());
    }

    /// <summary>【第二轮复核·同值重复不是歧义】服务把同一 option（同一 value）列了两次
    /// （真实实现会把当前取值插回其分组首位）：去重后唯一 → 正常采用，不误判歧义。</summary>
    [Fact]
    public async Task Resume_IdenticalDuplicateOptions_IsNotAmbiguous()
    {
        var groups = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                group = "deepseek-official",
                name = "DeepSeek",
                options = new[]
                {
                    new { value = "[\"deepseek-official\",\"deepseek-v4-pro\"]", name = "deepseek-v4-pro" },
                    new { value = "[\"deepseek-official\",\"deepseek-v4-pro\"]", name = "deepseek-v4-pro" },
                },
            },
        });
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "1",
            ["FAKE_DSH_MODEL_GROUPS_JSON"] = groups,
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "deepseek-v4-pro", providerId: "deepseek");
        session.SetResumeSessionId("fake-old-session");

        await SendAndWaitText(session, "继续吧");

        Assert.Equal(DshSess.ResumeModelSelectionKind.Applied, session.ResumeModelSelection);
        Assert.False(session.IsModelSwitchBlocked);
        Assert.Equal("[\"deepseek-official\",\"deepseek-v4-pro\"]", ReadSetConfigLog()["value"]!.GetValue<string>());
    }

    /// <summary>【第二轮复核·歧义拒绝】同一逻辑选项的两种编码（去重后仍不唯一）：
    /// 必须拒绝（Ambiguous）——不猜第一个、不发起 set_config_option、本轮零 prompt；
    /// 用户确认后可放行（阻断不是死路）。</summary>
    [Fact]
    public async Task Resume_DuplicateEncodedOptions_Ambiguous_RejectsAndBlocksPrompt()
    {
        var groups = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                group = "deepseek-official",
                name = "DeepSeek",
                options = new[]
                {
                    new { value = "[\"deepseek-official\",\"deepseek-v4-pro\"]", name = "deepseek-v4-pro" },
                    new { value = "[\"deepseek-official\", \"deepseek-v4-pro\"]", name = "deepseek-v4-pro" },   // 同路由、不同编码
                },
            },
        });
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "1",
            ["FAKE_DSH_MODEL_GROUPS_JSON"] = groups,
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "deepseek-v4-pro", providerId: "deepseek");
        var diags = new List<string>();
        session.Diagnostic += d => { lock (diags) diags.Add(d); };
        session.SetResumeSessionId("fake-old-session");

        var (text, errors) = await SendAndCaptureAsync(session, "继续吧");

        Assert.Equal("", text);
        Assert.Equal(DshSess.ResumeModelSelectionKind.Ambiguous, session.ResumeModelSelection);
        Assert.True(session.IsModelSwitchBlocked);
        Assert.Contains(errors, e => e.Contains("模型切换未成立"));
        Assert.False(File.Exists(SetConfigLogPath), "歧义时不得猜测编码并发起 set_config_option");
        Assert.Equal(0, PromptCount());
        lock (diags) Assert.DoesNotContain(diags, d => d.Contains("已按当前选择设置历史会话模型"));

        session.AcknowledgeResumeModelFallback();
        Assert.False(session.IsModelSwitchBlocked);
        Assert.Contains("你好（fake）", await SendAndWaitText(session, "继续吧"));
        Assert.Equal(1, PromptCount());
    }

    /// <summary>【A07 / 第二轮复核】服务只回"成功"但设置未生效（currentValue 未变）：
    /// 必须靠响应核对发现，不得仅凭"没报错"就声称已切换；且本轮 prompt 被阻断。</summary>
    [Fact]
    public async Task Resume_SetConfigOptionNoop_ReportsNotAppliedAndBlocksPrompt()
    {
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "1",
            ["FAKE_DSH_SET_CONFIG_NOOP"] = "1",
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "deepseek-v4-pro");
        var diags = new List<string>();
        session.Diagnostic += d => { lock (diags) diags.Add(d); };
        session.SetResumeSessionId("fake-old-session");

        var (text, errors) = await SendAndCaptureAsync(session, "继续吧");

        var log = ReadSetConfigLog();
        Assert.False(log["applied"]!.GetValue<bool>());          // fake 侧确实"没生效"
        Assert.NotNull(log["value"]);
        Assert.Equal("", text);
        Assert.Equal(0, PromptCount());                          // 阻断：prompt 未被提交
        Assert.Equal(DshSess.ResumeModelSelectionKind.NotApplied, session.ResumeModelSelection);
        Assert.True(session.IsModelSwitchBlocked);
        Assert.Equal(TubaWinUi3.Services.Agent.AgentSendOutcome.Rejected, session.LastSendOutcome);
        Assert.Contains(errors, e => e.Contains("模型切换未成立"));
        lock (diags)
        {
            Assert.Contains(diags, d => d.Contains("模型切换未生效"));
            Assert.DoesNotContain(diags, d => d.Contains("已按当前选择设置历史会话模型"));
        }

        session.AcknowledgeResumeModelFallback();
        Assert.Contains("你好（fake）", await SendAndWaitText(session, "继续吧"));
        Assert.Equal(1, PromptCount());
    }

    /// <summary>【A07 / 第二轮复核】服务列出的模型里没有与当前选择【精确】匹配的项：不得拿用户原始串去猜编码
    /// （旧实现回退传原值），如实告知（NoMatch）且【不发起】set_config_option；本轮 prompt 被阻断。</summary>
    [Fact]
    public async Task Resume_NoMatchingModelOption_DoesNotGuessEncoding()
    {
        using var session = NewSession(new Dictionary<string, string>
        {
            ["FAKE_DSH_RESUME_OPTIONS"] = "1",
            ["FAKE_DSH_SET_CONFIG_LOG"] = SetConfigLogPath,
        }, model: "gpt-not-installed");
        var diags = new List<string>();
        session.Diagnostic += d => { lock (diags) diags.Add(d); };
        session.SetResumeSessionId("fake-old-session");

        var (text, errors) = await SendAndCaptureAsync(session, "继续吧");

        Assert.Equal("", text);
        Assert.Equal(DshSess.ResumeModelSelectionKind.NoMatch, session.ResumeModelSelection);
        Assert.True(session.IsModelSwitchBlocked);
        Assert.Equal(TubaWinUi3.Services.Agent.AgentSendOutcome.Rejected, session.LastSendOutcome);
        Assert.Contains(errors, e => e.Contains("模型切换未成立"));
        lock (diags)
        {
            Assert.Contains(diags, d => d.Contains("没有与「gpt-not-installed」"));
            Assert.DoesNotContain(diags, d => d.Contains("已按当前选择设置历史会话模型"));
        }
        Assert.False(File.Exists(SetConfigLogPath), "无匹配项时不得猜测编码并发起 set_config_option");
        Assert.Equal(0, PromptCount());

        // 明确状态可读 + 用户确认后放行（不是死路）
        Assert.NotNull(session.ResumeModelSelectionMessage);
        session.AcknowledgeResumeModelFallback();
        Assert.Contains("你好（fake）", await SendAndWaitText(session, "继续吧"));
    }

    /// <summary>【A07 / 第二轮复核】resume 返回空 configOptions（旧 fixture / 老版本服务）：
    /// 如实告知可能仍在 logged 模型上（NotProvided），不假称已切换；本轮 prompt 被阻断。</summary>
    [Fact]
    public async Task Resume_NoConfigOptions_ReportsLoggedModelMayRemain()
    {
        using var session = NewSession(model: "deepseek-v4-pro");   // 默认 fixture：configOptions:[]
        var diags = new List<string>();
        session.Diagnostic += d => { lock (diags) diags.Add(d); };
        session.SetResumeSessionId("fake-old-session");

        var (text, errors) = await SendAndCaptureAsync(session, "继续吧");

        Assert.Equal("", text);
        Assert.Equal(DshSess.ResumeModelSelectionKind.NotProvided, session.ResumeModelSelection);
        Assert.True(session.IsModelSwitchBlocked);
        Assert.Equal(TubaWinUi3.Services.Agent.AgentSendOutcome.Rejected, session.LastSendOutcome);
        Assert.Contains(errors, e => e.Contains("模型切换未成立"));
        lock (diags)
        {
            Assert.Contains(diags, d => d.Contains("服务未提供 model 配置项"));
            Assert.DoesNotContain(diags, d => d.Contains("已按当前选择设置历史会话模型"));
        }
        Assert.Equal(0, PromptCount());

        // 明确状态可读 + 用户确认后放行
        Assert.NotNull(session.ResumeModelSelectionMessage);
        session.AcknowledgeResumeModelFallback();
        Assert.Contains("你好（fake）", await SendAndWaitText(session, "继续吧"));
    }

    // ───────────── 【A09 返修】轮次清零 CAS（新旧轮交错的确定性回归） ─────────────

    /// <summary>【A09】旧轮的 finally 只允许清【自己】的轮次编号：在"旧轮 finally 的 _running=false
    /// 之后、CompareExchange 清零之前"插入暂停（BeforeTurnClearForTest），期间启动新轮——
    /// 旧轮恢复后不得把新轮编号抹成 0（旧实现 CAS 目标取当前值 = 无条件清零）。</summary>
    [Fact]
    public async Task TurnClear_InterleavedWithNewTurn_DoesNotClearNewTurnNumber()
    {
        using var session = NewSession(new Dictionary<string, string> { ["FAKE_DSH_PROMPT_DELAY_MS"] = "250" });
        await SendAndWaitText(session, "预热连接");   // 先建连接（首轮握手不参与本交错）

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookFired = 0;
        try
        {
            TubaWinUi3.Services.Ai.Dsh.DshSession.BeforeTurnClearForTest = async () =>
            {
                if (Interlocked.Exchange(ref hookFired, 1) != 0) return;   // 只拦第一轮（新轮收尾放行）
                TubaWinUi3.Services.Ai.Dsh.DshSession.BeforeTurnClearForTest = null;
                paused.TrySetResult();
                await release.Task.ConfigureAwait(false);
            };

            var oldTurn = session.SendAsync("旧轮");
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(15));   // 旧轮已进入"清零前"窗口
            var oldTurnNo = session.ActiveTurnForTest;
            Assert.True(oldTurnNo > 0, "旧轮应有轮次编号");

            // 旧轮尚未清零：新轮在此窗口启动（_running 已复位），Exchange 写入新编号
            var newTurn = session.SendAsync("新轮");
            var newTurnNo = session.ActiveTurnForTest;
            Assert.Equal(oldTurnNo + 1, newTurnNo);   // 新轮拿到新编号，旧编号此刻仍在

            release.TrySetResult();                   // 放行旧轮 → 它执行 CompareExchange(0, oldTurnNo)
            await oldTurn.WaitAsync(TimeSpan.FromSeconds(15));

            // 【核心断言】旧轮清零必须不生效：新轮编号不被打回 0
            Assert.Equal(newTurnNo, session.ActiveTurnForTest);

            await newTurn.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, session.ActiveTurnForTest);   // 新轮自己收尾时正常清零
        }
        finally
        {
            TubaWinUi3.Services.Ai.Dsh.DshSession.BeforeTurnClearForTest = null;
            release.TrySetResult();
        }
    }
}
