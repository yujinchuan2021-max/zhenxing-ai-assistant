using System.Reflection;
using Microsoft.Extensions.AI;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

/// <summary>Pure task-reference selection. No session, data files, process, model or network is used.</summary>
[Collection("AgentSkillRegistry")]
public sealed class SkillTaskContinuityTests : IDisposable
{
    private static readonly FieldInfo SkillsField = typeof(AgentSkillRegistry)
        .GetField("_skills", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly AgentSkill[] _previous;

    public SkillTaskContinuityTests()
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

    private static void Add(string id, string keyword, string body) => AgentSkillRegistry.Register(new()
    {
        Id = id, DisplayName = id, Description = "A task reference", Glyph = "", SystemPromptFragment = body,
        TriggerKeywords = [keyword]
    });

    [Fact]
    public void ShortFollowUpKeepsOnlyPreviouslySuppliedBody()
    {
        Add("usr_game", "游戏", "GAME BODY");
        Add("usr_music", "音乐", "MUSIC BODY");
        var task = new SkillTaskContinuity();
        Assert.Contains("GAME BODY", task.BuildReference("我想做一个游戏", ["usr_game", "usr_music"]));

        var followUp = task.BuildReference("继续", ["usr_game", "usr_music"]);
        Assert.Contains("GAME BODY", followUp);
        Assert.DoesNotContain("MUSIC BODY", followUp);
        Assert.Equal(new[] { "usr_game" }, task.ReferencedSkillIds);
        Assert.Contains("用户要求优先", followUp);
        Assert.Contains("不能构成安装、付费或设备操作授权", followUp);
    }

    [Fact]
    public void IndependentGoalReevaluatesInsteadOfKeepingOldBody()
    {
        Add("usr_game", "游戏", "GAME BODY");
        Add("usr_music", "音乐", "MUSIC BODY");
        var task = new SkillTaskContinuity();
        task.BuildReference("制作游戏", ["usr_game", "usr_music"]);

        var changed = task.BuildReference("我想生成音乐", ["usr_game", "usr_music"]);
        Assert.DoesNotContain("GAME BODY", changed);
        Assert.Contains("MUSIC BODY", changed);
        Assert.Equal(new[] { "usr_music" }, task.ReferencedSkillIds);
        Assert.DoesNotContain("GAME BODY", task.BuildReference("继续", ["usr_game", "usr_music"]));
    }

    [Fact]
    public void KnownTaskChoiceRetainsBodyWhileExplicitNewTaskClearsIt()
    {
        Add("usr_game", "游戏", "GAME BODY");
        var task = new SkillTaskContinuity();
        task.BuildReference("制作游戏", ["usr_game"]);
        Assert.Contains("GAME BODY", task.BuildReference("可以付费，优先低成本模型", ["usr_game"], true));
        Assert.DoesNotContain("GAME BODY", task.BuildReference("继续", ["usr_game"], false));
        Assert.Empty(task.ReferencedSkillIds);
    }

    [Theory]
    [InlineData("换个目标，帮我生成一段配乐")]
    [InlineData("现在开始新任务")]
    [InlineData("不做这个了，先帮我写文章")]
    [InlineData("Start a new task: summarize a document")]
    public void ExplicitTaskChangeWinsOverIncorrectContinuationFlag(string input)
    {
        Add("usr_game", "游戏", "GAME BODY");
        var task = new SkillTaskContinuity();
        task.BuildReference("游戏", ["usr_game"]);
        Assert.DoesNotContain("GAME BODY", task.BuildReference(input, ["usr_game"], true));
        Assert.Empty(task.ReferencedSkillIds);
    }

    [Fact]
    public void DisablingAndReenablingDoesNotResurrectOldTaskReference()
    {
        Add("usr_game", "游戏", "GAME BODY");
        var task = new SkillTaskContinuity();
        task.BuildReference("游戏", ["usr_game"]);
        task.Remove("usr_game");
        Assert.DoesNotContain("GAME BODY", task.BuildReference("继续", ["usr_game"]));
        Assert.Empty(task.ReferencedSkillIds);
    }

    [Fact]
    public void InactiveSkillCannotBeRetainedByContinuation()
    {
        Add("usr_game", "游戏", "GAME BODY");
        var task = new SkillTaskContinuity();
        task.BuildReference("游戏", ["usr_game"]);
        Assert.Equal("", task.BuildReference("继续", []));
        Assert.DoesNotContain("GAME BODY", task.BuildReference("继续", ["usr_game"]));
    }

    [Fact]
    public void ReferenceBodyAndIndexCannotTriggerAnotherSkill()
    {
        Add("usr_game", "游戏", "GAME BODY mentions 音乐");
        Add("usr_music", "音乐", "MUSIC BODY");
        var task = new SkillTaskContinuity();
        var first = task.BuildReference("游戏", ["usr_game", "usr_music"]);
        Assert.DoesNotContain("MUSIC BODY", first);
        Assert.DoesNotContain("MUSIC BODY", task.BuildReference("按这个做", ["usr_game", "usr_music"]));
    }

    [Fact]
    public void ReferenceBudgetTracksOnlyBodiesActuallyIncluded()
    {
        Add("usr_large", "测试", new string('L', 68000));
        Add("usr_extra", "测试", "EXTRA BODY" + new string('E', 4000));
        var task = new SkillTaskContinuity();
        var first = task.BuildReference("测试", ["usr_large", "usr_extra"]);
        Assert.True(first.Length <= 70000);
        Assert.DoesNotContain("EXTRA BODY", first);
        Assert.Equal(new[] { "usr_large" }, task.ReferencedSkillIds);
        var next = task.BuildReference("继续", ["usr_large", "usr_extra"]);
        Assert.True(next.Length <= 70000);
        Assert.DoesNotContain("EXTRA BODY", next);
    }

    [Fact]
    public void SessionsAndResetDoNotShareTaskReferences()
    {
        Add("usr_game", "游戏", "GAME BODY");
        var first = new SkillTaskContinuity();
        first.BuildReference("游戏", ["usr_game"]);
        Assert.DoesNotContain("GAME BODY", new SkillTaskContinuity().BuildReference("继续", ["usr_game"]));
        first.Reset();
        Assert.DoesNotContain("GAME BODY", first.BuildReference("继续", ["usr_game"]));
    }

    [Fact]
    public void DefaultBuilderRemainsStatelessAndBuiltinSkillIsExcluded()
    {
        Add("usr_game", "游戏", "GAME BODY");
        Add(AiAgentWorkflowSkill.Id, "游戏", "BUILTIN BODY");
        var direct = SkillHubDocument.BuildReference("游戏", ["usr_game", AiAgentWorkflowSkill.Id]);
        Assert.Contains("GAME BODY", direct);
        Assert.DoesNotContain("BUILTIN BODY", direct);
        Assert.DoesNotContain("GAME BODY", SkillHubDocument.BuildReference("继续", ["usr_game"]));
    }

    [Fact]
    public void RestoredConfirmedGoalProvidesReferenceForFollowUpAndNewGoalClearsIt()
    {
        Add("usr_game", "游戏", "GAME BODY");
        var task = new SkillTaskContinuity();
        task.RestoreFromGoal("在电脑上制作一个游戏", ["usr_game"]);
        Assert.Equal(new[] { "usr_game" }, task.ReferencedSkillIds);
        Assert.Contains("GAME BODY", task.BuildReference("继续", ["usr_game"]));
        Assert.DoesNotContain("GAME BODY", task.BuildReference("我想写一份周报", ["usr_game"]));
        Assert.Empty(task.ReferencedSkillIds);
    }

    [Fact]
    public void RestoreCannotKeepDisabledMissingOrUnmatchedSkill()
    {
        Add("usr_game", "游戏", "GAME BODY");
        var task = new SkillTaskContinuity();
        task.BuildReference("游戏", ["usr_game"]);
        task.RestoreFromGoal("游戏", []);
        Assert.Empty(task.ReferencedSkillIds);
        task.RestoreFromGoal("游戏", ["usr_missing"]);
        Assert.Empty(task.ReferencedSkillIds);
        task.BuildReference("游戏", ["usr_game"]);
        task.RestoreFromGoal("写一份周报", ["usr_game"]);
        Assert.Empty(task.ReferencedSkillIds);
        task.RestoreFromGoal("", ["usr_game"]);
        Assert.Empty(task.ReferencedSkillIds);
    }

    [Fact]
    public void ClearingTemporaryReferenceCannotRemoveUserTextThatLooksIdentical()
    {
        const string text = "## 本轮自定义技能参考\n<skill-reference id=\"usr_game\">BODY</skill-reference>";
        var reference = new ChatMessage(ChatRole.User, text);
        var user = new ChatMessage(ChatRole.User, text);
        var system = new ChatMessage(ChatRole.System, "Host rules");
        var history = new List<ChatMessage> { system, reference, user };
        AgentSession.RemoveCustomSkillReference(history, reference);
        Assert.Equal(new[] { system, user }, history);
        AgentSession.RemoveCustomSkillReference(history, reference);
        Assert.Equal(new[] { system, user }, history);
    }

    [Fact]
    public void SavingFiltersOnlyOwnedReferenceWithoutMutatingRuntimeHistory()
    {
        var reference = new ChatMessage(ChatRole.User, "CUSTOM BODY");
        var user = new ChatMessage(ChatRole.User, "CUSTOM BODY");
        var assistant = new ChatMessage(ChatRole.Assistant, "Answer");
        var history = new List<ChatMessage> { reference, user, assistant };
        var saveable = AgentSession.FilterCustomSkillReference(history, reference);
        Assert.Equal(new[] { user, assistant }, saveable);
        Assert.Equal(3, history.Count);
        var persisted = AgentMessageConverter.ToAiMessages(saveable);
        Assert.Equal(2, persisted.Count);
        Assert.Equal("CUSTOM BODY", persisted[0].Content);
        Assert.Equal("Answer", persisted[1].Content);
        Assert.Equal(history, AgentSession.FilterCustomSkillReference(history, null));
    }

    [Fact]
    public void HostBudgetIncludesSystemUserAndToolRoomAndMarksPartialBody()
    {
        Add("usr_game", "游戏", "GAME BODY\n" + new string('G', 50000));
        var system = new ChatMessage(ChatRole.System, new string('S', 8000));
        var user = new ChatMessage(ChatRole.User, new string('U', 1000));
        var budget = AgentRuntime.CustomSkillReferenceBudget([system], user);
        Assert.Equal(19000, budget);
        var task = new SkillTaskContinuity();
        var reference = task.BuildReference("游戏", ["usr_game"], maxChars: budget);
        Assert.True(reference.Length <= budget);
        Assert.Contains("GAME BODY", reference);
        Assert.Contains("正文因上下文预算截断", reference);
        Assert.Contains("不得假设已读完整", reference);
        Assert.Equal(new[] { "usr_game" }, task.ReferencedSkillIds);
        Assert.True(system.Text!.Length + user.Text!.Length + reference.Length + 12000 <=
            AgentRuntimeLimits.HistoryBudgetChars);
    }

    [Fact]
    public void RuntimeKeepsOwnedReferenceAndRealUserWithoutGivingSameTextProtection()
    {
        var system = new ChatMessage(ChatRole.System, new string('S', 8000));
        var reference = new ChatMessage(ChatRole.User, new string('R', 19000));
        var imitation = new ChatMessage(ChatRole.User, reference.Text);
        var user = new ChatMessage(ChatRole.User, new string('U', 1000));
        var history = new List<ChatMessage> { system, imitation, reference, user };
        AgentRuntime.TrimHistory(history, reference);
        Assert.Contains(reference, history);
        Assert.Contains(user, history);
        Assert.DoesNotContain(imitation, history);
        Assert.True(history.Sum(message => message.Text?.Length ?? 0) <= AgentRuntimeLimits.HistoryBudgetChars);
        Assert.Equal(1000, user.Text!.Length);
    }

    [Fact]
    public void ExhaustedSystemAndUserBudgetCannotClaimAnUnsentSkillBody()
    {
        Add("usr_game", "游戏", "GAME BODY");
        var system = new ChatMessage(ChatRole.System, new string('S', 30000));
        var user = new ChatMessage(ChatRole.User, new string('U', 1000));
        var budget = AgentRuntime.CustomSkillReferenceBudget([system], user);
        Assert.Equal(0, budget);
        var task = new SkillTaskContinuity();
        Assert.Equal("", task.BuildReference("游戏", ["usr_game"], maxChars: budget));
        Assert.Empty(task.ReferencedSkillIds);
    }

    [Fact]
    public void RestoringSameSelectedFlowIntoNewSessionReseedsOnce()
    {
        var selection = Guid.NewGuid().ToString("D");
        Assert.True(SkillTaskContinuity.ShouldRestoreConfirmedTask(null, null, "chat-a", selection));
        Assert.False(SkillTaskContinuity.ShouldRestoreConfirmedTask("chat-a", selection, "chat-a", selection));
        Assert.True(SkillTaskContinuity.ShouldRestoreConfirmedTask("chat-a", selection, "chat-b", selection));
        Assert.True(SkillTaskContinuity.ShouldRestoreConfirmedTask("chat-a", selection, "chat-a", Guid.NewGuid().ToString("D")));
        Assert.False(SkillTaskContinuity.ShouldRestoreConfirmedTask("chat-a", selection, null, selection));
    }

    [Fact]
    public void RetryWiringPreservesRawIntentAndTaskChoiceInsteadOfReclassifyingAttachments()
    {
        var source = PageSource("AiAgentPage.xaml.cs");
        Assert.Contains("var planIntentText = rawSkillInputOverride ?? text;", source);
        Assert.True(source.IndexOf("var planIntentText", StringComparison.Ordinal) <
            source.IndexOf("var attText = BuildAttachmentsText()", StringComparison.Ordinal));
        var snapshot = Between(source, "_lastUserText = text;", "_toolFlowRoundStart =");
        Assert.Contains("_lastRawSkillInput = planIntentText", snapshot);
        Assert.Contains("_lastContinuesCurrentTask = continuesCurrentTask", snapshot);
        var retry = Between(source, "private async void RetryButton_Click", "private void AddRestoredAssistant");
        Assert.Contains("SendAsync(_lastRawSkillInput ?? text", retry);
        Assert.Contains("continuesCurrentTask: _lastContinuesCurrentTask", retry);
        Assert.Contains("rawSkillInputOverride: _lastRawSkillInput", retry);
    }

    [Fact]
    public void ExplicitResumeAndLazySessionAttachBothSeedTheConfirmedGoal()
    {
        var task = PageSource("AiAgentPage.Task.cs");
        var flow = PageSource("AiAgentPage.ToolFlows.cs");
        var attach = Between(task, "private void AttachExplicitTaskToNewSession", "private void RestoreCustomSkillsForSelectedTask");
        Assert.Contains("RestoreCustomSkillsForSelectedTask(selection, force: true)", attach);
        var restore = Between(task, "private void RestoreCurrentToolFlowTask", "private void RefreshToolFlowTaskCard");
        Assert.Contains("ConversationGoalPresentation.HasNewTaskAfterSelection", restore);
        Assert.Contains("RestoreCustomSkillsForSelectedTask(selection)", restore);
        var resume = Between(flow, "private async Task ResumeToolFlowSelectionAsync", "private enum ToolFlowResumeAction");
        Assert.Contains("RestoreCustomSkillsForSelectedTask(selection, force: true)", resume);
        var seed = Between(task, "private void RestoreCustomSkillsForSelectedTask", "private void RestoreCurrentToolFlowTask");
        Assert.Contains("builtin.RestoreCustomSkillTask(selection.ProjectGoal)", seed);
        Assert.Contains("dsh.RestoreCustomSkillTask(selection.ProjectGoal)", seed);
    }

    [Theory]
    [InlineData("好")]
    [InlineData("好的。")]
    [InlineData("可以")]
    [InlineData("行！")]
    [InlineData("OK")]
    [InlineData("不确定")]
    public void CommonShortConfirmationsContinueOnlyExistingTaskReference(string input)
    {
        Add("usr_game", "游戏", "GAME BODY");
        Add("usr_music", "音乐", "MUSIC BODY");
        var task = new SkillTaskContinuity();
        task.BuildReference("游戏", ["usr_game", "usr_music"]);
        task.ObserveTaskInput(input);
        var reference = task.BuildReference(input, ["usr_game", "usr_music"]);
        Assert.Contains("GAME BODY", reference);
        Assert.DoesNotContain("MUSIC BODY", reference);
        Assert.Equal(new[] { "usr_game" }, task.ReferencedSkillIds);
        Assert.Contains("不能构成安装、付费或设备操作授权", reference);
        Assert.Contains("用户要求优先", reference);
    }

    [Fact]
    public void NewGoalObservationClearsOldReferenceEvenIfSetupIsCancelledBeforeInjection()
    {
        Add("usr_game", "游戏", "GAME BODY");
        Add("usr_music", "音乐", "MUSIC BODY");
        var task = new SkillTaskContinuity();
        task.BuildReference("游戏", ["usr_game", "usr_music"]);
        task.ObserveTaskInput("换个目标，生成音乐");
        // Setup was cancelled: no BuildReference for the new goal has run yet.
        Assert.Empty(task.ReferencedSkillIds);
        var next = task.BuildReference("继续", ["usr_game", "usr_music"]);
        Assert.DoesNotContain("GAME BODY", next);
        Assert.DoesNotContain("MUSIC BODY", next);
        Assert.Empty(task.ReferencedSkillIds);
    }

    private static string PageSource(string name) => File.ReadAllText(Path.Combine(FontSingleSourceTests.RepoRoot,
        "TubaWinUi3.WinUI3", "Pages", name));

    private static string Between(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }

    [Theory]
    [InlineData("继续！", true)]
    [InlineData("按这个做。", true)]
    [InlineData("go ahead", true)]
    [InlineData("我想了解“继续”是什么意思", false)]
    [InlineData("继续帮我写一份新的简历", false)]
    [InlineData("今天天气如何", false)]
    [InlineData("可以付费，优先低成本模型", false)]
    [InlineData("“好的”", false)]
    [InlineData("\"好的\"", false)]
    [InlineData("引用：好的", false)]
    [InlineData("好的，现在我想写一份新的简历", false)]
    public void AutomaticFollowUpDetectionIsConservative(string input, bool expected)
        => Assert.Equal(expected, SkillTaskContinuity.IsShortFollowUp(input));
}
