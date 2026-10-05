using System.Text.RegularExpressions;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>
/// Explicit desktop and regional editions must match their fixed Agent target.
/// A label cannot change the identity of an installer or an existing executable.
/// This check never selects a replacement target or infers intent from the plan.
/// </summary>
internal static class ToolFlowAgentVariantPolicy
{
    private static readonly HashSet<string> CliAgentTargets = new(StringComparer.OrdinalIgnoreCase)
        { "codex", "opencode", "claude-code" };
    private static readonly Regex DesktopLabel = new(
        @"桌面版|桌面客户端|桌面应用|桌面端|图形界面|(?<![a-z0-9_])(?:desktop|gui)(?![a-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Url = new(@"(?:[a-z][a-z0-9+.-]*://|\bwww\.)[^\s<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TraeName = new(@"(?<![a-z0-9_])trae(?=$|[^a-z0-9_]|cn\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ChinaEditionLabel = new(
        @"国内版|中国(?:大陆)?版|大陆版|(?<![a-z0-9_])trae[ -]*(?:cn|china)(?![a-z0-9_])|[（(]\s*cn\s*[)）]|\A\s*cn\s*\z|(?<![a-z0-9_])(?:china|domestic)[ -]+(?:edition|version)(?![a-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string? GetMismatchReason(ToolFlowItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.Equals(item.InstallTargetKey?.Trim(), "trae", StringComparison.OrdinalIgnoreCase) &&
            (HasChinaEditionLabel(item.Name) || HasChinaEditionLabel(item.Kind)))
            return LocalizationService.L("AiFlow_AgentRegionalVariantMismatch",
                LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage
                    ? "This item is labeled as Trae CN, but its installation target is Trae International (ByteDance.Trae), whose first sign-in requires an overseas connection. Select the matching edition; this target will not be installed or reused for the CN item."
                    : "该项标为 Trae 国内版，但安装目标是 Trae 国际版（ByteDance.Trae），首次登录需要海外网络。请重新选择匹配的版本；本次不会安装或复用国际版来代替国内版。");
        if (!CliAgentTargets.Contains(item.InstallTargetKey?.Trim() ?? "")) return null;
        // Names and kinds describe the selected variant. URL fields, hints,
        // model suggestions, whole-plan text and Windows/PC labels do not.
        if (!HasDesktopLabel(item.Name) && !HasDesktopLabel(item.Kind)) return null;
        return LocalizationService.L("AiFlow_AgentVariantMismatch",
            LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage
                ? "This item is labeled as a desktop app, but its installation target is a command-line Agent. Choose the matching desktop version; the CLI version will not be installed or reused."
                : "该项标为桌面版，但安装目标对应命令行 Agent。请重新选择匹配的桌面版本；本次不会安装或复用命令行版。");
    }

    private static bool HasDesktopLabel(string? value) => !string.IsNullOrWhiteSpace(value) &&
        DesktopLabel.IsMatch(Url.Replace(value, " "));

    // Used before stripping trailing notes from legacy names. A CN label must not
    // become the international read-only target merely by removing parentheses.
    internal static bool IsTraeChinaVariant(ToolFlowItem item) =>
        TraeName.IsMatch(Url.Replace(item.Name + " " + item.Kind, " ")) &&
        (HasChinaEditionLabel(item.Name) || HasChinaEditionLabel(item.Kind));

    private static bool HasChinaEditionLabel(string? value) => !string.IsNullOrWhiteSpace(value) &&
        ChinaEditionLabel.IsMatch(Url.Replace(value, " "));
}
