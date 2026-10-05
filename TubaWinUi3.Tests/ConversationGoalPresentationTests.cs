using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ConversationGoalPresentationTests
{
    [Fact]
    public void EmptyChatHasNoGoal()
        => Assert.Null(ConversationGoalPresentation.Create(null, null, [], false, false));

    [Fact]
    public void AShortFollowupDoesNotReplaceTheOriginalRequest()
    {
        ToolFlowConversationMessage[] history = [Message("user", "准备 2D 游戏工具流"),
            Message("assistant", "请选择预算"), Message("user", "低成本"), Message("user", "继续")];
        var goal = ConversationGoalPresentation.Create(null, null, history, false, true)!;
        Assert.Equal("准备 2D 游戏工具流", goal.Goal);
        Assert.Equal(ConversationGoalStage.AnsweringQuestion, goal.Stage);
        Assert.False(goal.IsConfirmed);
    }

    [Fact]
    public void ARestoredSelectionRemainsTheConfirmedGoal()
    {
        var goal = ConversationGoalPresentation.Create("生成音乐", "开发游戏",
            [Message("user", "试一下")], false, true)!;
        Assert.Equal("生成音乐", goal.Goal);
        Assert.True(goal.IsConfirmed);
        Assert.Equal(ConversationGoalStage.Preparing, goal.Stage);
    }

    [Fact]
    public void ACompletedProposalIsOnlyAProposedGoal()
    {
        var goal = ConversationGoalPresentation.Create(null, "准备安卓开发环境",
            [Message("user", "我想做一个 APP")], false, false)!;
        Assert.Equal("我想做一个 APP", goal.Goal);
        Assert.Equal(ConversationGoalStage.ChoosingPlan, goal.Stage);
        Assert.False(goal.IsConfirmed);
    }

    [Fact]
    public void WorkingOnTheReplyTakesPrecedenceOverAnEarlierQuestion()
    {
        var goal = ConversationGoalPresentation.Create(null, "开发 APP",
            [Message("user", "做 APP")], true, true)!;
        Assert.Equal(ConversationGoalStage.Understanding, goal.Stage);
    }

    [Fact]
    public void ProtocolReceiptsCannotBecomeTheVisibleGoal()
    {
        var goal = ConversationGoalPresentation.Create(null, null,
            [Message("user", "[TOOL_RESULT] internal"), Message("user", "[ACTION_CONFIRMED] internal"),
             Message("user", "  生成一首音乐  ")], false, false)!;
        Assert.Equal("生成一首音乐", goal.Goal);
    }

    [Fact]
    public void AnAssistantProposalCannotExpandTheUsersVisibleGoal()
    {
        var goal = ConversationGoalPresentation.Create(null, "搭建编曲、混音和音乐制作工作室",
            [Message("user", "我只想 AI 生成音乐")], false, false)!;
        Assert.Equal("我只想 AI 生成音乐", goal.Goal);
        Assert.Equal(ConversationGoalStage.ChoosingPlan, goal.Stage);
        Assert.False(goal.IsConfirmed);
    }

    private static ToolFlowConversationMessage Message(string role, string content)
        => new() { Role = role, Content = content };

    [Fact]
    public void ExplicitNewTaskBecomesTheAnchorWhileShortRepliesStayInThatTask()
    {
        ToolFlowConversationMessage[] history = [Message("user", "准备游戏工具"),
            Message("assistant", "游戏方案"), Message("user", "换个目标，我要 AI 生成音乐"),
            Message("assistant", "请选择预算"), Message("user", "低成本")];
        var goal = ConversationGoalPresentation.Create(null, null, history, false, true)!;
        Assert.Equal("换个目标，我要 AI 生成音乐", goal.Goal);
        Assert.Equal(2, ConversationGoalPresentation.FindExplicitNewTaskStart(history));
        Assert.False(ConversationGoalPresentation.StartsExplicitNewTask("预算可以改成做低成本吗？"));
        Assert.False(ConversationGoalPresentation.StartsExplicitNewTask("他说‘新目标’，是什么意思？"));
    }

    [Fact]
    public void OlderConfirmedTaskIsHiddenOnlyAfterAnExplicitChangeInTheSameHistory()
    {
        ToolFlowConversationMessage[] snapshot = [Message("user", "生成音乐"), Message("assistant", "音乐方案")];
        var selection = new ToolFlowSelection
        {
            FlowId = "goal", SubmissionId = "goal", Origin = ToolFlowOrigin.User,
            SelectedAtUtc = DateTimeOffset.UtcNow, GoalDescription = "生成音乐", FlowText = "方案",
            UploadEnabledAtSelection = false, Conversation = snapshot.ToList(), Items = [],
        };
        Assert.False(ConversationGoalPresentation.HasNewTaskAfterSelection(selection,
            [.. snapshot, Message("user", "继续")]));
        Assert.True(ConversationGoalPresentation.HasNewTaskAfterSelection(selection,
            [.. snapshot, Message("user", "新目标：做图片")]));
        Assert.False(ConversationGoalPresentation.HasNewTaskAfterSelection(selection,
            [Message("user", "其他会话"), Message("user", "新目标：做图片")]));
    }
}
