using Microsoft.UI.Dispatching;
using System.IO;
using System.Speech.Recognition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using TubaWinUi3.Services.Ai;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Pages;

/// <summary>
/// 一条助手消息的构建状态（流式文本 / 思考指示 / 内容宿主）。
/// </summary>
internal sealed class AssistantBubble
{
    public System.Text.StringBuilder RawContent { get; } = new();
    public string ReadableContent { get; set; } = "";
    public required Button CopyButton { get; init; }
    public Expander? ReplyDetails { get; set; }
    public required Border Root { get; set; }
    public required TextBlock StreamingText { get; init; }
    public required StackPanel StreamingRow { get; init; }
    public required StackPanel ThinkingRow { get; init; }
    public required TextBlock StatusText { get; init; }
    public required ReasoningDisclosureControl Reasoning { get; init; }
    public required ContentControl ContentHost { get; init; }
    public required StackPanel ToolFlowActions { get; init; }
    public required int Epoch { get; init; }
}

/// <summary>
/// AI 智能代理完整页面：Agent 循环 + 代码构建消息气泡（项目已验证的可靠模式）
/// + 步骤链可视化（运行中展开、完成后自动折叠为摘要）+ 危险操作确认卡片。
/// 引擎为 AgentSession（多轮工具调用循环）。
/// </summary>
public sealed partial class AiAgentPage : UserControl, ILocalizablePage
{
    private readonly DispatcherQueue _dq;
    private readonly bool _compactView;

    private static string Ui(string key, string fallback) => LocalizationService.L("AiAgent_" + key, fallback);

    private IAgentSession? _session;
    private AssistantBubble? _streamingBubble;
    private StepChainControl? _activeChain;
    private Control? _activeConfirmationCard;
    private bool _isProcessing;
    private bool _awaitingConfirmation;
    private bool _suppressToggleEvent;
    private bool _syncingCombos;
    private string _selectedPersonaId = AgentPersonaCatalog.DefaultId;
    private string? _lastUserText;
    private bool _lastSendPreservedDraft;
    private string? _lastRawSkillInput;
    private bool? _lastContinuesCurrentTask;
    // 【A06】dsh 会话的展示记录（文本气泡+步骤链，按序；Save 时落盘 {id}.display.json）
    private readonly List<TubaWinUi3.Services.Agent.ConversationDisplayItem> _displayLog = [];
    private bool _replayingDisplay;   // 【A06 返修】回放历史期间禁止写 _displayLog（防重复记录）
    // 【主审修复·2026-09-22】轮次/会话归属与代次：晚到的流式回调（FinalizeStreaming/步骤链）
    // 必须按"发起本轮的真实会话"归属，而非当前 _session 字段——临时离开时 _session 会被置 null，
    // 但本轮仍属该 dsh 会话；同时防止旧轮次收尾写入已切换的新会话展示日志。
    private TubaWinUi3.Services.Ai.Dsh.DshSession? _streamingOwner;
    private int _displayEpoch;    // 会话切换/清空时自增（LoadConversation/ResetToNewChat）
    private int _sendEpoch;       // 发送开始时捕获；与 _displayEpoch 不一致 → 结果不写共享日志
    // 【主审修复·2026-09-22】会话写入归属：同一「数据根 + 会话 ID」同一时刻只允许一个页面实例
    // 编辑/落盘（主窗口与独立窗口双实例同 ID 会互相覆盖——未编辑旧副本关闭即整体覆盖新记录）。
    // 【主审修复·第六轮】升级为 ConversationLease（单当前 ID 不变量；关闭终态不可重获——
    // 修复"生成中关窗：Send finally 迟到回调 TryAcquire 重获刚释放 ID → 幽灵占用直到重启"）。
    private readonly TubaWinUi3.Services.Ai.ConversationLease _lease =
        new(AiAssistantService.HistoryRootKey);
    private bool _resumeNoticeShown;  // 【A06 返修】恢复失败告知只弹一次
    private bool _dshRestoreFallback; // 【A06 返修】dsh 存档但引擎不可用（回放后如实告知）
    private string? _connectedFingerprint;   // 【UI 改版】已验证连通的配置指纹（换配置/重建即重置）
    // 【修复】会话历史目录监听：任一处（本窗口/其他窗口）删除、新建、改名会话 → 各窗口列表同步刷新
    private FileSystemWatcher? _historyWatcher;
    private DispatcherQueueTimer? _historyRefreshTimer;
    /// <summary>【A12】本轮发送是否出现过错误（决定失败时恢复待发送附件）。</summary>
    private bool _sendHadError;
    // 【UI 改版·第一批】动态控件主题化：登记 (元素弱引用, 属性, 主题资源键)，主题切换时统一重刷。
    // 代码里 Application.Current.Resources[key] 得到的是当前主题的静态快照——不登记的话，
    // 切换主题后已存在的消息/错误卡/状态色会停在旧主题（深色场景下可能不可读）。
    // 用弱引用：已从可视树回收的旧气泡树不阻止 GC，重刷时顺带清理失效项。
    private readonly List<(WeakReference<DependencyObject> Target, DependencyProperty Prop, string Key)> _themedBrushes = [];
    private bool _themeSubscribed;
    /// <summary>【主题复核·最后一公里】完整刷新防重入（Loaded 校正 / 双主题事件 / 状态重算交叉触发时）。</summary>
    private bool _isRefreshingTheme;

    /// <summary>API 请求发出后超过该时长仍无首个 chunk，视为排队中。</summary>
    private static readonly TimeSpan QueueThreshold = TimeSpan.FromSeconds(10);
    private DispatcherTimer? _queueTimer;
    private DispatcherTimer? _dotsTimer;
    private int _dots;
    private bool _roundHasText;
    private bool _followLatest = true;

    // ZXAI：待发送附件（上游 Agent 重写后按原生方式恢复附件上传）
    private readonly List<PendingAttachment> _attachments = [];

    // ZXAI：语音听写（System.Speech，本机 zh-CN 识别器）
    private SpeechRecognitionEngine? _speechEngine;
    private bool _isListening;

    // Raycast 指令式空态：3 个建议入口（图标 + 标题 + 说明 + 箭头，纵向列表）
    private static (string Title, string Desc, string Glyph)[] QuickQuestions =>
    [
        (Ui("QuickGameTitle", "我想做一款 2D 游戏"), Ui("QuickGameDescription", "先理解目标，再组合可选工具流并安装配置"), "\uE945"),
        (Ui("QuickHardwareTitle", "新电脑怎么验机"), Ui("QuickHardwareDescription", "检查硬件信息，确认配置真伪"), "\uE950"),
        (Ui("QuickSlowPcTitle", "电脑卡顿怎么办"), Ui("QuickSlowPcDescription", "排查卡顿，了解硬件状态"), "\uE7E8"),
    ];

    public AiAgentPage() : this(compact: false)
    {
    }

    internal AiAgentPage(bool compact) : this(compact, autoLoadLatest: true)
    {
    }

    /// <summary>【主审修复·2026-09-22】独立卡片窗口用：autoLoadLatest=false 时启动为全新会话，
    /// 不自动载入最近历史——避免与主窗口/其他窗口占用同一会话 ID 后互相覆盖记录。</summary>
    internal AiAgentPage(bool compact, bool autoLoadLatest)
    {
        InitializeComponent();
        ChatViewportLayout.Attach(MsgScroll, MsgViewport);
        ChatViewportLayout.Attach(WorkbenchScroll, WorkbenchViewport);
        InitializeToolFlowTaskCard();
        _compactView = compact;
        _selectedPersonaId = AgentPersonaCatalog.Resolve(AppSettings.Get(AgentPersonaCatalog.SettingKey)).Id;
        UpdatePersonaButton();
        if (compact)
        {
            FullAccessToggle.Visibility = Visibility.Collapsed;
            SkillsButton.Visibility = Visibility.Collapsed;
            AiSettingsButton.Visibility = Visibility.Collapsed;
            MsgPanel.MaxWidth = 760;
            InputBox.PlaceholderText = Ui("CompactInputPlaceholder", "输入问题（Enter 发送，Shift+Enter 换行）");
        }
        _dq = DispatcherQueue.GetForCurrentThread();
        InitializeMessageScrolling();
        // 【UI 改版】主题变化重刷动态控件；页面卸载退订（避免元素释放后仍被回调）。
        // 【主题复核·最后一公里】Loaded：①幂等订阅 ThemeService；②成对订阅页面 ActualThemeChanged；
        // ③做一次完整刷新——构造期元素尚未入树，ActualTheme 可能是系统主题的快照，必须校正。
        Loaded += (_, _) =>
        {
            RestoreCurrentToolFlowTask();
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            LocalizationService.LanguageChanged += OnLanguageChanged;
            SubscribeDynamicTheme();
            ActualThemeChanged -= OnPageActualThemeChanged;
            ActualThemeChanged += OnPageActualThemeChanged;
            RefreshDynamicTheme();
            StartHistoryWatcher();   // 【修复】启动会话目录监听（跨窗口列表同步）
            UpdateInputState();
            if (_followLatest) SmartScroll();
        };
        Unloaded += (_, _) =>
        {
            _toolFlowViewEpoch++; // An open proposal confirmation is invalid after leaving this view.
            ClearToolAccess();
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            UnsubscribeDynamicTheme();
            ActualThemeChanged -= OnPageActualThemeChanged;
            StopHistoryWatcher();    // 【修复】成对释放监听（不泄漏）
        };
        // 【UI 改版】小窗自适应：会话栏折叠/折叠按钮可见性（宽窗恢复）。
        SizeChanged += (_, e) => ApplyCompactSidebar(e.NewSize.Width);

        MsgPanel.ChildrenTransitions = new TransitionCollection
        {
            new EntranceThemeTransition { FromVerticalOffset = 16, IsStaggeringEnabled = true },
            new RepositionThemeTransition()
        };

        _suppressToggleEvent = true;
        FullAccessToggle.IsOn = AgentToolContext.IsFullAccess;
        _suppressToggleEvent = false;
        UpdateTokenUsage();
        UpdateRunState();
        BuildQuickPills();
        RefreshProviderCombos();
        // 【修复】读取上次已验证连通的配置指纹：配置未变时（重启/新对话/切会话）保持"已连接"显示
        try { _connectedFingerprint = TubaWinUi3.Services.AppSettings.Get("AiVerifiedFingerprint"); } catch { }
        if (autoLoadLatest) LoadLatestConversation();
        else Welcome();   // 【主审修复·2026-09-22】独立窗口默认新会话：不自动占用历史
        RefreshConversationList(); // ZXAI：左侧会话层初始化
        TubaWinUi3.Services.Ai.AgentEngine.PrewarmDshProbe(); // 【评审修复】后台探测 dsh 可用性（不阻塞 UI）
        ApplyEnginePermissionUi(); // 【A01】权限 UI 与真实引擎对齐（dsh 固定全权限）
        ApplyLocalization();
    }

    private void OnLanguageChanged() => _dq.TryEnqueue(ApplyLocalization);

