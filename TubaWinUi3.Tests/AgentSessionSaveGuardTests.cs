using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

public sealed class AgentSessionSaveGuardTests
{
    [Fact]
    public void ClosedOwnerCannotWriteProtocolMetadataOrDisplayDuringSaveOrDispose()
    {
        // This command's fresh test data root is required before any settings/store initialization.
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        using var session = AgentSession.CreateNew();
        var calls = 0;
        session.SaveGuard = _ => { calls++; return false; };
        session.Save();
        session.Dispose();
        Assert.True(calls >= 2);
        Assert.DoesNotContain(AiAssistantService.ListConversations(), meta => meta.Id == session.Id);
        Assert.Empty(AiAssistantService.LoadConversation(session.Id));
        Assert.Empty(AiAssistantService.LoadConversationDisplay(session.Id));
    }
}
