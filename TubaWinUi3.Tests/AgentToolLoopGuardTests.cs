using TubaWinUi3.Services.Ai;
using Xunit;

namespace TubaWinUi3.Tests;

/// <summary>
/// Direct tests for the loop guard used by AgentRuntime.
/// Runtime execution and result handling remain covered by AgentRuntimeLoopGuardTests.
/// </summary>
[Collection("AgentToolRegistry")]
public sealed class AgentToolLoopGuardTests : IDisposable
{
    public AgentToolLoopGuardTests() => AgentToolLoopGuard.Reset();

    public void Dispose() => AgentToolLoopGuard.Reset();

    [Fact]
    public void SameToolAndArgs_SecondCall_IsDuplicate()
    {
        var args = AgentToolLoopGuard.NormalizeArgs("""{"query":"显卡价格"}""")!;

        Assert.False(AgentToolLoopGuard.IsDuplicate("web_search", args));
        Assert.True(AgentToolLoopGuard.IsDuplicate("web_search", args));
    }

    [Fact]
    public void ReorderedArgsKeys_TreatedAsSameSignature()
    {
        var first = AgentToolLoopGuard.NormalizeArgs("""{"a":1,"b":2}""")!;
        var reordered = AgentToolLoopGuard.NormalizeArgs("""{"b":2,"a":1}""")!;

        Assert.Equal(first, reordered);
        Assert.False(AgentToolLoopGuard.IsDuplicate("launch_tool", first));
        Assert.True(AgentToolLoopGuard.IsDuplicate("launch_tool", reordered));
    }

    [Fact]
    public void DifferentArgs_AreNotDuplicates()
    {
        var first = AgentToolLoopGuard.NormalizeArgs("""{"url":"https://a.com"}""")!;
        var second = AgentToolLoopGuard.NormalizeArgs("""{"url":"https://b.com"}""")!;

        Assert.False(AgentToolLoopGuard.IsDuplicate("browser_navigate", first));
        Assert.False(AgentToolLoopGuard.IsDuplicate("browser_navigate", second));
    }

    [Fact]
    public void DifferentToolsWithSameArgs_AreNotDuplicates()
    {
        var args = AgentToolLoopGuard.NormalizeArgs("""{"path":"file.txt"}""")!;

        Assert.False(AgentToolLoopGuard.IsDuplicate("read_file", args));
        Assert.False(AgentToolLoopGuard.IsDuplicate("write_file", args));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("{")]
    public void EmptyNonObjectOrInvalidArgs_AreExemptFromDedup(string? json)
        => Assert.Null(AgentToolLoopGuard.NormalizeArgs(json));

    [Fact]
    public void NestedObjectKeyOrder_NormalizesButArrayOrderRemainsSignificant()
    {
        var first = AgentToolLoopGuard.NormalizeArgs("""{"items":[{"b":2,"a":1},3]}""");
        var reorderedKeys = AgentToolLoopGuard.NormalizeArgs("""{"items":[{"a":1,"b":2},3]}""");
        var reorderedArray = AgentToolLoopGuard.NormalizeArgs("""{"items":[3,{"a":1,"b":2}]}""");

        Assert.Equal(first, reorderedKeys);
        Assert.NotEqual(first, reorderedArray);
    }

    [Fact]
    public void Reset_ClearsSeenCalls()
    {
        var args = AgentToolLoopGuard.NormalizeArgs("""{"query":"显卡价格"}""")!;
        Assert.False(AgentToolLoopGuard.IsDuplicate("web_search", args));
        Assert.True(AgentToolLoopGuard.IsDuplicate("web_search", args));

        AgentToolLoopGuard.Reset();

        Assert.False(AgentToolLoopGuard.IsDuplicate("web_search", args));
    }

    [Fact]
    public void ReportRound_PureToolRounds_IncrementUntilThreshold()
    {
        Assert.False(AgentToolLoopGuard.ShouldInjectStopDirective);
        for (var i = 1; i < AgentToolLoopGuard.NoProgressThreshold; i++)
        {
            AgentToolLoopGuard.ReportRound(hadUserText: false, hadToolCalls: true);
            Assert.False(AgentToolLoopGuard.ShouldInjectStopDirective);
        }

        AgentToolLoopGuard.ReportRound(hadUserText: false, hadToolCalls: true);

        Assert.True(AgentToolLoopGuard.ShouldInjectStopDirective);
        Assert.Equal(AgentToolLoopGuard.NoProgressThreshold, AgentToolLoopGuard.ConsecutiveToolRounds);
    }

    [Fact]
    public void ReportRound_UserTextOrEmptyRound_ResetsProgress()
    {
        AgentToolLoopGuard.ReportRound(false, true);
        AgentToolLoopGuard.ReportRound(false, true);
        AgentToolLoopGuard.ReportRound(true, true);
        Assert.Equal(0, AgentToolLoopGuard.ConsecutiveToolRounds);

        AgentToolLoopGuard.ReportRound(false, true);
        AgentToolLoopGuard.ReportRound(false, false);
        Assert.Equal(0, AgentToolLoopGuard.ConsecutiveToolRounds);
    }

    [Fact]
    public void ReportRound_Reset_ClearsProgress()
    {
        for (var i = 0; i < AgentToolLoopGuard.NoProgressThreshold; i++)
            AgentToolLoopGuard.ReportRound(false, true);
        Assert.True(AgentToolLoopGuard.ShouldInjectStopDirective);

        AgentToolLoopGuard.Reset();

        Assert.False(AgentToolLoopGuard.ShouldInjectStopDirective);
        Assert.Equal(0, AgentToolLoopGuard.ConsecutiveToolRounds);
    }

    [Fact]
    public void WebSearchBlocked_CountsAndResets()
    {
        Assert.Equal(1, AgentToolLoopGuard.RegisterWebSearchBlocked());
        Assert.Equal(2, AgentToolLoopGuard.RegisterWebSearchBlocked());

        AgentToolLoopGuard.Reset();

        Assert.Equal(1, AgentToolLoopGuard.RegisterWebSearchBlocked());
    }

    [Fact]
    public void UserMessage_ClearsOnlyProgressAndPreservesDuplicateCalls()
    {
        var args = AgentToolLoopGuard.NormalizeArgs("""{"query":"显卡价格"}""")!;
        Assert.False(AgentToolLoopGuard.IsDuplicate("web_search", args));
        AgentToolLoopGuard.ReportRound(false, true);
        Assert.Equal(1, AgentToolLoopGuard.RegisterWebSearchBlocked());

        AgentToolLoopGuard.OnUserMessage();

        Assert.Equal(0, AgentToolLoopGuard.ConsecutiveToolRounds);
        Assert.True(AgentToolLoopGuard.IsDuplicate("web_search", args));
        Assert.Equal(2, AgentToolLoopGuard.RegisterWebSearchBlocked());
    }
}
