using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

public sealed class AgentSessionOwnershipSaveTests
{
    [Fact]
    public void RejectedOwnershipIsCheckedBeforeAnyProtocolOrDisplaySerialization()
    {
        // No constructor, settings, history paths or filesystem are needed here.
        // A poisoned history would fail serialization before the old late guard.
        var session = (AgentSession)RuntimeHelpers.GetUninitializedObject(typeof(AgentSession));
        typeof(AgentSession).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, new List<ChatMessage> { null! });
        var guardCalls = 0;
        session.SaveGuard = _ => { guardCalls++; return false; };
        session.Save();
        Assert.Equal(1, guardCalls);
    }

    [Fact]
    public void ReadbackCheckRequiresTheEntireVisibleDisplayIncludingReasoningAndStepSummary()
    {
        var session = (AgentSession)RuntimeHelpers.GetUninitializedObject(typeof(AgentSession));
        var memory = new List<ConversationDisplayItem>
        {
            new() { Type = "text", Role = "assistant", Content = "done", ReasoningContent = "reason" },
            new() { Type = "steps", SummaryText = "installed one tool" },
        };
        typeof(AgentSession).GetField("_displayItems", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, memory);
        Assert.True(session.MatchesPersistedDisplay(memory));
        Assert.False(session.MatchesPersistedDisplay(
        [
            new() { Type = "text", Role = "assistant", Content = "done", ReasoningContent = "old reason" },
            new() { Type = "steps", SummaryText = "previous step" },
        ]));
    }
}
