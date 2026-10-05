using TubaWinUi3.Services.Ai;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

public sealed class ChatConversationCacheTests
{
    [Fact]
    public async Task SwitchingViewsKeepsTheFirstReplyRunningAndItsOutputSeparate()
    {
        var root = "fake-chat-root-" + Guid.NewGuid().ToString("N");
        var first = new FakeView(root, "first") { Draft = "keep my draft" };
        first.Attachments.Add("first-image.png");
        var second = new FakeView(root, "second") { Draft = "second draft" };
        second.Attachments.Add("second.pdf");
        var cache = NewCache();
        try
        {
            Assert.True(cache.Activate(first));
            var firstRun = first.Session.SendAsync("first goal");
            first.Session.Emit("first partial");
            Assert.True(cache.Activate(second));
            var secondRun = second.Session.SendAsync("second goal");
            first.Session.Emit("first final");
            first.Session.Finish();
            await firstRun;

            Assert.Same(second, cache.Active);
            Assert.False(first.Session.IsRunning);
            Assert.True(second.Session.IsRunning);
            Assert.Equal(0, first.Session.CancelCount);
            Assert.Equal(0, first.Session.DisposeCount);
            Assert.Equal(["first partial", "first final"], first.Reply);
            Assert.Empty(second.Reply);
            Assert.Equal("second draft", second.Draft);
            Assert.Equal(["second.pdf"], second.Attachments);
            Assert.True(cache.Activate(cache.Find("first")!));
            Assert.Same(first, cache.Active);
            Assert.Equal("keep my draft", first.Draft);
            Assert.Equal(["first-image.png"], first.Attachments);
            second.Session.Finish();
            await secondRun;
        }
        finally { cache.Close(view => view.Close()); }
    }

    [Fact]
    public void DraftIdentityChangesOnFirstSendWithoutCreatingAnotherCachedView()
    {
        var view = new FakeView("fake-root-" + Guid.NewGuid(), "draft-one") { Draft = "unsent goal" };
        var cache = NewCache();
        try
        {
            cache.Activate(view);
            Assert.Same(view, cache.Find("draft-one"));
            view.Id = "persisted-first-send";
            Assert.Null(cache.Find("draft-one"));
            Assert.Same(view, cache.Find("persisted-first-send"));
            Assert.Single(cache.Views);
            Assert.Equal("unsent goal", view.Draft);
        }
        finally { cache.Close(item => item.Close()); }
    }

    [Fact]
    public void PendingPermissionStaysWithItsConversationAndDoesNotConfirmWhenHidden()
    {
        var first = new FakeView("fake-root-" + Guid.NewGuid(), "first") { AwaitingConfirmation = true };
        var second = new FakeView("fake-root-" + Guid.NewGuid(), "second");
        var cache = NewCache();
        try
        {
            cache.Activate(first);
            cache.Activate(second);
            Assert.False(second.AwaitingConfirmation);
            Assert.True(first.AwaitingConfirmation);
            Assert.Equal(0, first.Session.ResumeCount);
            cache.Activate(cache.Find("first")!);
            Assert.True(cache.Active!.AwaitingConfirmation);
            Assert.Equal(0, first.Session.CancelCount);
        }
        finally { cache.Close(view => view.Close()); }
    }

    [Fact]
    public void DifferentRunningConversationsRetainIndependentOwnershipUntilWindowCloses()
    {
        var root = "fake-root-" + Guid.NewGuid().ToString("N");
        var first = new FakeView(root, "first");
        var second = new FakeView(root, "second");
        var otherWindow = new ConversationLease(root);
        var cache = NewCache();
        try
        {
            cache.Activate(first);
            Assert.True(first.Lease.EnsureOwned(first.Id));
            cache.Activate(second);
            Assert.True(second.Lease.EnsureOwned(second.Id));
            Assert.False(otherWindow.CanTake(first.Id));
            Assert.False(otherWindow.CanTake(second.Id));
            cache.Close(view => view.Close());
            Assert.True(otherWindow.CanTake(first.Id));
            Assert.True(otherWindow.CanTake(second.Id));
            Assert.False(first.Lease.EnsureOwned(first.Id));
            Assert.False(second.Lease.EnsureOwned(second.Id));
            Assert.False(cache.Activate(first));
            first.Session.Emit("late output after close");
            Assert.Empty(first.Reply);
        }
        finally { cache.Close(view => view.Close()); otherWindow.Close(); }
    }

    [Fact]
    public void ActivatingTheSameViewRepeatedlyDoesNotDuplicateOrDisposeIt()
    {
        var page = new FakeView("fake-root-" + Guid.NewGuid(), "one");
        var cache = NewCache();
        cache.Activate(page);
        cache.Activate(page);
        Assert.Single(cache.Views);
        cache.Close(view => view.Close());
        cache.Close(view => view.Close());
        Assert.Equal(1, page.Session.DisposeCount);
        Assert.Null(cache.Active);
        Assert.Empty(cache.Views);
    }

    [Fact]
    public void FailureClosingOneViewDoesNotLeaveOtherViewsAlive()
    {
        var root = "fake-root-" + Guid.NewGuid();
        var first = new FakeView(root, "first");
        var second = new FakeView(root, "second");
        var cache = NewCache();
        cache.Activate(first);
        cache.Activate(second);
        cache.Close(view =>
        {
            view.Close();
            if (ReferenceEquals(view, first)) throw new IOException("simulated shutdown failure");
        });
        Assert.Equal(1, first.Session.DisposeCount);
        Assert.Equal(1, second.Session.DisposeCount);
        Assert.True(cache.IsClosed);
    }

