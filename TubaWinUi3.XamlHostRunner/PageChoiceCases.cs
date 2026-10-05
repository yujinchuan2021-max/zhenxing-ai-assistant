using System.Collections;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using TubaWinUi3.Services.Ai;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// Exercises the real page's completion and dialog-closing projections inside
/// the empty TestApp. A synthetic session never sends, installs or writes data.
/// The only dialog result permitted here is cancellation.
/// </summary>
internal static class PageChoiceCases
{
    internal static async Task PlanDialogCompletionRecovery()
    {
        RequireSafeIsolation();
        Assert.IsType<TestApp>(Application.Current);
        var previousResolver = AgentEngine.RuntimeResolverOverrideForTest;
        var probeStarted = EngineField("_probeStarted");
        var dshState = EngineField("_dshState");
        var previousProbeStarted = probeStarted.GetValue(null);
        var previousState = dshState.GetValue(null);
        var runtimeCalls = 0;
        var resources = new XamlControlsResources();
        var localResources = new ResourceDictionary { ["AppFontFamily"] = AppFonts.WinUI };
        AiAgentPage? page = null;
        try
        {
            AgentEngine.RuntimeResolverOverrideForTest = () =>
            {
                Interlocked.Increment(ref runtimeCalls);
                throw new InvalidOperationException("Synthetic unavailable runtime: no process is allowed.");
            };
            AgentEngine.ResetDshProbeCacheForTest();
            // Reset only clears _dshState. Prevent even a deferred Task.Run from
            // observing the restored resolver after this case has completed.
            probeStarted.SetValue(null, 1);
            dshState.SetValue(null, 2);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            Application.Current.Resources.MergedDictionaries.Add(localResources);
            await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
            {
                // Direct contrast: clearing the backing flag without a page
                // refresh leaves the already rendered native buttons disabled.
                page = await AttachPage(root);
                var synthetic = new SyntheticSession();
                Set(page, "_session", synthetic);
                BeginFirstReply(page);
                Set(page, "_godotPlanDialogOpen", true);
                Invoke(page, "UpdateInputState");
                FinishFirstReply(page);
                var action = LatestQuestion(page);
                var choice = QuestionControl(action);
                await Settle(root);
                AssertNativeButtons(choice);
                AssertQuestion(choice, enabled: false);
                Assert.False(CanAnswer(page, action));
                Set(page, "_godotPlanDialogOpen", false);
                Assert.True(CanAnswer(page, action));
                AssertQuestion(choice, enabled: false); // Existing production display is stale until refreshed.
                Console.WriteLine("CHOICE_RECOVERY|flag-cleared-without-refresh|native-buttons-still-disabled");
                Invoke(page, "UpdateInputState");
                AssertQuestion(choice, enabled: true);
                Assert.True(CanAnswer(page, action));

                // Temporary navigation keeps this live page and its unselected
                // question. This is not the terminal public Unload() operation.
                root.Children.Remove(page);
                await WaitUntil(() => !page.IsLoaded, "The synthetic page did not unload from the host.");
                root.Children.Add(page);
                await WaitUntil(() => page.IsLoaded, "The synthetic page did not return to the host.");
                await Settle(root);
                AssertQuestion(choice, enabled: true);
                Assert.True(CanAnswer(page, action));
                Invoke(page, "AddUserBubble", "这是一条新的用户消息。");
                AssertQuestion(choice, enabled: false);
                Assert.False(CanAnswer(page, action));
                Assert.Equal(0, synthetic.SendCalls);
                RemovePage(root, page);
                page = null;

                // Fresh first reply, with the real production ShowAsync/finally.
                // Do not replace the page's choice handler or click its buttons.
                page = await AttachPage(root);
                synthetic = new SyntheticSession();
                Set(page, "_session", synthetic);
                BeginFirstReply(page);
                var dialogTask = Assert.IsAssignableFrom<Task>(Invoke(page, "ShowGodotTwoDPlanDialogAsync"));
                ContentDialog? dialog = null;
                try
                {
                    await WaitUntil(() =>
                    {
                        if (dialogTask.IsFaulted) dialogTask.GetAwaiter().GetResult();
                        return (dialog = OpenDialog(page.XamlRoot)) is not null;
                    },
                        "The production example dialog did not attach in the isolated host.");
                    Assert.True(Get<bool>(page, "_godotPlanDialogOpen"));
                    var primaryAttempts = 0;
                    ContentDialogResult? result = null;
                    dialog!.PrimaryButtonClick += (_, args) => { primaryAttempts++; args.Cancel = true; };
                    dialog.Closing += (_, args) => result = args.Result;
                    FinishFirstReply(page);
                    action = LatestQuestion(page);
                    choice = QuestionControl(action);
                    await Settle(root);
                    AssertNativeButtons(choice);
                    AssertQuestion(choice, enabled: false);
                    Assert.False(CanAnswer(page, action));
                    Assert.Equal(0, synthetic.SendCalls);
                    dialog.Hide(); // None/close only; selection/install/follow-up paths remain unreachable.
                    await CompleteWithin(dialogTask, "The cancelled production dialog did not finish.");
                    await Settle(root);
                    Assert.Equal(ContentDialogResult.None, result);
                    Assert.Equal(0, primaryAttempts);
                    Assert.False(Get<bool>(page, "_godotPlanDialogOpen"));
                    AssertQuestion(choice, enabled: true);
                    Assert.True(CanAnswer(page, action));
                    Assert.Equal(0, synthetic.SendCalls);
                    Console.WriteLine("CHOICE_RECOVERY|production-dialog-cancel-finally|five-native-buttons-enabled");
                }
                finally
                {
                    if (!dialogTask.IsCompleted)
                    {
                        (dialog ?? OpenDialog(page.XamlRoot))?.Hide();
                        await CompleteWithin(dialogTask, "The production dialog could not be cancelled during cleanup.");
                    }
                }
                RemovePage(root, page);
                page = null;
            });
            Assert.Equal(0, Volatile.Read(ref runtimeCalls));
        }
        finally
        {
            page?.Unload();
            Application.Current.Resources.MergedDictionaries.Remove(localResources);
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            probeStarted.SetValue(null, previousProbeStarted);
            dshState.SetValue(null, previousState);
            AgentEngine.RuntimeResolverOverrideForTest = previousResolver;
        }
    }

    private static async Task<AiAgentPage> AttachPage(Grid root)
    {
        var page = new AiAgentPage(compact: false, autoLoadLatest: false);
        try
        {
            root.Children.Clear();
            root.Children.Add(page);
            await WaitUntil(() => page.IsLoaded && page.XamlRoot is not null,
                "The real AiAgentPage did not attach in the empty metadata host.");
            await Settle(root);
            Assert.True(page.ActualWidth > 0 && page.ActualHeight > 0);
            return page;
        }
        catch { RemovePage(root, page); throw; }
    }

    private static void BeginFirstReply(AiAgentPage page)
    {
        Set(page, "_sendEpoch", Get<int>(page, "_displayEpoch"));
        Set(page, "_toolFlowRoundStart", 0);
        Set(page, "_toolFlowDisplayRoundStart", 0);
        Set(page, "_toolFlowRoundSucceeded", false);
        Set(page, "_sendHadError", false);
        Invoke(page, "AddUserBubble", "我只需要 AI 帮我生成一段音乐。");
        Set(page, "_isProcessing", true);
        Invoke(page, "UpdateInputState");
    }

    private static void FinishFirstReply(AiAgentPage page)
    {
        const string reply = "先确认你想生成什么。\n```choice-question\n" +
            "{\"schema\":1,\"question\":\"你想生成哪一种音乐？\",\"options\":[" +
            "{\"id\":\"song\",\"label\":\"有人声的歌曲\",\"answer\":\"我想生成有人声的歌曲。\"}," +
            "{\"id\":\"instrumental\",\"label\":\"纯音乐\",\"answer\":\"我想生成纯音乐。\"}," +
            "{\"id\":\"background\",\"label\":\"背景音乐\",\"answer\":\"我想生成背景音乐。\"}," +
            "{\"id\":\"effect\",\"label\":\"短音效\",\"answer\":\"我想生成短音效。\"}," +
            "{\"id\":\"unsure\",\"label\":\"你帮我选\",\"answer\":\"请先帮我推荐。\"}]}\n```";
        Invoke(page, "AppendChunk", reply);
        Invoke(page, "FinalizeStreaming");
        // The actual send finally clears processing, refreshes, finalizes, then
        // marks successful question actions. No session SendAsync is called.
        Set(page, "_isProcessing", false);
        Invoke(page, "UpdateInputState");
        Invoke(page, "FinalizeStreaming");
        Invoke(page, "CompleteToolFlowMessageActions", Get<int>(page, "_sendEpoch"), true);
    }

    private static object LatestQuestion(AiAgentPage page) =>
        Assert.Single(Get<IEnumerable>(page, "_modelChoiceActions").Cast<object>());

    private static ChatChoiceControl QuestionControl(object action) =>
        Assert.IsType<ChatChoiceControl>(action.GetType().GetProperty("QuestionControl")!.GetValue(action));

    private static bool CanAnswer(AiAgentPage page, object action) =>
        Assert.IsType<bool>(Invoke(page, "CanAnswerModelChoice", action));

    private static void AssertQuestion(ChatChoiceControl choice, bool enabled)
    {
        Assert.Equal(5, choice.ChoiceButtons.Count);
        Assert.Null(choice.SelectedId);
        Assert.All(choice.ChoiceButtons, button => Assert.Equal(enabled, button.IsEnabled));
    }

    private static void AssertNativeButtons(ChatChoiceControl choice)
    {
        Assert.True(choice.IsLoaded && choice.ActualHeight > 0);
        Assert.All(choice.ChoiceButtons, button =>
        {
            Assert.True(button.ActualHeight > 0);
            Assert.True(VisualTreeHelper.GetChildrenCount(button) > 0);
        });
    }

    private static ContentDialog? OpenDialog(XamlRoot? root)
    {
        if (root is null) return null;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            var nodes = new Queue<DependencyObject>();
            if (popup.Child is { } child) nodes.Enqueue(child);
            var examined = 0;
            while (nodes.Count > 0 && examined++ < 10000)
            {
                var node = nodes.Dequeue();
                if (node is ContentDialog dialog) return dialog;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    nodes.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        return null;
    }

    private static void RemovePage(Grid root, AiAgentPage page)
    {
        root.Children.Remove(page);
        page.Unload(); // Stub Save/Dispose are no-ops; close lease and stop page timers/watchers.
    }

    private static async Task Settle(Grid root) { await Task.Delay(180); root.UpdateLayout(); }

    private static async Task WaitUntil(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(40);
        Assert.True(condition(), failure);
    }

    private static async Task CompleteWithin(Task task, string failure)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(6000)));
        Assert.True(task.IsCompleted, failure);
        await task;
    }

    private static FieldInfo EngineField(string name) => typeof(AgentEngine).GetField(name,
        BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Engine guard field missing: " + name);

    private static FieldInfo PageField(string name) => typeof(AiAgentPage).GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Page field missing: " + name);

    private static T Get<T>(AiAgentPage page, string name) => (T)PageField(name).GetValue(page)!;
    private static void Set(AiAgentPage page, string name, object value) => PageField(name).SetValue(page, value);

    private static object? Invoke(AiAgentPage page, string name, params object[] arguments)
    {
        var method = typeof(AiAgentPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Page method missing: " + name);
        return method.Invoke(page, arguments);
    }

    private static void RequireSafeIsolation()
    {
        var root = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.NotNull(DataRoots.TestRoot);
        Assert.Equal(Path.GetFullPath(root!), Path.GetFullPath(DataRoots.EffectiveTestRoot!), ignoreCase: true);
        Assert.Equal(Path.GetFullPath(root!), Path.GetFullPath(ConfigManager.GetDataDir()), ignoreCase: true);
        Assert.Null(AiAssistantService.HistoryDirOverride);
        Assert.Null(AiProviderStore.StoragePathOverride);
        Assert.Empty(Environment.GetCommandLineArgs().Where(argument => argument.StartsWith("--zxtest-", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class SyntheticSession : IAgentSession
    {
        public string Id { get; } = "synthetic-choice-" + Guid.NewGuid().ToString("N");
        public string Title => "Synthetic choice recovery";
        public bool IsRunning => false;
        public string PersonaId => AgentPersonaCatalog.DefaultId;
        public AgentSendOutcome LastSendOutcome => AgentSendOutcome.Completed;
        public int TotalPromptTokens => 0;
        public int TotalCompletionTokens => 0;
        public int TotalCacheHitTokens => 0;
        public int TotalCacheMissTokens => 0;
        public IReadOnlyCollection<string> ActiveSkillIds => [];
        internal int SendCalls { get; private set; }
        public event Action<string>? TextChunk { add { } remove { } }
        public event Action<string>? ReasoningChunk { add { } remove { } }
        public event Action<AgentStep>? StepStarted { add { } remove { } }
        public event Action<AgentStep>? StepCompleted { add { } remove { } }
        public event Action<IReadOnlyList<AgentConfirmationRequest>>? ConfirmationsRequested { add { } remove { } }
        public event Action<string>? Error { add { } remove { } }
        public event Action? RunCompleted { add { } remove { } }
        public event Action? RoundStarted { add { } remove { } }
        public event Action<AgentStepGroupSummary>? StepGroupCompleted { add { } remove { } }
        public Task SendAsync(string userText, IReadOnlyList<(byte[] Bytes, string MediaType)>? images = null)
        {
            SendCalls++;
            throw new InvalidOperationException("No send is permitted by the page-choice fixture.");
        }
        public Task ResumeConfirmationsAsync(IReadOnlyList<AgentConfirmationDecision> decisions)
            => throw new InvalidOperationException("No confirmation is permitted by the page-choice fixture.");
        public void Cancel() { }
        public void SetSkillEnabled(string id, bool enabled) { }
        public void SetPersona(string personaId) { }
        public void Rename(string title) { }
        public void Save() { }
        public void Dispose() { }
    }
}
