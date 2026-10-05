using System.Text;
using System.Text.Json;

namespace TubaWinUi3.Services.Agent;

/// <summary>草稿、已应用正文与提交回执各自保存；只有显式应用才改变本机助手。</summary>
public sealed class SkillRevisionStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly string _official;
    private readonly string _clientVersion;
    private string DraftPath => Path.Combine(_root, "draft.json");
    private string TrialPath => Path.Combine(_root, "local-trial.md");

    public SkillRevisionStore(string? dataRoot = null, string? officialDocument = null, string? clientVersion = null)
    {
        _root = Path.Combine(Path.GetFullPath(dataRoot ?? ConfigManager.GetDataDir()), "AiAssistant", "SkillRevisions");
        _official = officialDocument ?? AiAgentWorkflowSkill.Document;
        _clientVersion = clientVersion ?? typeof(AiAgentWorkflowSkill).Assembly.GetName().Version?.ToString() ?? "0.1";
        _ = SkillRevisionDocument.Split(_official);
    }

    public bool HasLocalTrial => File.Exists(TrialPath);

    public SkillRevisionDraft? LoadDraft()
    {
        if (!File.Exists(DraftPath)) return null;
        var draft = JsonSerializer.Deserialize<SkillRevisionDraft>(ReadBounded(DraftPath, 1024 * 1024), JsonOptions)
            ?? throw new InvalidDataException("技能草稿不可读。");
        SkillRevisionDocument.Validate(draft);
        return draft;
    }

    public string ReadEffectiveDocument()
    {
        if (!File.Exists(TrialPath)) return _official;
        var trial = ReadBounded(TrialPath, SkillRevisionDocument.MaxDocumentBytes);
        var body = SkillRevisionDocument.Split(trial).Body;
        return SkillRevisionDocument.WithBody(_official, body);
    }

    public SkillRevisionDraft SaveDraft(string editedBody, string changeSummary)
    {
        if (changeSummary.Length > SkillRevisionDocument.MaxSummaryLength)
            throw new InvalidDataException("修改说明过长。");
        var modified = SkillRevisionDocument.WithBody(_official, editedBody);
        SkillRevisionDraft? existing = null;
        try { existing = LoadDraft(); } catch (InvalidDataException) { } catch (JsonException) { }
        if (existing is not null && existing.BaseDocument == _official &&
            existing.ModifiedDocument == modified && existing.ChangeSummary == changeSummary) return existing;
        var draft = new SkillRevisionDraft
        {
            SubmissionId = Guid.NewGuid(), SubmittedAt = DateTimeOffset.UtcNow,
            ClientVersion = _clientVersion, BaseDocument = _official, ModifiedDocument = modified,
            BaseSha256 = SkillRevisionDocument.Hash(_official),
            ModifiedSha256 = SkillRevisionDocument.Hash(modified), ChangeSummary = changeSummary,
        };
        SkillRevisionDocument.Validate(draft);
        AtomicWrite(DraftPath, JsonSerializer.Serialize(draft, JsonOptions));
        return draft;
    }

    public void ApplyLocal(SkillRevisionDraft draft, Action? synchronize = null)
    {
        SkillRevisionDocument.Validate(draft);
        var effective = SkillRevisionDocument.WithBody(_official, draft.EditedBody);
        ChangeTrial(effective, synchronize);
    }

    public void RestoreOfficial(Action? synchronize = null) => ChangeTrial(null, synchronize);

    private void ChangeTrial(string? document, Action? synchronize)
    {
        // Preserve bytes without parsing, so corrupt or oversized trials can be restored.
        var backup = TrialPath + "." + Guid.NewGuid().ToString("N") + ".rollback";
        if (File.Exists(TrialPath)) File.Copy(TrialPath, backup);
        try
        {
            if (document is null) { if (File.Exists(TrialPath)) File.Delete(TrialPath); }
            else AtomicWrite(TrialPath, document);
            synchronize?.Invoke();
        }
        catch
        {
            if (File.Exists(backup)) File.Move(backup, TrialPath, overwrite: true);
            else if (File.Exists(TrialPath)) File.Delete(TrialPath);
            try { synchronize?.Invoke(); } catch { /* Original failure is shown; persisted trial is rolled back. */ }
            throw;
        }
        if (File.Exists(backup)) File.Delete(backup);
    }

    public void SaveReceipt(SkillRevisionDraft draft, SkillRevisionReceipt receipt)
    {
        if (draft.SubmissionId != receipt.SubmissionId || draft.ModifiedSha256 != receipt.ModifiedSha256)
            throw new InvalidDataException("技能提交回执不匹配。");
        AtomicWrite(Path.Combine(_root, "Receipts", draft.SubmissionId.ToString("D") + ".json"),
            JsonSerializer.Serialize(receipt, JsonOptions));
    }

    public SkillRevisionReceipt? ReadReceipt(SkillRevisionDraft draft)
    {
        var path = Path.Combine(_root, "Receipts", draft.SubmissionId.ToString("D") + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<SkillRevisionReceipt>(ReadBounded(path, 8192), JsonOptions);
            return receipt?.SubmissionId == draft.SubmissionId && receipt.ModifiedSha256 == draft.ModifiedSha256 &&
                receipt.Status is ("pending" or "accepted" or "rejected") && receipt.ReceivedAt != default
                ? receipt : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static string ReadBounded(string path, int maxBytes)
    {
        if (new FileInfo(path).Length > maxBytes) throw new InvalidDataException("技能文件过长。");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    internal static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