    public void ApplyLocalization()
    {
        RefreshToolFlowTaskCard();
        _refreshToolFlowGoalDialog?.Invoke();
        SidebarNewChatText.Text = Ui("NewChat", "新对话");
        ToolTipService.SetToolTip(SidebarNewChatButton, Ui("NewChatTip", "开始一个新对话（当前对话会保留在列表中）"));
        ToolTipService.SetToolTip(LogoHomeButton, Ui("HomeTip", "回到新对话（欢迎页）"));
        ToolTipService.SetToolTip(SidebarToggleButton, Ui("SidebarTip", "显示/隐藏会话列表"));
        if (_session is null || TitleText.Text is "新对话" or "New chat")
            TitleText.Text = Ui("NewChat", "新对话");
        ToolFlowResumeButtonLabel.Text = Ui("ResumeToolFlow", "继续工具流");
        ToolTipService.SetToolTip(ToolFlowResumeButton, Ui("ResumeToolFlowTip", "打开最近一次选定的工具流，继续处理清单（只读本机快照）"));
        PersonaFlyoutTitleText.Text = Ui("ChoosePersona", "选择协作人格");
        PersonaFlyoutDescriptionText.Text = Ui("PersonaDescription", "只调整交流方式，不改变工具权限；下一条消息生效。");
        AutomationProperties.SetName(PersonaFlyoutButton, Ui("ChoosePersonaAutomation", "选择 AI 协作人格"));
        ModelFlyoutTitleText.Text = Ui("CurrentAiService", "当前 AI 服务");
        ModelFlyoutDescriptionText.Text = Ui("ModelNextMessage", "切换后将在下一条消息生效。");
        ToolTipService.SetToolTip(ModelFlyoutButton, Ui("ModelTip", "AI 服务与模型（下一条消息生效）"));
        ToolTipService.SetToolTip(ProviderCombo, Ui("ProviderTip", "AI 服务提供商"));
        ToolTipService.SetToolTip(ModelCombo, Ui("CurrentModelTip", "当前模型，下一条消息生效"));
        AiSettingsText.Text = Ui("ManageAiService", "AI 与 Agent 设置");
        WelcomeBrandText.Text = Ui("Brand", "枕星 AI 助手");
        WelcomeTitleText.Text = Ui("WelcomeTitle", "今天，想完成什么？");
        WelcomeDescriptionText.Text = Ui("WelcomeDescription", "说出你想完成的事，我来帮你选工具、准备环境，再告诉你从哪里开始。");
        ScrollToBottomText.Text = Ui("ScrollLatest", "回到最新");
        // 【接入引导】空态接入卡文案随卡片状态在 UpdateOnboardingCard() 内统一刷新（R2 改动态）
        ToolTipService.SetToolTip(ScrollToBottomButton, Ui("ScrollLatestTip", "回到最新消息"));
        InputBox.PlaceholderText = _compactView
            ? Ui("CompactInputPlaceholder", "输入问题（Enter 发送，Shift+Enter 换行）")
            : Ui("InputPlaceholder", "说说你想完成什么，例如做一款 2D 游戏或解决电脑问题…");
        AutomationProperties.SetName(InputBox, Ui("InputAutomation", "向 AI 助手发送消息"));
        ToolTipService.SetToolTip(AttachButton, Ui("AttachTip", "上传文件（Word/PPT/Excel/TXT/思维导图/代码等，内容随消息发送）"));
        SkillsButtonLabel.Text = Ui("Skills", "技能");
        ToolTipService.SetToolTip(SkillsButton, Ui("Skills", "技能"));
        ToolTipService.SetToolTip(VoiceButton, Ui("VoiceTip", "语音输入（点击开始/停止听写）"));
        AutomationProperties.SetName(SendButton, Ui("Send", "发送消息"));
        AutomationProperties.SetName(StopButton, Ui("Stop", "停止生成"));
        BuildQuickPills();
        RefreshToolFlowMessageActions();
        ApplyEnginePermissionUi();
        UpdateServiceStatus();
        UpdateRunState();
        RefreshWorkbenchLayout();
        UpdateTokenUsage();
        UpdatePersonaButton();
        RefreshConversationList();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // 【R2】Loaded 链路重读已验证指纹：设置页测试成功/真实发送都会写 AppSettings，构造期快照可能已过期
        try { _connectedFingerprint = TubaWinUi3.Services.AppSettings.Get("AiVerifiedFingerprint"); } catch { }
        // ZXAI：每次进入页面重新读模型列表（设置页加了新模型后，重新打开主页即可见）
        RefreshProviderCombos();

        // 页面会在进入设置时保活；离开期间切换的语言在重新进入时同步，不重建会话或输入。
        ApplyLocalization();

        // ZXAI 自检（--zxtest-send / --zxtest-cat 等）：上游 v1.6.4 Agent 重写后恢复挂载
        _ = TubaWinUi3.Services.ZxSelfTest.MaybeRunAsync(this);

        // 页面淡入
        var sb = new Storyboard();
        var opacity = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(300)) };
        Storyboard.SetTarget(opacity, this);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        sb.Children.Add(opacity);
        sb.Begin();
    }

    /// <summary>页面卸载（内置工具关闭时调用）：取消后台运行、保存并释放会话。</summary>
    /// <summary>ZXAI 自检：设置输入框并发送（--zxtest-send 用）。</summary>
    internal void ZxSendText(string text)
    {
        InputBox.Text = text;
        _ = SendAsync(text);
    }

    /// <summary>ZXAI：外部触发刷新提供商/模型下拉（设置页改动后回主页不重建实例时用）。</summary>
    public void ZxRefreshProviders()
    {
        try
        {
            // 【R2】设置页测试/保存后返回复用页面：从 AppSettings 重读已验证指纹（否则 Settings 绿、AI 页仍灰）
            _connectedFingerprint = TubaWinUi3.Services.AppSettings.Get("AiVerifiedFingerprint");
            RefreshProviderCombos();
        }
        catch { }
    }

    /// <summary>ZXAI：外部预填输入框文本（点卡片/收藏页跳转用）。</summary>
    public bool TryPrefillInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            InputBox.Text = text;
            if (_workbenchSelectionId is not null)
            {
                _workbenchShowConversation = true;
                RefreshWorkbenchLayout();
            }
            InputBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            return true;
        }
        catch { return false; }
    }

    public void Unload()
    {
        ClearToolAccess();
        StopToolFlowPreparation();
        StopVoiceInput();
        StopQueueWatch();
        var session = _session;
        _session = null;
        if (session is not null)
        {
            // 【主审补正·2026-09-22】关闭路径也必须先落盘：保留用户消息与已有部分回复。
            // （旧实现直接 Dispose：未落盘的新会话随生命周期取消一起丢失——T1 复现 d9fa6004e0fc 无留档。）
            TryPersistSessionBeforeDispose(session);
            session.Dispose();
        }
        // 【主审修复·第六轮】真关闭：释放归属并封死（终态）——迟到回调/Send finally 之后
        // 不可再重获该会话（防幽灵占用）、不可用旧快照覆盖（防覆盖他人新记录）、不可更新失活 UI。
        // 临时导航离开不调用本方法（OnNavigatedAway 只落盘快照）。
        _lease.Close();
    }

    /// <summary>【主审补正·2026-09-22】导航离开（视图被替换，非关闭）：
    /// 只落盘当前会话快照（用户消息 + 已有部分回复 + 引擎会话 ID），不结束会话、不取消在途生成。
    /// 正常"去设置再回来"不应默默取消并丢新会话；用户显式"停止生成"仍走原有 Cancel 路径
    /// （本轮取消，但用户消息与已产出的部分回复保留在展示记录中）。</summary>
    public void OnNavigatedAway()
    {
        _toolFlowViewEpoch++;
        if (_isListening) { StopVoiceInput(); UpdateRunState(); }
        var session = _session;
        if (session is null) return;
        try { SaveSessionGuarded(session); } catch { }
        TryPersistSessionBeforeDispose(session);
    }

    /// <summary>【主审修复·2026-09-22】构造落盘快照：已定稿展示记录 + 在途部分回复
    /// （_streamingBubble 的文本/思考仍属用户可见内容，真关闭/临时离开时一并纳入）。
    /// 返回副本——不修改 _displayLog 本体，避免同轮流后续完成时重复追加。</summary>
    private List<TubaWinUi3.Services.Agent.ConversationDisplayItem> BuildDisplaySnapshot()
    {
        var list = new List<TubaWinUi3.Services.Agent.ConversationDisplayItem>(_displayLog);
        if (_streamingBubble is { } bubble)
        {
            var text = bubble.RawContent.ToString();
            var reasoning = bubble.Reasoning.ReasoningContent;
            if (!string.IsNullOrWhiteSpace(text) || !string.IsNullOrWhiteSpace(reasoning))
                list.Add(new TubaWinUi3.Services.Agent.ConversationDisplayItem
                {
                    Type = "text",
                    Role = "assistant",
                    Content = text,
                    ReasoningContent = reasoning
                });
        }
        return list;
    }

    /// <summary>【主审补正】会话结束前的统一落盘（dsh=展示记录+引擎身份；builtin=会话存档）。</summary>
    private void TryPersistSessionBeforeDispose(IAgentSession session)
    {
        try
        {
            if (session is TubaWinUi3.Services.Ai.Dsh.DshSession dsh)
            {
                // 【主审修复】用快照（含在途部分回复），不是裸 _displayLog。
                var snapshot = BuildDisplaySnapshot();
                // 【主审修复·2026-09-22】写盘归属守卫：非所有者不落盘——防止未编辑的旧副本
                // （如从未发送就关闭的独立窗口）把对方窗口的新记录整体覆盖。
                if (!EnsureWriteOwnership(dsh.Id)) return;
                dsh.Save();
                if (snapshot.Count > 0)
                    AiAssistantService.SaveConversationDisplay(dsh.Id, snapshot);
                if (snapshot.Count > 0 || !string.IsNullOrEmpty(dsh.DshSessionId))
                    AiAssistantService.SaveConversationEngineInfo(dsh.Id, dsh.Title, "dsh", dsh.DshSessionId, snapshot.Count, dsh.EffectiveWorkspace, dsh.PersonaId);
            }
            else
            {
                SaveSessionGuarded(session);
            }
        }
        catch { /* 落盘失败不应阻断关闭/导航 */ }
    }

    // ---------- 会话 ----------

    private IAgentSession CreateSession()
    {
        if (_session is null)
        {
            _selectedPersonaId = AgentPersonaCatalog.Resolve(AppSettings.Get(AgentPersonaCatalog.SettingKey)).Id;
            UpdatePersonaButton();
        }
        // 【ZXAI 换核心】按引擎选择：dsh（DeepSeek Harness）/ builtin（自研）；
        // dsh 不可用时工厂内自动回退，切换零风险。
        var session = AgentEngine.CreateSession();
        session.SetPersona(_selectedPersonaId);
        AttachSessionEvents(session);
        return session;
    }

    /// <summary>
    /// 【A06 返修】会话事件统一绑定（Diagnostic 日志 + 页面事件）——每个会话恰调用一次。
    /// 恢复历史（LoadConversation 的 dsh 分支）也必须走这里，否则发送后文本/思考/工具/完成
    /// 事件不进页面（此前恢复路径漏绑，历史会话打开后完全"哑"）。
    /// </summary>
    private void AttachSessionEvents(IAgentSession session)
    {
        if (session is TubaWinUi3.Services.Ai.Dsh.DshSession dsh)
            dsh.Diagnostic += line => TubaWinUi3.Services.Agent.AgentDebugLog.Info($"[dsh] {line}");
        HookSession(session);
    }

    private void HookSession(IAgentSession session)
    {
        // 【主审修复·第六轮】builtin 统一保存接入：会话内部 Save（含 Dispose 内调用）也经租约写守卫，
        // 与 dsh 的 PersistDshConversation 走同一归属规则（取得/释放/关闭终态完全一致）。
        if (session is TubaWinUi3.Services.Agent.AgentSession builtinSession)
            builtinSession.SaveGuard = EnsureWriteOwnership;
        // Detached conversation pages keep their own live stream. Events from an engine
        // replaced within this page must never append to the new engine's display.
        void DispatchOwned(Action action) => _dq.TryEnqueue(() =>
        {
            if (OwnsSession(session)) SafeInvoke(action);
        });
        session.TextChunk += chunk => DispatchOwned(() => AppendChunk(chunk));
        session.ReasoningChunk += chunk => DispatchOwned(() => AppendReasoningChunk(chunk));
        session.StepStarted += step => DispatchOwned(() => OnStepStarted(step));
        session.StepCompleted += step => DispatchOwned(() => OnStepCompleted(step));
        session.ConfirmationsRequested += requests => DispatchOwned(() => OnConfirmations(requests));
        session.Error += error => DispatchOwned(() => OnError(error));
        session.RunCompleted += () => _dq.TryEnqueue(() => SafeInvoke(() =>
        {
            if (!OwnsSession(session)) return;
            StopQueueWatch();
            FinalizeStreaming();
            // Usually SendAsync's finally determines the outcome; this covers a queued late UI callback.
            if (!_isProcessing && ReferenceEquals(_session, session))
            {
                CompleteToolFlowMessageActions(_sendEpoch,
                    session.LastSendOutcome == AgentSendOutcome.Completed && !_sendHadError && !_awaitingConfirmation);
                if (session is TubaWinUi3.Services.Ai.Dsh.DshSession completedDsh)
                    PersistDshConversation(completedDsh);
            }
        }));
        session.RoundStarted += () => DispatchOwned(StartQueueWatch);
        session.StepGroupCompleted += summary => DispatchOwned(() => OnStepGroupCompleted(summary));
    }

    /// <summary>
    /// UI 线程事件回调的安全壳：任何异常都转为错误气泡，
    /// 防止 marshal 回调中的未处理异常导致 XAML 崩溃（0xc000027b）。
    /// </summary>
    private void SafeInvoke(Action action)
    {
        // 【主审修复·第六轮】已关闭页面：迟到回调不得更新失活 UI（关闭为终态）。
        if (_lease.IsClosed) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AgentDebugLog.Error("UI 回调异常", ex);
            try { AddErrorBubble(string.Format(Ui("UiProcessingError", "界面处理出错：{0}"), ex.Message)); } catch { }
        }
    }

    // ---------- 排队检测（API 请求发出后久无响应 → 「正在排队」+ 动画） ----------

    /// <summary>新一轮 API 请求开始：重置状态为「正在思考…」，并启动排队定时器。</summary>
    private void StartQueueWatch()
    {
        StopQueueWatch();
        _roundHasText = false;
        // 多轮运行中上一轮的气泡可能已被定稿（_streamingBubble = null），
        // 这里只复位仍在使用的气泡；迟到的排队提示会在 QueueTimer_Tick 里自建气泡。
        if (_streamingBubble is not null)
        {
            _streamingBubble.ThinkingRow.Visibility = Visibility.Visible;
            SetStreamingStatus(Ui("Thinking", "正在思考…"));
        }

        _queueTimer = new DispatcherTimer { Interval = QueueThreshold };
        _queueTimer.Tick += QueueTimer_Tick;
        _queueTimer.Start();
    }

    /// <summary>超过阈值仍无首个 chunk → 切换为「正在排队等待响应」+ 点号动画。</summary>
    private void QueueTimer_Tick(object? sender, object e)
    {
        _queueTimer?.Stop();
        if (_roundHasText) return;

        // 气泡可能尚未创建（如工具步骤后的新一轮）→ 先建气泡再显示排队状态
        if (_streamingBubble is null)
            _streamingBubble = BeginAssistantBubble();
        _streamingBubble.ThinkingRow.Visibility = Visibility.Visible;

        _dots = 0;
        SetStreamingStatus(Ui("Queued", "正在排队等待响应"));
        _dotsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _dotsTimer.Tick += (_, _) =>
        {
            _dots = (_dots + 1) % 4;
            SetStreamingStatus(Ui("Queued", "正在排队等待响应") + new string('·', _dots));
        };
        _dotsTimer.Start();
    }

    private void StopQueueWatch()
    {
        _queueTimer?.Stop();
        _queueTimer = null;
        _dotsTimer?.Stop();
        _dotsTimer = null;
    }

    private void SetStreamingStatus(string text)
    {
        if (_streamingBubble is null) return;
        _streamingBubble.StatusText.Text = text;
    }

    // ---------- 发送 / 停止 ----------

    private async void SendButton_Click(object sender, RoutedEventArgs e)
        => await SendAsync(InputBox.Text);

    // ZXAI：用 PreviewKeyDown（隧道事件，从根向下先到处理器）而非 KeyDown——
    // WinUI3 TextBox 对 Enter 的 KeyDown（冒泡）会被内部处理流程截走，实测不到位。
    private void InputBox_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;

        // Enter 发送；Shift+Enter 换行（TextBox 默认行为，不拦截）。
        // 不做 _isComposing 拦截：IME 组合中的 Enter 由系统先吞、根本到不了这里；
        // TextCompositionEnded 一旦漏发，卡 true 会永久拦死发送（实测踩过）。
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        AgentDebugLog.Info($"[ZXAI] InputBox Enter shift={shift} send={!shift}");
        if (shift) return;

        e.Handled = true;
        _ = SendAsync(InputBox.Text);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        StopButton.IsEnabled = false;
        RunStateText.Text = Ui("Stopping", "正在停止…");
        _session.Cancel();
        if (_awaitingConfirmation)
        {
            _awaitingConfirmation = false;
            _activeConfirmationCard?.IsEnabled = false;
            AddSystemBubble(Ui("ConfirmationCancelled", "已取消待确认操作"));
            UpdateInputState();
        }
    }

    private void FullAccessToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvent) return;

        // 【A01 审计修复】dsh 引擎为固定全权限（DSH_PERMISSION_MODE=danger-full-access +
        // 权限请求自动允许），开关仅为状态显示（已禁用）；此分支为防御性回退，
        // 不允许出现"关掉开关"却不改变真实权限的失实状态。
        if (IsDshEngineActive)
        {
            ApplyEnginePermissionUi();
            return;
        }

        AgentToolContext.IsFullAccess = FullAccessToggle.IsOn;
        UpdateRunState();
        if (FullAccessToggle.IsOn)
            AddSystemBubble(Ui("FullAccessWarning", "⚠️ 已开启完全访问模式：AI 可直接执行命令、修改注册表等操作，不再逐项确认。"));
    }

    /// <summary>
    /// 当前会话是否为 dsh（DeepSeek Harness）引擎——决定权限 UI 的真实语义。
    /// 【评审修复】只依据实际会话实例；无会话时为中性（false，不锁死开关）。
    /// 会话创建（含懒创建 ??=）后由 ApplyEnginePermissionUi 刷新；绝不在此路径探测外部进程。
    /// </summary>
    private bool IsDshEngineActive => _session is TubaWinUi3.Services.Ai.Dsh.DshSession;

    /// <summary>
    /// 【A01 审计修复】权限 UI 与真实权限一致：
    /// dsh 引擎固定全权限（见 DshAcpClient 的 DSH_PERMISSION_MODE=danger-full-access 与
    /// 权限请求自动允许）——开关锁死为"全权限"且不可关闭，消除"受控执行/逐项确认"的失实承诺；
    /// builtin（内置）引擎保留可切换语义（其确认流真实生效）。
    /// </summary>
    private void ApplyEnginePermissionUi()
    {
        _suppressToggleEvent = true;
        try
        {
            if (IsDshEngineActive)
            {
                FullAccessToggle.IsOn = true;
                FullAccessToggle.IsEnabled = false;
                FullAccessToggle.OnContent = Ui("EngineFullAccess", "全权限（本引擎固定）");
                FullAccessToggle.OffContent = Ui("ControlledExecution", "受控执行");
                ToolTipService.SetToolTip(FullAccessToggle,
                    Ui("EngineFullAccessTip", "当前引擎（DeepSeek Harness）固定全权限执行：危险操作直接运行、不逐项确认。如需受控执行（危险操作逐项确认），请切换回内置引擎。"));
            }
            else
            {
                FullAccessToggle.IsEnabled = true;
                FullAccessToggle.IsOn = AgentToolContext.IsFullAccess;
                FullAccessToggle.OnContent = Ui("FullAccess", "完全访问");
                FullAccessToggle.OffContent = Ui("ControlledExecution", "受控执行");
                ToolTipService.SetToolTip(FullAccessToggle, Ui("ControlledExecutionTip", "危险操作在受控执行模式下需要逐项确认"));
            }
        }
        finally
        {
            _suppressToggleEvent = false;
        }
        UpdateRunState();
    }

    // ---------- 技能（Skills）菜单 ----------

    private void SkillsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSkillsPanel();
        if (Resources["SkillsFlyout"] is Flyout flyout)
            flyout.ShowAt(SkillsButton);
    }

    /// <summary>按当前会话的技能状态重建菜单项（技能默认全部加载）。</summary>
    private void RefreshSkillsPanel()
    {
        if (_session is null) { _session = CreateSession(); AttachExplicitTaskToNewSession(); ApplyEnginePermissionUi(); } // 【评审修复】懒创建后刷新真实引擎权限 UI
        if (Resources["SkillsFlyout"] is not Flyout { Content: StackPanel panel } skillsFlyout) return;
        EnsureSkillsFlyoutTheme(skillsFlyout, panel);
        panel.Children.Clear();

        // dsh ACP 没有技能逐项启停 RPC；产品内置目标技能随启动补丁固定投影。
        var skillsDisabled = IsDshEngineActive;
        panel.Children.Add(new TextBlock
        {
            Text = skillsDisabled
                ? LocalizationService.L("SkillHub_DshNotice", "目标助手固定启用；自定义技能可以勾选，命中场景时作为参考提供给当前引擎。")
                : Ui("SkillsBuiltInNotice", "技能默认全部启用；取消勾选即禁用（勾选/取消后立即生效，下一条消息按新状态执行）"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 2, 8, 8),
            Style = (Style)Resources["SkillsFlyoutDescriptionStyle"]
        });

        foreach (var skill in AgentSkillRegistry.All)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(new FontIcon
            {
                Glyph = skill.Glyph,
                FontSize = 14,
                Style = (Style)Resources["SkillsFlyoutIconStyle"]
            });
            var texts = new StackPanel { Spacing = 1 };
            texts.Children.Add(new TextBlock
            {
                Text = skill.Id == AiAgentWorkflowSkill.Id
                    ? Ui("GoalSkillName", skill.DisplayName) : skill.DisplayName,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Style = (Style)Resources["SkillsFlyoutTitleStyle"]
            });
            texts.Children.Add(new TextBlock
            {
                Text = skill.Id == AiAgentWorkflowSkill.Id
                    ? Ui("GoalSkillDescription", skill.Description) : skill.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 230,
                Style = (Style)Resources["SkillsFlyoutDescriptionStyle"]
            });
            content.Children.Add(texts);

            var check = new CheckBox
            {
                Content = content,
                // dsh 中产品内置目标技能固定加载，其他技能未接入；禁用开关以免假报成功。
                IsChecked = _session.ActiveSkillIds.Contains(skill.Id),
                IsEnabled = !skillsDisabled || skill.Id != AiAgentWorkflowSkill.Id,
                Tag = skill.Id,
                MinWidth = 0,
                Padding = new Thickness(0, 6, 0, 6)
            };
            if (check.IsEnabled)
            {
                check.Checked += SkillToggle_Changed;
                check.Unchecked += SkillToggle_Changed;
            }
            panel.Children.Add(check);
            if (skill.Id == AiAgentWorkflowSkill.Id)
            {
                var edit = new Button { Content = LocalizationService.L("SkillEdit_Edit", "修改技能"),
                    FontSize = 12, Margin = new Thickness(32, 0, 8, 8), Padding = new Thickness(10, 5, 10, 5) };
                edit.Click += async (_, _) =>
                {
                    if (Resources["SkillsFlyout"] is Flyout menu) menu.Hide();
                    await ShowSkillRevisionEditorAsync();
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var make = new Button { Content = LocalizationService.L("SkillHub_Make", "制作技能"), FontSize = 12,
                    Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 8) };
                make.Click += async (_, _) => { if (Resources["SkillsFlyout"] is Flyout menu) menu.Hide(); await ShowSkillMakerAsync(); };
                row.Children.Add(edit); row.Children.Add(make); panel.Children.Add(row);
            }
        }

        // ZXAI：自定义技能导入区（文件 / Git / 打开目录）
        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(4, 8, 4, 4),
            Style = (Style)Resources["SkillsFlyoutDividerStyle"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = skillsDisabled
                ? LocalizationService.L("SkillHub_CustomNotice", "制作或导入技能后可在本机使用。开关影响下一条消息，已有对话保留。")
                : Ui("CustomSkillsNotice", "自定义技能：把 .skill.json 放进技能目录（或下方导入），重启后自动加载"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 2, 8, 6),
            Style = (Style)Resources["SkillsFlyoutDescriptionStyle"],
        });
        var importRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 0, 8, 6) };
        var importFileBtn = new Button { Content = Ui("ImportSkillFile", "从文件导入"), FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        importFileBtn.Click += async (_, _) => await ImportSkillFromFileAsync();
        importRow.Children.Add(importFileBtn);
        var importGitBtn = new Button { Content = Ui("ImportSkillGit", "从 Git 导入"), FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        importGitBtn.Click += async (_, _) => await ImportSkillFromGitAsync();
        importRow.Children.Add(importGitBtn);
        var openSkillsDirBtn = new Button { Content = Ui("OpenSkillsFolder", "打开技能目录"), FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        openSkillsDirBtn.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", UserSkillLoader.SkillsDir); } catch { }
        };
        importRow.Children.Add(openSkillsDirBtn);
        panel.Children.Add(importRow);
        var library = new Button { Content = LocalizationService.L("SkillHub_Library", "官方技能库"),
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 6, 8, 2) };
        library.Click += (_, _) => { if (Resources["SkillsFlyout"] is Flyout menu) menu.Hide(); App.MainWindow.NavigateToSkillLibrary(); };
        panel.Children.Add(library);
    }

    /// <summary>从文件导入自定义技能（*.skill.json）。</summary>
    private async Task ImportSkillFromFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var (ok, msg) = UserSkillLoader.ImportFromFile(file.Path);
        AddSystemBubble(ok ? "✅ " + msg : "⚠️ " + msg);
        if (ok) RefreshSkillsPanel();
    }

    /// <summary>从 Git 仓库导入自定义技能（浅克隆 → 扫描 *.skill.json）。</summary>
    private async Task ImportSkillFromGitAsync()
    {
        var box = new TextBox { PlaceholderText = Ui("SkillGitPlaceholder", "https://github.com/用户名/技能仓库"), Margin = new Thickness(0, 8, 0, 0) };
        var dialog = new ContentDialog
        {
            Title = Ui("ImportSkillGitTitle", "从 Git 导入技能"),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = Ui("ImportSkillGitDescription", "输入含 *.skill.json 的仓库地址（自动浅克隆；单仓建议 <50MB）"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                },
            },
            PrimaryButtonText = Ui("Import", "导入"),
            CloseButtonText = Ui("Cancel", "取消"),
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var url = box.Text.Trim();
        if (url.Length == 0) return;
        AddSystemBubble(string.Format(Ui("ImportSkillGitProgress", "⏳ 正在从 Git 导入：{0}（可能需要片刻）"), url));
        var (ok, msg) = await UserSkillLoader.ImportFromGitAsync(url);
        AddSystemBubble(ok ? "✅ " + msg : "⚠️ " + msg);
        if (ok) RefreshSkillsPanel();
    }

    private void SkillToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id } cb) return;
        if (_session is null) return;
        // 【A11】dsh 引擎不支持技能启停：如实拒绝，不假报成功
        if (IsDshEngineActive && id == AiAgentWorkflowSkill.Id)
        {
            AddSystemBubble(Ui("SkillToggleUnsupported", "当前引擎（DeepSeek Harness）不支持技能逐项启停，此开关未生效。"));
            return;
        }
        var skill = AgentSkillRegistry.Find(id);
        if (skill is null) return;

        var on = cb.IsChecked == true;
        _session.SetSkillEnabled(id, on);
        SaveSessionGuarded(_session);
        AddSystemBubble(on
            ? string.Format(Ui("SkillEnabled", "✅ 已启用技能：{0}（{1}）"), skill.DisplayName, skill.Description)
            : string.Format(Ui("SkillDisabled", "已禁用技能：{0}（恢复默认状态可重新勾选）"), skill.DisplayName));
    }

    private async Task SendAsync(string text, bool preserveDraft = false, Action? onAccepted = null,
        bool? continuesCurrentTask = null, string? rawSkillInputOverride = null)
    {
        if (_isProcessing || _awaitingConfirmation || _toolFlowActionOpen || _toolFlowInstalling) return;

        // 【接入引导 2026-09-25】当前服务未接通：不发送、不吞草稿（含附件），先引导完成接入。
        // 「稍后再说」的收起状态在此撤销——用户已明确想发消息，把入口亮回来。
        if (NeedsOnboarding())
        {
            try
            {
                // 【R2】输入框为空时把本次尝试发送的内容留作草稿——首页快捷目标按钮直接 SendAsync(title)，
                // 拦下后目标不丢失（配置完回来可直接发）；绝不动用户已有草稿。
                if (!preserveDraft && string.IsNullOrWhiteSpace(InputBox.Text) && !string.IsNullOrWhiteSpace(text))
                    InputBox.Text = text;
                TubaWinUi3.Services.AppSettings.Remove("AiOnboardingDismissed");
                TubaWinUi3.Services.AppSettings.Save();
                UpdateOnboardingCard();
                if (EmptyStateBlock.Visibility == Visibility.Visible)
                    AiOnboardingConfigureButton.Focus(FocusState.Programmatic);
                else
                    AddSystemBubble(Ui("OnboardingSendBlocked", "⚠️ 尚未接通 AI 服务：请先到「AI 与 Agent」选择服务并填写 Key（或配置本地服务），测试通过后再发送；输入内容已保留。"));
            }
            catch { }
            return;
        }

        text = text.Trim();
        // 【可见方案】用合并附件/OCR 之前的原始输入判定「2D 游戏」目标（附件内容不触发提示）。
        var planIntentText = rawSkillInputOverride ?? text;
        // ZXAI：有附件时允许空文字发送——文本类拼进消息；图片按模型能力分流：
        // 多模态模型（deepseek-flash）直发视觉通道；非多模态（如 deepseek-v4-pro）降级本地 OCR
        List<(byte[] Bytes, string MediaType)>? images = null;
        if (!preserveDraft && _attachments.Count > 0)
        {
            var imgAtts = _attachments.Where(a => a.ImageBytes is not null).ToList();
            if (imgAtts.Count > 0)
            {
                if (IsMultimodalModel(AiProviderStore.SelectedModelId))
                {
                    images = imgAtts.Select(a => (a.ImageBytes!, a.MediaType ?? "image/png")).ToList();
                }
                else
                {
                    // 当前模型不支持看图 → 本地 OCR 提取图片文字进消息（尽量不丢信息）
                    var ocrSb = new System.Text.StringBuilder();
                    foreach (var a in imgAtts)
                    {
                        var ocr = await OcrImageBytesAsync(a.ImageBytes!);
                        ocrSb.AppendLine(string.IsNullOrWhiteSpace(ocr)
                            ? string.Format(Ui("ImgOcrEmpty", "【图片：{0}】（未识别到文字）"), a.FileName)
                            : string.Format(Ui("ImgOcrText", "【图片：{0} 的文字内容（本地OCR）】\n{1}"), a.FileName, ocr.Trim()));
                    }
                    AddSystemBubble(string.Format(Ui("ModelNoDirectImage", "当前模型（{0}）不支持直接读图，已自动用本地 OCR 提取图片文字。需要更准的看图能力可切换到 deepseek-flash。"), AiProviderStore.SelectedModelId));
                    var ocrText = ocrSb.ToString().TrimEnd();
                    text = text.Length == 0 ? ocrText : (ocrText.Length > 0 ? text + "\n\n" + ocrText : text);
                }
            }
            var attText = BuildAttachmentsText();
            text = text.Length == 0 ? attText : (attText.Length > 0 ? text + "\n\n" + attText : text);
            // 【A12 审计修复】附件清空移至"发送确定成立"之后（见下方），避免仅图片路径丢弃附件
        }
        // 【A12 审计修复】仅图片也允许发送（此前仅图片时 text 为空直接 return，
        // 而附件已被 ClearAttachments 清空——附件被丢弃且未发送）。
        var hasImages = images is { Count: > 0 };
        if (string.IsNullOrEmpty(text) && !hasImages) return;

        if (_session is null) { _session = CreateSession(); AttachExplicitTaskToNewSession(); ApplyEnginePermissionUi(); } // 【评审修复】懒创建后刷新真实引擎权限 UI

        // 【A07 返修】安全轮次边界：所选 provider/model/endpoint/key 与当前会话启动配置不一致时，
        // 在【本轮发送之前】重建会话（本方法仅在非处理中可达——在途轮次天然不受影响）。
        if (_session is TubaWinUi3.Services.Ai.Dsh.DshSession dshCfg)
        {
            try
            {
                var currentFp = AgentEngine.CurrentLaunchFingerprint();
                if (AgentEngine.NeedsSessionRebuild(dshCfg.LaunchFingerprint, currentFp))
                {
                    // 【主审修复·第六轮】会话身份替换成对处理：先落盘旧会话（此刻仍持有其归属），
                    // 再释放旧归属——新会话在其首次落盘时取得自己的归属（先断后立，不双持、不泄漏）。
                    try { PersistDshConversation(dshCfg); } catch { }
                    _lease.ReleaseCurrent();
                    _session.Dispose();
                    _session = CreateSession();
                    _connectedFingerprint = null;   // 【UI 改版】换了配置：连通状态重置为未验证
                    ApplyEnginePermissionUi();
                    AddSystemBubble(Ui("AiConfigurationChanged", "检测到 AI 服务配置已更改，已按新配置建立新会话（模型上下文重新开始；此前对话仍可在上方查看）。"));
                }
            }
            catch { }
        }

        // 【A12】dsh 已连接且明确不支持图片：不发送、保留待发送附件
        if (hasImages && _session is TubaWinUi3.Services.Ai.Dsh.DshSession dsh && dsh.IsConnected && !dsh.SupportsImageInput)
        {
            AddSystemBubble(Ui("ImageUnsupported", "⚠️ 当前引擎连接未协商图片能力（模型可能不支持视觉）：图片未发送，附件已保留。可切换模型/引擎后重试，或移除图片附件。"));
            return;
        }

        // 【A12 审计修复】发送前快照待发送附件；清空推迟到「本轮发送确认成功」之后——
        // 握手失败/图片能力不协商/网络异常时恢复快照，附件可重试（不能用 Error 提示代替保留附件）。
        var pendingAtts = !preserveDraft && _attachments.Count > 0 ? _attachments.ToList() : null;
        _sendHadError = false;

        _lastUserText = text;
        _lastSendPreservedDraft = preserveDraft;
        _lastRawSkillInput = planIntentText;
        _lastContinuesCurrentTask = continuesCurrentTask;
        _toolFlowRoundStart = _toolFlowVisibleMessages.Count;
        _toolFlowDisplayRoundStart = _displayLog.Count;
        _toolFlowRoundSucceeded = false;

        // 【主审修复·2026-09-22】捕获轮次身份（在 AddUserBubble 之前）：本轮归属的 dsh 会话 +
        // 展示日志代次——晚到回调/临时离开路径均按此归属判定，不依赖 _session 字段当前值。
        _streamingOwner = _session as TubaWinUi3.Services.Ai.Dsh.DshSession;
        _sendEpoch = _displayEpoch;

        // All accepted submissions (including choice answers) start at the newest
        // message. Rejected submissions and history replay never reset reading.
        ResumeLatestFollowing();
        onAccepted?.Invoke();
        AddUserBubble(text);
        MaybeOfferGodotTwoDPlan(planIntentText);   // 【可见方案】只给「打开方案」入口提示，不弹窗、不安装

        _streamingBubble = BeginAssistantBubble();

        _isProcessing = true;
        UpdateInputState();
        if (!preserveDraft) InputBox.Text = "";
        ConversationStateChanged?.Invoke();
        SmartScroll();

        // 【主审补正·2026-09-22】捕获本轮运行时会话引用：生成期间页面可能被导航离开
        // （_session 会被置 null），落盘/结果判定必须基于发起本轮的真实会话，而非当前字段。
        var sendSession = _session;
        var sendDisplayEpoch = _displayEpoch;
        // 【R3】发送前捕获配置指纹：Task.Run 期间用户可能切换提供商/Key/模型/地址，
        // 成功后只在「当前指纹仍与发送时相同」才记录已验证，避免把变更后的新配置误标为已验证。
        var sendVerifyFingerprint = "";
        try { sendVerifyFingerprint = AgentEngine.CurrentLaunchFingerprint(); } catch { }

        try
        {
            await Task.Run(() => sendSession is TubaWinUi3.Services.Ai.Dsh.DshSession dsh
                ? dsh.SendWithSkillInputAsync(text, planIntentText, images, continuesCurrentTask)
                : sendSession is AgentSession builtin
                    ? builtin.SendWithSkillInputAsync(text, planIntentText, images, continuesCurrentTask)
                    : sendSession!.SendAsync(text, images));
        }
        catch (OperationCanceledException)
        {
            if (sendSession is null || !OwnsSession(sendSession) || sendDisplayEpoch != _displayEpoch) return;
            _sendHadError = true; // 【A12】取消=本轮未成功：保留未发送附件
            FinalizeStreaming(); // 清理排队/思考中的空气泡
            AddSystemBubble(Ui("Cancelled", "已取消"));
        }
        catch (Exception ex)
        {
            if (sendSession is null || !OwnsSession(sendSession) || sendDisplayEpoch != _displayEpoch) return;
            _sendHadError = true; // 【A12】直接异常：保留未发送附件（不依赖异步 Error 回调）
            AddErrorBubble(AgentErrorPolicy.FormatApiError(ex));
        }
        finally
        {
            if (sendSession is not null && OwnsSession(sendSession) && sendDisplayEpoch == _displayEpoch)
            {
                StopQueueWatch();
                _isProcessing = false;
                UpdateInputState();
                var outcome = sendSession?.LastSendOutcome ?? Services.Agent.AgentSendOutcome.Rejected;
                var sendSucceeded = outcome == Services.Agent.AgentSendOutcome.Completed && !_sendHadError;
                FinalizeStreaming();
                CompleteToolFlowMessageActions(_sendEpoch, sendSucceeded && !_awaitingConfirmation);
                // Completion metadata must precede the existing guarded display save.
                // 【主审补正·2026-09-22】落盘基于运行时捕获的局部引用：即使页面已在生成期间被导航离开
                // （_session 置 null），用户消息与已产出的部分回复/完整回复仍必须落盘；UI 更新仅在
                // 页面仍持有同一会话（未被导航离开）时执行，避免对已移除元素操作。
                if (sendSession is not null)
                {
                    // 【主审修复·第六轮】统一经租约写守卫（所有引擎；builtin 在此落盘，dsh 由下方 Persist 落盘）。
                    SaveSessionGuarded(sendSession);
                    if (sendSession is TubaWinUi3.Services.Ai.Dsh.DshSession sendDsh
                        && _sendEpoch == _displayEpoch)   // 【主审修复】不把旧轮次的结果写进已切换的会话日志
                        PersistDshConversation(sendDsh);   // 【A06】dsh 会话：展示记录 + 引擎身份落盘
                    if (_session is not null)
                    {
                        TitleText.Text = sendSession.Title;
                        // 【A06 返修】历史恢复失败 → 明确告知（不再"无感"换会话、不再假称上下文延续）
                        if (sendSession is TubaWinUi3.Services.Ai.Dsh.DshSession dshN
                            && dshN.ResumeOutcome == TubaWinUi3.Services.Ai.Dsh.DshSession.ResumeOutcomeKind.Recreated
                            && !_resumeNoticeShown)
                        {
                            _resumeNoticeShown = true;
                            AddSystemBubble(Ui("SessionRestoreFailed", "未能恢复此前的引擎会话（已新建）——历史对话仍可查看，但模型不再记得那段上下文；后续消息将从新上下文开始。"));
                        }
                    }
                }
                // 【A12 返修】发送结果判定（与附件无关，先行计算）：
                // 【UI 改版·返修2】真实成功事件（无论有无附件）都更新已验证配置指纹——
                // 主审复核发现上一版仍在 pendingAtts != null 分支内，纯文本发送成功不会变为"已连接"；此次真正移出。
                // 【R3】只记录与发送时相同的配置：发送期间配置变更 → 不把新配置误标为已验证。
                if (sendSucceeded)
                {
                    try
                    {
                        var nowFingerprint = "";
                        try { nowFingerprint = AgentEngine.CurrentLaunchFingerprint(); } catch { }
                        if (sendVerifyFingerprint.Length > 0 && nowFingerprint == sendVerifyFingerprint)
                        {
                            _connectedFingerprint = sendVerifyFingerprint;
                            // 【修复】持久化已验证指纹：重启/新对话/切会话后配置未变则仍显示"已连接"
                            TubaWinUi3.Services.AppSettings.Set("AiVerifiedFingerprint", _connectedFingerprint);
                            TubaWinUi3.Services.AppSettings.Save();
                        }
                    }
                    catch { }
                    UpdateServiceStatus();
                }
                // 【A12 返修】附件去留由【明确发送结果】决定：LastSendOutcome == Completed 才算成功；
                // 取消 / 失败 / 拒绝（含握手期取消、能力拒绝、异常）一律保留附件（_sendHadError 仅作兜底）。
                if (pendingAtts is not null)
                {
                    if (sendSucceeded)
                    {
                        // 【A12 返修】只消费本轮发送快照里实际带走的附件（运行期间若有新增则保留）
                        foreach (var a in pendingAtts) _attachments.Remove(a);
                    }
                    else
                    {
                        // 失败/取消：恢复快照并把运行期间新增的附件保留在前（不吞）
                        var kept = new List<PendingAttachment>();
                        foreach (var a in _attachments) if (!pendingAtts.Contains(a)) kept.Add(a);
                        _attachments.Clear();
                        _attachments.AddRange(pendingAtts);
                        _attachments.AddRange(kept);
                        AddSystemBubble(string.Format(Ui("SendFailedAttachmentsSaved", "⚠️ 本轮发送未成功（{0}），待发送附件已保留——可修改后重试。"), OutcomeText(outcome)));
                    }
                    UpdateAttachmentBar();
                }
                UpdateTokenUsage();
                SmartScroll();
                RefreshConversationList(); // ZXAI：发送后会话标题可能更新，刷新左侧列表
                ConversationStateChanged?.Invoke();
            }
        }
    }

    // ---------- 确认流 ----------

    private void Confirmation_Resolved(object? sender, ConfirmationResolvedEventArgs e)
        => _ = ResumeAfterConfirmationAsync(e.Decisions);

    private void Plan_Resolved(object? sender, PlanResolvedEventArgs e)
    {
        if (_session is null || !_awaitingConfirmation) return;
        if (_pendingPlanRequest is not { } request) return;

        _ = ResumeAfterConfirmationAsync(
            [new AgentConfirmationDecision { Request = request, Confirmed = e.Approved }]);
    }

    private AgentConfirmationRequest? _pendingPlanRequest;

    private async Task ResumeAfterConfirmationAsync(IReadOnlyList<AgentConfirmationDecision> decisions)
    {
        if (_session is null || !_awaitingConfirmation || decisions.Count == 0) return;
        ResumeLatestFollowing();
        var resumeSession = _session;
        var resumeDisplayEpoch = _displayEpoch;
        _awaitingConfirmation = false;
        _activeConfirmationCard = null;
        _isProcessing = true;
        _sendHadError = false;
        _toolFlowRoundSucceeded = false;
        UpdateInputState();

        try
        {
            await Task.Run(() => resumeSession.ResumeConfirmationsAsync(decisions));
        }
        catch (OperationCanceledException)
        {
            if (!OwnsSession(resumeSession) || resumeDisplayEpoch != _displayEpoch) return;
            _sendHadError = true;
            FinalizeStreaming(); // 清理排队/思考中的空气泡
            AddSystemBubble(Ui("Cancelled", "已取消"));
        }
        catch (Exception ex)
        {
            if (!OwnsSession(resumeSession) || resumeDisplayEpoch != _displayEpoch) return;
            _sendHadError = true;
            AddErrorBubble(AgentErrorPolicy.FormatApiError(ex));
        }
        finally
        {
            if (OwnsSession(resumeSession) && resumeDisplayEpoch == _displayEpoch)
            {
                StopQueueWatch();
                _isProcessing = false;
                UpdateInputState();
                // 页面已关闭（Unload 已置空 _session）时跳过，避免空引用
                if (_session is { } session)
                {
                    FinalizeStreaming();
                    CompleteToolFlowMessageActions(_sendEpoch,
                        session.LastSendOutcome == AgentSendOutcome.Completed && !_sendHadError && !_awaitingConfirmation);
                    SaveSessionGuarded(session);   // 【第六轮】统一经租约写守卫
                    if (session is TubaWinUi3.Services.Ai.Dsh.DshSession resumedDsh)
                        PersistDshConversation(resumedDsh);
                }
                UpdateTokenUsage();
                SmartScroll();
                ConversationStateChanged?.Invoke();
            }
        }
    }

    // ---------- 会话事件 ----------

    private void AppendReasoningChunk(string chunk)
    {
        if (_streamingBubble is null)
            _streamingBubble = BeginAssistantBubble();

        _roundHasText = true;
        StopQueueWatch();
        _streamingBubble.ThinkingRow.Visibility = Visibility.Collapsed;
        _streamingBubble.Reasoning.Visibility = Visibility.Visible;
        _streamingBubble.Reasoning.Append(chunk);
        SmartScroll();
    }

    private void AppendChunk(string chunk)
    {
        // 步骤链之后的新文本块 → 新的助手气泡（文本与步骤按顺序交错展示）
        if (_streamingBubble is null)
        {
            _streamingBubble = BeginAssistantBubble();
        }

        // 首个 chunk 到达：本轮未排队，停止排队检测并收起思考指示
        if (!_roundHasText)
        {
            _roundHasText = true;
            StopQueueWatch();
            if (_streamingBubble.ThinkingRow is { } row)
                row.Visibility = Visibility.Collapsed;
        }

        _streamingBubble.RawContent.Append(chunk);
        var raw = _streamingBubble.RawContent.ToString();
        var excerpt = AssistantReplyPresentation.Create(raw);
        _streamingBubble.ReadableContent = excerpt.Body;
        _streamingBubble.StreamingText.Text = excerpt.Preview;
        if (excerpt.Body != raw.Trim() || excerpt.HasDetails)
        {
            _streamingBubble.StatusText.Text = ModelPreferenceQuestion.GetVisibleContent(raw) != raw
                || ChatChoiceQuestion.GetVisibleContent(raw) != raw
                ? LocalizationService.L("AiChoice_Preparing", "正在整理选项…")
                : LocalizationService.L("AiFlow_PreparingCards", "正在整理方案卡片…");
            _streamingBubble.ThinkingRow.Visibility = Visibility.Visible;
        }
        else _streamingBubble.ThinkingRow.Visibility = Visibility.Collapsed;
        SmartScroll();
    }

    /// <summary>
    /// 首个工具步骤：定稿当前文本气泡，并在其后的消息流位置插入
    /// 独立的步骤链节点（执行中展开，整链完成后自动折叠为摘要）。
    /// </summary>
    private void OnStepStarted(AgentStep step)
    {
        if (_activeChain is null)
        {
            FinalizeStreaming();
            _activeChain = CreateStepChainNode();
        }
        _activeChain.RunVm.AddStep(new StepRowVm(step));
        SmartScroll();
    }

    private void OnStepCompleted(AgentStep step)
    {
        if (_activeChain is null) return;
        var row = _activeChain.RunVm.FindByCallId(step.CallId ?? "");
        row?.Update(step);
        SmartScroll();
    }

    private void OnStepGroupCompleted(AgentStepGroupSummary summary)
    {
        FinalizeStreaming();
        // 【A06】dsh 会话：步骤链快照记入展示记录（与 builtin 的 display 格式一致，恢复时走 AddPersistedStepChain）
        if (!_replayingDisplay && _streamingOwner is not null && _sendEpoch == _displayEpoch && _activeChain?.RunVm is { } vm && vm.Steps.Count > 0)
        {
            _displayLog.Add(new TubaWinUi3.Services.Agent.ConversationDisplayItem
            {
                Type = "steps",
                Steps = vm.Steps.Select(s => TubaWinUi3.Services.Agent.AgentStepSnapshot.From(s.Step)).ToList(),
                SummaryText = summary.ToDisplayText(),
                DurationSeconds = summary.Duration?.TotalSeconds,
                PromptTokens = summary.PromptTokens,
                CompletionTokens = summary.CompletionTokens
            });
        }
        _activeChain?.RunVm.Complete(summary);
        _activeChain = null;
        UpdateTokenUsage();
        SmartScroll();
    }

    private void OnConfirmations(IReadOnlyList<AgentConfirmationRequest> requests)
    {
        StopQueueWatch();
        FinalizeStreaming();
        _awaitingConfirmation = true;
        UpdateInputState();

        if (requests.Count == 1 && requests[0].Kind == "plan")
        {
            _pendingPlanRequest = requests[0];
            var planCard = new PlanCardControl
            {
                Request = requests[0],
                Margin = new Thickness(38, 0, 0, 10)
            };
            planCard.Resolved += Plan_Resolved;
            _activeConfirmationCard = planCard;
            MsgPanel.Children.Add(planCard);
        }
        else
        {
            var card = new ConfirmationCardControl
            {
                Requests = requests,
                Margin = new Thickness(38, 0, 0, 10)
            };
            card.Resolved += Confirmation_Resolved;
            _activeConfirmationCard = card;
            MsgPanel.Children.Add(card);
        }
        SmartScroll();
    }

    private void OnError(string error)
    {
        _sendHadError = true; // 【A12】本轮失败：发送流程据此恢复待发送附件
        StopQueueWatch();
        FinalizeStreaming();
        AddErrorBubble(error);
        SmartScroll();
    }

    /// <summary>流式气泡定稿：文本渲染为 markdown 内容；无文本的气泡（纯工具轮次）移除。</summary>
    private void FinalizeStreaming()
    {
        if (_streamingBubble is null) return;
        var bubble = _streamingBubble;
        _streamingBubble = null;

        var content = bubble.RawContent.ToString();
        if (!string.IsNullOrWhiteSpace(content))
        {
            RenderAssistantContent(bubble, content);
        }
        var reasoningText = bubble.Reasoning.ReasoningContent;
        bubble.Reasoning.Complete(collapse: true);
        if (string.IsNullOrWhiteSpace(content) && !bubble.Reasoning.HasContent && MsgPanel.Children.Contains(bubble.Root))
            MsgPanel.Children.Remove(bubble.Root);
        bubble.StreamingRow.Visibility = Visibility.Collapsed;
        if (MsgPanel.Children.Contains(bubble.Root)) AddToolFlowMessageAction(bubble, content);

        // 【A06】dsh 会话：助手文本定稿记入展示记录（空内容不记）
        // 【主审修复·2026-09-22】按轮次身份判定（_streamingOwner + epoch），而非当前 _session：
        // 页面临时离开（_session=null）时，队列里的完成事件仍须归属本轮会话并记入日志。
        if (!_replayingDisplay && _streamingOwner is not null && _sendEpoch == _displayEpoch &&
            (!string.IsNullOrWhiteSpace(content) || !string.IsNullOrWhiteSpace(reasoningText)))
        {
            _displayLog.Add(new TubaWinUi3.Services.Agent.ConversationDisplayItem
            {
                Type = "text",
                Role = "assistant",
                Content = content,
                ReasoningContent = reasoningText
            });
        }
    }

    /// <summary>
    /// 【A06】dsh 会话落地：展示记录（文本气泡+步骤链按序）+ 引擎身份（真实引擎、ACP 会话 id）。
    /// dsh 自身只持久化 ACP 会话（resume 用；session/list 不返回标题/逐消息历史）——
    /// 界面所需内容必须本地保存（协议参考：WinUI 应保存自己的显示历史、实际引擎、ACP ID 与 cwd）。
    /// </summary>
    private void PersistDshConversation(TubaWinUi3.Services.Ai.Dsh.DshSession? target = null)
    {
        var dsh = target ?? (_session as TubaWinUi3.Services.Ai.Dsh.DshSession);
        if (dsh is null) return;
        try
        {
            if (_displayLog.Count == 0) return;
            // 【主审修复·2026-09-22】写盘归属守卫（非所有者不落盘，避免覆盖对方窗口的新记录）。
            if (!EnsureWriteOwnership(dsh.Id)) return;
            AiAssistantService.SaveConversationDisplay(dsh.Id, _displayLog);
            AiAssistantService.SaveConversationEngineInfo(dsh.Id, dsh.Title, "dsh", dsh.DshSessionId, _displayLog.Count, dsh.EffectiveWorkspace, dsh.PersonaId);
        }
        catch { }
    }

    /// <summary>【主审修复·2026-09-22】写盘归属守卫：仅当本页面是该会话的所有者
    /// （或可立即取得——新会话首次落盘）时才允许写盘；被其他窗口持有时拒绝写入，
    /// 确保同一「数据根 + 会话 ID」始终只有一个写入者。
    /// 【第六轮】改由 ConversationLease 承载：关闭终态下永远拒绝（迟到回调不可重获/不可覆盖）。</summary>
    private bool EnsureWriteOwnership(string conversationId) => _lease.EnsureOwned(conversationId);

    /// <summary>【主审修复·第六轮】所有引擎的统一保存接入：保存前经租约写守卫
    /// （新会话首次落盘取得归属；关闭终态/非所有者拒写）——builtin 与 dsh 同一规则。</summary>
    private void SaveSessionGuarded(IAgentSession session)
    {
        if (!EnsureWriteOwnership(session.Id)) return;
        session.Save();
    }

    /// <summary>【主审修复·2026-09-22】释放当前会话写入归属（幂等：非持有者调用无效果）。
    /// 临时导航离开不调用；真关闭（Unload→Close）与切换会话/新对话时调用。
    /// 【第六轮】释放语义由 ConversationLease 承载（单当前 ID；关闭终态）。</summary>
    private void ReleaseOwnedConversation() => _lease.ReleaseCurrent();

    private bool _sidebarManualOpen;
    private const double CompactSidebarThreshold = 900;

    /// <summary>【UI 改版】小窗会话栏折叠/展开（宽窗下按钮隐藏）。
    /// 会话栏宽度由代码按窗口宽度驱动（VisualState 不支持 ColumnDefinition 目标）。</summary>
    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarManualOpen = !_sidebarManualOpen;
        ApplyCompactSidebar(ActualWidth);
    }

    private void ApplyCompactSidebar(double width)
    {
        try
        {
            var compact = width < CompactSidebarThreshold;
            SidebarToggleButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(compact && !_sidebarManualOpen ? 0 : 230);

            // 【UI 改版·返修】极窄窗（<640）：收纳权限开关与用量气泡，保证发送/停止始终可点。
            var veryNarrow = width < 640;
            FullAccessToggle.Visibility = veryNarrow ? Visibility.Collapsed : Visibility.Visible;
            PersonaButtonLabel.Visibility = veryNarrow ? Visibility.Collapsed : Visibility.Visible;
            PersonaCompactIcon.Visibility = veryNarrow ? Visibility.Visible : Visibility.Collapsed;
            ToolFlowResumeButtonLabel.Visibility = veryNarrow ? Visibility.Collapsed : Visibility.Visible;
            ToolFlowResumeCompactIcon.Visibility = veryNarrow ? Visibility.Visible : Visibility.Collapsed;
            ModelButtonLabel.MaxWidth = veryNarrow ? 72 : 180;
            if (veryNarrow)
            {
                TokenUsageBubble.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateTokenUsage();   // 恢复宽窗时按真实用量刷新可见性
            }
            RefreshWorkbenchLayout();
        }
        catch { }
    }

    /// <summary>【A12】发送结果的用户可读文案。</summary>
    private static string OutcomeText(Services.Agent.AgentSendOutcome o) => o switch
    {
        Services.Agent.AgentSendOutcome.Cancelled => Ui("Cancelled", "已取消"),
        Services.Agent.AgentSendOutcome.Failed => Ui("Failed", "失败"),
        Services.Agent.AgentSendOutcome.Rejected => Ui("NotSubmitted", "未提交"),
        _ => Ui("Unsuccessful", "未成功"),
    };

    /// <summary>创建独立的步骤链节点（与文本气泡按事件顺序交错排列）。</summary>
    private StepChainControl CreateStepChainNode()
    {
        var chain = new StepChainControl
        {
            Margin = new Thickness(38, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        chain.RunVm = new RunVm();
        MsgPanel.Children.Add(chain);
        return chain;
    }

    // ---------- 左侧会话层（ZXAI 2026-09-20 恢复：上游 Agent 重写 --theirs 时被冲掉） ----------

    /// <summary>会话列表项：展示用短标题/短时间 + 原元数据（供重命名/删除/切换）。</summary>
    private sealed record ConversationListItem(string Id, string Title, string TimeText, ConversationMeta Meta);

    /// <summary>刷新左侧会话列表（保持当前会话选中；读元数据失败时静默跳过）。</summary>
    private void RefreshConversationList()
    {
        try
        {
            var window = WindowConversations?.Invoke() ?? [];
            var windowIds = window.Select(meta => meta.Id).ToHashSet(StringComparer.Ordinal);
            var items = window.Concat(AiAssistantService.ListConversations().Where(meta => !windowIds.Contains(meta.Id)))
                .Select(m => new ConversationListItem(
                    m.Id,
                    string.IsNullOrWhiteSpace(m.Title) ? Ui("NewChat", "新对话") : m.Title,
                    m.CreatedAt.ToString("MM-dd HH:mm"),
                    m))
                .ToList();

            ConversationList.ItemsSource = items;

            var current = items.FirstOrDefault(i => i.Id == ConversationId);
            if (current is not null)
                ConversationList.SelectedItem = current;
        }
        catch { }
    }

    private void SidebarNewChatButton_Click(object sender, RoutedEventArgs e)
    {
        RequestNewConversation();
        RefreshConversationList();
    }

    /// <summary>页头机器人 Logo：点击回到新对话（欢迎页）。</summary>
    private void LogoHomeButton_Click(object sender, RoutedEventArgs e)
    {
        RequestNewConversation();
        RefreshConversationList();
    }

    // ===== 【修复】跨窗口会话列表同步：监听历史目录，任何窗口删/建/改会话 → 全部窗口同步刷新 =====

    private void StartHistoryWatcher()
    {
        if (_historyWatcher is not null) return;
        try
        {
            var dir = AiAssistantService.HistoryDirForWatch;
            if (!Directory.Exists(dir)) return;
            var w = new FileSystemWatcher(dir)
            {
                Filter = "*.json",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            w.Deleted += OnHistoryDirChanged;
            w.Created += OnHistoryDirChanged;
            w.Renamed += OnHistoryDirChanged;
            w.EnableRaisingEvents = true;
            _historyWatcher = w;
        }
        catch { }
    }

    private void StopHistoryWatcher()
    {
        try
        {
            if (_historyWatcher is not null)
            {
                _historyWatcher.EnableRaisingEvents = false;
                _historyWatcher.Dispose();
                _historyWatcher = null;
            }
            if (_historyRefreshTimer is not null)
            {
                _historyRefreshTimer.Stop();
                _historyRefreshTimer = null;
            }
        }
        catch { }
    }

    private void OnHistoryDirChanged(object sender, FileSystemEventArgs e)
    {
        // 后台线程回调 → 切 UI 线程 + 防抖 600ms（合并自己保存引发的连续写事件）
        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (_historyRefreshTimer is null)
                    {
                        _historyRefreshTimer = DispatcherQueue.CreateTimer();
                        _historyRefreshTimer.Interval = TimeSpan.FromMilliseconds(600);
                        _historyRefreshTimer.IsRepeating = false;
                        _historyRefreshTimer.Tick += (_, _) => { try { RefreshConversationList(); } catch { } };
                    }
                    _historyRefreshTimer.Stop();
                    _historyRefreshTimer.Start();
                }
                catch { }
            });
        }
        catch { }
    }

    private void ConversationList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ConversationListItem item) return;
        if (item.Id == _session?.Id) return;
        RequestOpenConversation(item.Meta);
        RefreshConversationList();
    }

    /// <summary>会话项「更多」菜单：重命名 / 删除（复用现有对话框流程）。</summary>
    private void ConversationMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not ConversationListItem item) return;

        var flyout = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = Ui("Rename", "重命名") };
        rename.Click += (_, _) => OnRenameConversation(item.Meta);
        var delete = new MenuFlyoutItem { Text = Ui("Delete", "删除") };
        delete.Click += (_, _) => OnDeleteConversation(item.Meta);
        flyout.Items.Add(rename);
        flyout.Items.Add(delete);
        AiHistoryMenu.FollowOwnerTheme(flyout, btn);
        flyout.ShowAt(btn);
    }

    // ---------- 附件上传（ZXAI 2026-09-20 恢复：上游重写为原生后按原生方式重做） ----------

    /// <summary>待发送附件：TextContent=已读取文本；ImageBytes=图片原始字节（多模态直发）；
    /// 两者皆 null 时发送时给路径说明。</summary>
    private sealed record PendingAttachment(string FileName, string? TextContent, string Path, byte[]? ImageBytes = null, string? MediaType = null);

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            };
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");

            var files = await picker.PickMultipleFilesAsync();
            if (files is null) return;
            // 【A12 返修】选择器是异步的：返回时可能已进入执行中——丢弃迟到结果并明确告知，
            // 避免运行期间新增附件被本轮 finally 的全量清空/快照恢复吞掉。
            if (_isProcessing || _awaitingConfirmation)
            {
                AddSystemBubble(Ui("AttachmentIgnoredWhileRunning", "正在执行中，本次附件选择已忽略；停止或完成后再添加。"));
                return;
            }

            foreach (var file in files)
            {
                if (_attachments.Count >= 5) break;   // 上限 5 个
                // 图片：优先走多模态直发通道（读原始字节，≤4MB）
                if (IsDirectImage(file.Name))
                {
                    try
                    {
                        var props0 = await file.GetBasicPropertiesAsync();
                        if (props0.Size <= 4_000_000)
                        {
                            var ms = new System.IO.MemoryStream();
                            using (var src = await file.OpenStreamForReadAsync())
                                await src.CopyToAsync(ms);
                            _attachments.Add(new PendingAttachment(file.Name, null, file.Path, ms.ToArray(), GetImageMediaType(file.Name)));
                            continue;
                        }
                    }
                    catch { }
                    // 读失败或过大 → 掉到 OCR 兜底（下方 TryExtractDocumentTextAsync 的图片分支）
                }

                string? textContent = null;
                try
                {
                    if (IsTextLikeFile(file.Name))
                    {
                        var props = await file.GetBasicPropertiesAsync();
                        if (props.Size <= 200_000)
                        {
                            textContent = await Windows.Storage.FileIO.ReadTextAsync(file);
                            if (textContent.Length > 24_000)
                                textContent = textContent[..24_000] + Ui("Truncated", "\n…（内容过长已截断）");
                        }
                        else
                            textContent = Ui("AttachmentNotInlined", "（文件较大，未内联；可按路径按需读取）");
                    }
                    else
                    {
                        var (doc, handled) = await TryExtractDocumentTextAsync(file);
                        if (handled)
                        {
                            textContent = doc;
                            if (textContent is { Length: > 24_000 })
                                textContent = textContent[..24_000] + Ui("Truncated", "\n…（内容过长已截断）");
                        }
                    }
                }
                catch { }
                _attachments.Add(new PendingAttachment(file.Name, textContent, file.Path));
            }
            UpdateAttachmentBar();
        }
        catch (Exception ex)
        {
            TubaWinUi3.Services.Agent.AgentDebugLog.Info($"[Attach] EX: {ex}");
        }
    }

    private static bool IsTextLikeFile(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".txt" or ".md" or ".markdown" or ".cs" or ".js" or ".ts" or ".py" or ".java" or ".c" or ".cpp"
            or ".h" or ".hpp" or ".json" or ".xml" or ".yml" or ".yaml" or ".csv" or ".log" or ".ini" or ".cfg"
            or ".bat" or ".ps1" or ".sh" or ".sql" or ".html" or ".htm" or ".css" or ".lua" or ".gd" or ".toml"
            or ".csproj" or ".sln" or ".kt" or ".rs" or ".go" or ".rb" or ".php" or ".vue" or ".svelte"
            or ".srt" or ".vtt" or ".ass" or ".ssa" or ".mm" or ".opml";
    }

    /// <summary>ZXAI：文档内容提取（docx 用 DocxReader；xlsx/pptx/xmind/epub/zip 解包；pdf 用 PdfPig；图片走本地 OCR）。
    /// 返回 (文本, 是否已处理)——未处理=按二进制附件给路径说明。全部本地完成，不上传。</summary>
    private static async Task<(string? Text, bool Handled)> TryExtractDocumentTextAsync(Windows.Storage.StorageFile file)
    {
        var ext = System.IO.Path.GetExtension(file.Name).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".docx":
                    return (DocxReader.ToPlainText(file.Path), true);
                case ".xlsx" or ".xlsm":
                    return (ExtractXlsxText(file.Path), true);
                case ".pptx":
                    return (ExtractPptxText(file.Path), true);
                case ".xmind":
                    return (ExtractXmindText(file.Path), true);
                case ".pdf":
                    return (await ExtractPdfTextSmartAsync(file), true);
                case ".epub":
                    return (ExtractEpubText(file.Path), true);
                case ".zip":
                    return (ListZipContents(file.Path), true);
                case ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".webp":
                {
                    var ocr = await ExtractImageTextAsync(file);
                    if (ocr is null)
                        return (Ui("OcrUnavailable", "（未能对图片做 OCR：系统缺少 OCR 引擎）"), true);
                    return (ocr.Trim().Length == 0 ? Ui("OcrNoText", "（图片中未识别到文字）") : ocr, true);
                }
                case ".wav" or ".mp3" or ".m4a" or ".ogg" or ".flac" or ".aac" or ".wma":
                    return (await ExtractAudioTextAsync(file), true);
                case ".doc":
                    return (await ConvertLegacyOfficeAsync(file, LegacyApp.Word), true);
                case ".xls":
                    return (await ConvertLegacyOfficeAsync(file, LegacyApp.Excel), true);
                case ".ppt":
                    return (await ConvertLegacyOfficeAsync(file, LegacyApp.PowerPoint), true);
            }
        }
        catch { }
        return (null, false);
    }

    /// <summary>pdf：PdfPig 本地提取文本（前 40 页）。</summary>
    private static string ExtractPdfText(string path)
    {
        var sb = new System.Text.StringBuilder();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        var pages = Math.Min(doc.NumberOfPages, 40);
        for (int i = 1; i <= pages; i++)
        {
            string text;
            try { text = doc.GetPage(i).Text; }
            catch { continue; }
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(string.Format(Ui("PdfPageHeading", "【第 {0} 页】"), i));
                sb.AppendLine(text.Trim());
            }
        }
        if (doc.NumberOfPages > pages)
            sb.AppendLine(string.Format(Ui("PdfTruncated", "…（共 {0} 页，已提取前 {1} 页）"), doc.NumberOfPages, pages));
        return sb.ToString();
    }

    /// <summary>epub：解 zip 逐章剥 HTML 标签提纯文本。</summary>
    private static string ExtractEpubText(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var sb = new System.Text.StringBuilder();
        foreach (var e in zip.Entries
                     .Where(x => x.FullName.EndsWith(".xhtml") || x.FullName.EndsWith(".html") || x.FullName.EndsWith(".htm"))
                     .OrderBy(x => x.FullName).Take(60))
        {
            using var sr = new StreamReader(e.Open());
            var html = sr.ReadToEnd();
            var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\s{2,}", " ").Trim();
            if (text.Length > 0) sb.AppendLine(text);
        }
        return sb.ToString();
    }

    /// <summary>zip：列内容清单（不递归提取，避免炸弹）。</summary>
    private static string ListZipContents(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var sb = new System.Text.StringBuilder();
        foreach (var e in zip.Entries.Take(200))
            if (!e.FullName.EndsWith("/"))
                sb.AppendLine(e.FullName);
        if (zip.Entries.Count > 200)
            sb.AppendLine(string.Format(Ui("ZipTruncated", "…（共 {0} 项）"), zip.Entries.Count));
        return sb.ToString();
    }

    /// <summary>pdf 智能提取：先文本层（PdfPig）；文本过少视为扫描件 → WinRT 渲染前 10 页 → OCR 兜底。</summary>
    private static async Task<string> ExtractPdfTextSmartAsync(Windows.Storage.StorageFile file)
    {
        string text;
        try { text = ExtractPdfText(file.Path); }
        catch { text = ""; }
        if (text.Trim().Length >= 50) return text;

        try
        {
            var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans-CN"))
                      ?? Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) return text.Length > 0 ? text : Ui("PdfNoTextNoOcr", "（PDF 未提取到文字，且无 OCR 引擎）");

            var sb = new System.Text.StringBuilder();
            var pages = Math.Min((int)pdf.PageCount, 10);
            for (uint i = 0; i < pages; i++)
            {
                using var page = pdf.GetPage(i);
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1600 });
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                using var bmp = await decoder.GetSoftwareBitmapAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                var res = await engine.RecognizeAsync(bmp);
                if (!string.IsNullOrWhiteSpace(res.Text))
                    sb.AppendLine(string.Format(Ui("PdfPageOcrHeading", "【第 {0} 页·OCR】{1}"), i + 1, res.Text));
            }
            if (pdf.PageCount > pages)
                sb.AppendLine(string.Format(Ui("PdfOcrTruncated", "…（共 {0} 页，已 OCR 前 {1} 页）"), pdf.PageCount, pages));
            var ocrText = sb.ToString().Trim();
            return ocrText.Length > 0 ? ocrText : (text.Length > 0 ? text : Ui("PdfNoReadableText", "（PDF 未提取到可读文字）"));
        }
        catch
        {
            return text.Length > 0 ? text : Ui("PdfFailed", "（PDF 提取与 OCR 均失败）");
        }
    }

    /// <summary>音频转写：ffmpeg 转 16k 单声道 wav → System.Speech 文件识别（zh-CN，本地离线）。</summary>
    private static async Task<string> ExtractAudioTextAsync(Windows.Storage.StorageFile file)
    {
        var ext = System.IO.Path.GetExtension(file.Name).ToLowerInvariant();
        string? tempWav = null;
        try
        {
            var wavPath = file.Path;
            if (ext != ".wav")
            {
                tempWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"zxai_stt_{Guid.NewGuid():N}.wav");
                if (!await ConvertToWavAsync(file.Path, tempWav))
                    return Ui("AudioNoFfmpeg", "（无法转码该音频：需要 ffmpeg。可先把音频转成 .wav 再上传）");
                wavPath = tempWav;
            }

            var wav = wavPath;
            var text = await Task.Run(() =>
            {
                using var engine = new SpeechRecognitionEngine(new System.Globalization.CultureInfo("zh-CN"));
                engine.LoadGrammar(new DictationGrammar());
                engine.SetInputToWaveFile(wav);
                var result = engine.Recognize();
                return result?.Text ?? "";
            });
            return string.IsNullOrWhiteSpace(text) ? Ui("SpeechEmpty", "（未识别到语音内容）") : string.Format(Ui("SpeechTranscript", "【语音转写】{0}"), text);
        }
        catch (Exception ex)
        {
            TubaWinUi3.Services.Agent.AgentDebugLog.Info($"[STT] EX: {ex.Message}");
            return Ui("SpeechFailed", "（语音转写失败：需要系统装有中文语音识别）");
        }
        finally
        {
            if (tempWav is not null) { try { System.IO.File.Delete(tempWav); } catch { } }
        }
    }

    /// <summary>ffmpeg 转 16kHz 单声道 PCM wav。</summary>
    private static async Task<bool> ConvertToWavAsync(string input, string output)
    {
        try
        {
            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null) return false;
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
            {
                Arguments = $"-y -i \"{input}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{output}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0 && System.IO.File.Exists(output);
        }
        catch { return false; }
    }

    private static string? FindFfmpeg()
    {
        var local = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (System.IO.File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var p = System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (System.IO.File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    private enum LegacyApp { Word, Excel, PowerPoint }

    /// <summary>旧版 Office（.doc/.xls/.ppt）：COM 转换（Word/Excel/PowerPoint 或 WPS 兼容）→ 临时新格式 → 递归提取 → 删临时文件。
    /// A05：①打开文件【之前】必须通过禁宏闸门（<see cref="OfficeMacroSafety.RunGated"/> → AutomationSecurity=3，安全模式ForceDisable），
    /// 设置失败 / 属性不可用 / 读回抛错 / 读回值≠3 时【中止处理该文件并返回错误】，绝不打开文件
    /// （伪对象回归测试 OfficeMacroSafetyTests）；②旧版 Excel 二进制（.xls/.xla/.xlt）【拒绝】自动转换 ——
    /// Excel 4.0 宏（XLM）不在 ForceDisable 覆盖范围内，见 <see cref="OfficeMacroSafety.TryRefuseXlmUnsafeAutoConversion"/>，
    /// 不靠注释声称已修复。</summary>
    private static async Task<string> ConvertLegacyOfficeAsync(Windows.Storage.StorageFile file, LegacyApp app)
    {
        var newExt = app switch { LegacyApp.Word => "docx", LegacyApp.Excel => "xlsx", _ => "pptx" };
        var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"zxai_conv_{Guid.NewGuid():N}.{newExt}");
        var srcPath = file.Path;

        // A05（XLM 策略）：旧版 Excel 二进制（.xls/.xla/.xlt）在自动转换前【拒绝】——
        // 打开前的闸门只设置 AutomationSecurity=3（安全模式ForceDisable），Microsoft 明确说明该设置不覆盖
        // Excel 4.0 宏（XLM 宏表可驻留在 .xls 工作簿流内），本工具也无法在打开前判断文件是否含 XLM，
        // 因此不创建 Office/WPS 实例、不打开该文件，直接返回拒绝原因 + 不执行活动内容的替代路径。
        var xlmRefusal = OfficeMacroSafety.TryRefuseXlmUnsafeAutoConversion(srcPath);
        if (xlmRefusal is not null)
        {
            TubaWinUi3.Services.Agent.AgentDebugLog.Info($"[Attach] XLM 策略拒绝自动转换 {srcPath}：{xlmRefusal}");
            return string.Format(Ui("OpenRefused", "（未打开该文件：{0}）"), xlmRefusal);
        }

        // A05：禁宏闸门失败原因（非 null = 该文件已被阻止打开，用于向用户展示）。
        string? macroGateError = null;

        var ok = await Task.Run(() =>
        {
            object? application = null;
            try
            {
                var progIds = app switch
                {
                    LegacyApp.Word => new[] { "Word.Application", "KWps.Application" },
                    LegacyApp.Excel => new[] { "Excel.Application", "KET.Application" },
                    _ => new[] { "PowerPoint.Application", "KWPP.Application" },
                };
                var componentName = app switch
                {
                    LegacyApp.Word => Ui("ComponentWord", "Word / WPS 文字"),
                    LegacyApp.Excel => Ui("ComponentExcel", "Excel / WPS 表格"),
                    _ => Ui("ComponentPowerPoint", "PowerPoint / WPS 演示"),
                };

                foreach (var progId in progIds)
                {
                    var type = Type.GetTypeFromProgID(progId);
                    if (type is null) continue;
                    try
                    {
                        application = Activator.CreateInstance(type);
                        if (application is null) continue;

                        // A05（审计整改）：在 Open 之前强制禁宏 —— AutomationSecurity = msoAutomationSecurityForceDisable(3)。
                        // 设置失败 / 属性不可用 / 读回抛错 / 读回值≠3 → RunGated 返回错误，本方法停止处理该文件，绝不继续打开
                        // （Office 自动化默认 msoAutomationSecurityLow = 允许宏；Visible=false、DisplayAlerts=false 都不控制宏执行）。
                        // 闸门与 Open 由 RunGated 串成一次调用：闸门失败时 openAction 一次也不会执行（回归测试 OfficeMacroSafetyTests）。
                        // 能力边界：AutomationSecurity 不覆盖 Excel 4.0（XLM）宏 —— 旧版 Excel 二进制已在本方法开头按 XLM 策略【拒绝自动转换】。
                        var comApp = application;
                        macroGateError = OfficeMacroSafety.RunGated(
                            new DynamicComAppAdapter(comApp),
                            componentName,
                            () => ConvertLegacyByCom(comApp, app, srcPath, outPath));

                        if (macroGateError is not null)
                        {
                            TubaWinUi3.Services.Agent.AgentDebugLog.Info($"[Attach] 禁宏闸门阻止打开 {srcPath}：{macroGateError}");
                            return false; // 不再尝试其它组件，也不再打开该文件（Quit 交给 finally）
                        }

                        return System.IO.File.Exists(outPath);
                    }
                    catch
                    {
                        QuitQuietly(application);
                        application = null;
                    }
                }
                return false;
            }
            catch { return false; }
            finally
            {
                QuitQuietly(application);
            }
        });

        if (macroGateError is not null)
            return string.Format(Ui("OpenRefusedGated", "（未打开该文件：{0}）"), macroGateError);

        if (!ok)
            return Ui("LegacyConvertNoOffice", "（无法转换旧版格式：本机未找到可用的 Office/WPS —— 请在 Office/WPS 里另存为新格式后重试）");

        try
        {
            var converted = await Windows.Storage.StorageFile.GetFileFromPathAsync(outPath);
            var (text, _) = await TryExtractDocumentTextAsync(converted);
            return text ?? Ui("ConvertEmpty", "（转换成功但内容为空）");
        }
        catch
        {
            return Ui("ConvertReadFailed", "（转换成功但读取失败）");
        }
        finally
        {
            try { System.IO.File.Delete(outPath); } catch { }
        }
    }

    /// <summary>A05：禁宏闸门（RunGated）确认通过后才执行 —— 按旧格式家族打开源文件并另存为临时新格式。
    /// dynamic 只出现在本方法内，闸门契约集中在 <see cref="OfficeMacroSafety.RunGated"/>，便于伪对象回归。</summary>
    private static void ConvertLegacyByCom(object application, LegacyApp family, string srcPath, string outPath)
    {
        dynamic com = application;
        switch (family)
        {
            case LegacyApp.Word:
            {
                com.Visible = false;
                dynamic doc = com.Documents.Open(srcPath);
                doc.SaveAs2(outPath, 16); // wdFormatXMLDocument
                doc.Close(false);
                break;
            }
            case LegacyApp.Excel:
            {
                com.Visible = false;
                com.DisplayAlerts = false;
                dynamic wb = com.Workbooks.Open(srcPath);
                wb.SaveAs(outPath, 51); // xlOpenXMLWorkbook
                wb.Close(false);
                break;
            }
            case LegacyApp.PowerPoint:
            {
                dynamic pres = com.Presentations.Open(srcPath, -1, 0, 0); // ReadOnly, Untitled:0, WithWindow:0
                pres.SaveAs(outPath, 24); // ppSaveAsOpenXMLPresentation
                pres.Close();
                break;
            }
        }
    }

    /// <summary>COM 退出：失败静默（组件可能已自行退出或从未启动；退出失败不应覆盖真正的错误）。</summary>
    private static void QuitQuietly(object? application)
    {
        if (application is null) return;
        try { ((dynamic)application).Quit(); } catch { }
    }

    /// <summary>模型是否支持图片视觉输入（多模态）。当前仅 deepseek-flash(V4.1)；
    /// 名单可用设置项 AppSettings "MultimodalModels"（逗号分隔）覆盖扩展。</summary>
    private static bool IsMultimodalModel(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return false;
        var custom = AppSettings.Get("MultimodalModels");
        var list = string.IsNullOrWhiteSpace(custom)
            ? new[] { "deepseek-flash" }
            : custom.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Any(m => modelId.Equals(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>对内存中的图片字节做本地 OCR（非多模态模型的降级路径）。</summary>
    private static async Task<string?> OcrImageBytesAsync(byte[] bytes)
    {
        try
        {
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans-CN"))
                      ?? Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) return null;

            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            using var bmp = await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bmp);
            return result.Text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>图片扩展名判断（直发集合：png/jpg/jpeg/gif/webp）。</summary>
    private static bool IsDirectImage(string fileName)
        => System.IO.Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    private static string GetImageMediaType(string fileName)
        => System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/png",
        };

    /// <summary>图片：Windows 本地 OCR（zh-Hans-CN 优先，离线免费）。</summary>
    private static async Task<string?> ExtractImageTextAsync(Windows.Storage.StorageFile file)
    {
        try
        {
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans-CN"))
                      ?? Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) return null;

            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            return result.Text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>xlsx：解 zip 读 sharedStrings + 工作表单元格（逗号/竖线分隔行）。</summary>
    private static string ExtractXlsxText(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var shared = new List<string>();
        var ss = zip.GetEntry("xl/sharedStrings.xml");
        if (ss is not null)
        {
            using var sr = new StreamReader(ss.Open());
            var xml = sr.ReadToEnd();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xml, "<t[^>]*>([^<]*)</t>"))
                shared.Add(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
        }

        var sb = new System.Text.StringBuilder();
        foreach (var sheet in zip.Entries
                     .Where(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml"))
                     .OrderBy(e => e.FullName).Take(8))
        {
            sb.AppendLine($"【{System.IO.Path.GetFileNameWithoutExtension(sheet.FullName)}】");
            using var sr = new StreamReader(sheet.Open());
            var xml = sr.ReadToEnd();
            foreach (System.Text.RegularExpressions.Match row in System.Text.RegularExpressions.Regex.Matches(
                         xml, "<row[^>]*>(.*?)</row>", System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                var cells = new List<string>();
                foreach (System.Text.RegularExpressions.Match c in System.Text.RegularExpressions.Regex.Matches(
                             row.Groups[1].Value, "<c[^>]*?(?: t=\"(\\w+)\")?[^>]*>(?:<v>([^<]*)</v>)?",
                             System.Text.RegularExpressions.RegexOptions.Singleline))
                {
                    var v = c.Groups[2].Value;
                    if (c.Groups[1].Value == "s" && int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count)
                        v = shared[idx];
                    if (!string.IsNullOrWhiteSpace(v)) cells.Add(v);
                }
                if (cells.Count > 0) sb.AppendLine(string.Join(" | ", cells));
            }
        }
        return sb.ToString();
    }

    /// <summary>pptx：解 zip 逐页提取 <a:t> 文本。</summary>
    private static string ExtractPptxText(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var sb = new System.Text.StringBuilder();
        foreach (var slide in zip.Entries
                     .Where(e => e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml"))
                     .OrderBy(e => e.FullName))
        {
            using var sr = new StreamReader(slide.Open());
            var xml = sr.ReadToEnd();
            var texts = System.Text.RegularExpressions.Regex.Matches(xml, "<a:t>([^<]*)</a:t>")
                .Select(m => m.Groups[1].Value)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            if (texts.Count > 0)
                sb.AppendLine($"【{System.IO.Path.GetFileNameWithoutExtension(slide.FullName)}】{string.Join(" ／ ", texts)}");
        }
        return sb.ToString();
    }

    /// <summary>xmind：解 zip 从 content.json/xml 提取全部 title（含子主题层级去括号）。</summary>
    private static string ExtractXmindText(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.json") ?? zip.GetEntry("content.xml");
        if (entry is null) return string.Empty;
        using var sr = new StreamReader(entry.Open());
        var raw = sr.ReadToEnd();
        var sb = new System.Text.StringBuilder();
        var isJson = entry.FullName.EndsWith(".json");
        var pattern = isJson ? "\"title\"\\s*:\\s*\"([^\"]+)\"" : "title=\"([^\"]+)\"";
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw, pattern))
            sb.AppendLine(m.Groups[1].Value);
        return sb.ToString();
    }

    // ---------- 语音听写（ZXAI 2026-09-20：System.Speech + 本机 zh-CN 识别器） ----------

    private void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isListening)
            {
                StopVoiceInput();
                AddSystemBubble(Ui("VoiceStopped", "语音输入已停止。"));
                return;
            }

            _speechEngine ??= CreateSpeechEngine();
            if (_speechEngine is null) return;

            _speechEngine.RecognizeAsync(RecognizeMode.Multiple);
            _isListening = true;
            VoiceIcon.Glyph = "\uE71A"; // 停止图标
            RunStateText.Text = Ui("Listening", "正在听写…（再点麦克风停止）");
        }
        catch (Exception ex)
        {
            TubaWinUi3.Services.Agent.AgentDebugLog.Info($"[Voice] start EX: {ex}");
            AddSystemBubble(string.Format(Ui("VoiceUnavailable", "语音输入不可用：{0}"), ex.Message));
            StopVoiceInput();
        }
    }

    private SpeechRecognitionEngine? CreateSpeechEngine()
    {
        try
        {
            var engine = new SpeechRecognitionEngine(new System.Globalization.CultureInfo("zh-CN"));
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();
            engine.SpeechRecognized += (_, e) =>
            {
                var text = e.Result?.Text;
                if (string.IsNullOrWhiteSpace(text)) return;
                _dq.TryEnqueue(() =>
                {
                    InputBox.Text = string.IsNullOrEmpty(InputBox.Text) ? text : InputBox.Text + text;
                    InputBox.SelectionStart = InputBox.Text.Length;
                });
            };
            return engine;
        }
        catch (Exception ex)
        {
            TubaWinUi3.Services.Agent.AgentDebugLog.Info($"[Voice] init failed: {ex.Message}");
            AddSystemBubble(Ui("VoiceRecognizerInitFailed", "语音识别初始化失败（需在 系统设置 → 时间和语言 → 语音 中确认已装中文语音识别）。"));
            return null;
        }
    }

    private void StopVoiceInput()
    {
        try { _speechEngine?.RecognizeAsyncStop(); } catch { }
        _isListening = false;
        try
        {
            VoiceIcon.Glyph = "\uE720"; // 麦克风
            RunStateText.Text = Ui("Ready", "就绪");
        }
        catch { }
    }

    /// <summary>刷新附件条（每项：图标 + 文件名 + 移除按钮）。</summary>
    private void UpdateAttachmentBar()
    {
        AttachmentBar.Children.Clear();
        for (int i = 0; i < _attachments.Count; i++)
        {
            var att = _attachments[i];
            var idx = i;

            var remove = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Background = null,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new FontIcon { Glyph = "\uE711", FontSize = 9 }
            };
            remove.Click += (_, _) => { _attachments.RemoveAt(idx); UpdateAttachmentBar(); };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var attachmentIcon = new FontIcon { Glyph = "\uE723", FontSize = 12 };
            var attachmentName = new TextBlock { Text = att.FileName, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(attachmentIcon);
            panel.Children.Add(attachmentName);
            panel.Children.Add(remove);

            var attachmentCard = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 4, 6, 4),
                Child = panel
            };
            ThemeBind(attachmentCard, Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
            ThemeBind(attachmentIcon, FontIcon.ForegroundProperty, "TextFillColorSecondaryBrush");
            ThemeBind(attachmentName, TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            AttachmentBar.Children.Add(attachmentCard);
        }
        AttachmentBar.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearAttachments()
    {
        _attachments.Clear();
        UpdateAttachmentBar();
    }

    /// <summary>把附件拼成消息文本：文本类内联；其他类型给路径说明。</summary>
    private string BuildAttachmentsText()
    {
        if (_attachments.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var a in _attachments)
        {
            if (a.ImageBytes is not null)
                continue; // 图片在 SendAsync 中按模型能力单独处理（直发/OCR），不在此拼文本
            if (a.TextContent is not null)
                sb.AppendLine(string.Format(Ui("AttachmentText", "【附件：{0}】\n```\n{1}\n```"), a.FileName, a.TextContent));
            else
                sb.AppendLine(string.Format(Ui("AttachmentByPath", "【附件：{0}】路径：{1}（非文本或较大文件）"), a.FileName, a.Path));
        }
        return sb.ToString().TrimEnd();
    }

    // ---------- 历史 / 新对话 ----------

    private void LoadLatestConversation()
    {
        var conversations = AiAssistantService.ListConversations();
        if (conversations.Count > 0)
        {
            LoadConversation(conversations[0]);
            // 历史会话也展示当前技能状态，避免"技能没加载"的误解
            ShowActiveSkillsBubble();
        }
        else
            Welcome();
    }

    private void Welcome()
    {
        // 【UI 改版·Raycast】新会话不再插入旧版欢迎/技能/说明气泡：
        // 空态卡片（消息区居中）承担引导；模型与权限信息放在页头状态行与权限菜单，
        // 避免出现"危险操作先确认"与"完全访问免确认"并存的矛盾文案。
    }

    /// <summary>展示「已加载技能」可见提示（技能菜单开关会即时生效）。</summary>
    private void ShowActiveSkillsBubble()
    {
        var skills = AgentSkillRegistry.All
            .Where(s => _session?.ActiveSkillIds.Contains(s.Id) == true).ToList();
        if (skills.Count == 0) return;

        var parts = skills.Select(s => $"{s.DisplayName}（{s.Description}）");
        AddSystemBubble(IsDshEngineActive
            ? string.Format(LocalizationService.L("SkillHub_DshActive", "🔧 已启用技能：{0}\n目标助手固定调用，自定义技能按场景提供参考。"), string.Join("、", parts))
            : string.Format(Ui("SkillsActive", "🔧 已加载技能：{0}\n可在顶部「技能」菜单开关，咨询对应场景时我将按技能要求执行。"), string.Join("、", parts)));
    }

    private void NewChatButton_Click(object sender, RoutedEventArgs e)
    {
        RequestNewConversation();
    }

    /// <summary>重置为新对话：释放当前会话并清空界面（删除当前会话时复用）。</summary>
    private void ResetToNewChat()
    {
        if (_toolFlowInstalling || _toolFlowResumeOpen) return;
        ClearToolFlowTask();
        _session?.Dispose();
        _session = null;
        _selectedPersonaId = AgentPersonaCatalog.Resolve(AppSettings.Get(AgentPersonaCatalog.SettingKey)).Id;
        UpdatePersonaButton();
        _streamingBubble = null;
        _activeChain = null;
        _awaitingConfirmation = false;
        _pendingPlanRequest = null;
        MsgPanel.Children.Clear();
        _toolFlowVisibleMessages.Clear();
        _toolFlowMessageActions.Clear();
        _modelChoiceActions.Clear();
        _toolFlowRoundStart = 0;
        _toolFlowDisplayRoundStart = 0;
        _toolFlowRoundSucceeded = false;
        _displayLog.Clear();   // 【A06】展示记录随会话释放
        _displayEpoch++;       // 【主审修复】代次自增：旧轮次晚到回调不再写入本展示日志
        _streamingOwner = null;
        ReleaseOwnedConversation();   // 【主审修复·2026-09-22】新对话：释放旧会话写入归属（幂等）
        // 【修复】不再因新对话清除"已连接"：验证结果是配置级的（指纹比对），不属于单个会话
        // 【UI 改版】回到新对话：恢复指令式空态
        EmptyStateBlock.Visibility = Visibility.Visible;
        QuickPillPanel.Visibility = Visibility.Visible;
        TitleText.Text = Ui("NewChat", "新对话");
        RefreshConversationList(); // ZXAI：新对话后清选中态
        RefreshToolFlowResumeEntry();   // 【恢复入口】按本机快照刷新「继续工具流」的可见性
        Welcome();
        UpdateInputState();
        ApplyEnginePermissionUi(); // 【A01】会话置空后按引擎预测刷新权限 UI
        ResumeLatestFollowing();
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigateConversations) return;
        if (_session is { } hs) SaveSessionGuarded(hs);   // 【第六轮】统一经租约写守卫
        PersistDshConversation();   // 【A06】打开历史列表前先落盘当前 dsh 会话

        var flyout = AiHistoryMenu.Build(
            AiAssistantService.ListConversations(),
            onOpen: RequestOpenConversation,
            onRename: OnRenameConversation,
            onDelete: OnDeleteConversation);

        if (sender is FrameworkElement owner)
        {
            AiHistoryMenu.FollowOwnerTheme(flyout, owner);
            flyout.ShowAt(owner);
        }
    }

    /// <summary>重命名会话：当前打开的会话同步更新内存标题，并立即持久化到 meta.json。</summary>
    private async void OnRenameConversation(ConversationMeta conv)
    {
        var newTitle = await AiHistoryMenu.PromptRenameAsync(XamlRoot, conv.Title);
        if (newTitle is null) return;

        if (RenameConversationRequested is { } request)
        {
            request(conv, newTitle);
            return;
        }

        if (_session?.Id == conv.Id)
        {
            _session.Rename(newTitle);
            TitleText.Text = newTitle;
        }
        AiAssistantService.RenameConversation(conv.Id, newTitle);
        RefreshConversationList(); // ZXAI：改名后刷新左侧列表
    }

    /// <summary>删除会话：确认后移除全部关联文件；删的是当前会话则回到新对话。</summary>
    private async void OnDeleteConversation(ConversationMeta conv)
    {
        if (!await AiHistoryMenu.ConfirmDeleteAsync(XamlRoot, conv.Title)) return;

        if (DeleteConversationRequested is { } request)
        {
            request(conv);
            return;
        }

        AiAssistantService.DeleteConversation(conv.Id);
        if (_session?.Id == conv.Id)
            ResetToNewChat();
        RefreshConversationList(); // ZXAI：删除后刷新左侧列表
    }

    private void LoadConversation(ConversationMeta meta)
    {
        if (_isProcessing || _toolFlowInstalling || _toolFlowResumeOpen) return;

        // 【主审修复·2026-09-22】写入归属检查：同一会话不可由两个窗口同时编辑/落盘
        // （主窗口 + 独立窗口双实例同 ID 时，任一方的关闭会把对方的新记录旧副本覆盖）。
        // 【第六轮】改由 ConversationLease 预检：已关闭页面不可再打开任何历史。
        if (!_lease.CanTake(meta.Id))
        {
            // 清楚提示占用、不加载（避免两个写入者 / 静默 fork 丢上下文）。
            AddSystemBubble(Ui("ConversationOpenElsewhere", "该会话正由另一个窗口打开。为避免两个窗口互相覆盖记录，已阻止在本窗口加载——请先在另一窗口关闭它，或点「新对话」。"));
            return;
        }
        // 占有转移（成对）：先落盘旧会话（此刻仍是其所有者；所有引擎统一）→ Lease.Replace（释放旧 + 取得新）。
        try { if (_session is { } oldSession) SaveSessionGuarded(oldSession); } catch { }
        try { PersistDshConversation(); } catch { }
        if (!_lease.Replace(meta.Id))
        {
            AddSystemBubble(Ui("ConversationOpenElsewhere", "该会话正由另一个窗口打开。为避免两个窗口互相覆盖记录，已阻止在本窗口加载——请先在另一窗口关闭它，或点「新对话」。"));
            return;
        }

        _session?.Dispose();
        _selectedPersonaId = AgentPersonaCatalog.Resolve(meta.PersonaId).Id;
        UpdatePersonaButton();
        // 【A06 返修】按【存档记录的引擎】恢复，不按全局 Current 静默换引擎
        // （旧存档引擎为 null 时才回落到当前设置）。
        var restoredEngine = string.IsNullOrWhiteSpace(meta.Engine) ? AgentEngine.Current : meta.Engine;
        if (restoredEngine == "dsh")
        {
            // 【A06 返修】显式按【存档引擎】构建 dsh 会话——不再调用随全局 Current 的 CreateSession
            // （dsh 存档在全局 builtin 时会被错误载入 builtin 实例，历史引擎语义丢失）。
            _session = AgentEngine.TryCreateDshSessionForRestore();
            if (_session is TubaWinUi3.Services.Ai.Dsh.DshSession dshSession)
            {
                AttachSessionEvents(dshSession);   // 【A06 返修】恢复路径同样绑定事件（缺此步=历史会话发送后事件全哑）
                dshSession.AdoptIdentity(meta.Id, meta.Title);                    // 【A06 返修】恢复同一应用会话身份（不再产生新副本）
                dshSession.SetResumeContext(meta.DshSessionId, meta.Workspace);   // 【A06 返修】原始 cwd（resume 要求与持久记录匹配）
                dshSession.SetPersona(_selectedPersonaId);
            }
            else
            {
                // dsh 当前不可用：按存档数据回退 builtin，并在回放完成后如实告知（不静默换引擎）
                _session?.Dispose();
                _session = AgentSession.Load(meta);
                HookSession(_session);
                _dshRestoreFallback = true;
            }
        }
        else
        {
            _session = AgentSession.Load(meta);
            HookSession(_session);
        }
        _resumeNoticeShown = false;
        // 【修复】不再因切换/恢复会话清除"已连接"：验证结果是配置级的（指纹比对），不属于单个会话
        RefreshConversationList(); // ZXAI：切换会话后刷新选中态
        ApplyEnginePermissionUi(); // 【A01】会话重建后刷新权限 UI（引擎可能不同）
        _streamingBubble = null;
        _activeChain = null;
        _awaitingConfirmation = false;
        _pendingPlanRequest = null;
        MsgPanel.Children.Clear();
        _toolFlowVisibleMessages.Clear();
        _toolFlowMessageActions.Clear();
        _modelChoiceActions.Clear();
        _toolFlowRoundStart = 0;
        _toolFlowDisplayRoundStart = 0;
        _toolFlowRoundSucceeded = false;
        ClearToolFlowTask();
        _displayLog.Clear();   // 【A06】切换会话：展示记录重建（防串会话）
        _displayEpoch++;       // 【主审修复】代次自增：旧会话在途轮次的晚到回调不再写入本展示日志
        _streamingOwner = null;
        _replayingDisplay = true;   // 【A06 返修】回放期间禁止写 _displayLog（防用户消息重复记录）

        // 展示记录（文本 + 步骤链按原始顺序恢复）；旧会话无记录时回退到协议消息
        var display = AiAssistantService.LoadConversationDisplay(meta.Id);
        var protocolMessages = AiAssistantService.LoadConversation(meta.Id);
        var assistantMessages = new Queue<AiChatMessage>(protocolMessages
            .Where(m => m.Role == "assistant" && (m.ToolCalls is null || m.ToolCalls.Count == 0)));
        if (display.Any(i => i.Type is "text" or "steps"))
        {
            foreach (var item in display)
            {
                if (item.Type == "meta") continue; // token 统计条目，不渲染
                if (item.Type == "steps")
                {
                    AddPersistedStepChain(item);
                }
                else if (item.Role == "user")
                {
                    AddUserBubble(item.Content);
                }
                else if (item.Role == "assistant")
                {
                    var protocol = assistantMessages.Count > 0 ? assistantMessages.Dequeue() : null;
                    var reasoning = !string.IsNullOrWhiteSpace(item.ReasoningContent)
                        ? item.ReasoningContent
                        : protocol?.ReasoningContent ?? "";
                    AddRestoredAssistant(item.Content, reasoning, item.CanRestoreToolFlowAction);
                }
            }
        }
        else
        {
            foreach (var msg in AiAssistantService.LoadConversation(meta.Id))
            {
                if (msg.Role == "system") continue;
                if (msg.Role == "user")
                {
                    if (msg.Content.StartsWith("[ACTION_CONFIRMED]", StringComparison.OrdinalIgnoreCase)) continue;
                    if (msg.Content.StartsWith("[TOOL_RESULT]", StringComparison.OrdinalIgnoreCase)) continue;
                    AddUserBubble(msg.Content);
                }
                else if (msg.Role == "assistant" &&
                         (!string.IsNullOrWhiteSpace(msg.Content) || !string.IsNullOrWhiteSpace(msg.ReasoningContent)))
                {
                    AddRestoredAssistant(msg.Content, msg.ReasoningContent ?? "");
                }
            }
        }

        _replayingDisplay = false;   // 【A06 返修】回放结束——此后正常记录
        // 【A06 返修】dsh 存档但引擎不可用：如实告知（不静默换引擎、不假称上下文延续）
        if (_dshRestoreFallback)
        {
            _dshRestoreFallback = false;
            AddSystemBubble(Ui("DshUnavailableForHistory", "此会话由 DeepSeek Harness 引擎创建；当前该引擎不可用，已按内置引擎打开（历史内容可查看，模型上下文不延续）。"));
        }
        // 【A06】回填展示记录（再次保存/追加时保持完整；meta 条目跳过——token 统计另有通道）
        if (_session is TubaWinUi3.Services.Ai.Dsh.DshSession)
        {
            foreach (var item in display)
                if (item.Type is "text" or "steps") _displayLog.Add(item);
        }

        MaybeOfferGodotPlanFromRestoredHistory(display, protocolMessages);
        RefreshToolFlowResumeEntry();   // 【恢复入口】打开历史会话时同样刷新「继续工具流」

        TitleText.Text = meta.Title;
        UpdateTokenUsage();
        UpdateInputState();
        ResumeLatestFollowing();
    }

    /// <summary>从展示记录恢复步骤链节点（折叠态，可点击展开查看每步详情）。</summary>
    private void AddPersistedStepChain(ConversationDisplayItem item)
    {
        var chain = new StepChainControl
        {
            Margin = new Thickness(38, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var run = new RunVm();
        foreach (var snap in item.Steps)
            run.Steps.Add(new StepRowVm(snap.ToAgentStep()));
        run.SummaryText = string.IsNullOrWhiteSpace(item.SummaryText)
            ? string.Format(Ui("StepsCompleted", "{0} 步完成"), item.Steps.Count)
            : item.SummaryText;
        run.IsExpanded = false;
        chain.RunVm = run;
        chain.ShowCollapsed();
        MsgPanel.Children.Add(chain);
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        // 【A12】纯图片场景 _lastUserText 为空——有附件时也允许重试
        if (_lastUserText is { Length: > 0 } text)
            await SendAsync(_lastRawSkillInput ?? text, preserveDraft: _lastSendPreservedDraft,
                continuesCurrentTask: _lastContinuesCurrentTask, rawSkillInputOverride: _lastRawSkillInput);
        else if (_attachments.Count > 0)
            await SendAsync("", continuesCurrentTask: _lastContinuesCurrentTask,
                rawSkillInputOverride: _lastRawSkillInput);
    }

    private void AddRestoredAssistant(string content, string reasoning, bool? replyCompletedSuccessfully = null)
    {
        var bubble = BeginAssistantBubble(streaming: false);
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            bubble.Reasoning.Visibility = Visibility.Visible;
            bubble.Reasoning.SetContent(reasoning);
        }
        if (!string.IsNullOrWhiteSpace(content))
        {
            bubble.RawContent.Append(content);
            RenderAssistantContent(bubble, content);
        }
        bubble.StreamingRow.Visibility = Visibility.Collapsed;
        AddToolFlowMessageAction(bubble, content, restored: true,
            replyCompletedSuccessfully: replyCompletedSuccessfully);
    }

    // ---------- 气泡构建 ----------

    private void AddUserBubble(string text)
    {
        _toolFlowVisibleMessages.Add(new TubaWinUi3.Services.ToolFlows.ToolFlowConversationMessage
        { Role = "user", Content = text });
        if (!_toolFlowInstalling && ConversationGoalPresentation.StartsExplicitNewTask(text))
            ClearToolFlowTask(); // Keep the saved snapshot available in the resume list.
        RefreshModelChoiceActions();
        RefreshConversationGoalSummary();
        var bubble = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            CornerRadius = new CornerRadius(12, 12, 4, 12),
            Padding = new Thickness(14, 9, 14, 9),
            MaxWidth = 520,
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            }
        };
        MsgPanel.Children.Add(bubble);
        AnimateMessageIn(bubble, fromX: 18);
        // 【UI 改版】会话已开始：隐藏指令式空态（不再常驻占位）
        EmptyStateBlock.Visibility = Visibility.Collapsed;
        QuickPillPanel.Visibility = Visibility.Collapsed;
        // A fixed bubble retains its original bound wrappers while its native tree is
        // alive. A weak page list can otherwise lose the TextBlock after collection.
        var bubbleTheme = ThemeRefreshScope.AttachRenderedContent(bubble);
        bubbleTheme.Bind(bubble, Border.BackgroundProperty, "AssistantAccentBrush");
        if (bubble.Child is TextBlock userText)
            bubbleTheme.Bind(userText, TextBlock.ForegroundProperty, "AssistantAccentForegroundBrush");
        void RefreshBubbleTheme() => bubble.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                // Wait until inherited ActualTheme has also reached the text. An
                // unloaded bubble keeps its bindings, but does not receive a repaint.
                if (bubble.IsLoaded) bubbleTheme.RefreshAll();
            });
        Windows.Foundation.TypedEventHandler<FrameworkElement, object> bubbleThemeChanged = (_, _) => RefreshBubbleTheme();
        bubble.ActualThemeChanged += bubbleThemeChanged;
        bubble.Loaded += (_, _) =>
        {
            // After remount WinUI need not raise every descendant's inherited event.
            // The containing page is a second signal, owned only by this attachment.
            ActualThemeChanged -= bubbleThemeChanged;
            ActualThemeChanged += bubbleThemeChanged;
            RefreshBubbleTheme();
        };
        bubble.Unloaded += (_, _) => ActualThemeChanged -= bubbleThemeChanged;
        if (bubble.IsLoaded)
        {
            ActualThemeChanged += bubbleThemeChanged;
            RefreshBubbleTheme();
        }
        // 【A06】记入展示记录（仅 dsh 会话且非回放；builtin 由其内部 _displayItems 负责）
        // 【主审修复】按轮次身份（_streamingOwner + epoch）判定归属。
        if (!_replayingDisplay && _streamingOwner is not null && _sendEpoch == _displayEpoch)
            _displayLog.Add(new TubaWinUi3.Services.Agent.ConversationDisplayItem
            { Type = "text", Role = "user", Content = text });
    }

    private AssistantBubble BeginAssistantBubble(bool streaming = true)
    {
        // 头像（ZXAI：枕星四芒星 logo）
        var avatar = new Border
        {
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/zxai-logo.png")),
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform
            }
        };

        // 名称行
        var nameRow = new TextBlock
        {
            Text = Ui("AssistantName", "枕星图吧AI助手"),
            FontSize = 12,
            FontFamily = TubaWinUi3.Services.AppFonts.WinUI,
        };

        // 流式文本 + 思考指示（ProgressRing + 状态文字；排队时文字变「正在排队等待响应…」）
        var streamingText = new TextBlock
        {
            FontSize = 14,
            FontFamily = TubaWinUi3.Services.AppFonts.WinUI,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        var statusText = new TextBlock
        {
            Text = streaming ? Ui("Thinking", "正在思考…") : "",
            FontSize = 12,
            FontFamily = TubaWinUi3.Services.AppFonts.WinUI,
        };
        var thinkingRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new ProgressRing { Width = 14, Height = 14, IsActive = true },
                statusText
            }
        };
        var streamingRow = new StackPanel { Spacing = 4, Children = { streamingText, thinkingRow } };

        var reasoning = new ReasoningDisclosureControl
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 2, 0, 4)
        };

        // 最终内容宿主
        var contentHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        var toolFlowActions = new StackPanel { Spacing = 12 };
        var copyButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 13 },
            Padding = new Thickness(6), CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var bubble = new AssistantBubble
        {
            Root = null!,
            StreamingText = streamingText,
            StreamingRow = streamingRow,
            ThinkingRow = thinkingRow,
            StatusText = statusText,
            Reasoning = reasoning,
            ContentHost = contentHost,
            ToolFlowActions = toolFlowActions,
            CopyButton = copyButton,
            Epoch = _displayEpoch
        };

        copyButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(bubble.ReadableContent)) return;
            try
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(bubble.ReadableContent);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            }
            catch { }
        };
        ToolTipService.SetToolTip(copyButton, LocalizationService.L("AiFlow_CopyReply", "复制回复"));
        AutomationProperties.SetName(copyButton, LocalizationService.L("AiFlow_CopyReply", "复制回复"));
        var messageHeader = new Grid { ColumnSpacing = 8 };
        messageHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        messageHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        messageHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(nameRow, 1);
        nameRow.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(copyButton, 2);
        messageHeader.Children.Add(avatar);
        messageHeader.Children.Add(nameRow);
        messageHeader.Children.Add(copyButton);

        var body = new StackPanel
        {
            Spacing = 8,
            Children = { messageHeader, reasoning, streamingRow, contentHost, toolFlowActions }
        };

        var root = new Border
        {
            Child = body,
            Margin = new Thickness(0, 0, 0, 16),
            Padding = new Thickness(16), CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
        };
        bubble.Root = root;
        root.Tag = bubble;
        ApplyAssistantBubbleLayout(bubble);

        MsgPanel.Children.Add(root);
        AnimateMessageIn(root, fromX: -18);
        // Keep the original bound wrappers with this rendering, including while the
        // native visual tree survives collection, reparenting or a theme change.
        var messageTheme = ThemeRefreshScope.AttachRenderedContent(root);
        messageTheme.Bind(nameRow, TextBlock.ForegroundProperty, "AssistantSecondaryTextBrush");
        messageTheme.Bind(statusText, TextBlock.ForegroundProperty, "AssistantSecondaryTextBrush");
        messageTheme.Bind(streamingText, TextBlock.ForegroundProperty, "AssistantPrimaryTextBrush");
        messageTheme.Bind(contentHost, ContentControl.ForegroundProperty, "AssistantPrimaryTextBrush");
        messageTheme.Bind(root, Border.BackgroundProperty, "AssistantSurfaceBrush");
        messageTheme.Bind(root, Border.BorderBrushProperty, "AssistantSeparatorBrush");
        return bubble;
    }

    private static void RenderAssistantContent(AssistantBubble bubble, string original)
    {
        var excerpt = AssistantReplyPresentation.Create(original);
        bubble.ReadableContent = excerpt.Body;
        var panel = new StackPanel { Spacing = 12 };
        var replyTheme = ThemeRefreshScope.AttachRenderedContent(panel);
        if (excerpt.HasDetails)
        {
            var preview = new TextBlock
            {
                Text = excerpt.Preview, TextWrapping = TextWrapping.Wrap, FontSize = 14,
                IsTextSelectionEnabled = true, FontFamily = AppFonts.WinUI,
            };
            replyTheme.Bind(preview, TextBlock.ForegroundProperty, "AssistantPrimaryTextBrush");
            panel.Children.Add(preview);
            var details = new Expander
            {
                Header = LocalizationService.L("AiFlow_ExpandReply", "展开说明"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            details.Expanding += (_, _) =>
            {
                if (details.Content is null)
                    details.Content = AiMarkdownRenderer.Render(excerpt.Body, allowActions: false);
            };
            panel.Children.Add(details);
            bubble.ReplyDetails = details;
        }
        else if (!string.IsNullOrWhiteSpace(excerpt.Body))
            panel.Children.Add(AiMarkdownRenderer.Render(excerpt.Body, allowActions: false));
        bubble.ContentHost.Content = panel;
    }

    private void AddSystemBubble(string text)
    {
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 16, 10),
            MaxWidth = 700,
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            }
        };
        MsgPanel.Children.Add(bubble);
        var messageTheme = ThemeRefreshScope.AttachRenderedContent(bubble);
        messageTheme.Bind(bubble, Border.BackgroundProperty, "AssistantSoftFillBrush");
        if (bubble.Child is TextBlock sysText)
            messageTheme.Bind(sysText, TextBlock.ForegroundProperty, "AssistantSecondaryTextBrush");
        AnimateMessageIn(bubble, fromY: 10);
        // 【修复】系统提示也算会话已开始：同步隐藏指令式空态，避免与空态标题文字重叠
        EmptyStateBlock.Visibility = Visibility.Collapsed;
        QuickPillPanel.Visibility = Visibility.Collapsed;
    }

    private void AddErrorBubble(string text)
    {
        var retryBtn = new Button
        {
            Content = Ui("Retry", "重试"),
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 4),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        retryBtn.Click += RetryButton_Click;

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 16, 10),
            MaxWidth = 700,
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new FontIcon
                            {
                                Glyph = "\uE783",
                                FontSize = 13,
                            },
                            new TextBlock
                            {
                                Text = Ui("ErrorOccurred", "出错了"),
                                FontSize = 12,
                                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                            }
                        }
                    },
                    // 详细错误（异常链）可滚动查看，不占大块版面
                    new ScrollViewer
                    {
                        MaxHeight = 240,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = new TextBlock
                        {
                            Text = text,
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                        }
                    },
                    retryBtn
                }
            }
        };
        MsgPanel.Children.Add(bubble);
        AnimateMessageIn(bubble, fromY: 10);
        // 【UI 改版】主题切换重刷登记（结构：StackPanel[标题行(FontIcon+TextBlock), 详情 ScrollViewer, 重试按钮]——
        // 若调整错误卡结构，此处索引同步更新）
        var messageTheme = ThemeRefreshScope.AttachRenderedContent(bubble);
        messageTheme.Bind(bubble, Border.BackgroundProperty, "AssistantErrorFillBrush");
        messageTheme.Bind(bubble, Border.BorderBrushProperty, "AssistantErrorTextBrush");
        if (bubble.Child is StackPanel errStack && errStack.Children.Count >= 2)
        {
            if (errStack.Children[0] is StackPanel errTitle && errTitle.Children.Count >= 2)
            {
                messageTheme.Bind(errTitle.Children[0], FontIcon.ForegroundProperty, "AssistantErrorTextBrush");
                messageTheme.Bind(errTitle.Children[1], TextBlock.ForegroundProperty, "AssistantErrorTextBrush");
            }
            if (errStack.Children[1] is ScrollViewer errScroll && errScroll.Content is TextBlock errDetail)
                messageTheme.Bind(errDetail, TextBlock.ForegroundProperty, "AssistantPrimaryTextBrush");
        }
    }

    private static void AnimateMessageIn(UIElement element, double fromX = 0, double fromY = 0)
    {
        // Keep the durable value at the final position. The explicit animation
        // From values supply the entrance, including when the element is removed
        // before completion or rendered without animation clocks.
        element.Opacity = 1;
        element.RenderTransform = new TranslateTransform();

        var sb = new Storyboard();
        var opacity = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, element);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        sb.Children.Add(opacity);

        if (Math.Abs(fromX) > 0.1)
        {
            var translateX = new DoubleAnimation
            {
                From = fromX,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(translateX, element);
            Storyboard.SetTargetProperty(translateX, "(UIElement.RenderTransform).(TranslateTransform.X)");
            sb.Children.Add(translateX);
        }

        if (Math.Abs(fromY) > 0.1)
        {
            var translateY = new DoubleAnimation
            {
                From = fromY,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(translateY, element);
            Storyboard.SetTargetProperty(translateY, "(UIElement.RenderTransform).(TranslateTransform.Y)");
            sb.Children.Add(translateY);
        }

        sb.Completed += (_, _) => sb.Stop();
        sb.Begin();
    }

    // ---------- UI 辅助 ----------

    // Dynamic messages follow this page, including children not yet loaded into the visual tree.
    private void ThemeBind(DependencyObject target, DependencyProperty prop, string key)
    {
        _themedBrushes.Add((new WeakReference<DependencyObject>(target), prop, key));
        ApplyPageThemeBrush(target, prop, key);
    }

    private void ApplyPageThemeBrush(DependencyObject target, DependencyProperty prop, string key)
    {
        if (TryResolveThemeBrush(this, key, out var b) && b is not null)
            target.SetValue(prop, b);
    }

    /// <summary>
    /// 【主题复核·最后一公里】统一委托 ThemeResourceResolver —— 按"主题+资源键"逐层查找
    /// （元素自身 → 祖先 → 应用级；各层先主题字典后普通项，含 merged 子树），不再保留
    /// 第二套解析规则。解析失败返回 false，调用方【保留控件原有 ThemeResource 值】；
    /// 不得回退 Application.Current.Resources 的"当前解析值"——系统深色+页面浅色组合下
    /// 那是错误主题的静态快照，会复现浅色页白字不可见。
    /// </summary>
    private bool TryResolveThemeBrush(DependencyObject target, string key, out Brush? brush)
    {
        brush = ThemeResourceResolver.ResolveBrush(target, key);
        return brush is not null;
    }

    /// <summary>【主题复核·最后一公里】文字语义色：解析失败保留原值（不覆盖为错误主题快照）。</summary>
    private void SetTextColor(Microsoft.UI.Xaml.Controls.TextBlock target, string key)
    {
        var b = ResolveThemeBrush(target, key);
        if (b is not null) target.Foreground = b;
    }

    /// <summary>即时按目标元素实际主题解析（code 取色专用）。</summary>
    private Brush? ResolveThemeBrush(DependencyObject target, string key)
        => TryResolveThemeBrush(target, key, out var b) ? b : null;

    private void SubscribeDynamicTheme()
    {
        if (_themeSubscribed) return;
        ThemeService.ThemeChanged += OnAppThemeChanged;
        _themeSubscribed = true;
    }

    private void UnsubscribeDynamicTheme()
    {
        if (!_themeSubscribed) return;
        ThemeService.ThemeChanged -= OnAppThemeChanged;
        _themeSubscribed = false;
    }

    private void OnAppThemeChanged(ElementTheme theme)
    {
        _ = theme;   // 实际主题已通过 ThemeService 应用到根元素；这里只做动态控件重刷
        RefreshDynamicTheme();
    }

    /// <summary>【主题复核·最后一公里】元素级实际主题变化（如从设置页返回缓存的会话页）。</summary>
    private void OnPageActualThemeChanged(FrameworkElement sender, object args) => RefreshDynamicTheme();

    /// <summary>
    /// 【主题复核·最后一公里】完整刷新：对登记中的存活元素按当前页面主题重新赋色，再重算
    /// 页面级动态区域（服务状态/运行状态/附件条/技能面板）。_isRefreshingTheme 防重入；
    /// 本方法不重建建议列表、不重复登记 ThemeBind、不调用 ThemeService.SetTheme、不写设置；
    /// 只 SetValue 画刷——流式内容、滚动位置与展开状态均保留。
    /// </summary>
    private void RefreshDynamicTheme()
    {
        if (_isRefreshingTheme) return;
        _isRefreshingTheme = true;
        try
        {
            // ① 已登记动态元素逐个重刷（消息气泡、错误卡、空态建议等）；
            //    弱引用失效（已回收的旧气泡树）的条目顺带清理，避免登记表只增不减。
            _themedBrushes.RemoveAll(entry => !entry.Target.TryGetTarget(out _));
            foreach (var (weak, prop, key) in _themedBrushes)
            {
                if (!weak.TryGetTarget(out var target)) continue;
                try
                {
                    ApplyPageThemeBrush(target, prop, key);
                }
                catch { }
            }
            // ② 页面级动态区域：状态色/徽标按语义键重算（各自只写自己的区域）。
            try { UpdateServiceStatus(); } catch { }
            try { UpdateRunState(); } catch { }
            try { UpdateAttachmentBar(); } catch { }
            // A theme change must not create a session, rebuild an open skill list,
            // or alter its checkbox state. Native expressions update the existing items.
            try { _refreshSkillsFlyoutTheme?.Invoke(); } catch { }
        }
        finally
        {
            _isRefreshingTheme = false;
        }
    }

    private void BuildQuickPills()
    {
        QuickPillPanel.Children.Clear();
        var index = 0;
        foreach (var (title, desc, glyph) in QuickQuestions)
        {
            // Raycast 风格：左侧图标 + 标题/说明两行 + 右侧箭头（紧凑纵向列表行）
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var description = new TextBlock { Text = desc, FontSize = 12 };
            texts.Children.Add(description);
            var arrow = new FontIcon { Glyph = "\uE72A", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon, 0); Grid.SetColumn(texts, 1); Grid.SetColumn(arrow, 2);
            row.Children.Add(icon); row.Children.Add(texts); row.Children.Add(arrow);

            var btn = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(13, 9, 13, 9),
                Tag = title
            };
            // A local native state palette covers hover/pressed as well as Normal.
            // The title and glyphs inherit that native ContentPresenter foreground.
            btn.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("ms-appx:///Styles/QuickPillThemeResources.xaml") });
            // Native ThemeResource also follows HighContrast; keep lookup local to this card.
            description.Style = (Style)btn.Resources["QuickPillDescriptionStyle"];
            btn.Click += (_, _) => _ = SendAsync(title);
            QuickPillPanel.Children.Add(btn);

            // 错落入场动画（间隔 50ms）
            btn.RenderTransform = new TranslateTransform();
            var sb = new Storyboard { BeginTime = TimeSpan.FromMilliseconds(index++ * 50) };
            var opacity = new DoubleAnimation
            {
                From = 0, To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(opacity, btn);
            Storyboard.SetTargetProperty(opacity, "Opacity");
            var translate = new DoubleAnimation
            {
                From = 10, To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(translate, btn);
            Storyboard.SetTargetProperty(translate, "(UIElement.RenderTransform).(TranslateTransform.Y)");
            sb.Children.Add(opacity);
            sb.Children.Add(translate);
            sb.Begin();
        }
    }

    // ---------- 提供商 / 模型切换 ----------

    /// <summary>同步顶栏提供商/模型下拉框与当前选中状态（选中状态全局持久化）。</summary>
    private void RefreshProviderCombos()
    {
        _syncingCombos = true;
        try
        {
            var providers = AiProviderStore.GetProviders();
            var selectedId = AiProviderStore.SelectedProviderId;
            // 传副本（见 SettingsPage.RefreshAiProviderList：活列表原地修改会导致
            // ItemsSourceView 快照过期，同步设置 SelectedItem 抛 E_INVALIDARG）
            ProviderCombo.ItemsSource = providers.ToList();
            ProviderCombo.SelectedItem = providers.FirstOrDefault(p => p.Id == selectedId) ?? providers.FirstOrDefault();

            var provider = AiProviderStore.SelectedProvider;
            var modelId = AiProviderStore.SelectedModelId;
                ModelCombo.ItemsSource = provider.Models.ToList();
                ModelCombo.SelectedItem = provider.Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                                          ?? provider.Models.FirstOrDefault();
        }
        finally
        {
            _syncingCombos = false;
        }
        UpdateServiceStatus();
    }


    private void UpdatePersonaButton()
    {
        var persona = AgentPersonaCatalog.Resolve(_selectedPersonaId);
        PersonaButtonLabel.Text = Ui("Persona_" + persona.Id + "_Name", persona.Name);
        ToolTipService.SetToolTip(PersonaFlyoutButton,
            string.Format(Ui("PersonaTip", "协作人格：{0} · {1}（下一条消息生效）"),
                Ui("Persona_" + persona.Id + "_Name", persona.Name),
                Ui("Persona_" + persona.Id + "_Description", persona.Description)));
    }

    private void PersonaFlyout_Opening(object sender, object e)
    {
        PersonaOptionsPanel.Children.Clear();
        foreach (var persona in AgentPersonaCatalog.All)
        {
            var label = new StackPanel { Spacing = 1 };
            label.Children.Add(new TextBlock { Text = Ui("Persona_" + persona.Id + "_Name", persona.Name), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            label.Children.Add(new TextBlock
            {
                Text = Ui("Persona_" + persona.Id + "_Description", persona.Description),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ResolveThemeBrush(PersonaButtonLabel, "AssistantSecondaryTextBrush")
            });
            var option = new RadioButton
            {
                Tag = persona.Id,
                Content = label,
                IsChecked = persona.Id == _selectedPersonaId,
                IsEnabled = !_isProcessing && !_awaitingConfirmation,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            option.Checked += PersonaOption_Checked;
            PersonaOptionsPanel.Children.Add(option);
        }
    }

    private void PersonaOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || _awaitingConfirmation || sender is not RadioButton { Tag: string id }) return;
        var persona = AgentPersonaCatalog.Resolve(id);
        if (persona.Id == _selectedPersonaId) return;

        _selectedPersonaId = persona.Id;
        AppSettings.Set(AgentPersonaCatalog.SettingKey, persona.Id); // 后续新对话默认沿用用户上次选择
        _session?.SetPersona(persona.Id);
        UpdatePersonaButton();
        if (_session is { } session)
        {
            if (session is TubaWinUi3.Services.Ai.Dsh.DshSession) PersistDshConversation();
            else SaveSessionGuarded(session);
        }
        PersonaFlyout.Hide();
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCombos || _isProcessing) return;
        if (ProviderCombo.SelectedItem is not AiProvider provider) return;

        var prevId = AiProviderStore.SelectedProviderId;
        if (provider.Id == prevId) return;

        AiProviderStore.SetSelected(provider.Id);
        RefreshProviderCombos();
        NotifyModelSwitch(provider.Name, AiProviderStore.SelectedModelId);
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCombos || _isProcessing) return;
        if (ProviderCombo.SelectedItem is not AiProvider provider) return;
        if (ModelCombo.SelectedItem is not AiModelOption model) return;

        var prev = AiProviderStore.SelectedModelId;
        if (model.Id.Equals(prev, StringComparison.OrdinalIgnoreCase)) return;

        AiProviderStore.RegisterModel(provider.Id, model.Id); // 动态模型（如本地列表）登记后选
        AiProviderStore.SetGlobalModel(provider.Id, model.Id);
        UpdateServiceStatus();
        NotifyModelSwitch(provider.Name, model.Id);
    }

    /// <summary>跳转设置页 AI 服务配置（提供商 / API Key / 模型）。</summary>
    private void AiSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateToSettings("AiApiEndpoint");
    }

    // ---------- 接入引导（2026-09-25）----------

    /// <summary>接入卡「去配置」：跳设置页 AI 服务区（深链高亮，复用既有配置入口）。</summary>
    private void AiOnboardingConfigure_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateToSettings("AiApiKey");
    }

    /// <summary>接入卡「稍后再说」：本次收起并记住；不阻塞工具箱，仍可随时从页头「AI 服务」进入配置。</summary>
    private void AiOnboardingLater_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TubaWinUi3.Services.AppSettings.Set("AiOnboardingDismissed", true);
            TubaWinUi3.Services.AppSettings.Save();
        }
        catch { }
        UpdateOnboardingCard();
    }

    /// <summary>【接入引导】当前所选服务是否缺可用配置。
    /// 【R3 2026-09-25】删除「空白自定义=内置默认服务」例外：空白自定义提供商同样是未配置——
    /// 显示引导、发送拦下；请求配置绝不注入旧默认地址/内置 Key（见 AiService.GetConfig）。</summary>
    private bool NeedsOnboarding()
    {
        try
        {
            return !AiProviderStore.IsProviderReady(AiProviderStore.SelectedProvider);
        }
        catch { return false; }
    }

    /// <summary>【接入引导】空态接入卡的两类状态：未配置（四步引导）/ 已保存未验证（去测试连接）。</summary>
    private enum AiOnboardingCardMode { None, NeedsConfig, NeedsTest }

    private AiOnboardingCardMode ResolveOnboardingCardMode()
    {
        try
        {
            if (TubaWinUi3.Services.AppSettings.GetBool("AiOnboardingDismissed")) return AiOnboardingCardMode.None;
        }
        catch { }

        if (NeedsOnboarding()) return AiOnboardingCardMode.NeedsConfig;

        // 【R2】已保存未验证：配置齐全但尚无验证指纹——给出「去测试连接」入口；
        // 发送可作为首次实际验证（成功后写指纹才显示已连接），不提前宣称连通。
        try
        {
            // 【R3】到此处必为已配置（NeedsOnboarding=false）——原「默认服务」例外已删除，无需再排除。
            if (!AgentEngine.IsSelectedConfigVerified())
                return AiOnboardingCardMode.NeedsTest;
        }
        catch { }
        return AiOnboardingCardMode.None;
    }

    /// <summary>【接入引导】空态接入卡：按状态切换文案与显隐（语言切换经 ApplyLocalization→UpdateServiceStatus 刷新）。</summary>
    private void UpdateOnboardingCard()
    {
        if (AiOnboardingCard is null) return;
        var mode = ResolveOnboardingCardMode();
        switch (mode)
        {
            case AiOnboardingCardMode.NeedsConfig:
                AiOnboardingTitleText.Text = Ui("OnboardingTitle", "先接通 AI 服务，再开始对话");
                AiOnboardingStepsText.Text = Ui("OnboardingSteps", "选择 AI 服务 → 获取/填写 Key（或配置本地服务）→ 测试连接 → 开始描述目标");
                AiOnboardingConfigureText.Text = Ui("OnboardingConfigure", "配置 AI 与 Agent");
                break;
            case AiOnboardingCardMode.NeedsTest:
                AiOnboardingTitleText.Text = Ui("OnboardingVerifyTitle", "AI 服务已保存，尚未验证");
                AiOnboardingStepsText.Text = Ui("OnboardingVerifySteps", "点「测试连接」确认可用；也可以直接发送消息——首次发送成功即视为验证通过。");
                AiOnboardingConfigureText.Text = Ui("OnboardingGoTest", "去测试连接");
                break;
        }
        AiOnboardingCard.Visibility = mode == AiOnboardingCardMode.None ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>会话已有内容时，切换后提示新模型从下一条消息生效。</summary>
    private void NotifyModelSwitch(string providerName, string modelId)
    {
        if (_session is null || _session.TotalTokens <= 0) return;
        AddSystemBubble(string.Format(Ui("ModelSwitched", "已切换到 {0} · {1}，后续消息将使用该模型。"), providerName, modelId));
    }

    private void UpdateServiceStatus()
    {
        var provider = AiProviderStore.SelectedProvider;
        var (_, model, _) = AiService.GetConfig();
        // 【UI 改版·返修】已选择 ≠ 已配置：缺地址/缺 Key 分开展示（R2 统一 IsProviderReady 口径）
        // 【R3】原「空白自定义=内置默认模型」分支已删除：空白自定义走下方未配置分支（统一语义）。
        var (unconfigured, connected) = GetServiceState();
        if (unconfigured)
        {
            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
                ModelStatusText.Text = string.Format(Ui("ApiEndpointMissing", "未配置服务地址（{0}）"), provider.Name);
            else
                ModelStatusText.Text = AiProviderStore.NeedsKeyReentry(provider.Id)
                    ? Ui("ApiKeyDecryptFailed", "API Key 无法解密，请到设置中重新填写")
                    : string.Format(Ui("ApiKeyMissing", "未配置 API Key（{0}）"), provider.Name);
            SetTextColor(ModelStatusText, "AssistantCautionTextBrush");
        }
        else
        {
            ModelStatusText.Text = connected
                ? string.Format(Ui("ConnectedModel", "已连接 {0} · {1}"), provider.Name, model)
                : string.Format(Ui("SelectedModelUnverified", "已选择 {0} · {1}（尚未验证连通）"), provider.Name, model);
            SetTextColor(ModelStatusText, connected ? "AssistantSuccessTextBrush" : "AssistantSecondaryTextBrush");
        }
        // 【UI 改版】Raycast：页头"提供商 · 模型"合并胶囊的显示文本
        ModelButtonLabel.Text = $"{provider.Name} · {FormatModelShort(model)}";
        UpdateOnboardingCard();
        UpdateHeaderBadge();
    }

    /// <summary>【主题复核·最后一公里】服务配置状态：未配置（缺地址 / 缺 Key）/ 已验证连接 / 已选择未验证。
    /// 【R2】未配置 = 提供商配置不完整（地址或 Key 缺一，见 AiProviderStore.IsProviderReady）；
    /// 「已验证」读 AppSettings 实时指纹（与设置页同一来源），返回复用页面不再依赖构造期快照。
    /// 【R3】原「默认模型」例外（空白自定义走内置默认服务）已删除：空白自定义=未配置。</summary>
    private (bool Unconfigured, bool Connected) GetServiceState()
    {
        var unconfigured = false;
        try { unconfigured = !AiProviderStore.IsProviderReady(AiProviderStore.SelectedProvider); } catch { }
        var connected = false;
        if (!unconfigured)
        {
            try { connected = AgentEngine.IsSelectedConfigVerified(); } catch { }
        }
        return (unconfigured, connected);
    }

    /// <summary>模型名紧凑显示（保留前缀关键信息，过长截断）。</summary>
    private static string FormatModelShort(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return Ui("NoModelSelected", "未选择");
        var m = model.Trim();
        return m.Length <= 18 ? m : m[..18] + "…";
    }

    private void UpdateInputState()
    {
        var busy = _isProcessing || _awaitingConfirmation || _toolFlowActionOpen || _toolFlowInstalling || _toolFlowResumeOpen || _godotPlanDialogOpen;
        SidebarNewChatButton.IsEnabled = CanNavigateConversations;
        LogoHomeButton.IsEnabled = CanNavigateConversations;
        RefreshToolFlowTaskCard();
        RefreshToolFlowMessageActions();
        InputBox.IsEnabled = !busy;
        SendButton.IsEnabled = !busy;
        ProviderCombo.IsEnabled = !busy;
        ModelCombo.IsEnabled = !busy;
        PersonaFlyoutButton.IsEnabled = !busy;
        AttachButton.IsEnabled = !busy;   // 【A12 返修】执行期间禁止变更附件（配合迟到的选择器结果丢弃）
        SendButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = _isProcessing || _awaitingConfirmation ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = _isProcessing || _awaitingConfirmation;
        UpdateRunState();
        RefreshWorkbenchAttention();
    }

    /// <summary>发送按钮旁的气泡：实时展示本会话累计 token 消耗（多轮累加），
    /// 提供商/网关返回缓存统计时附带缓存命中量。</summary>
    private void UpdateTokenUsage()
    {
        if (_workbenchSelectionId is not null && AssistantPane.ActualWidth is > 0 and < 640)
        {
            TokenUsageBubble.Visibility = Visibility.Collapsed;
            return;
        }
        var session = _session;
        var tokens = session?.TotalTokens ?? 0;
        if (tokens <= 0)
        {
            TokenUsageBubble.Visibility = Visibility.Collapsed;
            return;
        }
        TokenUsageBubble.Visibility = Visibility.Visible;
        var text = $"{FormatTokens(tokens)} tokens";
        if (session is { TotalCacheHitTokens: > 0 })
        {
            var total = session.TotalCacheHitTokens + session.TotalCacheMissTokens;
            var pct = total > 0 ? (int)Math.Round(session.TotalCacheHitTokens * 100.0 / total) : 100;
            text += string.Format(Ui("CacheHits", " · 缓存命中 {0} ({1}%)"), FormatTokens(session.TotalCacheHitTokens), pct);
        }
        TokenUsageText.Text = text;
    }

    private void UpdateRunState()
    {
        if (_awaitingConfirmation)
        {
            RunStateText.Text = Ui("AwaitingConfirmation", "等待确认");
            SetTextColor(RunStateText, "AssistantCautionTextBrush");
        }
        else if (_isProcessing)
        {
            RunStateText.Text = Ui("Running", "执行中");
            SetTextColor(RunStateText, "AssistantAccentBrush");
        }
        else
        {
            RunStateText.Text = IsDshEngineActive
                ? Ui("EngineFullAccessStatus", "全权限（引擎固定）")
                : AgentToolContext.IsFullAccess ? Ui("FullAccess", "完全访问") : Ui("ControlledExecution", "受控执行");
            // 【主题复核·最后一公里】空闲描述用页面语义中性色（原应用级快照在浅色页近白不可读）。
            SetTextColor(RunStateText, "AssistantSecondaryTextBrush");
        }
        UpdateHeaderBadge();
    }

    /// <summary>
    /// 【主题复核·最后一公里】页头状态徽标【唯一写入点】。原 UpdateServiceStatus / UpdateRunState
    /// 双写会互相覆写——"未配置"警告刚写入，又被空闲分支刷回绿色成功勾，出现自相矛盾的徽标。
    /// 优先级：等待确认 → 执行中 → 未配置（警告，不得绿勾）→ 已验证连接 → 已选择未验证（中性）。
    /// </summary>
    private void UpdateHeaderBadge()
    {
        // 【优化·2026-09-22】右下角状态点（纯色小圆圈，不叠图标）：
        //   通 = 绿色；不通 = 红色（未验证/未配置 Key 均视为不通）；
        //   生成中 = 品牌蓝；待确认 = 琥珀。外圈描边在 XAML 中定义。
        string bgKey;
        if (_awaitingConfirmation)
            bgKey = "AssistantCautionTextBrush";
        else if (_isProcessing)
            bgKey = "AssistantAccentBrush";
        else
        {
            var (_, connected) = GetServiceState();
            bgKey = connected ? "AssistantStatusOkBrush" : "AssistantStatusDownBrush";
        }
        var bg = ResolveThemeBrush(StateBadge, bgKey);
        if (bg is not null) StateBadge.Background = bg;
        StateBadge.IconSource = null;   // 纯色圆点（无图标）
    }

    private static string FormatTokens(int tokens)
        => tokens >= 1000 ? $"{tokens / 1000.0:F1}k" : tokens.ToString();

}
