using System.Text.RegularExpressions;
using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>Interprets an item's own labels. It never proves installation or grants installation permission.</summary>
internal static class ToolFlowItemSemantics
{
    internal static bool IsAgent(ToolFlowItem item) => !NeedsUserSetup(item) &&
        Match(item.Kind, @"(?<![a-z])agent(?![a-z])|编程助手|编程智能体|开发智能体");

    internal static bool NeedsUserSetup(ToolFlowItem item) =>
        Match(item.Kind.Trim(), @"\A(?:account|subscription|membership|license|model|network|service|website|web|platform|online_service|asset|assets|resource|resources|configuration|setup)\z") ||
        Match(item.Kind + " " + item.Name,
            @"账号|账户|登录|注册|订阅|会员|模型接入|模型配置|接入配置|人工配置|网络配置|素材|教程|插件|导出模板|(?<![a-z])(?:account|subscription|membership|sign[ -]?in|log[ -]?in|registration|assets?|tutorial|plugin|export templates?)(?![a-z])|(?:model|api)[ -]?(?:access|setup|configuration|key)|API\s*(?:接入|密钥|开通)|配置\s*(?:Godot|SDK|JDK|模型)") ||
        Match(item.ManualHint ?? "", @"(?:配置|设置|填入|填写).{0,12}(?:路径|SDK|JDK|密钥|模型|代理)|安装.{0,8}(?:插件|导出模板)|(?:configure|set up|enter).{0,18}(?:path|sdk|jdk|api key|model|proxy)");

    internal static bool IsModelSetup(ToolFlowItem item) =>
        Match(item.Kind.Trim(), @"\Amodel\z") || Match(item.Kind + " " + item.Name + " " + item.ManualHint,
            @"模型接入|模型配置|配置模型|API\s*(?:接入|密钥|Key)|model[ -]?(?:access|setup|configuration)|configure.{0,12}model|api[ -]?key");

    internal static bool HasWebAction(ToolFlowItem item) =>
        Match(item.Kind.Trim(), @"\A(?:account|subscription|membership|license|service|website|web|platform|online_service)\z") ||
        Match(item.Kind + " " + item.Name + " " + item.ManualHint,
            @"账号|账户|登录|注册|订阅|会员|网页|在线服务|开放平台|开通|获取.{0,8}(?:密钥|API)|(?<![a-z])(?:account|subscription|membership|sign[ -]?in|log[ -]?in|register|website|web service)(?![a-z])");

    internal static string? TryGetReadOnlyTarget(ToolFlowItem item) =>
        string.IsNullOrWhiteSpace(item.InstallTargetKey) && !NeedsUserSetup(item) &&
        !ToolFlowAgentVariantPolicy.IsTraeChinaVariant(item) ? NamedDesktopTarget(item.Name) : null;

    internal static bool HasConflictingTarget(ToolFlowItem item) => !NeedsUserSetup(item) &&
        !string.IsNullOrWhiteSpace(item.InstallTargetKey) && NamedDesktopTarget(item.Name) is { } named &&
        !named.Equals(item.InstallTargetKey.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static string? ReadyTarget(ToolFlowItem item, ToolFlowResumeItemRow row)
    {
        if (row.IsExistingToolReuse) return row.ExistingToolTargetKey;
        if (!string.IsNullOrWhiteSpace(item.InstallTargetKey)) return item.InstallTargetKey;
        var target = TryGetReadOnlyTarget(item);
        return row.State == ToolFlowResumeItemState.InstalledOrDetected && row.IsPreparationEvidence &&
            target is not null && target.Equals(row.ExistingToolTargetKey, StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    private static string? NamedDesktopTarget(string name)
    {
        // A precise product identity plus trailing annotations/version can trigger
        // a read-only probe. A mention inside a tutorial, URL or combined name cannot.
        var label = Regex.Replace(name.Trim(), @"(?:\s*(?:（[^（）]*）|\([^()]*\)))*\s*\z", "").Trim();
        foreach (var key in SystemInstaller.KnownTargets)
        {
            // Archive requirements use their separate capability/version policy.
            if (key == "7zip" || !SystemInstaller.TryGetToolAccessMetadata(key, out var metadata) || !metadata.IsGui) continue;
            var names = key == "godot" ? new[] { metadata.Name, "Godot Engine" } : new[] { metadata.Name };
            if (names.Any(alias => Match(label, @"\A" + Regex.Escape(alias) + @"(?:\s+v?\d+(?:\.\d+){0,3}(?:\s+(?:stable|LTS))?)?\z")))
                return key;
        }
        return null;
    }

    private static bool Match(string value, string pattern) => Regex.IsMatch(value, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
