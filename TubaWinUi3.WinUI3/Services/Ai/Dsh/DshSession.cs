using System.Text.Json.Nodes;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Services.Ai.Dsh;

/// <summary>
/// 【ZXAI】dsh 引擎会话（换核心 M2）：实现 <see cref="IAgentSession"/>，
/// 对 UI 呈现与自研 AgentSession 相同的事件/方法面。
/// 连接为惰性：构造后首次 SendAsync 才起子进程 + 握手（页面创建是同步的）。
/// ACP update → UI 事件映射：
///   agent_message_chunk → TextChunk；agent_thought_chunk → ReasoningChunk
///   tool_call / tool_call_update → StepStarted / StepCompleted
///   usage_update → 用量（dsh 只报 used/size；CompletionTokens 恒 0）
/// 【A09 审计修复】会话生命周期所有权：
///   ① 进程在握手开始前即登记（_pendingClient），握手期 Dispose/Cancel 可见并回收；
///   ② 会话级 _lifecycleCts 随 Dispose 取消，中断在途握手/请求；
///   ③ _disposed 标志：关闭后拒绝一切新发送；
///   ④ 握手失败自动回收进程，连接单飞（并发首发送共享同一连接任务）。
/// </summary>
public sealed class DshSession : IAgentSession
{
    private readonly string _apiKey;
    private readonly string _workspace;
    private readonly CancellationTokenSource _lifecycleCts = new();  // 【A09】会话生命周期
    private volatile bool _disposed;                                 // 【A09】
    // ---------- 连接状态机（【A09 第二轮复核】检查/迁移/发布全部在同一把锁内） ----------
    private enum ConnPhase { Idle, Connecting, Ready }
    private readonly object _gate = new();
    private ConnPhase _phase = ConnPhase.Idle;
    private Task<DshAcpClient>? _connectTask;
    private CancellationTokenSource? _connectCts;
    private DshAcpClient? _pendingClient;                            // Connecting 期的进程（回收用）
    private volatile int _stopRequested;                             // 停止状态：跨"握手→prompt"窗口
    private long _turnSeq;                                           // 轮次序号（每次发送递增）
    private long _activeTurn;                                        // 进行中的轮次（0=无）
    private DshAcpClient? _client;
    private string? _sessionId;
    private bool _running;
    private readonly Dictionary<string, AgentStep> _steps = new();
    private readonly List<AgentStep> _turnSteps = new();             // 【A06 返修】本轮工具步骤（组汇总存档）
    private DateTime _turnStartedAt;
    private int _turnStartTokens;
    private string? _resumeCwd;                                      // 【A06 返修】历史会话原始 cwd（resume 要求匹配）

    /// <summary>【A09 测试】取消后回收超时（生产 15 秒；测试可注入短值）。</summary>
    internal static TimeSpan CancelReclaimTimeout = TimeSpan.FromSeconds(15);

    /// <summary>【A09 测试】回收定时器进锁前的 barrier（生产 null）——用于 TOCTOU 确定性回归。</summary>
    internal static Func<Task>? ReclaimBarrierForTest;

    /// <summary>【A09 测试】轮次清零前的 barrier（生产 null）——在【本轮 finally 的 _running=false 之后、
    /// CompareExchange 清零之前】插入暂停，用于"旧轮 finally 与新轮 Exchange 交错"的确定性回归
    /// （旧轮不得清掉刚开始的新轮编号）。生产路径只是一个 null 判断。</summary>
    internal static Func<Task>? BeforeTurnClearForTest;

    /// <summary>【A09 测试】进行中的轮次编号（0=无）——交错回归断言用。</summary>
    internal long ActiveTurnForTest => Interlocked.Read(ref _activeTurn);

    // ---------- IAgentSession ----------
    public string Id { get; private set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; private set; } = MiscTexts.T("新会话");
    public bool IsRunning => _running;
    public string PersonaId { get; private set; } = AgentPersonaCatalog.DefaultId;

    public int TotalPromptTokens { get; private set; }
    public int TotalCompletionTokens => 0;
    public int TotalTokens => TotalPromptTokens;
    public int TotalCacheHitTokens => 0;   // dsh usage_update 暂不提供缓存拆分
    public int TotalCacheMissTokens => TotalPromptTokens;

    private readonly object _skillsGate = new();
    private readonly HashSet<string> _customSkillIds = new(StringComparer.Ordinal);
    private readonly SkillTaskContinuity _skillTaskContinuity = new();
    private bool _skillStateUnreadable;
    internal IReadOnlyCollection<string> ReferencedCustomSkillIds => _skillTaskContinuity.ReferencedSkillIds;
    internal void RestoreCustomSkillTask(string confirmedGoal)
    {
        if (!_disposed) _skillTaskContinuity.RestoreFromGoal(confirmedGoal, ActiveSkillIds);
    }
    public IReadOnlyCollection<string> ActiveSkillIds
    {
        get
        {
            lock (_skillsGate) return _launch?.BuiltinGoalSkillProjected == true
                ? _customSkillIds.Append(AiAgentWorkflowSkill.Id).ToArray() : _customSkillIds.ToArray();
        }
    }

    public event Action<string>? TextChunk;
    public event Action<string>? ReasoningChunk;
    public event Action<AgentStep>? StepStarted;
    public event Action<AgentStep>? StepCompleted;
    public event Action<IReadOnlyList<AgentConfirmationRequest>>? ConfirmationsRequested;
    public event Action<string>? Error;
    public event Action? RunCompleted;
    public event Action? RoundStarted;
    public event Action<AgentStepGroupSummary>? StepGroupCompleted;

    /// <summary>诊断/日志行（stderr 与未知 update 等）。</summary>
    public event Action<string>? Diagnostic;

    /// <summary>dsh 侧会话 id（可持久化用于 resume）。</summary>
    public string? DshSessionId => _sessionId;

    /// <summary>【A06】指定要恢复的历史 dsh 会话 id（首次连接时尝试；失败自动重建新会话）。
    /// 依据协议参考：resume 需 cwd 匹配持久记录、拒绝已活动会话、响应不含历史——展示历史由本地存档承载。</summary>
    public void SetResumeSessionId(string? sessionId) => _resumeSessionId = sessionId;

