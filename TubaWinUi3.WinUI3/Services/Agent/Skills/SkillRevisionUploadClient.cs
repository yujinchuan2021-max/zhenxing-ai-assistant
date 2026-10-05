using System.Net;
using System.Text;
using System.Text.Json;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Services.Agent;

public sealed record SkillRevisionReceipt(Guid SubmissionId, string ModifiedSha256, string Status, DateTimeOffset ReceivedAt);

/// <summary>用户点击提交时只发送当前冻结修订；不发送对话，也不自动重试。</summary>
public sealed class SkillRevisionUploadClient
{
    private static readonly HttpClient OfficialHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _http;
    private readonly Uri _uri;

    public SkillRevisionUploadClient(HttpClient http, Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length > 0 ||
            (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback)))
            throw new ArgumentException("Invalid skill review endpoint.", nameof(endpoint));
        _http = http;
        _uri = new Uri(new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/"), "v1/skill-revisions");
    }

    public static SkillRevisionUploadClient CreateOfficial()
    {
        if (DataRoots.EffectiveTestRoot is not null)
            throw new InvalidOperationException("隔离测试必须使用假传输。");
        return new(OfficialHttp, ToolFlowUploadService.OfficialEndpoint);
    }

    public async Task<SkillRevisionReceipt> SubmitAsync(SkillRevisionDraft draft, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ct = timeout.Token;
        SkillRevisionDocument.Validate(draft);
        if (SkillRevisionDocument.Split(draft.BaseDocument).Body == draft.EditedBody)
            throw new InvalidDataException("请先修改技能正文。");
        if (string.IsNullOrWhiteSpace(draft.ChangeSummary)) throw new InvalidDataException("请填写简短修改说明。");
        if (new[] { draft.ModifiedDocument, draft.ChangeSummary }.Any(text => ToolFlowPayloadRedactor.Redact(text) != text))
            throw new InvalidDataException("修改内容含疑似密钥，请移除后提交。");
        using var request = new HttpRequestMessage(HttpMethod.Post, _uri)
        { Content = new StringContent(JsonSerializer.Serialize(draft, SkillRevisionStore.JsonOptions), Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("审核服务尚未开放，草稿已保留。", null, response.StatusCode);
        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.Created)
            throw new HttpRequestException("提交未成功，草稿已保留，可以重试。", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[2048];
        int read;
        while ((read = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > 8192) throw new InvalidDataException("审核回执过长。");
            buffer.Write(bytes, 0, read);
        }
        var receipt = JsonSerializer.Deserialize<SkillRevisionReceipt>(buffer.ToArray(), SkillRevisionStore.JsonOptions)
            ?? throw new InvalidDataException("审核回执无效。");
        if (receipt.SubmissionId != draft.SubmissionId || receipt.ModifiedSha256 != draft.ModifiedSha256 ||
            receipt.Status is not ("pending" or "accepted" or "rejected") || receipt.ReceivedAt == default)
            throw new InvalidDataException("审核回执与本次修改不匹配。");
        return receipt;
    }
}
