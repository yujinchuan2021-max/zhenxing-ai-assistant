using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TubaWinUi3.Services.Ai;

namespace TubaWinUi3.Services.Agent;

/// <summary>
/// 一次 AI 对话会话：封装 Agent 循环、确认流、上下文记忆与持久化。
/// 事件在工作线程触发，UI 层通过 DispatcherQueue marshal。
/// </summary>
public sealed class AgentSession : IAgentSession
{
    private readonly List<ChatMessage> _history = [];
    private readonly List<AgentStep> _groupSteps = [];
    private readonly List<ConversationDisplayItem> _displayItems = [];
    private readonly StringBuilder _textSb = new();
    private readonly StringBuilder _reasoningSb = new();
    private readonly ConversationMemory _memory;
    private readonly HashSet<string> _activeSkillIds = [];
    private readonly SkillTaskContinuity _skillTaskContinuity = new();
    // One client-owned, transient user message combines current core and custom references.
    private ChatMessage? _turnSkillReference;
    internal CoreSkillReferenceState LastCoreSkillReferenceState { get; private set; } = CoreSkillReferenceState.NotProvided;
    internal IReadOnlyCollection<string> ReferencedCustomSkillIds => _skillTaskContinuity.ReferencedSkillIds;
    internal void RestoreCustomSkillTask(string confirmedGoal)
        => _skillTaskContinuity.RestoreFromGoal(confirmedGoal, _activeSkillIds);
    private bool _skillTriggerActive;
    private CancellationTokenSource _cts = new();
    private bool _awaitingConfirmation;
    private int _replyRunDisplayStart;
    private bool _replyRunHadError;
    private IReadOnlyList<AgentConfirmationRequest> _pendingConfirmations = [];
    private System.Diagnostics.Stopwatch? _groupTimer;
    private int _groupPromptTokens;
    private int _groupCompletionTokens;
    private int _groupCacheHitTokens;
    private int _groupCacheMissTokens;
    /// <summary>上一组是否已结算。为 false 表示处于确认暂停期（步骤组保持打开）。</summary>
    private bool _groupCompleted = true;
    /// <summary>当前正在填充的步骤链展示项。</summary>
    private ConversationDisplayItem? _openStepsItem;

    public string Id { get; private set; }
    public string Title { get; private set; }
    public bool IsRunning { get; private set; }
    public string PersonaId { get; private set; } = AgentPersonaCatalog.DefaultId;

    /// <summary>【主审修复·第六轮】保存守卫（由页面注入：写盘前校验本会话的写入归属；
    /// null=不校验——供独立/测试场景）。builtin 的 Save（含 Dispose 内部调用）统一经此守卫。</summary>
    internal Func<string, bool>? SaveGuard { get; set; }

    /// <summary>本会话累计 token 消耗（跨轮次累加，不受步骤组重置影响）。</summary>
    public int TotalPromptTokens { get; private set; }
    public int TotalCompletionTokens { get; private set; }
    public int TotalTokens => TotalPromptTokens + TotalCompletionTokens;

    /// <summary>本会话累计缓存命中/未命中 token（提供商/网关返回缓存统计时才有值）。</summary>
    public int TotalCacheHitTokens { get; private set; }
    public int TotalCacheMissTokens { get; private set; }

    /// <summary>流式文本增量。</summary>
    public event Action<string>? TextChunk;

    /// <summary>流式思维过程增量，供界面独立折叠展示。</summary>
    public event Action<string>? ReasoningChunk;

    /// <summary>步骤开始。</summary>
    public event Action<AgentStep>? StepStarted;

    /// <summary>步骤结束（成功/失败/拒绝/取消）。</summary>
    public event Action<AgentStep>? StepCompleted;

    /// <summary>需要用户确认（循环暂停）。</summary>
    public event Action<IReadOnlyList<AgentConfirmationRequest>>? ConfirmationsRequested;

    /// <summary>错误（连接失败/轮次上限）。</summary>
    public event Action<string>? Error;

    /// <summary>本轮正常完成（无待确认）。</summary>
    public event Action? RunCompleted;