    [Fact]
    public void DeleteRequiresLeavingTheActiveViewAndDoesNotRemoveAnUnrelatedDraft()
    {
        var root = "fake-root-" + Guid.NewGuid();
        var first = new FakeView(root, "first");
        var draft = new FakeView(root, "draft") { Draft = "not sent" };
        var cache = NewCache();
        try
        {
            cache.Activate(first);
            Assert.False(cache.Remove(first));
            cache.Activate(draft);
            Assert.True(cache.Remove(first));
            first.Close();
            Assert.Same(draft, cache.Active);
            Assert.Equal("not sent", draft.Draft);
            Assert.Equal(0, draft.Session.DisposeCount);
        }
        finally { cache.Close(view => view.Close()); }
    }

    private static ChatConversationCache<FakeView> NewCache() => new(view => view.Id);

    [Fact]
    public void CacheLimitEvictsOnlyIdleViewsAndKeepsActiveDraftAndPendingPermission()
    {
        var root = "fake-root-" + Guid.NewGuid();
        var idle = new FakeView(root, "idle");
        var draft = new FakeView(root, "draft") { Draft = "unsent" };
        var pending = new FakeView(root, "pending") { AwaitingConfirmation = true };
        var active = new FakeView(root, "active");
        var cache = NewCache();
        try
        {
            foreach (var view in new[] { idle, draft, pending, active }) cache.Activate(view);
            cache.Trim(2, view => view.Draft.Length == 0 && !view.AwaitingConfirmation, view => view.Close());
            Assert.Equal(1, idle.Session.DisposeCount);
            Assert.Equal(0, draft.Session.DisposeCount);
            Assert.Equal(0, pending.Session.DisposeCount);
            Assert.Equal(0, active.Session.DisposeCount);
            Assert.Equal(3, cache.Views.Count); // Keep protected views even when temporarily above the cap.
            Assert.Same(active, cache.Active);
        }
        finally { cache.Close(view => view.Close()); }
    }

    [Fact]
    public void FailedEvictionKeepsTheViewAndCanEvictAnotherSafePeer()
    {
        var root = "fake-root-" + Guid.NewGuid();
        var failing = new FakeView(root, "failing");
        var idle = new FakeView(root, "idle");
        var active = new FakeView(root, "active");
        var cache = NewCache();
        try
        {
            cache.Activate(failing);
            cache.Activate(idle);
            cache.Activate(active);
            cache.Trim(2, _ => true, view =>
            {
                if (ReferenceEquals(view, failing)) throw new IOException("cannot close");
                view.Close();
            });
            Assert.Same(failing, cache.Find("failing"));
            Assert.Null(cache.Find("idle"));
            Assert.Same(active, cache.Active);
        }
        finally { cache.Close(view => view.Close()); }
    }

    private sealed class FakeView
    {
        internal string Id { get; set; }
        internal string Draft { get; set; } = "";
        internal List<string> Attachments { get; } = [];
        internal List<string> Reply { get; } = [];
        internal bool AwaitingConfirmation { get; set; }
        internal ConversationLease Lease { get; }
        internal FakeSession Session { get; }

        internal FakeView(string root, string id)
        {
            Id = id;
            Lease = new ConversationLease(root);
            Session = new FakeSession(id);
            Session.TextChunk += text => { if (!Lease.IsClosed) Reply.Add(text); };
        }

        internal void Close() { Lease.Close(); Session.Dispose(); }
    }

    private sealed class FakeSession(string id) : IAgentSession
    {
        private TaskCompletionSource? _completion;
        public string Id => id;
        public string Title { get; private set; } = "fake conversation";
        public bool IsRunning { get; private set; }
        public string PersonaId => "default";
        public AgentSendOutcome LastSendOutcome { get; private set; } = AgentSendOutcome.Rejected;
        public int TotalPromptTokens => 0;
        public int TotalCompletionTokens => 0;
        public int TotalCacheHitTokens => 0;
        public int TotalCacheMissTokens => 0;
        public IReadOnlyCollection<string> ActiveSkillIds => [];
        internal int CancelCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal int ResumeCount { get; private set; }
        public event Action<string>? TextChunk;
        public event Action<string>? ReasoningChunk { add { } remove { } }
        public event Action<AgentStep>? StepStarted { add { } remove { } }
        public event Action<AgentStep>? StepCompleted { add { } remove { } }
        public event Action<IReadOnlyList<AgentConfirmationRequest>>? ConfirmationsRequested { add { } remove { } }
        public event Action<string>? Error { add { } remove { } }
        public event Action? RunCompleted;
        public event Action? RoundStarted;
        public event Action<AgentStepGroupSummary>? StepGroupCompleted { add { } remove { } }

        public Task SendAsync(string userText, IReadOnlyList<(byte[] Bytes, string MediaType)>? images = null)
        {
            IsRunning = true;
            _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoundStarted?.Invoke();
            return _completion.Task;
        }
        internal void Emit(string text) => TextChunk?.Invoke(text);
        internal void Finish()
        {
            IsRunning = false;
            LastSendOutcome = AgentSendOutcome.Completed;
            RunCompleted?.Invoke();
            _completion?.TrySetResult();
        }
        public Task ResumeConfirmationsAsync(IReadOnlyList<AgentConfirmationDecision> decisions)
        { ResumeCount++; return Task.CompletedTask; }
        public void Cancel() { CancelCount++; IsRunning = false; _completion?.TrySetCanceled(); }
        public void Dispose() { DisposeCount++; IsRunning = false; _completion?.TrySetCanceled(); }
        public void SetSkillEnabled(string skillId, bool enabled) { }
        public void SetPersona(string personaId) { }
        public void Rename(string title) => Title = title;
        public void Save() { }
    }
}
