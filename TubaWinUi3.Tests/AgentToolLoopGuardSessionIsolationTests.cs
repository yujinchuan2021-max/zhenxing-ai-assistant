using System.Runtime.CompilerServices;
using TubaWinUi3.Services.Ai;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

[Collection("AgentToolRegistry")]
public sealed class AgentToolLoopGuardSessionIsolationTests
{
    private static AgentSession NewIdentity() =>
        (AgentSession)RuntimeHelpers.GetUninitializedObject(typeof(AgentSession));

    [Fact]
    public void DifferentSessionsDoNotShareDuplicatesProgressOrBlockedSearchCounts()
    {
        var original = AgentToolContext.Current;
        var first = NewIdentity();
        var second = NewIdentity();
        var args = AgentToolLoopGuard.NormalizeArgs("""{"path":"project/readme.txt"}""")!;
        try
        {
            AgentToolContext.Current = first;
            Assert.False(AgentToolLoopGuard.IsDuplicate("read_file", args));
            for (var i = 0; i < AgentToolLoopGuard.NoProgressThreshold; i++)
                AgentToolLoopGuard.ReportRound(false, true);
            Assert.True(AgentToolLoopGuard.ShouldInjectStopDirective);
            Assert.Equal(1, AgentToolLoopGuard.RegisterWebSearchBlocked());

            AgentToolContext.Current = second;
            Assert.False(AgentToolLoopGuard.IsDuplicate("read_file", args));
            Assert.Equal(0, AgentToolLoopGuard.ConsecutiveToolRounds);
            Assert.Equal(1, AgentToolLoopGuard.RegisterWebSearchBlocked());
            AgentToolLoopGuard.OnUserMessage();
            AgentToolLoopGuard.Reset();

            AgentToolContext.Current = first;
            Assert.True(AgentToolLoopGuard.IsDuplicate("read_file", args));
            Assert.Equal(AgentToolLoopGuard.NoProgressThreshold, AgentToolLoopGuard.ConsecutiveToolRounds);
            Assert.Equal(2, AgentToolLoopGuard.RegisterWebSearchBlocked());
            AgentToolLoopGuard.OnUserMessage();
            Assert.Equal(0, AgentToolLoopGuard.ConsecutiveToolRounds);
            Assert.True(AgentToolLoopGuard.IsDuplicate("read_file", args));
        }
        finally { AgentToolContext.Current = original; }
    }

    [Fact]
    public async Task ConcurrentAsyncSessionsRetainTheirOwnStateAndLeaveFallbackUntouched()
    {
        var original = AgentToolContext.Current;
        var args = AgentToolLoopGuard.NormalizeArgs("""{"query":"same goal"}""")!;
        try
        {
            AgentToolContext.Current = null;
            AgentToolLoopGuard.Reset();
            for (var i = 0; i < 3; i++) AgentToolLoopGuard.ReportRound(false, true);
            Assert.False(AgentToolLoopGuard.IsDuplicate("web_search", args));
            Assert.Equal(1, AgentToolLoopGuard.RegisterWebSearchBlocked());

            static async Task<(bool FirstAllowed, bool SecondBlocked, int Rounds, int Searches)> Run(
                AgentSession session, string normalizedArgs)
            {
                AgentToolContext.Current = session;
                var firstAllowed = !AgentToolLoopGuard.IsDuplicate("web_search", normalizedArgs);
                AgentToolLoopGuard.ReportRound(false, true);
                var searches = AgentToolLoopGuard.RegisterWebSearchBlocked();
                await Task.Yield();
                return (firstAllowed, AgentToolLoopGuard.IsDuplicate("web_search", normalizedArgs),
                    AgentToolLoopGuard.ConsecutiveToolRounds, searches);
            }

            var results = await Task.WhenAll(
                Task.Run(() => Run(NewIdentity(), args)),
                Task.Run(() => Run(NewIdentity(), args)));
            Assert.All(results, result =>
            {
                Assert.True(result.FirstAllowed);
                Assert.True(result.SecondBlocked);
                Assert.Equal(1, result.Rounds);
                Assert.Equal(1, result.Searches);
            });
            Assert.Null(AgentToolContext.Current);
            Assert.Equal(3, AgentToolLoopGuard.ConsecutiveToolRounds);
            Assert.True(AgentToolLoopGuard.IsDuplicate("web_search", args));
            Assert.Equal(2, AgentToolLoopGuard.RegisterWebSearchBlocked());
        }
        finally
        {
            AgentToolContext.Current = null;
            AgentToolLoopGuard.Reset();
            AgentToolContext.Current = original;
        }
    }
}