    /// <summary>新一轮 API 请求开始（UI 据此重置「思考/排队」状态）。</summary>
    public event Action? RoundStarted;

    /// <summary>整条步骤链完成（UI 据此折叠为摘要节点）。</summary>
    public event Action<AgentStepGroupSummary>? StepGroupCompleted;

    /// <summary>会话记忆文件读写（供记忆工具访问）。</summary>
    internal ConversationMemory Memory => _memory;

    /// <summary>本会话已激活的技能 Id（技能菜单 UI 读取）。</summary>
    public IReadOnlyCollection<string> ActiveSkillIds => _activeSkillIds;

    /// <summary>
    /// 技能强制触发激活中（本次发送内 web_search 被 Runtime 禁用，强制模型用浏览器查价）。
    /// </summary>
    internal bool IsSkillTriggerActive => _skillTriggerActive;

    private static string HistoryDir => Path.Combine(ConfigManager.GetDataDir(), "AiAssistant");

    private AgentSession(string id, string title)
    {
        Id = id;
        Title = title;
        _memory = new ConversationMemory(Path.Combine(HistoryDir, $"{id}.memory.md"));
        LoadSkills();
    }

    public static AgentSession CreateNew()
    {
        var session = new AgentSession(Guid.NewGuid().ToString("N")[..12], MiscTexts.T("新对话"));
        session.EnsureSystemPrompt();
        return session;
    }

    public static AgentSession Load(ConversationMeta meta)
    {
        var session = new AgentSession(meta.Id, meta.Title);
        session.PersonaId = AgentPersonaCatalog.Resolve(meta.PersonaId).Id;
        var aiMessages = AiAssistantService.LoadConversation(meta.Id);
        session._history.AddRange(AgentMessageConverter.ToChatMessages(aiMessages));
        var display = AiAssistantService.LoadConversationDisplay(meta.Id);
        session._displayItems.AddRange(display);
        // 恢复会话级 token 统计（meta 条目，多轮累计展示用）
        var metaItem = display.FirstOrDefault(i => i.Type == "meta");
        if (metaItem is not null)
        {
            session.TotalPromptTokens = metaItem.PromptTokens;
            session.TotalCompletionTokens = metaItem.CompletionTokens;
            session.TotalCacheHitTokens = metaItem.CacheHitTokens;
            session.TotalCacheMissTokens = metaItem.CacheMissTokens;
        }
        session.EnsureSystemPrompt();
        return session;
    }

    /// <summary>【A12】最近一轮发送的明确结果（页面按「成功才清空附件」消费）。</summary>
    public AgentSendOutcome LastSendOutcome { get; private set; } = AgentSendOutcome.Completed;

    /// <summary>发送用户消息并运行 Agent 循环（工作线程执行；异常会向上抛出由 UI 处理）。
    /// images：可选图片附件（多模态，直发模型视觉通道——DeepSeek V4.1 支持 image_url）。</summary>
    public Task SendAsync(string userText, IReadOnlyList<(byte[] Bytes, string MediaType)>? images = null)
        => SendWithSkillInputAsync(userText, userText, images);

