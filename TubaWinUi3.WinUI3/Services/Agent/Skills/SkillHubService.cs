using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TubaWinUi3.Services.Ai;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Services.Agent;

internal sealed class SkillMakerConfigurationException(string message) : InvalidOperationException(message);

internal sealed class SkillMakerSession : IDisposable
{
    private readonly IChatClient _client;
    private readonly List<ChatMessage> _history = [];
    private readonly string _id = "usr_" + Guid.NewGuid().ToString("N");
    private int _questions;
    private bool _busy;
    internal string ModelName { get; }
    internal SkillMakerSession()
    {
        var (_, endpoint, model, key) = AiProviderStore.GetSelectedSnapshot();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model) ||
            (string.IsNullOrWhiteSpace(key) && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback)))
            throw new SkillMakerConfigurationException("先在 AI 设置中配置接口和模型，再制作技能。");
        ModelName = model;
        _client = AgentClientFactory.CreateClient(endpoint, model, string.IsNullOrWhiteSpace(key) ? "local" : key, noRetry: true);
        _history.Add(new(ChatRole.System, "你是枕星技能制作助手。帮助用户制作适用于当前AI助手的简短场景指导，不替用户扩展目标。" +
            "使用用户的语言。先看用户用途，只问影响流程的关键问题，每次只问1个，最多3个，不需要的跳过。选项必须可点击，提供2到4个短选项。" +
            "一次只返回一个JSON对象，不输出其他说明。问题格式：{\"kind\":\"question\",\"question\":\"短问题\",\"options\":[\"选项1\",\"选项2\"]}。" +
            "资料足够时生成：{\"kind\":\"skill\",\"name\":\"技能名\",\"description\":\"何时用、做什么\",\"category\":\"分类\",\"keywords\":[\"触发词\"],\"instructions\":\"指导正文\"}。" +
            "分类只能为development/design/writing/office/data/game/other。正文写清适用场景、必要步骤、短输出格式和用户目标边界，" +
            "不超过3000字；关键词1到12个，要对应真实用户表达。不得索取密码、Key或个人信息；不得伪造工具、网站测试、安装能力、网络权限。" +
            "这只是文本技能，不能附带脚本、依赖文件或全局系统规则；不制造必装工具，不自动上传。"));
    }
    internal async Task<SkillMakerReply> ReplyAsync(string input, CancellationToken token)
    {
        if (_busy || string.IsNullOrWhiteSpace(input) || input.Length > 4000) throw new InvalidDataException("请简短描述技能用途。");
        _busy = true;
        var message = new ChatMessage(ChatRole.User, input);
        var messages = new List<ChatMessage>(_history) { message };
        if (_questions >= 3) messages.Add(new(ChatRole.System, "不再提问。按已有回答给出完整skill草稿。"));
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(90));
            var response = await _client.GetResponseAsync(messages, new ChatOptions { Tools = null, ToolMode = ChatToolMode.None,
                Temperature = 0.2f, MaxOutputTokens = 5000 }, budget.Token);
            token.ThrowIfCancellationRequested();
            if (response.Messages.Any(m => m.Contents.Any(c => c is FunctionCallContent))) throw new InvalidDataException("技能制作不能执行工具。");
            var reply = SkillHubDocument.ParseReply(response.Text, _id, _questions < 3);
            _history.Add(message);
            _history.Add(new(ChatRole.Assistant, response.Text));
            if (reply.Kind == "question") _questions++;
            return reply;
        }
        finally { _busy = false; }
    }
    public void Dispose() => _client.Dispose();
}

