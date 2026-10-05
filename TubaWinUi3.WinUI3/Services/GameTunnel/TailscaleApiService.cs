using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TubaWinUi3.Services;

/// <summary>API 调用结果（无返回体）。</summary>
public sealed record ApiResult(bool Ok, string? Error = null);

/// <summary>创建一个授权密钥的结果。</summary>
public sealed record ApiAuthKeyResult(
    bool Ok,
    string? Key = null,
    string? KeyId = null,
    DateTimeOffset? Expires = null,
    string? Error = null);

/// <summary>tailnet 里的一台设备。</summary>
public sealed record ApiDevice(
    string Id,
    string HostName,
    string? Ipv4,
    string? Os,
    bool Online,
    DateTimeOffset? LastSeen);

/// <summary>
/// Tailscale API（api.tailscale.com/api/v2）客户端。
/// 只做三件对本工具有用的事：自动生成邀请用授权密钥、列设备、删设备。
/// 访问令牌是可选的加速项——没配置时用户仍可在控制台手动生成密钥并粘贴。
/// </summary>
public static class TailscaleApiService
{
    public const string ApiBase = "https://api.tailscale.com/api/v2";

    /// <summary>创建密钥的默认有效期：够一场联机用，过期后自动失效。</summary>
    public const int DefaultKeyExpirySeconds = 2 * 60 * 60;