    internal async Task SendWithSkillInputAsync(string userText, string rawSkillInput,
        IReadOnlyList<(byte[] Bytes, string MediaType)>? images = null, bool? continuesCurrentTask = null)
    {
        if (IsRunning || _awaitingConfirmation || string.IsNullOrWhiteSpace(userText))
        {
            LastSendOutcome = AgentSendOutcome.Rejected;   // 【A12】未提交：明确结果
            return;
        }

        // ZXAI：新用户消息 = 新任务段，重置无进展计数（否则上次触顶后每条消息都被秒杀）
        AgentToolContext.Current = this;
        AgentToolLoopGuard.OnUserMessage();

        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _groupSteps.Clear();
        _groupTimer = System.Diagnostics.Stopwatch.StartNew();
        _groupPromptTokens = 0;
        _groupCompletionTokens = 0;
        _groupCompleted = true;
        _replyRunDisplayStart = _displayItems.Count;
        _replyRunHadError = false;
        _displayItems.Add(new ConversationDisplayItem { Type = "text", Role = "user", Content = userText });
        AgentDebugLog.Info($"SendAsync 开始：{AgentToolHelpers.Truncate(userText, 60)}");

        try
        {
            // Remove only the message object created by this client, never text that a user can imitate.
            RemoveCustomSkillReference(_history, _turnSkillReference);
            _turnSkillReference = null;
            LastCoreSkillReferenceState = CoreSkillReferenceState.NotProvided;
            _skillTaskContinuity.ObserveTaskInput(rawSkillInput, continuesCurrentTask);
            EnsureSystemPrompt();

            // 原始输入与附件、OCR、技能正文分开；续接只复用本任务实际注入过的正文。
            var matchedSkills = AgentSkillRegistry.All.Where(s => _activeSkillIds.Contains(s.Id) &&
                s.TriggerKeywords.Any(k => !string.IsNullOrWhiteSpace(k) && rawSkillInput.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
            // 保留旧技能的直接触发行为；续接参考不能新增工具权限或改变工具拦截规则。
            _skillTriggerActive = matchedSkills.Any(s => s.Id != AiAgentWorkflowSkill.Id);
            // 标题：第一条用户消息
            if (_history.Count <= 1 && Title == "新对话")
                Title = userText.Length > 30 ? userText[..30] + "…" : userText;

            // 上下文预算：先本地截断，超预算滚动摘要。
            // 注意：PrepareHistoryAsync 返回的列表必须与 _history 不同引用
            // （历史 <=1 条时若返回原引用，下方的 Clear 会连带清空 system）。
            using var client = AgentClientFactory.CreateClient();
            var prepared = await AgentMemory.PrepareHistoryAsync(client, _history, ct: ct);
            if (ReferenceEquals(prepared, _history))
                prepared = _history.ToList();
            _history.Clear();
            _history.AddRange(prepared);

            // Calculate against the actual user message, including the appended time, and all system
            // context. Leave room for tool replies without increasing the existing history budget.
            var currentUserText = AiAssistantService.WithCurrentTime(userText);
            var currentUserMessage = new ChatMessage(ChatRole.User, currentUserText);
            var reference = SkillTurnReferenceComposer.Compose(_activeSkillIds.Contains(AiAgentWorkflowSkill.Id),
                () => AiAgentWorkflowSkill.EffectiveBody, _skillTaskContinuity, rawSkillInput, _activeSkillIds,
                continuesCurrentTask, AgentRuntime.CustomSkillReferenceBudget(_history, currentUserMessage));
            LastCoreSkillReferenceState = reference.CoreState;
            // Rebuild the current product body even for a short confirmation, after compression.
            // ReferenceEquals owns this combined message; it is not a permanent user instruction.
            _turnSkillReference = ReplaceTurnSkillReference(_history, null, reference.Text);

            // 当前时间追加在用户消息末尾（不放进系统提示词，避免前缀缓存失效）
            if (images is { Count: > 0 })
            {
                // 多模态：文本 + 图片（DataContent 由 Microsoft.Extensions.AI 的 OpenAI 适配器
                // 自动映射为 image_url content，直发模型视觉通道）
                var contents = new List<AIContent> { new TextContent(currentUserText) };
                foreach (var (bytes, mediaType) in images)
                    contents.Add(new DataContent(bytes, mediaType));
                _history.Add(new ChatMessage(ChatRole.User, contents));
            }
            else
            {
                _history.Add(currentUserMessage);
            }

            var cb = BuildCallbacks();
            await AgentRuntime.RunLoopAsync(_history, cb, ct, clientOverride: client,
                protectedReference: _turnSkillReference);
            LastSendOutcome = AgentSendOutcome.Completed;   // 【A12】正常完成
        }
        catch (OperationCanceledException)
        {
            // 用户取消：结算当前打开的步骤链（含已取消步骤），UI 折叠
            LastSendOutcome = AgentSendOutcome.Cancelled;   // 【A12】明确结果后照常上抛（页面既有 catch 不变）
            CompleteGroup(raise: true);
            throw;
        }
        catch (Exception)
        {
            LastSendOutcome = AgentSendOutcome.Failed;      // 【A12】
            throw;
        }
        finally
        {
            FinalizeOpenTextItem();
            ConversationDisplayItem.RecordReplyCompletion(_displayItems, _replyRunDisplayStart,
                LastSendOutcome, _replyRunHadError, _awaitingConfirmation);
            IsRunning = false;
            _skillTriggerActive = false;
            AgentToolContext.Current = null;
        }
    }

    /// <summary>应用确认决策（已确认的会真正执行），然后继续循环。</summary>
    public async Task ResumeConfirmationsAsync(IReadOnlyList<AgentConfirmationDecision> decisions)
    {
        if (IsRunning || !_awaitingConfirmation || decisions.Count == 0)
        {
            AgentDebugLog.Error($"ResumeConfirmations 被跳过：IsRunning={IsRunning}, awaiting={_awaitingConfirmation}, decisions={decisions.Count}");
            return;
        }

        AgentDebugLog.Info($"ResumeConfirmations 开始，决策 {decisions.Count} 条");
        _awaitingConfirmation = false;
        _pendingConfirmations = [];
        IsRunning = true;
        _replyRunHadError = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        AgentToolContext.Current = this;

        try
        {
            // A local trial can change while confirmation is paused. Re-read the enabled core body
            // and reinsert before the actual user message, leaving assistant/tool pairs untouched.
            RemoveCustomSkillReference(_history, _turnSkillReference);
            _turnSkillReference = null;
            LastCoreSkillReferenceState = CoreSkillReferenceState.NotProvided;
            var currentUser = _history.LastOrDefault(message => message.Role == ChatRole.User);
            if (currentUser is not null)
            {
                var reference = SkillTurnReferenceComposer.Compose(_activeSkillIds.Contains(AiAgentWorkflowSkill.Id),
                    () => AiAgentWorkflowSkill.EffectiveBody, _skillTaskContinuity, "", _activeSkillIds,
                    true, AgentRuntime.CustomSkillReferenceBudget(_history, currentUser));
                LastCoreSkillReferenceState = reference.CoreState;
                _turnSkillReference = ReplaceTurnSkillReference(_history, null, reference.Text, currentUser);
            }
            var cb = BuildCallbacks();
            await AgentRuntime.ResumeLoopAsync(_history, decisions, cb, ct, protectedReference: _turnSkillReference);
            LastSendOutcome = AgentSendOutcome.Completed;
            AgentDebugLog.Info("ResumeConfirmations 结束");
        }
        catch (OperationCanceledException)
        {
            LastSendOutcome = AgentSendOutcome.Cancelled;
            CompleteGroup(raise: true);
            throw;
        }
        catch (Exception)
        {
            LastSendOutcome = AgentSendOutcome.Failed;
            throw;
        }
        finally
        {
            FinalizeOpenTextItem();
            ConversationDisplayItem.RecordReplyCompletion(_displayItems, _replyRunDisplayStart,
                LastSendOutcome, _replyRunHadError, _awaitingConfirmation);
            IsRunning = false;
            AgentToolContext.Current = null;
        }
    }

    /// <summary>取消当前运行；等待确认时直接结算为已取消，不再继续请求模型。</summary>
    public void Cancel()
    {
        if (_awaitingConfirmation)
        {
            foreach (var request in _pendingConfirmations)
            {
                if (request.Step is { } step)
                {
                    step.Status = AgentStepStatus.Cancelled;
                    step.Result = MiscTexts.T("用户取消了待确认操作。");
                    step.Duration = DateTime.Now - step.StartedAt;
                    StepCompleted?.Invoke(step);
                }
                _history.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(request.CallId, MiscTexts.T("用户取消了待确认操作，任务已停止。")) ]));
            }
            _pendingConfirmations = [];
            _awaitingConfirmation = false;
            LastSendOutcome = AgentSendOutcome.Cancelled;
            ConversationDisplayItem.RecordReplyCompletion(_displayItems, _replyRunDisplayStart,
                AgentSendOutcome.Cancelled);
            CompleteGroup(raise: true);
            Save();
            return;
        }
        _cts.Cancel();
    }

    /// <summary>
    /// 启用/禁用技能，立即重建系统提示词（技能开关即时生效）。
    /// </summary>
    public void SetSkillEnabled(string id, bool enabled)
    {
        if (AgentSkillRegistry.Find(id) is null) return;
        if (enabled) _activeSkillIds.Add(id);
        else _activeSkillIds.Remove(id);
        if (!enabled) _skillTaskContinuity.Remove(id);
        EnsureSystemPrompt();
    }

    public void SetPersona(string personaId)
    {
        if (IsRunning) return;
        PersonaId = AgentPersonaCatalog.Resolve(personaId).Id;
        EnsureSystemPrompt();
    }

    /// <summary>用户重命名会话（持久化由调用方触发 Save）。</summary>
    public void Rename(string title) => Title = title.Trim();

    /// <summary>持久化：协议历史（messages.json）+ 展示记录（display.json，含步骤链与 token 统计）。</summary>
    public void Save()
    {
        if (_history.Count == 0) return;
        try
        {
            // The guard covers protocol history and metadata as well as display items.
            // A cancelled background run must not recreate ownership after its window closes.
            if (SaveGuard is not null && !SaveGuard(Id)) return;
            // 跳过注入的「系统指令」消息（内部指令，不写入用户可见历史）
            var saveable = FilterCustomSkillReference(_history, _turnSkillReference)
                .Where(m => m.Role != ChatRole.User || m.Text is null || !m.Text.StartsWith("【系统指令】"))
                .ToList();
            AiAssistantService.SaveConversation(Id, Title, AgentMessageConverter.ToAiMessages(saveable), PersonaId);
            // 会话级 token 统计写入 meta 条目（多轮累计，加载时恢复）
            if (TotalTokens > 0)
            {
                var meta = _displayItems.FirstOrDefault(i => i.Type == "meta");
                if (meta is null)
                {
                    meta = new ConversationDisplayItem { Type = "meta" };
                    _displayItems.Add(meta);
                }
                meta.PromptTokens = TotalPromptTokens;
                meta.CompletionTokens = TotalCompletionTokens;
                meta.CacheHitTokens = TotalCacheHitTokens;
                meta.CacheMissTokens = TotalCacheMissTokens;
            }
            // 【主审修复·第六轮】统一保存接入：无归属/已关闭/被他人持有时不写盘
            // （页面注入 SaveGuard=租约写守卫；未注入时保持旧行为）。
            if (SaveGuard is null || SaveGuard(Id))
                AiAssistantService.SaveConversationDisplay(Id, _displayItems);
            SaveSkills();
        }
        catch { }
    }

    internal bool MatchesPersistedDisplay(IReadOnlyList<ConversationDisplayItem> persisted)
        => JsonSerializer.Serialize(_displayItems.Where(item => item.Type is "text" or "steps")) ==
           JsonSerializer.Serialize(persisted.Where(item => item.Type is "text" or "steps"));

    public void Dispose()
    {
        // 注意：不能释放 _cts —— 后台运行循环（Task.Run 中的 SendAsync）可能仍持有其 token，
        // 对已释放的 CTS 调用 Cancel / CreateLinkedTokenSource 会抛 ObjectDisposedException。
        // 这里只请求取消让运行收尾；CTS 无强非托管资源，交由 GC 回收。
        _cts.Cancel();
        Save();
    }

    // ---------- 内部 ----------

    internal static void RemoveCustomSkillReference(List<ChatMessage> history, ChatMessage? reference)
    {
        if (reference is not null) history.RemoveAll(message => ReferenceEquals(message, reference));
    }

    internal static ChatMessage? ReplaceTurnSkillReference(List<ChatMessage> history, ChatMessage? previous,
        string text, ChatMessage? beforeUser = null)
    {
        RemoveCustomSkillReference(history, previous);
        if (string.IsNullOrEmpty(text)) return null;
        var reference = new ChatMessage(ChatRole.User, text);
        var index = beforeUser is null ? -1 : history.FindIndex(message => ReferenceEquals(message, beforeUser));
        if (index >= 0) history.Insert(index, reference);
        else history.Add(reference);
        return reference;
    }

    internal static List<ChatMessage> FilterCustomSkillReference(IEnumerable<ChatMessage> history,
        ChatMessage? reference)
        => history.Where(message => !ReferenceEquals(message, reference)).ToList();

    private AgentRunCallbacks BuildCallbacks() => new()
    {
        OnTextChunk = chunk =>
        {
            _textSb.Append(chunk);
            TextChunk?.Invoke(chunk);
        },
        OnReasoningChunk = chunk =>
        {
            _reasoningSb.Append(chunk);
            ReasoningChunk?.Invoke(chunk);
        },
        OnRoundStarted = () =>
        {
            // 新一轮：若上一组已结算则重置统计（确认暂停期内保持打开，与新步骤同组）
            if (_groupCompleted)
            {
                _groupSteps.Clear();
                _groupPromptTokens = 0;
                _groupCompletionTokens = 0;
                _groupCacheHitTokens = 0;
                _groupCacheMissTokens = 0;
                _groupTimer = System.Diagnostics.Stopwatch.StartNew();
            }
            RoundStarted?.Invoke();
        },
        OnStepStarted = step =>
        {
            _groupSteps.Add(step);
            if (_openStepsItem is null)
            {
                // 首个工具步骤：定稿本轮文本，并新建步骤链展示项（位置紧随文本之后）
                FinalizeOpenTextItem();
                _openStepsItem = new ConversationDisplayItem { Type = "steps" };
                _displayItems.Add(_openStepsItem);
            }
            StepStarted?.Invoke(step);
        },
        OnStepCompleted = step => StepCompleted?.Invoke(step),
        OnConfirmationsRequested = requests =>
        {
            _awaitingConfirmation = true;
            _pendingConfirmations = requests;
            // 暂停轮不结算：确认后的执行与暂停轮同属一组
            _groupCompleted = false;
            ConfirmationsRequested?.Invoke(requests);
        },
        OnError = error =>
        {
            _replyRunHadError = true;
            Error?.Invoke(error);
        },
        OnCompleted = _ => RunCompleted?.Invoke(),
        OnUsage = usage =>
        {
            _groupPromptTokens += usage.PromptTokens;
            _groupCompletionTokens += usage.CompletionTokens;
            TotalPromptTokens += usage.PromptTokens;
            TotalCompletionTokens += usage.CompletionTokens;
            if (usage.CacheHitTokens is { } hit)
            {
                // 未命中数缺失时按 prompt = hit + miss 推算（DeepSeek/GLM 语义）
                var miss = usage.CacheMissTokens ?? Math.Max(0, usage.PromptTokens - hit);
                _groupCacheHitTokens += hit;
                _groupCacheMissTokens += miss;
                TotalCacheHitTokens += hit;
                TotalCacheMissTokens += miss;
            }
        },
        OnRoundCompleted = () => CompleteGroup(raise: true)
    };

    /// <summary>结算当前步骤组：快照展示项 + 触发 UI 折叠事件。</summary>
    private void CompleteGroup(bool raise)
    {
        _groupCompleted = true;
        if (_groupSteps.Count == 0)
        {
            _openStepsItem = null;
            return;
        }

        _groupTimer?.Stop();
        var summary = new AgentStepGroupSummary
        {
            Total = _groupSteps.Count,
            Success = _groupSteps.Count(s => s.Status == AgentStepStatus.Success),
            Failed = _groupSteps.Count(s => s.Status is AgentStepStatus.Failed or AgentStepStatus.Rejected),
            Duration = _groupTimer?.Elapsed,
            PromptTokens = _groupPromptTokens,
            CompletionTokens = _groupCompletionTokens,
            CacheHitTokens = _groupCacheHitTokens,
            CacheMissTokens = _groupCacheMissTokens,
            ByTool = _groupSteps
                .GroupBy(s => s.DisplayName)
                .ToDictionary(g => g.Key, g => g.Count())
        };

        if (_openStepsItem is not null)
        {
            _openStepsItem.Steps = _groupSteps.Select(AgentStepSnapshot.From).ToList();
            _openStepsItem.SummaryText = summary.ToDisplayText();
            _openStepsItem.DurationSeconds = summary.Duration?.TotalSeconds;
            _openStepsItem.PromptTokens = summary.PromptTokens;
            _openStepsItem.CompletionTokens = summary.CompletionTokens;
            _openStepsItem = null;
        }

        if (raise)
            StepGroupCompleted?.Invoke(summary);
    }

    /// <summary>定稿当前流式文本展示项（无文本则不产生条目）。</summary>
    private void FinalizeOpenTextItem()
    {
        if (_textSb.Length == 0 && _reasoningSb.Length == 0) return;
        _displayItems.Add(new ConversationDisplayItem
        {
            Type = "text",
            Role = "assistant",
            Content = _textSb.ToString(),
            ReasoningContent = _reasoningSb.ToString()
        });
        _textSb.Clear();
        _reasoningSb.Clear();
    }

    private static string SkillsPath(string id) => Path.Combine(HistoryDir, $"{id}.skills.json");

    /// <summary>加载会话技能状态：文件缺失时默认全部技能激活。</summary>
    private void LoadSkills()
    {
        _activeSkillIds.Clear();
        try
        {
            if (File.Exists(SkillsPath(Id)))
            {
                var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(SkillsPath(Id)));
                if (ids is not null)
                    foreach (var id in ids)
                        if (AgentSkillRegistry.Find(id) is not null)
                            _activeSkillIds.Add(id);
            }
            else
            {
                foreach (var skill in AgentSkillRegistry.All)
                    _activeSkillIds.Add(skill.Id);
            }
        }
        catch
        {
            foreach (var skill in AgentSkillRegistry.All)
                _activeSkillIds.Add(skill.Id);
        }
    }

    private void SaveSkills()
    {
        try
        {
            var dir = Path.GetDirectoryName(SkillsPath(Id));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SkillsPath(Id), JsonSerializer.Serialize(_activeSkillIds.OrderBy(x => x).ToList()));
        }
        catch { }
    }

    /// <summary>
    /// 构建完整系统提示词（主提示词 + 已加载技能 + 工具箱索引 + 系统上下文）。
    /// 静态公开：ChatPanel 重构后的 AiAgentPage 复用同一套组装逻辑（会话记忆走 ChatPanel.MemoryText）。
    /// </summary>
    public static string BuildSystemPromptContent(IEnumerable<string> activeSkillIds)
    {
        var content = AgentPrompts.SystemPrompt + "\n\n" +
            TubaWinUi3.Services.ToolFlows.ToolFlowProposalParser.BuildOutputInstructions(
                TubaWinUi3.Services.AppManagement.SystemInstaller.KnownTargets,
                TubaWinUi3.Services.AppManagement.SystemInstaller.GetTargetDescriptions());
        var active = AgentSkillRegistry.All.Where(s => activeSkillIds.Contains(s.Id)).ToList();
        var skillsContext = AgentSkillRegistry.BuildActiveSkillsContext(active);
        if (!string.IsNullOrEmpty(skillsContext))
            content += "\n\n" + skillsContext;

        content += "\n\n" + CliToolboxCatalog.Default.BuildIndexContext() + "\n\n" +
                   AiAssistantService.BuildSystemContext() + "\n\n" +
                   AiAssistantService.BuildSystemInfoContext();
        return content;
    }

    private string BuildSystemContent()
    {
        var content = BuildSystemPromptContent(_activeSkillIds);
        var style = AgentPersonaCatalog.BuildStyleInstruction(PersonaId);
        return string.IsNullOrEmpty(style) ? content : content + "\n\n" + style;
    }

    private void EnsureSystemPrompt()
    {
        var content = BuildSystemContent();
        var notes = _memory.Read();
        if (!string.IsNullOrWhiteSpace(notes))
            content += "\n\n## 会话记忆（由 read_memory / write_memory / clear_memory 维护，回答时参考）\n" + notes;

        if (_history.Count == 0 || _history[0].Role != ChatRole.System)
            _history.Insert(0, new ChatMessage(ChatRole.System, content));
        else
            _history[0] = new ChatMessage(ChatRole.System, content);
    }
}
