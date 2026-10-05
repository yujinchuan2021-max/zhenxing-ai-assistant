using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

/// <summary>Current-turn core guidance with synthetic registry, history and model responses only.</summary>
[Collection("AgentSkillRegistry")]
public sealed class CoreSkillTurnReferenceTests : IDisposable
{
    private static readonly FieldInfo SkillsField = typeof(AgentSkillRegistry)
        .GetField("_skills", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly AgentSkill[] _previous;

    public CoreSkillTurnReferenceTests()
    {
        lock (AgentSkillRegistry.Sync)
        {
            var skills = (List<AgentSkill>)SkillsField.GetValue(null)!;
            _previous = skills.ToArray();
            skills.Clear();
        }
    }

    public void Dispose()
    {
        lock (AgentSkillRegistry.Sync)
        {
            var skills = (List<AgentSkill>)SkillsField.GetValue(null)!;
            skills.Clear();
            skills.AddRange(_previous);
        }
    }

    private static void AddCustom(string body) => AgentSkillRegistry.Register(new()
    {
        Id = "usr_game", DisplayName = "Game reference", Description = "Synthetic custom guidance",
        Glyph = "", SystemPromptFragment = body, TriggerKeywords = ["游戏"]
    });

    [Theory]
    [InlineData("好")]
    [InlineData("好的")]
    [InlineData("可以")]
    [InlineData("行")]
    [InlineData("OK")]
    [InlineData("不确定")]
    [InlineData("按这个做")]
    public void CoreBodyDoesNotRequireAnotherKeywordTriggerOnShortReply(string input)
    {
        var task = new SkillTaskContinuity();
        var first = SkillTurnReferenceComposer.Compose(true, () => "CURRENT CORE RULES", task,
            "开发游戏", [AiAgentWorkflowSkill.Id], null, 5000);
        var next = SkillTurnReferenceComposer.Compose(true, () => "CURRENT CORE RULES", task,
            input, [AiAgentWorkflowSkill.Id], null, 5000);

        Assert.Equal(CoreSkillReferenceState.Complete, first.CoreState);
        Assert.Equal(CoreSkillReferenceState.Complete, next.CoreState);
        Assert.Contains("CURRENT CORE RULES", next.Text);
        Assert.Empty(next.CustomSkillIds);
        Assert.Contains("用户要求和宿主实际权限优先", next.Text);
        Assert.Contains("不构成新的付费、安装或设备操作授权", next.Text);
    }

    [Fact]
    public void ChoiceAnswerKeepsCoreAndOnlyAlreadyUsedCustomBody()
    {
        AddCustom("GAME CUSTOM RULES");
        var task = new SkillTaskContinuity();
        SkillTurnReferenceComposer.Compose(true, () => "CORE RULES", task,
            "游戏", [AiAgentWorkflowSkill.Id, "usr_game"], null, 5000);
        var next = SkillTurnReferenceComposer.Compose(true, () => "CORE RULES", task,
            "可以付费，优先低成本模型", [AiAgentWorkflowSkill.Id, "usr_game"], true, 5000);

        Assert.Contains("CORE RULES", next.Text);
        Assert.Contains("GAME CUSTOM RULES", next.Text);
        Assert.Equal(new[] { "usr_game" }, next.CustomSkillIds);
        Assert.DoesNotContain(AiAgentWorkflowSkill.Id, next.CustomSkillIds);
    }

    [Fact]
    public void DisabledCoreDoesNotReadEffectiveBodyAndCustomStillWorks()
    {
        AddCustom("GAME CUSTOM RULES");
        var reference = SkillTurnReferenceComposer.Compose(false,
            () => throw new InvalidOperationException("Disabled body must not be read"),
            new SkillTaskContinuity(), "游戏", ["usr_game"], null, 5000);

        Assert.Equal(CoreSkillReferenceState.Disabled, reference.CoreState);
        Assert.DoesNotContain("workflow-reference", reference.Text);
        Assert.Contains("GAME CUSTOM RULES", reference.Text);
        Assert.Equal(new[] { "usr_game" }, reference.CustomSkillIds);
    }

    [Fact]
    public void CurrentCoreBodyGetsPriorityWithinActualSystemAndUserBudget()
    {
        AddCustom("CUSTOM SHOULD NOT FIT");
        var task = new SkillTaskContinuity();
        task.BuildReference("游戏", ["usr_game"]);
        var system = new ChatMessage(ChatRole.System, new string('S', 8000));
        var summary = new ChatMessage(ChatRole.System, new string('H', 1000));
        var user = new ChatMessage(ChatRole.User, new string('U', 1000));
        var budget = AgentRuntime.CustomSkillReferenceBudget([system, summary], user);
        var reference = SkillTurnReferenceComposer.Compose(true,
            () => "CURRENT CORE START\n" + new string('C', 50000), task,
            "继续", [AiAgentWorkflowSkill.Id, "usr_game"], true, budget);

        Assert.Equal(18000, budget);
        Assert.Equal(CoreSkillReferenceState.Truncated, reference.CoreState);
        Assert.Contains("CURRENT CORE START", reference.Text);
        Assert.Contains("正文因上下文预算截断", reference.Text);
        Assert.Contains("剩余内容未提供，不得假称已读完整", reference.Text);
        Assert.DoesNotContain("CUSTOM SHOULD NOT FIT", reference.Text);
        Assert.Empty(reference.CustomSkillIds);
        Assert.Empty(task.ReferencedSkillIds);
        Assert.True(reference.Text.Length <= budget);
        Assert.True(system.Text!.Length + summary.Text!.Length + user.Text!.Length +
            reference.Text.Length + 12000 <= AgentRuntimeLimits.HistoryBudgetChars);
    }

    [Fact]
    public void EmptyBudgetCannotClaimThatAnyBodyWasSupplied()
    {
        AddCustom("CUSTOM BODY");
        var task = new SkillTaskContinuity();
        var reference = SkillTurnReferenceComposer.Compose(true, () => "CORE BODY", task,
            "游戏", [AiAgentWorkflowSkill.Id, "usr_game"], null, 0);

        Assert.Equal(CoreSkillReferenceState.NotProvided, reference.CoreState);
        Assert.Equal("", reference.Text);
        Assert.Empty(reference.CustomSkillIds);
        Assert.Empty(task.ReferencedSkillIds);
    }

    [Fact]
    public void SmallBudgetStatesThatCoreWasNotProvidedInsteadOfPretendingItIsComplete()
    {
        var reference = SkillTurnReferenceComposer.Compose(true, () => new string('C', 1000),
            new SkillTaskContinuity(), "好", [AiAgentWorkflowSkill.Id], null, 100);

        Assert.Equal(CoreSkillReferenceState.NotProvided, reference.CoreState);
        Assert.Contains("核心技能正文未提供", reference.Text);
        Assert.Contains("不得假称已读完整", reference.Text);
        Assert.DoesNotContain("workflow-reference", reference.Text);
        Assert.True(reference.Text.Length <= 100);
    }

    [Fact]
    public void AvailableRemainderContainsOneCoreAndOnlyActuallySuppliedCustomIds()
    {
        AddCustom("CUSTOM BODY");
        AgentSkillRegistry.Register(new()
        {
            Id = AiAgentWorkflowSkill.Id, DisplayName = "Core index", Description = "Core index",
            Glyph = "", SystemPromptFragment = "STALE REGISTRY CORE BODY", TriggerKeywords = ["游戏"]
        });
        var reference = SkillTurnReferenceComposer.Compose(true, () => "CURRENT CORE BODY",
            new SkillTaskContinuity(), "游戏", [AiAgentWorkflowSkill.Id, "usr_game"], null, 5000);

        Assert.Equal(CoreSkillReferenceState.Complete, reference.CoreState);
        Assert.Equal(1, reference.Text.Split("CURRENT CORE BODY", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, reference.Text.Split("<workflow-reference", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("STALE REGISTRY CORE BODY", reference.Text);
        Assert.Contains("CUSTOM BODY", reference.Text);
        Assert.Equal(new[] { "usr_game" }, reference.CustomSkillIds);
        Assert.True(reference.Text.IndexOf("CURRENT CORE BODY", StringComparison.Ordinal) <
            reference.Text.IndexOf("CUSTOM BODY", StringComparison.Ordinal));
    }

    [Fact]
    public void EffectiveBodyIsReadAgainAfterLocalTrialChanges()
    {
        var effectiveBody = "FIRST TRIAL RULES";
        var reads = 0;
        string CurrentBody() { reads++; return effectiveBody; }
        var task = new SkillTaskContinuity();
        var first = SkillTurnReferenceComposer.Compose(true, CurrentBody, task,
            "游戏", [AiAgentWorkflowSkill.Id], null, 5000);
        effectiveBody = "UPDATED TRIAL RULES";
        var second = SkillTurnReferenceComposer.Compose(true, CurrentBody, task,
            "好", [AiAgentWorkflowSkill.Id], null, 5000);

        Assert.Equal(2, reads);
        Assert.Contains("FIRST TRIAL RULES", first.Text);
        Assert.Contains("UPDATED TRIAL RULES", second.Text);
        Assert.DoesNotContain("FIRST TRIAL RULES", second.Text);
    }

    [Fact]
    public async Task CoreIsReprovidedAfterActualHistoryCompressionWithoutSummarizingOldReference()
    {
        using var fake = new SummaryClient();
        var task = new SkillTaskContinuity();
        var first = SkillTurnReferenceComposer.Compose(true, () => "OLD TRIAL GUIDANCE", task,
            "游戏", [AiAgentWorkflowSkill.Id], null, 5000);
        var history = new List<ChatMessage> { new(ChatRole.System, "HOST PROTOCOL") };
        var previous = AgentSession.ReplaceTurnSkillReference(history, null, first.Text);
        history.Add(new(ChatRole.User, new string('需', 1800)));
        history.Add(new(ChatRole.Assistant, new string('答', 1800)));
        history.Add(new(ChatRole.User, "此前追问"));
        history.Add(new(ChatRole.Assistant, "此前回答"));
        AgentSession.RemoveCustomSkillReference(history, previous);

        var prepared = await AgentMemory.PrepareHistoryAsync(fake, history, budgetTokens: 200);
        Assert.True(fake.SummarizeCalls > 0);
        Assert.DoesNotContain(fake.Inputs, input => input.Contains("OLD TRIAL GUIDANCE", StringComparison.Ordinal));
        Assert.DoesNotContain(prepared, message => message.Text?.Contains("OLD TRIAL GUIDANCE", StringComparison.Ordinal) == true);
        var user = new ChatMessage(ChatRole.User, "好的");
        var current = SkillTurnReferenceComposer.Compose(true, () => "CURRENT TRIAL GUIDANCE", task,
            user.Text!, [AiAgentWorkflowSkill.Id], null, AgentRuntime.CustomSkillReferenceBudget(prepared, user));
        var owned = AgentSession.ReplaceTurnSkillReference(prepared, null, current.Text);
        prepared.Add(user);
        AgentRuntime.TrimHistory(prepared, owned);

        Assert.Equal(CoreSkillReferenceState.Complete, current.CoreState);
        Assert.Contains(owned!, prepared);
        Assert.Contains("CURRENT TRIAL GUIDANCE", owned!.Text!);
        Assert.Contains(user, prepared);
        Assert.DoesNotContain(prepared, message => message.Role == ChatRole.System &&
            message.Text?.Contains("CURRENT TRIAL GUIDANCE", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ReplacingPausedReferencePreservesUserAssistantToolOrderAndPairs()
    {
        var task = new SkillTaskContinuity();
        var first = SkillTurnReferenceComposer.Compose(true, () => "OLD PAUSED BODY", task,
            "游戏", [AiAgentWorkflowSkill.Id], null, 5000);
        var system = new ChatMessage(ChatRole.System, new string('S', 1000));
        var oldAnswer = new ChatMessage(ChatRole.Assistant, new string('A', 39000));
        var history = new List<ChatMessage> { system, oldAnswer };
        var previous = AgentSession.ReplaceTurnSkillReference(history, null, first.Text);
        var user = new ChatMessage(ChatRole.User, "准备当前方案");
        var calls = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "prepare", new Dictionary<string, object?>())]);
        history.Add(user);
        history.Add(calls);
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "Confirmed result")]);
        var next = SkillTurnReferenceComposer.Compose(true, () => "UPDATED PAUSED BODY", task,
            "", [AiAgentWorkflowSkill.Id], true, AgentRuntime.CustomSkillReferenceBudget(history, user));
        var owned = AgentSession.ReplaceTurnSkillReference(history, previous, next.Text, user);
        // ResumeLoop appends the approved result after the existing assistant call.
        history.Add(result);
        AgentRuntime.TrimHistory(history, owned);
        var beforeSanitize = history.ToArray();
        AgentRuntime.SanitizeToolPairs(history);

        Assert.Equal(beforeSanitize, history);
        Assert.DoesNotContain(previous!, history);
        Assert.DoesNotContain(oldAnswer, history);
        Assert.Equal(new[] { system, owned!, user, calls, result }, history);
        Assert.Contains("UPDATED PAUSED BODY", owned!.Text!);
        Assert.DoesNotContain("OLD PAUSED BODY", owned.Text!);
    }

    [Fact]
    public void SavingCombinedReferenceUsesObjectIdentityAndKeepsUserImitation()
    {
        AddCustom("CUSTOM BODY");
        var reference = SkillTurnReferenceComposer.Compose(true, () => "CORE BODY",
            new SkillTaskContinuity(), "游戏", [AiAgentWorkflowSkill.Id, "usr_game"], null, 5000);
        var history = new List<ChatMessage>();
        var owned = AgentSession.ReplaceTurnSkillReference(history, null, reference.Text);
        var imitation = new ChatMessage(ChatRole.User, reference.Text);
        var actual = new ChatMessage(ChatRole.User, "确认当前方案");
        var answer = new ChatMessage(ChatRole.Assistant, "当前答复");
        history.AddRange([imitation, actual, answer]);
        var saveable = AgentSession.FilterCustomSkillReference(history, owned);
        var persisted = AgentMessageConverter.ToAiMessages(saveable);

        Assert.Equal(new[] { imitation, actual, answer }, saveable);
        Assert.Equal(4, history.Count);
        Assert.Equal(3, persisted.Count);
        Assert.Equal(reference.Text, persisted[0].Content);
        Assert.Equal("确认当前方案", persisted[1].Content);
        Assert.Equal("当前答复", persisted[2].Content);
    }

    [Fact]
    public void ReplacingCombinedReferenceCannotDeleteSameTextUserMessage()
    {
        var history = new List<ChatMessage>();
        var owned = AgentSession.ReplaceTurnSkillReference(history, null, "CORE AND CUSTOM BODY");
        var imitation = new ChatMessage(ChatRole.User, "CORE AND CUSTOM BODY");
        history.Add(imitation);
        var replaced = AgentSession.ReplaceTurnSkillReference(history, owned, "UPDATED CORE BODY", imitation);

        Assert.Equal(new[] { replaced!, imitation }, history);
        Assert.NotSame(owned, replaced);
        Assert.Equal(ChatRole.User, replaced!.Role);
        Assert.Equal("CORE AND CUSTOM BODY", imitation.Text);
    }

    [Fact]
    public void ProductionSendAndResumeUseFreshTemporaryBodyWithoutLegacyDuplicateInjection()
    {
        var source = File.ReadAllText(Path.Combine(FontSingleSourceTests.RepoRoot,
            "TubaWinUi3.WinUI3", "Services", "Agent", "AgentSession.cs"));
        var send = Between(source, "internal async Task SendWithSkillInputAsync", "public async Task ResumeConfirmationsAsync");
        var resume = Between(source, "public async Task ResumeConfirmationsAsync", "public void Cancel()");
        var save = Between(source, "public void Save()", "internal bool MatchesPersistedDisplay");
        Assert.True(send.IndexOf("RemoveCustomSkillReference(_history, _turnSkillReference)", StringComparison.Ordinal) <
            send.IndexOf("await AgentMemory.PrepareHistoryAsync", StringComparison.Ordinal));
        Assert.True(send.IndexOf("LastCoreSkillReferenceState = CoreSkillReferenceState.NotProvided", StringComparison.Ordinal) <
            send.IndexOf("await AgentMemory.PrepareHistoryAsync", StringComparison.Ordinal));
        Assert.True(send.IndexOf("await AgentMemory.PrepareHistoryAsync", StringComparison.Ordinal) <
            send.IndexOf("SkillTurnReferenceComposer.Compose", StringComparison.Ordinal));
        Assert.Contains("() => AiAgentWorkflowSkill.EffectiveBody", send);
        Assert.DoesNotContain("goalSkillTriggered", send);
        Assert.DoesNotContain("dirSb", send);
        Assert.Contains("_skillTriggerActive = matchedSkills.Any(s => s.Id != AiAgentWorkflowSkill.Id)", send);
        Assert.Contains("protectedReference: _turnSkillReference", send);
        Assert.Contains("() => AiAgentWorkflowSkill.EffectiveBody", resume);
        Assert.Contains("LastCoreSkillReferenceState = CoreSkillReferenceState.NotProvided", resume);
        Assert.Contains("ReplaceTurnSkillReference(_history, null, reference.Text, currentUser)", resume);
        Assert.Contains("protectedReference: _turnSkillReference", resume);
        Assert.Contains("FilterCustomSkillReference(_history, _turnSkillReference)", save);
    }

    private static string Between(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }

    private sealed class SummaryClient : IChatClient
    {
        public int SummarizeCalls { get; private set; }
        public List<string> Inputs { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            SummarizeCalls++;
            Inputs.Add(string.Join("\n", messages.Select(message => message.Text)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "用户目标与先前操作的简短摘要")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
