using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TubaWinUi3.Services.AiNews;

namespace TubaWinUi3.Tests;

public sealed class AiNewsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    private static AiNewsItem Item(string id = "one") => new(id, "Official title", "Official facts", "Official source", "https://example.org/" + id, Now, "ai-models");
    private static string Body(string cursor = "") => JsonSerializer.Serialize(new { schemaVersion = 1,
        items = new[] { new { id = "one", title = "Official title", summary = "Official facts", source = new { name = "Official source" }, links = new { original = "https://example.org/one" }, publishedAt = (string?)null, category = "ai-models" } },
        page = new { hasMore = cursor.Length > 0, nextCursor = cursor.Length > 0 ? cursor : null } });
    private static string Summary(string id = "one") => JsonSerializer.Serialize(new { items = new[] { new { id, summary = "简短摘要", featured = true } } });

    [Fact] public void ParsePreservesUnknownDateAndValidatesLinksAndPagination()
    {
        var batch = AiNewsDocument.Parse(Body("next"), Now);
        Assert.Null(batch.Items[0].PublishedAt); Assert.Equal("next", batch.NextCursor);
        Assert.Throws<InvalidDataException>(() => AiNewsDocument.Parse(Body().Replace("https://example.org/one", "javascript:alert(1)"), Now));
        Assert.Throws<InvalidDataException>(() => AiNewsDocument.Parse(Body().Replace("\"hasMore\":false", "\"hasMore\":\"false\""), Now));
        Assert.Throws<InvalidDataException>(() => AiNewsDocument.Parse(Body().Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\""), Now));
    }

    [Fact] public void AiCannotReplaceOriginalIdentityTitleLinkOrSource()
    {
        var original = Item();
        var enriched = AiNewsEnricher.Apply([original], "{\"items\":[{\"id\":\"one\",\"summary\":\"摘要\",\"featured\":true,\"title\":\"invented\",\"originalUrl\":\"https://bad.example/\"}]}")[0];
        Assert.Equal(original.Title, enriched.Title); Assert.Equal(original.Source, enriched.Source);
        Assert.Equal(original.OriginalUrl, enriched.OriginalUrl); Assert.True(enriched.AiGenerated && enriched.Featured);
    }

    [Theory]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":[{\"id\":\"invented\",\"summary\":\"x\",\"featured\":true}]}")]
    [InlineData("{\"items\":[{\"id\":\"one\",\"summary\":\"\",\"featured\":true}]}")]
    [InlineData("{\"items\":[{\"id\":\"one\",\"summary\":\"x\",\"featured\":\"yes\"}]}")]
    public void InvalidAiResultIsRejected(string result) => Assert.Throws<InvalidDataException>(() => AiNewsEnricher.Apply([Item()], result));

    [Fact] public async Task NoConfigurationNeverCreatesModelClient()
    {
        int captures = 0;
        var session = new AiNewsFeedSession(new Reader((_, _) => Task.FromResult(new AiNewsBatch([Item()], "", Now))), new(), () => { captures++; return null; }, () => "zh-CN");
        session.Enter(); await session.LoadAsync(new());
        Assert.Equal(1, captures); Assert.Single(session.State.Items); Assert.Equal(AiNewsAiState.Unconfigured, session.State.AiState);
    }

    [Fact] public async Task BrokenConfigAndAiFailureKeepOfficialItems()
    {
        var reader = new Reader((_, _) => Task.FromResult(new AiNewsBatch([Item()], "", Now)));
        var broken = new AiNewsFeedSession(reader, new(), () => throw new IOException(), () => "zh-CN");
        broken.Enter(); await broken.LoadAsync(new()); Assert.Single(broken.State.Items); Assert.False(broken.State.Failed);
        var client = new FakeClient((_, _, _) => throw new HttpRequestException());
        var session = Session(reader, client); session.Enter(); await session.LoadAsync(new());
        Assert.Equal(AiNewsAiState.Failed, session.State.AiState); Assert.Equal("Official facts", session.State.Items[0].Summary);
    }

    [Fact] public async Task DuplicateRefreshUsesOneModelCallAndHasNoTools()
    {
        var completion = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient((_, options, _) => { Assert.Null(options!.Tools); Assert.Equal(ChatToolMode.None, options.ToolMode); return completion.Task; });
        var reader = new Reader((_, _) => Task.FromResult(new AiNewsBatch([Item()], "", Now)));
        var session = Session(reader, client); session.Enter(); var pending = session.LoadAsync(new());
        await session.LoadAsync(new()); Assert.Equal(1, client.Calls); Assert.Equal(1, reader.Calls);
        completion.SetResult(new(new ChatMessage(ChatRole.Assistant, Summary()))); await pending;
        Assert.Equal(AiNewsAiState.Ready, session.State.AiState);
    }

    [Fact] public async Task LateQueryAndLeaveResultsCannotOverwriteCurrentState()
    {
        var old = new TaskCompletionSource<AiNewsBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader((q, _) => q.Search == "old" ? old.Task : Task.FromResult(new AiNewsBatch([Item("new")], "", Now)));
        var session = new AiNewsFeedSession(reader, new(), () => null, () => "zh-CN"); session.Enter();
        var pending = session.LoadAsync(new(Search: "old")); await session.LoadAsync(new(Search: "new"));
        old.SetResult(new([Item("old")], "", Now)); await pending; Assert.Equal("new", session.State.Items[0].Id);
        var later = new TaskCompletionSource<AiNewsBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leave = new AiNewsFeedSession(new Reader((_, _) => later.Task), new(), () => null, () => "zh-CN"); leave.Enter();
        var loading = leave.LoadAsync(new()); leave.Leave(); later.SetResult(new([Item()], "", Now)); await loading;
        Assert.Empty(leave.State.Items); Assert.False(leave.State.Loading);
    }

    [Fact] public async Task LanguageIsCapturedBeforeModelAndLateOldLanguageIsDiscarded()
    {
        string language = "zh-CN";
        var batch = new TaskCompletionSource<AiNewsBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient((messages, _, _) => { Assert.Contains("en-US", messages.First().Text); called.SetResult(); return model.Task; });
        var session = Session(new Reader((_, _) => batch.Task), client, () => language); session.Enter(); var pending = session.LoadAsync(new());
        language = "en-US"; batch.SetResult(new([Item()], "", Now)); await called.Task;
        language = "zh-CN"; model.SetResult(new(new ChatMessage(ChatRole.Assistant, Summary()))); await pending;
        Assert.Equal(AiNewsAiState.Original, session.State.AiState); Assert.False(session.State.Items[0].AiGenerated);
    }

    [Fact] public async Task PaginationDeduplicatesAndFailurePreservesCursorAndDate()
    {
        bool fail = false;
        var reader = new Reader((q, _) => fail ? throw new IOException() : Task.FromResult(new AiNewsBatch(q.Cursor == "" ? [Item()] : [Item(), Item("two")], q.Cursor == "" ? "next" : "third", Now)));
        var session = new AiNewsFeedSession(reader, new(), () => null, () => "zh-CN"); session.Enter();
        await session.LoadAsync(new()); await session.LoadAsync(new(), more: true);
        Assert.Equal(2, session.State.Items.Count); fail = true; await session.LoadAsync(new(), more: true);
        Assert.Equal("third", session.State.NextCursor); Assert.Equal(Now, session.State.RetrievedAt); Assert.True(session.State.Failed);
        session.Leave(); session.Enter(); Assert.Equal(2, session.State.Items.Count);
    }

    [Fact] public async Task ReturningFromSettingsEnrichesExistingFeedWithoutResettingQueryOrPagination()
    {
        AiNewsModelSelection? selected = null;
        var client = new FakeClient((messages, _, _) =>
        {
            using var input = JsonDocument.Parse(messages.Last().Text);
            var rows = input.RootElement.EnumerateArray().Select(i => new { id = i.GetProperty("id").GetString(), summary = "摘要", featured = false });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new { items = rows }))));
        });
        var reader = new Reader((q, _) => Task.FromResult(new AiNewsBatch(q.Cursor == "" ? [Item()] : [Item("two")], q.Cursor == "" ? "next" : "third", Now)));
        var session = new AiNewsFeedSession(reader, new(), () => selected, () => "zh-CN");
        session.Enter(); await session.LoadAsync(new(Search: "goal")); await session.LoadAsync(session.State.Query, more: true);
        session.Leave(); selected = new(Guid.NewGuid().ToString(), () => client); await session.ResumeAsync();
        Assert.Equal(2, reader.Calls); Assert.Equal(1, client.Calls);
        Assert.Equal("goal", session.State.Query.Search); Assert.Equal("third", session.State.NextCursor); Assert.Equal(Now, session.State.RetrievedAt);
        Assert.All(session.State.Items, i => Assert.True(i.AiGenerated));
        session.Leave(); await session.ResumeAsync(); Assert.Equal(1, client.Calls);
        session.Leave(); selected = null; await session.ResumeAsync();
        Assert.All(session.State.Items, i => { Assert.False(i.AiGenerated); Assert.Equal("Official facts", i.Summary); });
        Assert.Equal(2, reader.Calls); Assert.Equal(AiNewsAiState.Unconfigured, session.State.AiState);
    }

    [Fact] public async Task LeavingDuringNextPageDoesNotDiscardAlreadyLoadedPagesOnReturn()
    {
        var pending = new TaskCompletionSource<AiNewsBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader((q, _) => q.Cursor == "third" ? pending.Task
            : Task.FromResult(new AiNewsBatch(q.Cursor == "" ? [Item()] : [Item("two")], q.Cursor == "" ? "next" : "third", Now, FromCache: true)));
        var session = new AiNewsFeedSession(reader, new(), () => null, () => "zh-CN");
        await session.ResumeAsync(); await session.LoadAsync(new(), more: true);
        var loading = session.LoadAsync(new(), more: true);
        session.Leave(); await session.ResumeAsync();
        Assert.Equal(3, reader.Calls); Assert.Equal(2, session.State.Items.Count);
        Assert.Equal("third", session.State.NextCursor); Assert.Equal(Now, session.State.RetrievedAt); Assert.True(session.State.FromCache);
        pending.SetResult(new([Item("late")], "fourth", Now)); await loading;
        Assert.Equal(2, session.State.Items.Count); Assert.Equal("third", session.State.NextCursor);
    }

    [Fact] public async Task ReenteringAfterAiFailureDoesNotRetryUntilConfigurationChanges()
    {
        var client = new FakeClient((_, _, _) => throw new HttpRequestException());
        string identity = Guid.NewGuid().ToString(), configuration = "before";
        var reader = new Reader((_, _) => Task.FromResult(new AiNewsBatch([Item()], "", Now)));
        var session = new AiNewsFeedSession(reader, new(), () => new(identity, () => client, configuration), () => "zh-CN");
        await session.ResumeAsync(); Assert.Equal(1, client.Calls);
        session.Leave(); await session.ResumeAsync(); Assert.Equal(1, client.Calls);
        session.Leave(); configuration = "after"; await session.ResumeAsync();
        Assert.Equal(2, client.Calls); Assert.Equal(1, reader.Calls); Assert.Equal("Official facts", session.State.Items[0].Summary);
    }

    [Fact] public async Task CacheFailureCannotRenewExpiryOrSaveApiCredentials()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zxai-news-tests-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var at = Now; bool fail = false; int calls = 0;
            using var http = new HttpClient(new Handler(() => { calls++; return new(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(Body()) }; }));
            var cache = Path.Combine(dir, "recent.json"); var reader = new AiNewsClient(http, new("https://example.org/v1/"), cache, () => at);
            await reader.ReadAsync(new(), true, default); string saved = File.ReadAllText(cache);
            at += TimeSpan.FromMinutes(5); fail = true; var fallback = await reader.ReadAsync(new(), true, default);
            Assert.True(fallback.FromCache); Assert.Equal(Now, fallback.RetrievedAt); Assert.Equal(saved, File.ReadAllText(cache));
            at += TimeSpan.FromHours(1); await Assert.ThrowsAsync<AiNewsUnavailableException>(() => reader.ReadAsync(new(), true, default));
            Assert.Equal(3, calls);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public async Task UserCancellationDoesNotBecomeOfflineFallback()
    {
        using var http = new HttpClient(new Handler(() => new(HttpStatusCode.OK) { Content = new StringContent(Body()) }));
        var reader = new AiNewsClient(http, new("https://example.org/v1/"));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(new(), false, canceled.Token));
    }

    private static AiNewsFeedSession Session(Reader reader, FakeClient client, Func<string>? language = null) =>
        new(reader, new(), () => new AiNewsModelSelection(Guid.NewGuid().ToString(), () => client), language ?? (() => "zh-CN"));
    private sealed class Reader(Func<AiNewsQuery, CancellationToken, Task<AiNewsBatch>> read) : IAiNewsReader
    { internal int Calls; public Task<AiNewsBatch> ReadAsync(AiNewsQuery q, bool force, CancellationToken token) { Calls++; return read(q, token); } }
    private sealed class Handler(Func<HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply()); }
    private sealed class FakeClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> reply) : IChatClient
    {
        internal int Calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; return reply(messages, options, cancellationToken); }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }
}
