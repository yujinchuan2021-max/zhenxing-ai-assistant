using System.Text.RegularExpressions;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>
/// 上报前的轻量凭据过滤：把明显形如 API Key / 令牌 / 密码赋值的片段替换为 <see cref="Placeholder"/>。
/// 结构性保证是载荷只来自用户核对过的选定快照；这里是保守兜底，不是通用脱敏引擎，
/// 不改变本地保存的原文（只作用于上传载荷）。
/// </summary>
public static class ToolFlowPayloadRedactor
{
    public const string Placeholder = "[已过滤]";

    // sk- 开头的 OpenAI 风格 Key
    private static readonly Regex SkStyle = new(
        @"sk-[A-Za-z0-9_\-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // GitHub 风格令牌
    private static readonly Regex GitHubStyle = new(
        @"\bgh[pousr]_[A-Za-z0-9]{20,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Authorization: Bearer <token>
    private static readonly Regex BearerStyle = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._\-+/=]{12,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 标签式赋值：api key / token / secret / 密码 / 口令 / 密钥 = <值>
    private static readonly Regex AssignmentStyle = new(
        @"(?i)(api[ _\-]?key|token|secret|密码|口令|密钥)\s*[:=：＝]\s*\S{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = SkStyle.Replace(text, Placeholder);
        result = GitHubStyle.Replace(result, Placeholder);
        result = BearerStyle.Replace(result, "Bearer " + Placeholder);
        result = AssignmentStyle.Replace(result,
            match => match.Groups[1].Value + "=" + Placeholder);
        return result;
    }
}
