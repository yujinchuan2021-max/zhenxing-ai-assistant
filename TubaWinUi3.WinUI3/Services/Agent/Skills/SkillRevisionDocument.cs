using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TubaWinUi3.Services.Agent;

public sealed record SkillRevisionDraft
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid SubmissionId { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public required string ClientVersion { get; init; }
    public string SkillId { get; init; } = AiAgentWorkflowSkill.Id;
    public string SkillName { get; init; } = AiAgentWorkflowSkill.DshName;
    public required string BaseDocument { get; init; }
    public required string ModifiedDocument { get; init; }
    public required string BaseSha256 { get; init; }
    public required string ModifiedSha256 { get; init; }
    public required string ChangeSummary { get; init; }
    [JsonIgnore] public string EditedBody => SkillRevisionDocument.Split(ModifiedDocument).Body;
}

/// <summary>正文可以编辑；技能身份与 frontmatter 由所编辑的官方版本保留。</summary>
public static class SkillRevisionDocument
{
    public const int MaxDocumentBytes = 128 * 1024;
    public const int MaxSummaryLength = 2000;

    public static string Hash(string text) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static (string Header, string Body) Split(string document)
    {
        if (string.IsNullOrWhiteSpace(document) || document.Length < 8)
            throw new InvalidDataException("技能正文不能为空。");
        if (Encoding.UTF8.GetByteCount(document) > MaxDocumentBytes)
            throw new InvalidDataException("技能内容过长。");
        var normalized = document.Replace("\r\n", "\n", StringComparison.Ordinal);
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal) || end < 0)
            throw new InvalidDataException("技能格式无效。");
        var header = normalized[..(end + 5)];
        if (!header.Split('\n').Any(line => line == "name: " + AiAgentWorkflowSkill.DshName))
            throw new InvalidDataException("技能身份不匹配。");
        var body = normalized[(end + 5)..];
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidDataException("技能正文不能为空。");
        return (header, body);
    }

    public static string WithBody(string baseDocument, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidDataException("技能正文不能为空。");
        var document = Split(baseDocument).Header + body.Replace("\r\n", "\n", StringComparison.Ordinal);
        _ = Split(document);
        return document;
    }

    public static void Validate(SkillRevisionDraft draft)
    {
        if (draft is null || draft.SchemaVersion != 1 || draft.SubmissionId == Guid.Empty || draft.SubmittedAt == default ||
            draft.SkillId != AiAgentWorkflowSkill.Id || draft.SkillName != AiAgentWorkflowSkill.DshName ||
            draft.SubmittedAt.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(draft.ClientVersion) ||
            draft.ClientVersion.Length > 80 || draft.ChangeSummary is null || draft.ChangeSummary.Length > MaxSummaryLength ||
            draft.BaseDocument is null || draft.ModifiedDocument is null)
            throw new InvalidDataException("技能修改记录无效。");
        var original = Split(draft.BaseDocument);
        var modified = Split(draft.ModifiedDocument);
        if (original.Header != modified.Header || Hash(draft.BaseDocument) != draft.BaseSha256 ||
            Hash(draft.ModifiedDocument) != draft.ModifiedSha256)
            throw new InvalidDataException("技能修改记录不完整。");
    }
}
