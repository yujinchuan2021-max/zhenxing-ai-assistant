using System.Collections;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using TubaWinUi3.Services.Ai;
using Windows.System;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// Real page, native scroll extent, real send/choice callbacks and synthetic
/// session events. The host is isolated by ZXAI_DATA_ROOT; the fake session has
/// no transport, process, persistence or installer implementation. These cases
/// never call SmartScroll to make an assertion pass. Wheel/key intent enters
/// the same production observer as the routed event; native ChangeView then
/// supplies the view movement (no OS input or fabricated WinRT event args).
/// </summary>
internal static class ConversationFollowCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("ConversationFollow_SendAndStreamingIntent", SendAndStreamingIntent),
        ("ConversationFollow_ChoiceSendResumes", ChoiceSendResumes),
        ("ConversationFollow_ResizeAndCachedRemount", ResizeAndCachedRemount),
    ];

    private static Task SendAndStreamingIntent() => WithPage(async (page, session, _, root) =>
    {
        await AssertInputOwnership(page, root);
        await SeedHistory(page, root);
        var scroll = Named<ScrollViewer>(page, "MsgScroll");
        await ScrollUp(page, root, keyboard: false);
        var oldOffset = scroll.VerticalOffset;
        await Send(page, "   ");
        await Settle(root);
        Assert.Equal(0, session.SendCalls);
        AssertPaused(page, oldOffset, "An empty send must not disturb history reading.");

        var first = Send(page, "请继续解释这个示例。 ");
        await WaitUntil(() => session.SendCalls == 1, "The real send did not reach the synthetic session.");
        await AssertBottom(page, root, "accepted send from old history");
        Assert.Equal("请继续解释这个示例。", session.LastText);

        // A programmatic view change produces actual ViewChanged events, but is
        // not a wheel/key/scrollbar/touch instruction to abandon following.
        scroll.ChangeView(null, scroll.ScrollableHeight / 3, null, disableAnimation: true);
        await Settle(root);
        Assert.True(Get<bool>(page, "_followLatest"), "Programmatic ViewChanged must not impersonate a user scroll.");
        for (var batch = 0; batch < 3; batch++)
            await GrowAtBottom(page, session, root, "stream batch " + batch);

        await ScrollUp(page, root, keyboard: false);
        var paused = scroll.VerticalOffset;
        await GrowVisibleReply(page, session, root, "paused incoming reply", following: false);
        AssertPaused(page, paused, "Incoming reply text must leave the reader's position alone.");
        Assert.Equal(Visibility.Visible, Named<Button>(page, "ScrollToBottomButton").Visibility);
        await Send(page, "这次发送应被忙碌状态拒绝。");
        Assert.Equal(1, session.SendCalls);
        AssertPaused(page, paused, "A rejected busy send must not re-enable following.");
        await Finish(page, session, root, first);
        AssertPaused(page, paused, "Reply finalization must not jump away from history.");

        // Same UI turn: the button queues a low-priority scroll, then a real
        // upward intent must invalidate it before that callback gets its turn.
        ClickLatest(page);
        Invoke(page, "ObserveMessageWheel", 120);
        scroll.ChangeView(null, paused, null, disableAnimation: true);
        await Settle(root);
        AssertPaused(page, paused, "An upward input must cancel an already queued return-to-latest.");

        var second = Send(page, "再发一条，回到最新。 ");
        await WaitUntil(() => session.SendCalls == 2, "The second accepted send did not arrive.");
        await AssertBottom(page, root, "sending again re-arms following");
        await GrowAtBottom(page, session, root, "second reply");
        await ScrollUp(page, root, keyboard: true);
        ClickLatest(page);
        await AssertBottom(page, root, "back-to-latest action");
        await GrowAtBottom(page, session, root, "reply after back-to-latest");
        await Finish(page, session, root, second);
        await AssertBottom(page, root, "completed reply");
        Console.WriteLine($"CONVERSATION_FOLLOW|send-stream-intent|sends={session.SendCalls}|extent={scroll.ScrollableHeight:0.0}|offset={scroll.VerticalOffset:0.0}");
    });

    private static Task ChoiceSendResumes() => WithPage(async (page, session, _, root) =>
    {
        await SeedHistory(page, root);
        var first = Send(page, "请让我选择一个示例。 ");
        await WaitUntil(() => session.SendCalls == 1, "The question round did not start.");
        const string choiceReply = "请选择一个展示方式。\n```choice-question\n" +
            "{\"schema\":1,\"question\":\"接下来查看什么？\",\"options\":[" +
            "{\"id\":\"example\",\"label\":\"查看例子\",\"answer\":\"请继续展示例子。\"}," +
            "{\"id\":\"details\",\"label\":\"解释细节\",\"answer\":\"请继续解释细节。\"}]}\n```";
        session.Emit(choiceReply);
        await WaitUntil(() => Get<object?>(page, "_streamingBubble") is not null, "The reply did not have a live bubble.");
        await Settle(root); // Drain the real session's queued TextChunk callback before completion.
        await Finish(page, session, root, first);
        var actions = Get<IEnumerable>(page, "_modelChoiceActions").Cast<object>().ToArray();
        var action = Assert.Single(actions);
        var choice = Assert.IsType<ChatChoiceControl>(action.GetType().GetProperty("QuestionControl")!.GetValue(action));
        Assert.True(choice.IsLoaded);
        Assert.Equal(2, choice.ChoiceButtons.Count);
        Assert.All(choice.ChoiceButtons, button => Assert.True(button.IsEnabled));

        await ScrollUp(page, root, keyboard: false);
        Named<TextBox>(page, "InputBox").Text = "保留这段尚未发出的草稿。";
        var button = choice.ChoiceButtons[0];
        Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke));
        // Use the installed control handler, including its enable/selection guard,
        // rather than directly calling the page's SendChoiceAnswerAsync method.
        typeof(ChatChoiceControl).GetMethod("OnChoiceClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(choice, [button, new RoutedEventArgs()]);
        await WaitUntil(() => session.SendCalls == 2, "The real choice callback did not send its answer.");
        Assert.Equal("请继续展示例子。", session.LastText);
        Assert.Equal("example", choice.SelectedId);
        Assert.Equal("保留这段尚未发出的草稿。", Named<TextBox>(page, "InputBox").Text);
        Assert.All(choice.ChoiceButtons, item => Assert.False(item.IsEnabled));
        await AssertBottom(page, root, "choice answer accepted from history");
        await GrowAtBottom(page, session, root, "choice response");
        session.Complete();
        await WaitUntil(() => !Get<bool>(page, "_isProcessing"), "The choice response did not finish.");
        await AssertBottom(page, root, "choice response completion");
        Assert.Equal("保留这段尚未发出的草稿。", Named<TextBox>(page, "InputBox").Text);
        Console.WriteLine("CONVERSATION_FOLLOW|choice-send|actual-callback|draft-preserved|native-bottom");
    });

    private static Task ResizeAndCachedRemount() => WithPage(async (page, session, window, root) =>
    {
        var scroll = Named<ScrollViewer>(page, "MsgScroll");
        var messages = Named<StackPanel>(page, "MsgPanel");
        // A short reply can cross a small real viewport without reaching the
        // product's intentional long-reply folding threshold.
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 860, 560));
        await Settle(root);
        var first = Send(page, "请解释这个简短示例。 ");
        await WaitUntil(() => session.SendCalls == 1, "The short first round did not start.");
        session.Emit("这是开头。\n\n");
        await Settle(root);
        Assert.InRange(scroll.ScrollableHeight, 0, 1);
        await GrowAtBottom(page, session, root, "first overflow from a non-scrollable reply");

        foreach (var width in new[] { 860, 1603 })
        {
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, width, 920));
            await AssertBottom(page, root, "following across width " + width);
            Assert.Same(scroll, Named<ScrollViewer>(page, "MsgScroll"));
            Assert.Same(messages, Named<StackPanel>(page, "MsgPanel"));
        }
        for (var segment = 0; segment < 4; segment++)
            await GrowAtBottom(page, session, root, "additional tool-separated reply " + segment);

        root.Children.Remove(page);
        await WaitUntil(() => !page.IsLoaded, "The cached page did not unload.");
        session.EmitCompletedStep();
        session.Emit(VisibleLines(0, 10));
        await Settle(root);
        root.Children.Add(page);
        await WaitUntil(() => page.IsLoaded, "The same cached page did not remount.");
        await AssertBottom(page, root, "following resumes on cached remount");
        Assert.Same(messages, Named<StackPanel>(page, "MsgPanel"));

        await ScrollUp(page, root, keyboard: true);
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 950, 920));
        await Settle(root);
        Assert.False(Get<bool>(page, "_followLatest"), "Reflow cannot count as a user request to follow again.");
        Assert.True(scroll.ScrollableHeight - scroll.VerticalOffset > 100);
        var paused = scroll.VerticalOffset;
        await GrowVisibleReply(page, session, root, "paused resized reply", following: false);
        AssertPaused(page, paused, "Reflow must retain the paused mode during subsequent chunks.");

        root.Children.Remove(page);
        await WaitUntil(() => !page.IsLoaded, "The paused cached page did not unload.");
        session.EmitCompletedStep();
        session.Emit(VisibleLines(0, 10));
        await Settle(root);
        root.Children.Add(page);
        await WaitUntil(() => page.IsLoaded, "The paused cached page did not return.");
        await Settle(root);
        Assert.Same(scroll, Named<ScrollViewer>(page, "MsgScroll"));
        Assert.Same(messages, Named<StackPanel>(page, "MsgPanel"));
        AssertPaused(page, paused, "A cached paused conversation must retain its historical position.");
        ClickLatest(page);
        await AssertBottom(page, root, "explicit return after paused remount");
        await GrowAtBottom(page, session, root, "stream after cached remount");
        await Finish(page, session, root, first);
        Console.WriteLine("CONVERSATION_FOLLOW|resize-remount|same-scroll-and-messages|follow-and-pause-retained");
    });

    private static async Task SeedHistory(AiAgentPage page, Grid root)
    {
        for (var i = 0; i < 18; i++)
        {
            Invoke(page, "AddUserBubble", "历史示例 " + i);
            Invoke(page, "AddRestoredAssistant", Paragraphs("已完成的历史回复 " + i, 4), "", true);
        }
        await Settle(root);
        Assert.True(Named<ScrollViewer>(page, "MsgScroll").ScrollableHeight > 2000,
            "The fixture must contain genuinely scrollable history.");
        await AssertBottom(page, root, "initial connected history");
    }

    private static async Task AssertInputOwnership(AiAgentPage page, Grid root)
    {
        var messages = Named<StackPanel>(page, "MsgPanel");
        var content = new StackPanel();
        var ordinaryText = new TextBlock { Text = "外层正文" };
        var editor = new TextBox { Text = "编辑器光标移动不是历史滚动" };
        var nestedText = new TextBlock { Text = Paragraphs("内层独立滚动内容", 8) };
        var nested = new ScrollViewer { Height = 70, Content = nestedText };
        content.Children.Add(ordinaryText);
        content.Children.Add(editor);
        content.Children.Add(nested);
        messages.Children.Add(content);
        try
        {
            await Settle(root);
            Assert.True(ordinaryText.IsLoaded && nested.IsLoaded && editor.IsLoaded);
            Assert.True(Assert.IsType<bool>(Invoke(page, "IsOuterMessageScrollInput", ordinaryText, false)));
            Assert.True(Assert.IsType<bool>(Invoke(page, "IsOuterMessageScrollInput", ordinaryText, true)));
            Assert.False(Assert.IsType<bool>(Invoke(page, "IsOuterMessageScrollInput", nestedText, false)));
            Assert.False(Assert.IsType<bool>(Invoke(page, "IsOuterMessageScrollInput", nestedText, true)));
            Assert.False(Assert.IsType<bool>(Invoke(page, "IsOuterMessageScrollInput", editor, true)));
            Assert.False(Assert.IsType<bool>(Invoke(page, "IsOuterMessageScrollInput", Named<TextBox>(page, "InputBox"), true)));
        }
        finally { messages.Children.Remove(content); }
    }

    private static string Paragraphs(string label, int count) => string.Concat(Enumerable.Range(0, count)
        .Select(i => $"{label} {i}：这是用于验证真实聊天布局的合成内容。持续增加正文高度，必须等待原生布局后观察实际滚动位置，不能依靠手工调用滚底方法。\n\n"));

    private static Task GrowAtBottom(AiAgentPage page, SyntheticSession session, Grid root, string label) =>
        GrowVisibleReply(page, session, root, label, following: true);

    private static string VisibleLines(int start, int count) => string.Concat(Enumerable.Range(start, count)
        .Select(i => $"第{i}行：合成回复持续增加内容，观察真实消息高度和滚动位置。\n"));

    private static async Task GrowVisibleReply(AiAgentPage page, SyntheticSession session, Grid root, string label, bool following)
    {
        var scroll = Named<ScrollViewer>(page, "MsgScroll");
        if (session.ActiveText.Length > 80)
        {
            // Long prose is deliberately folded by the product. A legal step
            // group ends the previous reply and opens the next streamed segment;
            // it is only a synthetic event record, never a real tool execution.
            session.EmitCompletedStep();
            session.Emit("接续说明。\n");
            await Settle(root);
        }
        var before = scroll.ScrollableHeight;
        session.Emit(VisibleLines(0, 5));
        Assert.False(AssistantReplyPresentation.Create(session.ActiveText).HasDetails,
            "The fixture must stream visible text rather than trigger the intentional reply fold.");
        await Settle(root);
        if (following) await AssertBottom(page, root, label + " first chunk");
        var bubble = Get<object>(page, "_streamingBubble");
        var firstHeight = scroll.ScrollableHeight;
        session.Emit(VisibleLines(5, 5));
        Assert.False(AssistantReplyPresentation.Create(session.ActiveText).HasDetails);
        await WaitUntil(() => scroll.ScrollableHeight > firstHeight + 30 && scroll.ScrollableHeight > before + 40,
            label + ": consecutive visible TextChunk callbacks must grow the native extent; " +
            $"before={before:0.0}, first={firstHeight:0.0}, current={scroll.ScrollableHeight:0.0}.");
        Assert.Same(bubble, Get<object>(page, "_streamingBubble"));
        if (following) await AssertBottom(page, root, label + " second chunk");
        else await Settle(root);
    }

    private static async Task ScrollUp(AiAgentPage page, Grid root, bool keyboard)
    {
        var scroll = Named<ScrollViewer>(page, "MsgScroll");
        Assert.True(scroll.ScrollableHeight > 600);
        if (keyboard) Invoke(page, "ObserveMessageScrollKey", VirtualKey.PageUp);
        else Invoke(page, "ObserveMessageWheel", 120);
        scroll.ChangeView(null, scroll.ScrollableHeight * .28, null, disableAnimation: true);
        await Settle(root);
        Assert.False(Get<bool>(page, "_followLatest"));
        Assert.True(scroll.ScrollableHeight - scroll.VerticalOffset > 300);
    }

    private static void ClickLatest(AiAgentPage page) => Invoke(page, "ScrollToBottomButton_Click",
        Named<Button>(page, "ScrollToBottomButton"), new RoutedEventArgs());

    private static async Task AssertBottom(AiAgentPage page, Grid root, string stage)
    {
        // Require several settled samples; one matching sample before the layout
        // grows would reproduce the very false-positive this regression prevents.
        var scroll = Named<ScrollViewer>(page, "MsgScroll");
        var samples = 0;
        var previousExtent = -1d;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(50);
            root.UpdateLayout();
            var extent = scroll.ScrollableHeight;
            samples = Math.Abs(extent - previousExtent) < .5 &&
                Math.Abs(extent - scroll.VerticalOffset) <= 2 ? samples + 1 : 0;
            previousExtent = extent;
            if (samples >= 4)
            {
                Assert.True(Get<bool>(page, "_followLatest"), stage + ": native bottom was reached but follow mode was lost.");
                Assert.Equal(Visibility.Collapsed, Named<Button>(page, "ScrollToBottomButton").Visibility);
                return;
            }
        }
        Assert.True(false, $"{stage}: expected stable native bottom; offset={scroll.VerticalOffset:0.00}, extent={scroll.ScrollableHeight:0.00}, follow={Get<bool>(page, "_followLatest")}");
    }

    private static void AssertPaused(AiAgentPage page, double expectedOffset, string message)
    {
        var scroll = Named<ScrollViewer>(page, "MsgScroll");
        Assert.False(Get<bool>(page, "_followLatest"), message);
        Assert.True(Math.Abs(scroll.VerticalOffset - expectedOffset) <= 3,
            $"{message} Expected offset={expectedOffset:0.00}, actual={scroll.VerticalOffset:0.00}");
        Assert.True(scroll.ScrollableHeight - scroll.VerticalOffset > 100, message);
    }

    private static Task Send(AiAgentPage page, string text) => Assert.IsAssignableFrom<Task>(
        Invoke(page, "SendAsync", text, false, null, null, null));

    private static async Task Finish(AiAgentPage page, SyntheticSession session, Grid root, Task send)
    {
        session.Complete();
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        await Settle(root);
        Assert.False(Get<bool>(page, "_isProcessing"));
    }

    private static async Task WithPage(Func<AiAgentPage, SyntheticSession, Window, Grid, Task> body)
    {
        var testRoot = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(testRoot));
        Assert.NotNull(DataRoots.TestRoot);
        Assert.Equal(Path.GetFullPath(testRoot!), Path.GetFullPath(ConfigManager.GetDataDir()), ignoreCase: true);
        Assert.Null(AiAssistantService.HistoryDirOverride);
        Assert.Null(AiProviderStore.StoragePathOverride);
        Assert.Empty(Environment.GetCommandLineArgs().Where(arg => arg.StartsWith("--zxtest-", StringComparison.OrdinalIgnoreCase)));
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        var resolver = AgentEngine.RuntimeResolverOverrideForTest;
        var probe = typeof(AgentEngine).GetField("_probeStarted", BindingFlags.Static | BindingFlags.NonPublic)!;
        var state = typeof(AgentEngine).GetField("_dshState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldProbe = probe.GetValue(null);
        var oldState = state.GetValue(null);
        var provider = AiProviderStore.SelectedProvider;
        var oldUrl = provider.BaseUrl;
        var oldKey = provider.ApiKey;
        var verified = AppSettings.Get("AiVerifiedFingerprint");
        var runtimeCalls = 0;
        AiAgentPage? page = null;
        SyntheticSession? session = null;
        try
        {
            probe.SetValue(null, 1);
            state.SetValue(null, 2);
            AgentEngine.RuntimeResolverOverrideForTest = () =>
            {
                runtimeCalls++;
                throw new InvalidOperationException("The scroll fixture must never resolve a real runtime.");
            };
            // In-memory synthetic readiness only. Do not save credentials or use
            // a network transport; an injected IAgentSession handles every send.
            provider.BaseUrl = "http://127.0.0.1:1/synthetic-no-network";
            provider.ApiKey = "synthetic-native-scroll-fixture";
            await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
            {
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1603, 920));
                page = new AiAgentPage(compact: false, autoLoadLatest: false) { RequestedTheme = ElementTheme.Light };
                session = new SyntheticSession();
                Set(page, "_session", session);
                Invoke(page, "AttachSessionEvents", session);
                root.Children.Add(page);
                await Settle(root);
                Assert.True(page.IsLoaded && page.XamlRoot is not null);
                try { await body(page, session, window, root); }
                finally
                {
                    await WaitUntil(() =>
                    {
                        session.Complete();
                        return !Get<bool>(page, "_isProcessing");
                    }, "The synthetic send did not release during cleanup.");
                    root.Children.Remove(page);
                    page.Unload();
                    page = null;
                }
            });
            Assert.Equal(0, runtimeCalls);
        }
        finally
        {
            session?.Complete();
            page?.Unload();
            provider.BaseUrl = oldUrl;
            provider.ApiKey = oldKey;
            if (verified is null) AppSettings.Remove("AiVerifiedFingerprint");
            else AppSettings.Set("AiVerifiedFingerprint", verified);
            AppSettings.Save(); // Only the asserted isolated ZXAI_DATA_ROOT.
            probe.SetValue(null, oldProbe);
            state.SetValue(null, oldState);
            AgentEngine.RuntimeResolverOverrideForTest = resolver;
        }
    }

    private static async Task Settle(Grid root)
    {
        root.UpdateLayout();
        await Task.Delay(360);
        root.UpdateLayout();
        await Task.Delay(60);
    }
    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(35);
        Assert.True(condition(), message);
    }
    private static T Named<T>(AiAgentPage page, string name) where T : class =>
        page.FindName(name) as T ?? throw new InvalidOperationException("Missing named element: " + name);
    private static T Get<T>(AiAgentPage page, string name) => (T)PageField(name).GetValue(page)!;
    private static void Set(AiAgentPage page, string name, object? value) => PageField(name).SetValue(page, value);
    private static FieldInfo PageField(string name) => typeof(AiAgentPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing page field: " + name);
    private static object? Invoke(AiAgentPage page, string name, params object?[] arguments) =>
        (typeof(AiAgentPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing page method: " + name)).Invoke(page, arguments);

    private sealed class SyntheticSession : IAgentSession
    {
        private TaskCompletionSource? _pending;
        private int _sendCalls;
        private string _lastText = "";
        private string _activeText = "";
        public string Id { get; } = "native-scroll-" + Guid.NewGuid().ToString("N");
        public string Title => "Synthetic conversation follow fixture";
        public bool IsRunning => Volatile.Read(ref _pending) is not null;
        public string PersonaId => AgentPersonaCatalog.DefaultId;
        public AgentSendOutcome LastSendOutcome => AgentSendOutcome.Completed;
        public int TotalPromptTokens => 0;
        public int TotalCompletionTokens => 0;
        public int TotalCacheHitTokens => 0;
        public int TotalCacheMissTokens => 0;
        public IReadOnlyCollection<string> ActiveSkillIds => [];
        internal int SendCalls => Volatile.Read(ref _sendCalls);
        internal string LastText => Volatile.Read(ref _lastText);
        internal string ActiveText => Volatile.Read(ref _activeText);
        public event Action<string>? TextChunk;
        public event Action<string>? ReasoningChunk { add { } remove { } }
        public event Action<AgentStep>? StepStarted;
        public event Action<AgentStep>? StepCompleted;
        public event Action<IReadOnlyList<AgentConfirmationRequest>>? ConfirmationsRequested { add { } remove { } }
        public event Action<string>? Error { add { } remove { } }
        public event Action? RunCompleted { add { } remove { } }
        public event Action? RoundStarted { add { } remove { } }
        public event Action<AgentStepGroupSummary>? StepGroupCompleted;
        public async Task SendAsync(string userText, IReadOnlyList<(byte[] Bytes, string MediaType)>? images = null)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.Null(Interlocked.CompareExchange(ref _pending, completion, null));
            Volatile.Write(ref _lastText, userText);
            Volatile.Write(ref _activeText, "");
            Interlocked.Increment(ref _sendCalls);
            try { await completion.Task; }
            finally { Interlocked.CompareExchange(ref _pending, null, completion); }
        }
        internal void Emit(string text)
        {
            Assert.True(IsRunning, "Synthetic text can only arrive during an accepted send.");
            _activeText += text;
            TextChunk?.Invoke(text);
        }
        internal void EmitCompletedStep()
        {
            Assert.True(IsRunning);
            var id = Guid.NewGuid().ToString("N");
            StepStarted?.Invoke(new AgentStep
            {
                CallId = id, ToolName = "synthetic_display_only", DisplayName = "合成展示步骤",
                Summary = "仅验证消息分段，不执行工具", Status = AgentStepStatus.Running,
            });
            StepCompleted?.Invoke(new AgentStep
            {
                CallId = id, ToolName = "synthetic_display_only", DisplayName = "合成展示步骤",
                Summary = "仅验证消息分段，不执行工具", Status = AgentStepStatus.Success,
            });
            StepGroupCompleted?.Invoke(new AgentStepGroupSummary { Total = 1, Success = 1 });
            _activeText = "";
        }
        internal void Complete() => Volatile.Read(ref _pending)?.TrySetResult();
        public Task ResumeConfirmationsAsync(IReadOnlyList<AgentConfirmationDecision> decisions) =>
            throw new InvalidOperationException("No tool confirmation belongs to this fixture.");
        public void Cancel() => Complete();
        public void SetSkillEnabled(string id, bool enabled) { }
        public void SetPersona(string personaId) { }
        public void Rename(string title) { }
        public void Save() { }
        public void Dispose() => Complete();
    }
}