    /// <summary>校验并规范化 API 访问令牌（tskey-api-…）。</summary>
    public static string? NormalizeApiKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().Trim('"', '\'', '，', ',', '。');
        return value.StartsWith("tskey-api-", StringComparison.Ordinal) && value.Length > 20 ? value : null;
    }

    /// <summary>识别「把授权密钥当成 API 令牌填进来」这类常见误操作。</summary>
    public static string? DescribeKeyKindMismatch(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith("tskey-auth-", StringComparison.Ordinal))
            return GameTunnelTexts.T("这是一把「授权密钥」（给设备登录用），不是 API 访问令牌。请在控制台 Keys 页面选择 API access token。");
        if (value.StartsWith("tskey-client-", StringComparison.Ordinal))
            return GameTunnelTexts.T("这是 OAuth 客户端密钥，本工具需要的是 API 访问令牌（tskey-api-…）。");
        return null;
    }

    /// <summary>
    /// 从一段文本里抠出完整密钥。控制台点「复制」时可能带上说明文字，
    /// 用户也可能复制了整行，所以按前缀定位后只取合法字符段。
    /// </summary>
    public static string? ExtractKey(string? text, string prefix)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(prefix)) return null;

        var index = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var end = index;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '-' or '_')) end++;

        var value = text[index..end];
        return value.Length > 20 ? value : null;
    }

    /// <summary>
    /// 清洗密钥描述。<b>Tailscale 只接受 [A-Za-z0-9 空格 _-]</b>——实测中文、点号、冒号、括号
    /// 一律被拒，服务端报 "keys: description had invalid characters"，整个创建请求 400。
    /// 中文游戏名必须过这一层，否则邀请码永远生成不出来。
    /// </summary>
    public static string SanitizeKeyDescription(string? raw, int maxLength = 60)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var builder = new StringBuilder(Math.Min(raw.Length, maxLength));
        var lastWasSpace = false;

        foreach (var ch in raw)
        {
            var allowed = ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or ' ';
            if (!allowed)
            {
                // 被挡掉的字符当成词间分隔，避免 "我的世界Java" 这种粘连
                lastWasSpace = false;
                if (builder.Length > 0) builder.Append(' ');
                continue;
            }

            if (ch == ' ')
            {
                if (lastWasSpace || builder.Length == 0) continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            builder.Append(ch);
            if (builder.Length >= maxLength) break;
        }

        return builder.ToString().Trim();
    }

    /// <summary>生成创建授权密钥的请求体（internal 供单测锁格式）。</summary>
    internal static string BuildCreateKeyBody(
        bool reusable,
        bool ephemeral,
        bool preauthorized,
        int expirySeconds,
        string? description,
        IReadOnlyList<string>? tags = null)
    {
        var create = new Dictionary<string, object>
        {
            ["reusable"] = reusable,
            ["ephemeral"] = ephemeral,
            ["preauthorized"] = preauthorized
        };
        // 没定义标签的 ACL 下带 tags 会被拒（400），因此只在显式指定时下发
        if (tags is { Count: > 0 }) create["tags"] = tags;

        var payload = new Dictionary<string, object?>
        {
            ["capabilities"] = new Dictionary<string, object>
            {
                ["devices"] = new Dictionary<string, object> { ["create"] = create }
            },
            ["expirySeconds"] = expirySeconds
        };

        // 描述必须过白名单，否则中文/标点会让整个请求 400
        var safeDescription = SanitizeKeyDescription(description);
        if (safeDescription.Length > 0) payload["description"] = safeDescription;

        return JsonSerializer.Serialize(payload);
    }

    internal static ApiAuthKeyResult ParseAuthKeyResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ApiAuthKeyResult(false, Error: GameTunnelTexts.T("服务端没有返回内容"));

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ApiAuthKeyResult(false, Error: GameTunnelTexts.T("服务端返回格式异常"));

            if (!root.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String)
                return new ApiAuthKeyResult(false, Error: GameTunnelTexts.T("服务端没有返回密钥内容"));

            var key = keyElement.GetString();
            if (NormalizeAuthKeyLike(key) is null)
                return new ApiAuthKeyResult(false, Error: GameTunnelTexts.T("服务端返回的密钥格式异常"));

            string? id = null;
            if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                id = idElement.GetString();

            DateTimeOffset? expires = null;
            if (root.TryGetProperty("expires", out var expiresElement)
                && expiresElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(expiresElement.GetString(), out var parsed))
            {
                expires = parsed;
            }

            return new ApiAuthKeyResult(true, key, id, expires);
        }
        catch
        {
            return new ApiAuthKeyResult(false, Error: GameTunnelTexts.T("服务端返回格式异常"));
        }
    }

    private static string? NormalizeAuthKeyLike(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        return value.StartsWith("tskey-auth-", StringComparison.Ordinal) && value.Length > 20 ? value : null;
    }

    internal static IReadOnlyList<ApiDevice> ParseDeviceList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                return [];

            var rows = new List<ApiDevice>();
            foreach (var device in devices.EnumerateArray())
            {
                if (device.ValueKind != JsonValueKind.Object) continue;

                var id = GetString(device, "id") ?? GetString(device, "nodeId") ?? "";
                var hostName = GetString(device, "hostname") ?? GetString(device, "name") ?? "";
                if (id.Length == 0 && hostName.Length == 0) continue;

                string? ipv4 = null;
                if (device.TryGetProperty("addresses", out var addresses) && addresses.ValueKind == JsonValueKind.Array)
                {
                    foreach (var address in addresses.EnumerateArray())
                    {
                        if (address.ValueKind != JsonValueKind.String) continue;
                        var value = address.GetString();
                        if (!string.IsNullOrWhiteSpace(value) && !value.Contains(':'))
                        {
                            ipv4 = value;
                            break;
                        }
                    }
                }

                DateTimeOffset? lastSeen = null;
                if (device.TryGetProperty("lastSeen", out var lastSeenElement)
                    && lastSeenElement.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(lastSeenElement.GetString(), out var parsedSeen))
                {
                    lastSeen = parsedSeen;
                }

                rows.Add(new ApiDevice(
                    id,
                    hostName,
                    ipv4,
                    GetString(device, "os"),
                    device.TryGetProperty("online", out var online) && online.ValueKind == JsonValueKind.True,
                    lastSeen));
            }

            return rows;
        }
        catch
        {
            return [];
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        try
        {
            if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 HTTP 状态码翻译成用户能看懂的原因。<b>一定带上服务端原话</b>，否则只剩状态码没法排查。</summary>
    public static string DescribeApiError(int status, string? body)
    {
        var server = ExtractServerMessage(body);

        var hint = status switch
        {
            400 => GameTunnelTexts.T("服务端拒绝了这个请求"),
            401 => GameTunnelTexts.T("API 访问令牌无效或已过期，请在控制台重新生成一个"),
            403 => GameTunnelTexts.T("这个令牌没有权限（需要 Owner / Admin 角色）"),
            404 => GameTunnelTexts.T("找不到对应的资源"),
            429 => GameTunnelTexts.T("请求过于频繁，请等几十秒再试"),
            >= 500 => GameTunnelTexts.TSub($"Tailscale 服务端出错（{status}），稍后重试"),
            _ => GameTunnelTexts.TSub($"请求失败（HTTP {status}）")
        };

        if (status == 400 && body is { Length: > 0 } && body.Contains("tags", StringComparison.OrdinalIgnoreCase))
        {
            hint = GameTunnelTexts.T("该网络的访问策略里没有定义标签（tag），无法签发带标签的密钥");
        }
        else if (status == 400 && body is { Length: > 0 } && body.Contains("invalid characters", StringComparison.OrdinalIgnoreCase))
        {
            hint = GameTunnelTexts.T("密钥描述里有 Tailscale 不接受的字符（只允许英文、数字、空格、连字符和下划线）");
        }
        else if (status == 400)
        {
            hint += GameTunnelTexts.T("。常见原因：访问策略里没有定义标签（tag）、密钥有效期超出 1 小时~90 天、或描述里有非法字符");
        }

        return server is { Length: > 0 } ? GameTunnelTexts.TSub($"{hint}（服务端：{server}）") : hint;
    }

    /// <summary>从错误响应里抠出服务端的人类可读说明（Tailscale 用 {"message": "…"}）。</summary>
    internal static string? ExtractServerMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var text = body.Trim();
        if (text.Length > 300) text = text[..300];

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "message", "error", "detail" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        var message = value.GetString();
                        if (!string.IsNullOrWhiteSpace(message)) return message.Trim();
                    }
                }
            }
        }
        catch
        {
        }

        // 不是 JSON 就别猜了，原样回一段（HTML 错误页会很长，已截断）
        return text.StartsWith('<') ? null : text;
    }

    // ══════════════════════ 实际请求 ══════════════════════

    private static HttpClient CreateClient()
        => HttpClientFactory.CreateIpv4Preferred(TimeSpan.FromSeconds(30));

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string token, string? jsonBody = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        return request;
    }

    /// <summary>用一次列设备请求验证令牌是否可用。</summary>
    public static async Task<ApiResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(token);
        if (normalized is null)
            return new ApiResult(false, DescribeKeyKindMismatch(token) ?? GameTunnelTexts.T("令牌格式不对（应以 tskey-api- 开头）"));

        try
        {
            using var client = CreateClient();
            using var request = CreateRequest(HttpMethod.Get, $"{ApiBase}/tailnet/-/devices", normalized);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return new ApiResult(true);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new ApiResult(false, DescribeApiError((int)response.StatusCode, body));
        }
        catch (OperationCanceledException)
        {
            return new ApiResult(false, GameTunnelTexts.T("请求超时，请检查网络或代理"));
        }
        catch (Exception ex)
        {
            return new ApiResult(false, GameTunnelTexts.TSub($"连接失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 生成一把用于邀请朋友的授权密钥：2 小时有效、可重复使用（一局可能有多个朋友）、
    /// 预授权（无需管理员手动批准）。过期后自动失效，主机无需手动清理。
    /// </summary>
    public static async Task<ApiAuthKeyResult> CreateInviteKeyAsync(
        string token,
        string? description = null,
        int expirySeconds = DefaultKeyExpirySeconds,
        CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(token);
        if (normalized is null)
            return new ApiAuthKeyResult(false, Error: DescribeKeyKindMismatch(token) ?? GameTunnelTexts.T("令牌格式不对（应以 tskey-api- 开头）"));

        try
        {
            var body = BuildCreateKeyBody(
                reusable: true,
                ephemeral: false,
                preauthorized: true,
                expirySeconds: expirySeconds,
                description: description);

            using var client = CreateClient();
            using var request = CreateRequest(HttpMethod.Post, $"{ApiBase}/tailnet/-/keys", normalized, body);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new ApiAuthKeyResult(false, Error: DescribeApiError((int)response.StatusCode, text));
            }

            var parsed = ParseAuthKeyResponse(text);
            if (!parsed.Ok) return parsed;

            // 服务端没给过期时间时用本地时间兜底，界面仍需显示有效期
            return parsed.Expires is null && expirySeconds > 0
                ? parsed with { Expires = DateTimeOffset.UtcNow.AddSeconds(expirySeconds) }
                : parsed;
        }
        catch (OperationCanceledException)
        {
            return new ApiAuthKeyResult(false, Error: GameTunnelTexts.T("请求超时，请检查网络或代理"));
        }
        catch (Exception ex)
        {
            return new ApiAuthKeyResult(false, Error: GameTunnelTexts.TSub($"连接失败：{ex.Message}"));
        }
    }

    /// <summary>撤销一把还没过期的邀请密钥（主机提前结束联机时用）。</summary>
    public static async Task<ApiResult> RevokeKeyAsync(string token, string keyId, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(token);
        if (normalized is null) return new ApiResult(false, GameTunnelTexts.T("令牌格式不对"));
        if (string.IsNullOrWhiteSpace(keyId)) return new ApiResult(false, GameTunnelTexts.T("缺少密钥 ID"));

        try
        {
            using var client = CreateClient();
            using var request = CreateRequest(HttpMethod.Delete, $"{ApiBase}/tailnet/-/keys/{Uri.EscapeDataString(keyId)}", normalized);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                return new ApiResult(true);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new ApiResult(false, DescribeApiError((int)response.StatusCode, body));
        }
        catch (Exception ex)
        {
            return new ApiResult(false, GameTunnelTexts.TSub($"连接失败：{ex.Message}"));
        }
    }

    public static async Task<IReadOnlyList<ApiDevice>> ListDevicesAsync(string token, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(token);
        if (normalized is null) return [];

        try
        {
            using var client = CreateClient();
            using var request = CreateRequest(HttpMethod.Get, $"{ApiBase}/tailnet/-/devices", normalized);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseDeviceList(text);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>把某台设备从 tailnet 里删除（清理朋友设备时用）。</summary>
    public static async Task<ApiResult> DeleteDeviceAsync(string token, string deviceId, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(token);
        if (normalized is null) return new ApiResult(false, GameTunnelTexts.T("令牌格式不对"));
        if (string.IsNullOrWhiteSpace(deviceId)) return new ApiResult(false, GameTunnelTexts.T("缺少设备 ID"));

        try
        {
            using var client = CreateClient();
            using var request = CreateRequest(HttpMethod.Delete, $"{ApiBase}/device/{Uri.EscapeDataString(deviceId)}", normalized);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                return new ApiResult(true);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new ApiResult(false, DescribeApiError((int)response.StatusCode, body));
        }
        catch (Exception ex)
        {
            return new ApiResult(false, GameTunnelTexts.TSub($"连接失败：{ex.Message}"));
        }
    }
}