    /// <summary>【A06 返修】恢复上下文：历史会话 id + 其【原始 cwd】——resume 要求 cwd 与持久记录匹配，
    /// 不能按当前 UI 的新 cwd 猜测。</summary>
    public void SetResumeContext(string? sessionId, string? cwd)
    {
        _resumeSessionId = sessionId;
        if (!string.IsNullOrWhiteSpace(cwd)) _resumeCwd = cwd;
    }

    /// <summary>【A06 返修】恢复同一应用会话身份（Id/Title）——历史恢复不再产生新副本。</summary>
    public void AdoptIdentity(string id, string title)
    {
        _skillTaskContinuity.Reset();
        if (!string.IsNullOrWhiteSpace(id)) Id = id;
        if (!string.IsNullOrWhiteSpace(title)) Title = title;
        LoadCustomSkills();
    }

    /// <summary>【A07】本会话的启动配置指纹（页面在轮次边界比较，检测用户切换 provider/model/endpoint/key）。</summary>
    public string? LaunchFingerprint => _launch?.Fingerprint;

    /// <summary>【A06 返修】本会话实际使用的 cwd（resume 成功恢复时=历史原始 cwd；否则=构造时 workspace）
    /// ——持久化用。resume 失败重建后回到 workspace（新建会话实际所用目录），不再残留历史 cwd。</summary>
    public string EffectiveWorkspace => _resumeCwd ?? _workspace;

    /// <summary>【A06】恢复结果（页面据此向用户如实告知）。</summary>
    public enum ResumeOutcomeKind { None, Resumed, Recreated }
    public ResumeOutcomeKind ResumeOutcome { get; private set; } = ResumeOutcomeKind.None;

    // ---------- 【第二轮复核】resume 后模型切换的可见结果 + 发送阻断点 ----------
    // 旧实现把「未提供/找不到/歧义/被拒/未生效」只写进 Diagnostic（Release 下用户与页面都看不见），
    // 且失败后仍会用历史模型把 prompt 悄悄发出去。现在：结果落成【可读状态】，且未确认前【不发 prompt】。

    /// <summary>【第二轮复核】“把历史会话切到当前所选模型”的明确结果（页面据此提醒/阻断）。</summary>
    public enum ResumeModelSelectionKind
    {
        /// <summary>本次启动未指定模型，或未走 resume（新会话直接用启动配置的模型）——无需切换。</summary>
        None,
        /// <summary>已按服务返回的 option.value 显式设置，并经响应 currentValue 核对通过。</summary>
        Applied,
        /// <summary>服务未返回 model 配置项：无法切换，会话可能仍在其 logged（历史）模型上。</summary>
        NotProvided,
        /// <summary>服务返回的候选列表为空：无法切换。</summary>
        NoChoices,
        /// <summary>候选里没有与所选模型【精确】同名的项（不做子串匹配——gpt-4 不得匹配 gpt-4o）。</summary>
        NoMatch,
        /// <summary>多个候选精确同名且无法按所选供应商收敛：拒绝猜测（不发起 set_config_option）。</summary>
        Ambiguous,
        /// <summary>服务拒绝 set_config_option（切换未确认）。</summary>
        Rejected,
        /// <summary>服务未报错但设置未生效（响应 currentValue 未变）。</summary>
        NotApplied,
    }

    /// <summary>【第二轮复核】最近一次 resume 后的模型切换结果（默认 None = 未请求/无需切换）。</summary>
    public ResumeModelSelectionKind ResumeModelSelection { get; private set; } = ResumeModelSelectionKind.None;

    /// <summary>【第二轮复核】结果的用户可读说明（与写入 Diagnostic 的文案同源；成功/未请求时为空）。
    /// 页面可直接展示，不必解析日志。</summary>
    public string? ResumeModelSelectionMessage { get; private set; }

    /// <summary>【第二轮复核】切换未成立：会话可能仍在历史（logged）模型上——正文/附件不得静默发出。</summary>
    public bool ResumeModelUnconfirmed => ResumeModelSelection is
        ResumeModelSelectionKind.NotProvided or ResumeModelSelectionKind.NoChoices or
        ResumeModelSelectionKind.NoMatch or ResumeModelSelectionKind.Ambiguous or
        ResumeModelSelectionKind.Rejected or ResumeModelSelectionKind.NotApplied;

    /// <summary>【第二轮复核】发送阻断点：切换未成立且用户尚未显式确认 → SendAsync 拒绝提交 prompt
    /// （不得用历史模型或其他配置悄悄发送）。页面读到本状态即可阻断/提醒。</summary>
    public bool IsModelSwitchBlocked => ResumeModelUnconfirmed && !_resumeModelFallbackAcknowledged;

    /// <summary>【第二轮复核】阻断文案（页面提示用；未阻断时为空）。</summary>
    public string? ModelSwitchBlockMessage => IsModelSwitchBlocked
        ? MiscTexts.TSub($"历史会话的模型切换未成立（{ModelSwitchKindText(ResumeModelSelection)}）：为避免在未确认的模型上发送，") +
          MiscTexts.T("本轮未提交。请新建会话以使用当前所选模型；此前对话仍可在历史列表中查看。")
        : null;

    /// <summary>接线层显式确认沿用历史模型后才可放行。当前客户端提供新建会话入口，不自动放行。</summary>
    public void AcknowledgeResumeModelFallback()
    {
        if (!ResumeModelUnconfirmed) return;
        _resumeModelFallbackAcknowledged = true;
        Diagnostic?.Invoke($"[引擎] 用户已知情：本会话沿用历史模型继续（{ResumeModelSelection}）");
    }

    private volatile bool _resumeModelFallbackAcknowledged;

    /// <summary>【A12 返修】最近一轮的发送结果（由 SendAsync 各出口设置；页面按【成功才清空附件】消费）。</summary>
    public Services.Agent.AgentSendOutcome LastSendOutcome { get; private set; } = Services.Agent.AgentSendOutcome.Completed;

    /// <summary>上下文窗口（usage_update 上报）。</summary>
    public long ContextSize { get; private set; }

    /// <summary>连接握手协商的图片输入能力（initialize.agentCapabilities.promptCapabilities.image）。</summary>
    public bool SupportsImageInput { get; private set; }