internal static class SkillHubLocalStore
{
    private static readonly object Sync = new();
    internal static string SaveAndLoad(SkillHubDraft input, SkillCatalogItem? source = null, bool allowUpdate = false)
    {
        var draft = SkillHubDocument.Validate(input);
        lock (Sync)
        {
            var folder = Path.Combine(UserSkillLoader.SkillsDir, "created");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, draft.Id + ".skill.json");
            bool exists = File.Exists(path), changed = false;
            if (exists)
            {
                var saved = File.ReadAllText(path);
                var old = UserSkillLoader.FromJson(saved);
                using var oldJson = JsonDocument.Parse(saved);
                var oldCategory = oldJson.RootElement.TryGetProperty("category", out var category) ? category.GetString() : null;
                changed = old is null || old.SystemPromptFragment != draft.SystemPromptFragment || old.DisplayName != draft.DisplayName ||
                    old.Description != draft.Description || oldCategory != draft.Category || !old.TriggerKeywords.SequenceEqual(draft.TriggerKeywords);
                if (changed && (!allowUpdate || source is not null || old?.Id != draft.Id || draft.Id.StartsWith("usr_catalog_", StringComparison.Ordinal)))
                    throw new InvalidDataException("同名技能已有不同内容，请保留原文件。");
            }
            if (!exists || changed)
            {
                var body = JsonSerializer.Serialize(new { draft.Id, draft.DisplayName, draft.Description, draft.Category,
                    draft.SystemPromptFragment, draft.TriggerKeywords, glyph = "\uE9D9", sourceId = source?.Id,
                    sourceUrl = source?.SourceUrl, author = source?.Author, license = source?.License,
                    licenseUrl = source?.LicenseUrl, licenseText = source?.LicenseText,
                    contentSha256 = SkillHubDocument.Hash(draft.SystemPromptFragment) }, SkillHubDocument.Json);
                AtomicNew(path, Encoding.UTF8.GetBytes(body), replace: exists);
            }
            UserSkillLoader.LoadAllDetailed();
            var loaded = AgentSkillRegistry.Find(draft.Id);
            if (loaded?.SystemPromptFragment != draft.SystemPromptFragment) throw new InvalidDataException("技能已保存但未加载，请检查已有ID。");
            return path;
        }
    }
    internal static void AtomicNew(string path, byte[] bytes, bool replace = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, overwrite: replace);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class SkillHubClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _http;
    private readonly Uri _base;
    internal SkillHubClient(HttpClient http, Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length > 0 ||
            (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback))) throw new ArgumentException("Invalid skill endpoint.");
        _http = http; _base = new(endpoint.AbsoluteUri.TrimEnd('/') + "/");
    }
    internal static SkillHubClient Official()
    {
        if (DataRoots.EffectiveTestRoot is not null) throw new InvalidOperationException("隔离测试必须使用假传输。");
        return new(Http, ToolFlowUploadService.OfficialEndpoint);
    }
    private async Task<T> SendAsync<T>(HttpRequestMessage request, int maxBytes, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(20)); token = budget.Token;
        using (request)
        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
                throw new HttpRequestException("技能服务请求未完成。", null, response.StatusCode);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[4096]; int n;
            while ((n = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { if (output.Length + n > maxBytes) throw new InvalidDataException("技能服务返回过长。"); output.Write(buffer, 0, n); }
            return JsonSerializer.Deserialize<T>(output.ToArray(), SkillHubDocument.Json) ?? throw new InvalidDataException("技能服务数据无效。");
        }
    }
    internal async Task<SkillCatalog> CatalogueAsync(CancellationToken token, int page = 0)
    {
        var result = await SendAsync<SkillCatalog>(new(HttpMethod.Get, new Uri(_base, "v1/skills?page=" + page)), 1024 * 1024, token);
        if (result.SchemaVersion != 1 || result.Page != page || result.Items is null || result.Items.Length > 400) throw new InvalidDataException("技能目录无效。");
        return result;
    }
    internal async Task<SkillCatalogItem> DetailAsync(string id, CancellationToken token)
    {
        if (id.Length is < 1 or > 100 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw new InvalidDataException();
        var result = await SendAsync<SkillCatalogItem>(new(HttpMethod.Get, new Uri(_base, "v1/skills/" + id)), 160000, token);
        if (result.Id != id) throw new InvalidDataException("技能详情不匹配。");
        return result;
    }
    internal async Task<SkillSubmissionReceipt> SubmitAsync(SkillHubDraft draft, string root, CancellationToken token)
    {
        draft = SkillHubDocument.Validate(draft);
        if (ToolFlowPayloadRedactor.Redact(draft.SystemPromptFragment) != draft.SystemPromptFragment ||
            ToolFlowPayloadRedactor.Redact(draft.Description + draft.DisplayName) != draft.Description + draft.DisplayName)
            throw new InvalidDataException("请移除技能中的密钥再提交。");
        var frozenKey = SkillHubDocument.Hash(JsonSerializer.Serialize(draft, SkillHubDocument.Json));
        var document = new SkillSubmission(1, new Guid(Convert.FromHexString(frozenKey)[..16]), "0.1", draft, "MIT", true);
        var frozen = JsonSerializer.Serialize(document, SkillHubDocument.Json);
        var folder = Path.Combine(root, "SkillSubmissions");
        var documentPath = Path.Combine(folder, document.SubmissionId.ToString("D") + ".json");
        if (!File.Exists(documentPath)) SkillHubLocalStore.AtomicNew(documentPath, Encoding.UTF8.GetBytes(frozen));
        else if (File.ReadAllText(documentPath) != frozen) throw new InvalidDataException("技能投稿已存在不同内容。");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "v1/skill-submissions"))
        { Content = new StringContent(frozen, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Skill-Token", GetOwner(root));
        var receipt = await SendAsync<SkillSubmissionReceipt>(request, 8192, token);
        if (receipt.SubmissionId != document.SubmissionId || receipt.ContentSha256 != SkillHubDocument.Hash(draft.SystemPromptFragment) ||
            receipt.ReceivedAt == default || receipt.Status is not ("pending" or "accepted" or "rejected")) throw new InvalidDataException("技能投稿回执不匹配。");
        var receiptPath = Path.Combine(folder, document.SubmissionId.ToString("D") + ".receipt.json");
        var temp = receiptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(receipt, SkillHubDocument.Json)); File.Move(temp, receiptPath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return receipt;
    }
    private static readonly object OwnerGate = new();
    private static string GetOwner(string root)
    {
        lock (OwnerGate)
        {
            var path = Path.Combine(root, "SkillSubmissions", "owner.dpapi");
            if (!File.Exists(path))
            {
                var generated = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                try { SkillHubLocalStore.AtomicNew(path, ProtectedData.Protect(Encoding.ASCII.GetBytes(generated), null, DataProtectionScope.CurrentUser)); }
                catch (IOException) when (File.Exists(path)) { }
            }
            var token = Encoding.ASCII.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
            if (token.Length != 64 || token.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("本机技能投稿凭据损坏，请保留文件。");
            return token;
        }
    }
}
