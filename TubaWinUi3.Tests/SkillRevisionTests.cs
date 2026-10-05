using System.Net;
using System.Text;
using System.Text.Json;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

public sealed class SkillRevisionTests : IDisposable
{
    private const string Official = "---\nname: zhenxing-assistant\ndescription: Original\n---\nOfficial instructions.\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-skill-revision-" + Guid.NewGuid().ToString("N"));
    private SkillRevisionStore Store => new(_root, Official, "0.1-test");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void DraftSavePreservesRetryIdentityAndDoesNotApply()
    {
        var first = Store.SaveDraft("Ask one useful question.\n", "Short questions");
        var retry = Store.SaveDraft(first.EditedBody, first.ChangeSummary);
        Assert.Equal(first, retry);
        Assert.Equal(first, Store.LoadDraft());
        Assert.Equal(Official, Store.ReadEffectiveDocument());
        Assert.False(Store.HasLocalTrial);
        Assert.NotEqual(first.SubmissionId, Store.SaveDraft("Different instructions.\n", "Short questions").SubmissionId);
    }

    [Fact]
    public void LocalTrialIsSeparateFromDraftAndReceipt()
    {
        var applied = Store.SaveDraft("Keep answers short.\n", "Shorter");
        Store.ApplyLocal(applied);
        Assert.Equal(applied.ModifiedDocument, Store.ReadEffectiveDocument());
        var laterDraft = Store.SaveDraft("Ask about the target.\n", "Target");
        Store.SaveReceipt(laterDraft, new(laterDraft.SubmissionId, laterDraft.ModifiedSha256, "pending", DateTimeOffset.UtcNow));
        Assert.Equal(applied.ModifiedDocument, Store.ReadEffectiveDocument());
        Assert.Equal("pending", Store.ReadReceipt(laterDraft)!.Status);
        Store.RestoreOfficial();
        Assert.Equal(Official, Store.ReadEffectiveDocument());
        Assert.Equal(laterDraft, Store.LoadDraft());
    }

    [Fact]
    public void FailedProjectionRollsBackApplyAndRestore()
    {
        var first = Store.SaveDraft("First local version.\n", "first");
        Assert.Throws<IOException>(() => Store.ApplyLocal(first, () => throw new IOException("fake projection failure")));
        Assert.False(Store.HasLocalTrial);
        Store.ApplyLocal(first);
        var second = Store.SaveDraft("Second local version.\n", "second");
        Assert.Throws<IOException>(() => Store.ApplyLocal(second, () => throw new IOException("fake projection failure")));
        Assert.Equal(first.ModifiedDocument, Store.ReadEffectiveDocument());
        Assert.Throws<IOException>(() => Store.RestoreOfficial(() => throw new IOException("fake projection failure")));
        Assert.Equal(first.ModifiedDocument, Store.ReadEffectiveDocument());
    }

    [Fact]
    public void CurrentOfficialHeaderIsRetainedAcrossLocalTrialUpgrade()
    {
        var draft = Store.SaveDraft("Local instructions.\n", "local");
        Store.ApplyLocal(draft);
        var updated = new SkillRevisionStore(_root, Official.Replace("Original", "Updated"), "0.2-test");
        var effective = SkillRevisionDocument.Split(updated.ReadEffectiveDocument());
        Assert.Contains("description: Updated", effective.Header);
        Assert.Equal(draft.EditedBody, effective.Body);
    }

    [Fact]
    public void FrozenIdentityHashesAndHeaderAreValidated()
    {
        var draft = Store.SaveDraft("Modified\n", "change");
        Assert.Throws<InvalidDataException>(() => SkillRevisionDocument.Validate(draft with { SkillId = "other" }));
        Assert.Throws<InvalidDataException>(() => SkillRevisionDocument.Validate(draft with { BaseSha256 = new string('0', 64) }));
        var changed = draft.ModifiedDocument.Replace("Original", "Changed");
        Assert.Throws<InvalidDataException>(() => SkillRevisionDocument.Validate(draft with
        { ModifiedDocument = changed, ModifiedSha256 = SkillRevisionDocument.Hash(changed) }));
        Assert.Throws<InvalidDataException>(() => SkillRevisionDocument.Validate(draft with { ChangeSummary = null! }));
        Assert.Throws<InvalidDataException>(() => SkillRevisionDocument.Split("---"));
        Assert.Throws<InvalidDataException>(() => Store.SaveDraft(new string('字', 50000), "too long"));
        Assert.Throws<InvalidDataException>(() => Store.SaveDraft("\n ", "empty"));
    }

    [Fact]
    public void BuiltinAndDshReadNewTrialAndRestoreFromSameDataRoot()
    {
        var previous = DataRoots.TestRootOverrideForTest;
        try
        {
            DataRoots.TestRootOverrideForTest = _root;
            var actual = new SkillRevisionStore(_root);
            var draft = actual.SaveDraft("UNIQUE LOCAL TRIAL INSTRUCTIONS\n", "Trial");
            actual.ApplyLocal(draft, () => AiAgentWorkflowSkill.WriteDshProjection(_root));
            Assert.Equal(draft.EditedBody.Trim(), AiAgentWorkflowSkill.EffectiveBody);
            var path = Path.Combine(_root, "dsh-home", "zxai-bundled-skills", "zhenxing-assistant", "SKILL.md");
            Assert.Equal(AiAgentWorkflowSkill.EffectiveDocument, File.ReadAllText(path));
            actual.RestoreOfficial(() => AiAgentWorkflowSkill.WriteDshProjection(_root));
            Assert.Equal(AiAgentWorkflowSkill.Body, AiAgentWorkflowSkill.EffectiveBody);
            Assert.Equal(AiAgentWorkflowSkill.Document, File.ReadAllText(path));
        }
        finally { DataRoots.TestRootOverrideForTest = previous; }
    }

    [Fact]
    public void UnreadableLocalTrialFallsBackToOfficialAndCorruptReceiptIsIgnored()
    {
        var previous = DataRoots.TestRootOverrideForTest;
        try
        {
            DataRoots.TestRootOverrideForTest = _root;
            var path = Path.Combine(_root, "AiAssistant", "SkillRevisions", "local-trial.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not a skill");
            Assert.Equal(AiAgentWorkflowSkill.Document, AiAgentWorkflowSkill.EffectiveDocument);
            var draft = Store.SaveDraft("New body\n", "change");
            var receipt = Path.Combine(_root, "AiAssistant", "SkillRevisions", "Receipts", draft.SubmissionId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
            File.WriteAllText(receipt, "{ broken }");
            Assert.Null(Store.ReadReceipt(draft));
        }
        finally { DataRoots.TestRootOverrideForTest = previous; }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("accepted")]
    [InlineData("rejected")]
    public async Task SubmitSendsOnlyFrozenSkillAndAcceptsMatchingReceipt(string status)
    {
        var draft = Store.SaveDraft("Short instructions\n", "shorter");
        using var handler = new FakeHandler(async request =>
        {
            Assert.Equal("https://example.test/api/toolflows/v1/skill-revisions", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            var json = await request.Content!.ReadAsStringAsync();
            var sent = JsonSerializer.Deserialize<SkillRevisionDraft>(json, SkillRevisionStore.JsonOptions)!;
            Assert.Equal(draft, sent);
            using var body = JsonDocument.Parse(json);
            Assert.Equal(11, body.RootElement.EnumerateObject().Count());
            Assert.DoesNotContain("conversation", json, StringComparison.OrdinalIgnoreCase);
            return Receipt(draft, status);
        });
        using var http = new HttpClient(handler);
        var receipt = await new SkillRevisionUploadClient(http, new("https://example.test/api/toolflows/" )).SubmitAsync(draft);
        Assert.Equal(status, receipt.Status);
        Assert.Equal(1, handler.Calls);
        Assert.False(Store.HasLocalTrial);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task SubmitFailureDoesNotRetryOrLoseDraft(int code)
    {
        var draft = Store.SaveDraft("Test body\n", "test");
        using var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)));
        using var http = new HttpClient(handler);
        var uploader = new SkillRevisionUploadClient(http, new("https://example.test/"));
        await Assert.ThrowsAsync<HttpRequestException>(() => uploader.SubmitAsync(draft));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(draft, Store.LoadDraft());
        Assert.Null(Store.ReadReceipt(draft));
    }

    [Fact]
    public async Task MismatchedReceiptCannotBeRecordedAsSubmissionSuccess()
    {
        var draft = Store.SaveDraft("Test body\n", "test");
        using var handler = new FakeHandler(_ => Task.FromResult(Receipt(draft with { SubmissionId = Guid.NewGuid() }, "pending")));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SkillRevisionUploadClient(http, new("https://example.test/")).SubmitAsync(draft));
        Assert.Null(Store.ReadReceipt(draft));
    }

    [Theory]
    [InlineData("sk-abcdefghijklmnopqrst")]
    [InlineData("apiKey=abcdefghijklm")]
    public async Task ObviousSecretsBlockSubmissionBeforeTransport(string secret)
    {
        var draft = Store.SaveDraft("Test body " + secret, "change");
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SkillRevisionUploadClient(http, new("https://example.test/")).SubmitAsync(draft));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void CorruptOversizedTrialCanBeReplacedOrRestored()
    {
        var path = Path.Combine(_root, "AiAssistant", "SkillRevisions", "local-trial.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('x', SkillRevisionDocument.MaxDocumentBytes + 1));
        Store.RestoreOfficial();
        Assert.False(Store.HasLocalTrial);
        File.WriteAllText(path, "broken");
        var draft = Store.SaveDraft("Repaired trial\n", "repair");
        Store.ApplyLocal(draft);
        Assert.Equal(draft.ModifiedDocument, Store.ReadEffectiveDocument());
        var receipt = Path.Combine(_root, "AiAssistant", "SkillRevisions", "Receipts", draft.SubmissionId + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
        File.WriteAllText(receipt, new string('x', 8193));
        Assert.Null(Store.ReadReceipt(draft));
    }

    [Fact]
    public async Task CrLfNormalizationAloneDoesNotSubmitAModification()
    {
        var original = Official.Replace("\n", "\r\n");
        var draft = new SkillRevisionStore(_root, original, "test").SaveDraft(
            SkillRevisionDocument.Split(original).Body, "Only a summary");
        Assert.NotEqual(draft.BaseSha256, draft.ModifiedSha256);
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SkillRevisionUploadClient(http, new("https://example.test/")).SubmitAsync(draft));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ActualProductSkillCanBeSubmittedWithoutFalseSecretAlarm()
    {
        var draft = new SkillRevisionStore(_root).SaveDraft(AiAgentWorkflowSkill.Body + "\nTest local addition.\n", "Test addition");
        using var handler = new FakeHandler(_ => Task.FromResult(Receipt(draft, "pending")));
        using var http = new HttpClient(handler);
        var result = await new SkillRevisionUploadClient(http, new("https://example.test/")).SubmitAsync(draft);
        Assert.Equal("pending", result.Status);
    }

    private static HttpResponseMessage Receipt(SkillRevisionDraft draft, string status) => new(HttpStatusCode.Created)
    { Content = new StringContent(JsonSerializer.Serialize(new SkillRevisionReceipt(draft.SubmissionId, draft.ModifiedSha256, status, DateTimeOffset.UtcNow), SkillRevisionStore.JsonOptions), Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request); }
    }
}