    /// <summary>是否已建立连接（页面图片能力预检用）。</summary>
    public bool IsConnected => _client is { IsRunning: true };

    /// <summary>会话是否已被关闭（关闭后拒绝新操作）。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>由引擎工厂创建；apiKey 仅保存在内存（传子进程环境变量，不落盘）。</summary>
    private readonly DshLaunchConfig? _launch;
    private string? _resumeSessionId;   // 【A06】历史会话恢复用（首次连接时尝试，一次性）

    public DshSession(string apiKey, string workspace, DshLaunchConfig? launch = null)
    {
        _launch = launch;
        _apiKey = apiKey;
        _workspace = workspace;
        foreach (var skill in AgentSkillRegistry.All.Where(s => s.Id != AiAgentWorkflowSkill.Id)) _customSkillIds.Add(skill.Id);
    }

    // ---------- 连接 ----------

    private async Task<DshAcpClient> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_disposed) throw new InvalidOperationException("会话已关闭");

        Task<DshAcpClient> task;
        lock (_gate)
        {
            // ① 健康的现役连接：直接用
            if (_phase == ConnPhase.Ready && _client is { IsRunning: true })
                return _client;

            // ② 连接进行中（且未失败）：复用——并发初始化只启动一次进程
            if (_phase == ConnPhase.Connecting && _connectTask is { IsFaulted: false })
            {
                task = _connectTask;
            }
            else
            {
                // ③ 需要（重新）连接：清死引用 + 迁移状态 + 发布新任务，全部在同一锁内完成，
                //    防止线程 A 发布后被线程 B 用旧状态覆盖（第二轮复核指出的竞态）。
                _client = null;
                _sessionId = null;   // 重连 = 新会话（旧 sid 一并作废，Cancel 不再误判）
                _connectCts?.Dispose();
                _connectCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
                _phase = ConnPhase.Connecting;
                _connectTask = ConnectCoreAsync(ct, _connectCts.Token);
                task = _connectTask;
            }
        }

        try
        {
            var client = await task.ConfigureAwait(false);
            lock (_gate)
            {
                // 仅当仍是最新发布者时才写入（旧任务不得覆盖新任务）
                if (ReferenceEquals(_connectTask, task))
                {
                    _client = client;
                    _phase = ConnPhase.Ready;
                }
            }
            return client;
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_connectTask, task))
                {
                    _connectTask = null;
                    _phase = ConnPhase.Idle;   // 允许下次重试；旧任务失败不清新任务
                }
            }
            throw;
        }
    }

    private async Task<DshAcpClient> ConnectCoreAsync(CancellationToken ct, CancellationToken handshakeToken)
    {
        // 调用方取消 + 会话生命周期 + 握手取消（Cancel 在握手期触发），任一都会中断握手
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, handshakeToken);

        // 【A07】有启动配置时按 provider/model/baseURL/key 生效；否则保持旧行为
        var client = _launch is null
            ? DshAcpClient.Start(_apiKey, _workspace)
            : DshAcpClient.Start(_launch, _workspace);
        _pendingClient = client;  // 【A09】进程启动立即登记：握手期 Dispose/Cancel 可见
        try
        {
            client.StderrLine += line =>
            {
                if (!string.IsNullOrWhiteSpace(line)) Diagnostic?.Invoke(line);
            };
            client.SessionUpdate += OnUpdate;

            var init = await client.InitializeAsync(linked.Token).ConfigureAwait(false);
            // 【A12 审计修复】图片能力从握手结果协商（deepseek-v4-flash 等默认未声明 image，
            // 不可按模型系列一概开放；false 的连接发图会被拒绝——须重连重新协商）。
            SupportsImageInput = init["agentCapabilities"]?["promptCapabilities"]?["image"]
                ?.GetValue<bool>() ?? false;
            var clientTools = DshClientToolsBridge.BuildDefault();
            Diagnostic?.Invoke($"[引擎] 图片输入能力：{(SupportsImageInput ? "支持" : "不支持")}");
            // 【A06 返修】有历史会话 id 时先尝试 resume：cwd 用【保存的原始 cwd】（服务要求与持久记录匹配），
            // 不按当前 UI 的新 cwd 猜测；失败重建时【明确记录原因】（页面据此向用户告知，不再"无感"）。
            if (!string.IsNullOrEmpty(_resumeSessionId))
            {
                try
                {
                    var (resumedSid, result) = await client.ResumeSessionWithOptionsAsync(
                        _resumeSessionId, _resumeCwd ?? _workspace, (JsonArray)clientTools.DeepClone(), linked.Token).ConfigureAwait(false);
                    _sessionId = resumedSid;
                    ResumeOutcome = ResumeOutcomeKind.Resumed;
                    Diagnostic?.Invoke($"[引擎] dsh 历史会话已恢复（{_sessionId}）");

                    // 【A07 返修】resume 会恢复历史 logged 模型（dsh 不自动应用当前 patch 的模型选择）。
                    // 本次启动带了明确模型时：按真实 dsh-acp schema 的 configOptions（id/options，选项可能按
                    // provider 分组）匹配到【服务实际给出的那个 option】，原样取其 value 显式设置并核对响应；
                    // 无法确定/未生效/失败时如实记录，不得假称已切换到新模型。
                    if (_launch?.Model is { Length: > 0 } wanted)
                        await ApplyResumeModelSelectionAsync(client, wanted, result, linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Diagnostic?.Invoke($"[引擎] 历史会话恢复失败（{ex.Message}），已新建会话：" +
                                       MiscTexts.T("历史对话内容仍可在界面查看，但模型上下文不再延续。"));
                    ResumeOutcome = ResumeOutcomeKind.Recreated;
                    // 【A06 返修】重建用的是【当前 workspace】：必须清掉历史 cwd，
                    // 否则 EffectiveWorkspace（持久化/展示用）仍指向旧目录，与新建会话实际所在目录不一致。
                    _resumeCwd = null;
                    _sessionId = await client.NewSessionAsync(_workspace, (JsonArray)clientTools.DeepClone(), linked.Token).ConfigureAwait(false);
                }
                _resumeSessionId = null;   // 一次性：仅首次连接尝试
            }
            else
            {
                _sessionId = await client.NewSessionAsync(_workspace, (JsonArray)clientTools.DeepClone(), linked.Token).ConfigureAwait(false);
            }
            Diagnostic?.Invoke($"[引擎] dsh 已连接（会话 {_sessionId}）");
            return client;
        }
        catch
        {
            _pendingClient = null;
            await client.DisposeAsync().ConfigureAwait(false);  // 【A09】握手失败/取消：回收进程
            throw;
        }
    }

    /// <summary>预先建立连接（可选；不调用则首次发送时自动连接）。</summary>
    public Task PrewarmAsync(CancellationToken ct = default) => EnsureConnectedAsync(ct);

    // ---------- 发送/取消 ----------

    public Task SendAsync(string userText, IReadOnlyList<(byte[] Bytes, string MediaType)>? images = null)
        => SendWithSkillInputAsync(userText, userText, images);

    internal async Task SendWithSkillInputAsync(string userText, string rawSkillInput,
        IReadOnlyList<(byte[] Bytes, string MediaType)>? images = null, bool? continuesCurrentTask = null)
    {
        // 【A09】关闭后拒绝一切新发送（含握手期销毁后的迟到发送）
        if (_disposed)
        {
            LastSendOutcome = AgentSendOutcome.Rejected;
            Error?.Invoke(MiscTexts.T("会话已关闭，无法发送消息。"));
            return;
        }

        if (_running)
        {
            LastSendOutcome = AgentSendOutcome.Rejected;
            Error?.Invoke(MiscTexts.T("上一轮还在进行中，请先停止或等待完成。"));
            return;
        }

        if (images is { Count: > 0 } && !SupportsImageInput && _client is not null)
        {
            Interlocked.Exchange(ref _stopRequested, 0);
            // 【A12 审计修复】连接已建立且未协商图片能力：明确反馈并保留本轮（不静默丢弃、
            // 不发送不带图的残缺消息）。连接尚未建立时先连接、在下方重新判断。
            LastSendOutcome = AgentSendOutcome.Rejected;
            Error?.Invoke(MiscTexts.T("当前引擎连接的图片输入不可用（图片能力未协商，模型可能不支持视觉）。图片未发送，请移除附件或切换模型/引擎后重试。"));
            return;
        }

        if ((Title == "新会话" || Title == "New session") && !string.IsNullOrWhiteSpace(userText))
            Title = userText.Length <= 20 ? userText : userText[..20] + "…";

        long myTurn = 0;   // 【A09 返修】本轮编号（finally 只允许清【自己】这一轮）
        var skillReference = _skillTaskContinuity.BuildReference(rawSkillInput, ActiveSkillIds, continuesCurrentTask);
        try
        {
            _running = true;
            myTurn = Interlocked.Increment(ref _turnSeq);
            Interlocked.Exchange(ref _activeTurn, myTurn);  // 【A09】轮次身份（取消定时器据此锁定）
            Interlocked.Exchange(ref _stopRequested, 0);  // 【A09】新一轮开始：清除陈旧停止标志
            _turnStartedAt = DateTime.Now;
            _turnStartTokens = TotalPromptTokens;
            _turnSteps.Clear();                           // 【A06 返修】本轮步骤收集重置
            RoundStarted?.Invoke();

            // 【A09】全程绑定会话生命周期：Dispose 会中断握手/请求
            await EnsureConnectedAsync(_lifecycleCts.Token).ConfigureAwait(false);

            // 【A09 第二轮复核】"握手完成 → 提交 prompt"窗口：期间被取消则不再提交
            if (Interlocked.CompareExchange(ref _stopRequested, 0, 1) == 1)
            {
                LastSendOutcome = AgentSendOutcome.Cancelled;   // 【A12】明确结果：窗口期被停止
                Diagnostic?.Invoke("[引擎] 本轮在发送前被停止（初始化/prompt 窗口）");
                return;   // finally 会复位 _running 并触发 RunCompleted
            }

            // 【第二轮复核】resume 后的模型切换未成立（未提供/未匹配/歧义/被拒/未生效）：
            // 不得用历史模型（或其它未确认配置）悄悄把 prompt 发出去——先阻断，并把结果暴露给页面
            // （ResumeModelSelection / ResumeModelSelectionMessage / IsModelSwitchBlocked）。
            // 当前页面提示用户新建会话；调用方显式确认历史模型时才可使用放行接口。
            if (IsModelSwitchBlocked)
            {
                LastSendOutcome = AgentSendOutcome.Rejected;
                Diagnostic?.Invoke($"[引擎] 本轮未提交：历史会话模型切换未成立（{ResumeModelSelection}，等待用户确认）");
                Error?.Invoke(ModelSwitchBlockMessage!);
                return;   // finally 会复位 _running 并触发 RunCompleted
            }

            // 【A12】首次发送时连接刚建立：按协商结果处置图片
            if (images is { Count: > 0 } && !SupportsImageInput)
            {
                LastSendOutcome = AgentSendOutcome.Rejected;
                Error?.Invoke(MiscTexts.T("当前引擎连接的图片输入不可用（图片能力未协商）。图片未发送，请移除附件或切换模型/引擎后重试。"));
                return;   // finally 会复位 _running 并触发 RunCompleted
            }

            // dsh 的技能目录只提供索引；显式 /name 可在这一轮加载完整 SKILL.md。
            // 标题、UI 和本地历史仍使用原始 userText，不把产品调用符显示成用户输入。
            var promptText = AgentPersonaCatalog.BuildDshPrompt(
                userText, PersonaId, _launch?.BuiltinGoalSkillProjected == true);
            promptText += skillReference;
            promptText += "\n\n[客户端只读查询能力]\n" + DshClientToolsBridge.Prompt;
            promptText += "\n\n" + TubaWinUi3.Services.ToolFlows.ToolFlowProposalParser.BuildOutputInstructions(
                TubaWinUi3.Services.AppManagement.SystemInstaller.KnownTargets,
                TubaWinUi3.Services.AppManagement.SystemInstaller.GetTargetDescriptions());
            var stop = await _client!.PromptAsync(_sessionId!, promptText,
                images is { Count: > 0 } ? images : null, _lifecycleCts.Token)
                .ConfigureAwait(false);
            // 【A12 返修】取消从低层传播为【明确发送结果】（不再只记 Diagnostic 后按正常返回）：
            // stopReason=cancelled → Cancelled；其余（end_turn/refusal 等）→ Completed。
            LastSendOutcome = stop == "cancelled" ? AgentSendOutcome.Cancelled : AgentSendOutcome.Completed;
            if (stop is "refusal" or "cancelled")
                Diagnostic?.Invoke($"[引擎] 本轮结束：{stop}");
        }
        catch (OperationCanceledException)
        {
            // 会话关闭/取消导致的正常中止：不报错，但向调用方给出【明确结果】
            LastSendOutcome = AgentSendOutcome.Cancelled;
            Diagnostic?.Invoke("[引擎] 本轮已被取消");
        }
        catch (Exception ex)
        {
            LastSendOutcome = AgentSendOutcome.Failed;
            Error?.Invoke(MiscTexts.TSub($"AI 服务请求失败：{ex.Message}"));
        }
        finally
        {
            // 【A06 返修】本轮工具步骤组完成 → 汇总事件（页面据此持久化 steps 展示记录）。
            // 旧实现从不 Invoke 本事件，dsh 工具链因此从未被存档。
            if (_turnSteps.Count > 0)
            {
                var summary = new AgentStepGroupSummary
                {
                    Total = _turnSteps.Count,
                    Success = _turnSteps.Count(s => s.Status == AgentStepStatus.Success),
                    Failed = _turnSteps.Count(s => s.Status == AgentStepStatus.Failed),
                    Duration = DateTime.Now - _turnStartedAt,
                    PromptTokens = Math.Max(0, TotalPromptTokens - _turnStartTokens),
                };
                _turnSteps.Clear();
                StepGroupCompleted?.Invoke(summary);
            }

            _running = false;
            // 【A09 测试】清零前的 barrier（生产 null）：把"旧轮 finally ⇄ 新轮 Exchange"的交错窗口
            // 拉长即可确定性回归（不得靠"第二轮完整跑完"这种宽松时序碰运气）。
            if (BeforeTurnClearForTest is { } turnBarrier) { try { await turnBarrier().ConfigureAwait(false); } catch { } }
            // 【A09 返修】只清【自己这一轮】的编号：旧实现的 CAS 目标是"读到的当前值"（=无条件清零），
            // 旧轮收尾时会把刚开始的【新轮】编号抹成 0——取消回收定时器按轮次核对（_gate 内 turnAtCancel
            // 比较）随之误判：该回收的滞留连接不回收，或把新轮正在用的连接当作旧轮回收。
            Interlocked.CompareExchange(ref _activeTurn, 0, myTurn);
            RunCompleted?.Invoke();
        }
    }

    public Task ResumeConfirmationsAsync(IReadOnlyList<AgentConfirmationDecision> decisions)
        => Task.CompletedTask; // dsh 引擎：权限/确认由 harness 自管

    public void Cancel()
    {
        if (_disposed) return;

        // 【停止状态】无论哪个阶段先置位：SendAsync 在"连接完成→提交 prompt"的窗口检查它，
        // 保证在初始化到 prompt 之间的取消也生效（第二轮复核指出的窗口）。
        Interlocked.Exchange(ref _stopRequested, 1);

        DshAcpClient? activeClient;
        string? activeSid;
        CancellationTokenSource? connectCts;
        bool ready;
        lock (_gate)
        {
            // 【阶段驱动】（不再用 sessionId 是否为空判断握手）：
            ready = _phase == ConnPhase.Ready && _sessionId is not null && _client is { IsRunning: true };
            activeClient = ready ? _client : null;
            activeSid = ready ? _sessionId : null;
            connectCts = ready ? null : _connectCts;
        }

        if (ready && activeClient is not null && activeSid is not null)
        {
            // 已建立会话（prompt 进行中或其前后）：发 session/cancel 通知（A02）；
            // 远端终结由 PromptAsync 返回体现；超时未见终结则【真正回收 owned client/进程】。
            var turnAtCancel = Interlocked.Read(ref _activeTurn);
            var clientAtCancel = activeClient;
            _ = activeClient.CancelAsync(activeSid);
            _ = Task.Delay(CancelReclaimTimeout).ContinueWith(async _ =>
            {
                // 【A09 测试】barrier：进锁前可暂停（TOCTOU 确定性回归用；生产为 null）
                if (ReclaimBarrierForTest is { } barrier) { try { await barrier().ConfigureAwait(false); } catch { } }

                DshAcpClient? stale = null;
                lock (_gate)
                {
                    // 【A09 返修·TOCTOU】同一 _gate 内一次性核对「captured 轮次」与「captured client 身份」后再摘取。
                    // 旧实现"锁外读轮次 → 锁内抓当前 client"的窗口：旧轮刚结束、新轮已复用同一 client 时，
                    // 会把新轮正在使用的 client 错误回收掉。
                    if (_disposed || turnAtCancel == 0) return;
                    if (Interlocked.Read(ref _activeTurn) != turnAtCancel) return;
                    if (!ReferenceEquals(_client, clientAtCancel) && !ReferenceEquals(_pendingClient, clientAtCancel)) return;

                    stale = _client ?? _pendingClient;
                    _client = null;
                    _pendingClient = null;
                    _sessionId = null;
                    _connectTask = null;
                    _phase = ConnPhase.Idle;   // 允许后续重连（重新走完整握手）
                }
                if (stale is not null)
                {
                    Diagnostic?.Invoke("[引擎] 取消后未收到结束响应：已回收连接（防止远端任务继续执行）");
                    try { await stale.DisposeAsync().ConfigureAwait(false); } catch { }  // Kill 进程树 + 等待退出
                }
            }, TaskScheduler.Default);
            return;
        }

        // 非 Ready 阶段（初次握手 / 重连握手 / 初始化到 prompt 之间）：
        // 取消连接令牌 → ConnectCoreAsync 中止并回收进程。旧 sid 已被 EnsureConnected 清除，
        // 不存在"旧 sid 非空 → 误发通知后 return"的路径。
        try { connectCts?.Cancel(); } catch { }
    }

    public void SetSkillEnabled(string id, bool enabled)
    {
        if (_disposed || id == AiAgentWorkflowSkill.Id || AgentSkillRegistry.Find(id) is null) return;
        lock (_skillsGate) { if (enabled) _customSkillIds.Add(id); else _customSkillIds.Remove(id); _skillStateUnreadable = false; }
        if (!enabled) _skillTaskContinuity.Remove(id);
    }

    public void SetPersona(string personaId)
    {
        if (_running) return;
        PersonaId = AgentPersonaCatalog.Resolve(personaId).Id;
    }

    public void Rename(string title)
    {
        if (!string.IsNullOrWhiteSpace(title)) Title = title.Trim();
    }

    public void Save()
    {
        var path = SkillsStatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string[] ids;
            lock (_skillsGate)
            {
                if (_skillStateUnreadable) return;
                ids = (_launch?.BuiltinGoalSkillProjected == true ? _customSkillIds.Append(AiAgentWorkflowSkill.Id) : _customSkillIds)
                    .Order(StringComparer.Ordinal).ToArray();
            }
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { System.Text.Json.JsonSerializer.Serialize(file, ids); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string SkillsStatePath()
    {
        if (Id.Length is < 1 or > 100 || Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))) throw new InvalidDataException("Invalid session id.");
        return Path.Combine(ConfigManager.GetDataDir(), "AiAssistant", Id + ".skills.json");
    }
    private void LoadCustomSkills()
    {
        lock (_skillsGate)
        {
            _customSkillIds.Clear();
            _skillStateUnreadable = false;
            try
            {
                var path = SkillsStatePath();
                var ids = File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))
                    ?? throw new InvalidDataException() : AgentSkillRegistry.All.Select(s => s.Id).ToArray();
                foreach (var id in ids)
                    if (id != AiAgentWorkflowSkill.Id && AgentSkillRegistry.Find(id) is not null) _customSkillIds.Add(id);
            }
            catch { _skillStateUnreadable = true; Diagnostic?.Invoke("[技能] 会话技能状态未读取，自定义技能暂未启用；原文件保留。"); }
        }
    }

    // ---------- 【A07 返修 / 第二轮复核】resume 后的模型切换（真实 dsh-acp configOptions schema） ----------

    /// <summary>【第二轮复核】切换未成立时附在结果文案后的统一提示（阻断语义）。</summary>
    private static readonly string ModelSwitchBlockNotice =
        MiscTexts.T("为避免在未确认的模型上发送，本轮发送已被阻断；用户确认后可继续沿用该会话的历史模型，或新建会话以使用当前所选模型。");

    /// <summary>
    /// 【A07 返修 / 第二轮复核】resume 后按"当前选择的模型"显式设置历史会话：
    /// ① 从 resume 返回的 configOptions 解析 model 配置项——真实 dsh-acp 的形态是
    ///    <c>configOptions:[{ id:'model', type:'select', currentValue, options:[…] }]</c>，
    ///    其中 options 可能是 provider 分组（<c>{group,name,options:[{value,name}]}</c>）或扁平列表；
    ///    【第二轮复核】分组形态必须保留供应商上下文（group/name），并从服务自有的不透明 value
    ///    （真实现 = <c>JSON.stringify([provider, model])</c>）还原真实路由供消歧；
    /// ② 按启动配置【实际产生的 ACP route provider】（<c>DshLaunchConfig.RouteProviderId</c>：
    ///    DeepSeek → deepseek-official，自定义 → zxai-selected）与 value 内的 API model ID 精确匹配；
    ///    供应商展示名、供应商前缀、模型展示名均【不作为身份】；候选唯一也不能忽略路由不符——
    ///    不匹配（含"唯一但供应商不对"）或仍不唯一时一律拒绝，走 NoMatch/Ambiguous + 发送阻断点；
    /// ③ 原样取用服务给出的 option.value——value 是服务自有的不透明编码，绝不自行拼造；
    /// ④ <c>session/set_config_option</c> 后用响应里的 <c>currentValue</c> 核对（真实实现返回设置后的
    ///    完整选项状态）——未生效/失败/无匹配一律如实记录，不得声称已切换；
    /// ⑤ 【第二轮复核】每种结果都落到【可读状态】（ResumeModelSelection / ResumeModelSelectionMessage），
    ///    页面据此提醒/阻断；且未确认前 SendAsync 不提交 prompt。
    /// </summary>
    private async Task ApplyResumeModelSelectionAsync(DshAcpClient client, string wanted, JsonObject resumeResult, CancellationToken ct)
    {
        var choices = ParseModelChoices(resumeResult);
        if (choices is null)
        {
            SetResumeModelFailure(ResumeModelSelectionKind.NotProvided,
                "[引擎] 历史会话可能仍使用其 logged 模型（服务未提供 model 配置项）——如需确认切换，请新建会话。" +
                ModelSwitchBlockNotice);
            return;
        }
        if (choices.Count == 0)
        {
            SetResumeModelFailure(ResumeModelSelectionKind.NoChoices,
                $"[引擎] 历史会话仍使用其 logged 模型（服务未列出可选模型，无法切换到「{wanted}」）——如需确认切换，请新建会话。" +
                ModelSwitchBlockNotice);
            return;
        }

        var launchProvider = _launch?.RouteProviderId;
        var pick = PickModelChoice(choices, wanted, launchProvider);
        switch (pick.Kind)
        {
            case ModelPickKind.NoMatch:
                SetResumeModelFailure(ResumeModelSelectionKind.NoMatch,
                    $"[引擎] 历史会话仍使用其 logged 模型（服务返回的模型选项里没有与「{wanted}」精确匹配的项）——" +
                    MiscTexts.T("如需确认切换，请新建会话。") + ModelSwitchBlockNotice);
                return;

            case ModelPickKind.Ambiguous:
                SetResumeModelFailure(ResumeModelSelectionKind.Ambiguous,
                    $"[引擎] 历史会话模型未切换：服务返回 {pick.Matches.Count} 个与「{wanted}」精确匹配的候选" +
                    $"（{DescribeChoices(pick.Matches)}），无法按所选供应商（{launchProvider ?? "（未知）"}）收敛——拒绝猜测编码。" +
                    ModelSwitchBlockNotice);
                return;
        }

        var chosen = pick.Choice!;   // 已按 route provider + API model ID 精确匹配（唯一）筛出，无需再核对
        try
        {
            var response = await client.SetConfigOptionAsync(_sessionId!, "model", chosen.Value, ct).ConfigureAwait(false);
            var applied = ModelCurrentValue(response);
            if (!string.Equals(applied, chosen.Value, StringComparison.Ordinal))
            {
                SetResumeModelFailure(ResumeModelSelectionKind.NotApplied,
                    $"[引擎] 历史会话模型切换未生效（请求值 {chosen.Value}，服务当前值 {applied ?? "（未返回）"}）——" +
                    MiscTexts.T("该会话可能仍使用历史模型。") + ModelSwitchBlockNotice);
                return;
            }
            ResumeModelSelection = ResumeModelSelectionKind.Applied;
            ResumeModelSelectionMessage = null;
            Diagnostic?.Invoke($"[引擎] 已按当前选择设置历史会话模型：{chosen.Label}");
        }
        catch (Exception setEx)
        {
            SetResumeModelFailure(ResumeModelSelectionKind.Rejected,
                $"[引擎] 历史会话模型切换未确认（{setEx.Message}）；该会话可能仍使用历史模型。" + ModelSwitchBlockNotice);
        }
    }

    /// <summary>【第二轮复核】模型切换未成立：同时写 Diagnostic（日志）与可读状态（页面消费）。</summary>
    private void SetResumeModelFailure(ResumeModelSelectionKind kind, string message)
    {
        ResumeModelSelection = kind;
        ResumeModelSelectionMessage = message;
        Diagnostic?.Invoke(message);
    }

    /// <summary>【第二轮复核】阻断/提示文案里的简短原因。</summary>
    private static string ModelSwitchKindText(ResumeModelSelectionKind kind) => kind switch
    {
        ResumeModelSelectionKind.NotProvided => MiscTexts.T("服务未提供模型配置项"),
        ResumeModelSelectionKind.NoChoices => MiscTexts.T("服务未列出可选模型"),
        ResumeModelSelectionKind.NoMatch => MiscTexts.T("服务候选里没有精确匹配所选模型的项"),
        ResumeModelSelectionKind.Ambiguous => MiscTexts.T("服务候选有多个同名模型，无法按供应商唯一确定"),
        ResumeModelSelectionKind.Rejected => MiscTexts.T("服务拒绝了模型切换"),
        ResumeModelSelectionKind.NotApplied => MiscTexts.T("服务未报错但切换未生效"),
        _ => kind.ToString(),
    };

    /// <summary>候选摘要（歧义提示用；最多列 4 个，避免文案过长）。</summary>
    private static string DescribeChoices(IReadOnlyList<ModelChoice> matches)
        => string.Join(MiscTexts.T("、"), matches.Take(4).Select(c => c.Label)) +
           (matches.Count > 4 ? MiscTexts.TSub($"…等 {matches.Count} 项") : "");

    /// <summary>configOptions 里的一个模型候选（Value = 服务自有的不透明编码，Name = 展示名）。
    /// 【第二轮复核】额外保留【供应商上下文】：分组 id / 分组名（分组形态）与 value 内还原出的真实路由。</summary>
    private sealed record ModelChoice(string Value, string? Name, string? ProviderId, string? ProviderLabel)
    {
        /// <summary>不透明编码还原出的 (provider, model) 路由（真实现 = JSON.stringify([provider, model])）；还原不出 = null。</summary>
        public (string Provider, string Model)? Route { get; init; }

        public string Label => Name is { Length: > 0 } n ? MiscTexts.TSub($"{n}（{Value}）") : Value;
    }

    /// <summary>解析 resume 返回的 model 配置项候选（【第二轮复核】保留供应商分组上下文）；
    /// 无 model 项/结构不符返回 null。</summary>
    private static List<ModelChoice>? ParseModelChoices(JsonObject resumeResult)
    {
        if (resumeResult["configOptions"] is not JsonArray configs) return null;
        var modelOpt = configs.OfType<JsonObject>().FirstOrDefault(o => GetStr(o, "id") == "model");
        if (modelOpt?["options"] is not JsonArray entries) return null;

        var choices = new List<ModelChoice>();
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var groupId = GetStr(entry, "group");
            var groupName = GetStr(entry, "name");
            // 扁平形态：[{value,name}]（供应商上下文只能靠 value 内的路由还原）
            if (GetStr(entry, "value") is { Length: > 0 } flatValue)
                choices.Add(MakeChoice(flatValue, GetStr(entry, "name"), groupId, groupName));
            // provider 分组形态：[{group,name,options:[{value,name}]}]
            if (entry["options"] is not JsonArray grouped) continue;
            foreach (var g in grouped.OfType<JsonObject>())
                if (GetStr(g, "value") is { Length: > 0 } groupedValue)
                    choices.Add(MakeChoice(groupedValue, GetStr(g, "name"), groupId, groupName));
        }
        return choices;
    }

    private static ModelChoice MakeChoice(string value, string? name, string? providerId, string? providerLabel)
        => new(value, name, providerId, providerLabel) { Route = ParseRoute(value) };

    /// <summary>还原不透明 value 的 (provider, model) 路由；形状必须是恰好两元素字符串数组
    /// （真实现 = JSON.stringify([provider, model])），否则 null（不抛、不猜）。</summary>
    private static (string Provider, string Model)? ParseRoute(string value)
    {
        try
        {
            if (JsonNode.Parse(value) is not JsonArray arr || arr.Count != 2) return null;
            var provider = arr[0]?.GetValue<string>();
            var model = arr[1]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) return null;
            return (provider!, model!);
        }
        catch { return null; }
    }

    private enum ModelPickKind { Matched, NoMatch, Ambiguous }

    /// <summary>匹配结果：Matched 时 Choice 非空；Matches = 全部【精确身份】候选（歧义提示用）。</summary>
    private sealed record ModelPickResult(ModelPickKind Kind, ModelChoice? Choice, IReadOnlyList<ModelChoice> Matches);

    /// <summary>
    /// 【第二轮复核】在启动配置【实际产生的 ACP route provider】内，按 API model ID 精确匹配：
    /// 身份 = value 内 route 的 model（Ordinal 完全相等）；展示名、供应商前缀、模型名前缀均不是身份
    /// （旧实现的 Contains 会把「gpt-4」误配到「gpt-4o」）。
    /// 候选必须【路由相符】——唯一的错误供应商候选同样拒绝。去重后仍不唯一 → Ambiguous。
    /// </summary>
    private static ModelPickResult PickModelChoice(List<ModelChoice> choices, string wanted, string? routeProviderId)
    {
        var matches = choices
            .Where(c => IsExactModelIdentity(c, wanted) && ChoiceMatchesProvider(c, routeProviderId))
            .DistinctBy(c => c.Value, StringComparer.Ordinal)
            .ToList();
        return matches.Count switch
        {
            0 => new ModelPickResult(ModelPickKind.NoMatch, null, matches),
            1 => new ModelPickResult(ModelPickKind.Matched, matches[0], matches),
            _ => new ModelPickResult(ModelPickKind.Ambiguous, null, matches),
        };
    }

    /// <summary>模型身份精确相等 = value 内 route 的 model ID 与所选完全一致（Ordinal，不做大小写/前缀近似）。</summary>
    private static bool IsExactModelIdentity(ModelChoice c, string wanted)
        => c.Route is { } route && string.Equals(route.Model, wanted, StringComparison.Ordinal);

    /// <summary>候选是否属于启动配置的真实路由：route provider 必须与 RouteProviderId 完全相等；
    /// 分组形态下 group 也必须等于该 route provider（分组存在时的自洽校验）。</summary>
    private static bool ChoiceMatchesProvider(ModelChoice c, string? routeProviderId)
        => !string.IsNullOrWhiteSpace(routeProviderId)
           && c.Route is { } route
           && string.Equals(route.Provider, routeProviderId, StringComparison.Ordinal)
           && (string.IsNullOrWhiteSpace(c.ProviderId)
               || string.Equals(c.ProviderId, route.Provider, StringComparison.Ordinal));

    /// <summary>响应里 model 配置项的当前值（核对设置是否真的生效；取不到返回 null）。</summary>
    private static string? ModelCurrentValue(JsonObject response)
        => response["configOptions"] is JsonArray configs
           && configs.OfType<JsonObject>().FirstOrDefault(o => GetStr(o, "id") == "model") is { } modelOpt
            ? GetStr(modelOpt, "currentValue")
            : null;

    /// <summary>从 JSON 对象取字符串字段（缺失/类型不符 → null，不抛）。</summary>
    private static string? GetStr(JsonObject obj, string key)
    {
        try { return obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; }
        catch { return null; }
    }

    // ---------- ACP update → 事件 ----------

    private void OnUpdate(DshAcpUpdate u)
    {
        switch (u.Kind)
        {
            case "agent_message_chunk" when u.Text is not null:
                TextChunk?.Invoke(u.Text);
                break;

            case "agent_thought_chunk" when u.Text is not null:
                ReasoningChunk?.Invoke(u.Text);
                break;

            case "tool_call":
            {
                var step = new AgentStep
                {
                    ToolName = u.ToolTitle ?? u.ToolCallId ?? "tool",
                    DisplayName = u.ToolTitle ?? MiscTexts.T("工具调用"),
                    Glyph = GlyphFor(u.ToolKind),
                    Summary = Summarize(u.RawInput),
                    Detail = u.RawInput,
                    CallId = u.ToolCallId,
                    Status = AgentStepStatus.Running,
                };
                if (u.ToolCallId is not null) _steps[u.ToolCallId] = step;
                _turnSteps.Add(step);   // 【A06 返修】本轮步骤收集（组完成时汇总存档）
                StepStarted?.Invoke(step);
                break;
            }

            case "tool_call_update":
            {
                if (u.ToolCallId is null || !_steps.TryGetValue(u.ToolCallId, out var step)) break;
                step.Status = u.ToolStatus switch
                {
                    "completed" => AgentStepStatus.Success,
                    "failed" => AgentStepStatus.Failed,
                    "cancelled" => AgentStepStatus.Cancelled,
                    "in_progress" or "pending" => AgentStepStatus.Running,
                    _ => step.Status,
                };
                if (u.RawOutput is not null) step.Result = u.RawOutput;
                if (step.Status is AgentStepStatus.Success or AgentStepStatus.Failed or AgentStepStatus.Cancelled)
                {
                    step.Duration = DateTime.Now - step.StartedAt;
                    StepCompleted?.Invoke(step);
                }
                break;
            }

            case "usage_update":
                if (u.UsedTokens is { } used) TotalPromptTokens = (int)Math.Min(used, int.MaxValue);
                if (u.ContextSize is { } size) ContextSize = size;
                break;

            default:
                Diagnostic?.Invoke($"[更新] {u.Kind}"); // 未知类型不丢（升级兼容）
                break;
        }
    }

    private static string GlyphFor(string? kind) => kind switch
    {
        "read" => "\uE8A5",
        "edit" => "\uE70F",
        "execute" => "\uE756",
        "search" => "\uE721",
        _ => "\uE712",
    };

    private static string Summarize(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return "";
        var s = rawInput.Replace('\n', ' ').Trim();
        return s.Length <= 120 ? s : s[..120] + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 【A09】生命周期取消：中断在途握手/请求（ConnectCore 的 linked 会观察它）
        try { _lifecycleCts.Cancel(); } catch { }

        // 【A09 第二轮复核】回收进程（含握手期已登记但未交付的进程）——引用读取在锁内
        DshAcpClient? client;
        Task<DshAcpClient>? pendingTask;
        lock (_gate)
        {
            client = _client ?? _pendingClient;
            pendingTask = _connectTask;
            _client = null;
            _pendingClient = null;
            _connectTask = null;
            _phase = ConnPhase.Idle;
        }
        if (client is not null)
        {
            try
            {
                // 同步等待有上限（进程树 Kill 已发出；超时后后台继续收尾）
                client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch { /* 超时/异常：进程树已被 Kill 请求，后台收尾 */ }
        }
        try { _lifecycleCts.Dispose(); } catch { }
    }
}
