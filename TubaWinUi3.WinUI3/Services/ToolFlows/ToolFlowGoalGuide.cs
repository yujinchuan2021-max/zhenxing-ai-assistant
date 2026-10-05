using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TubaWinUi3.Services.ToolFlows;

internal enum ToolFlowGoalStepState
{
    PendingAutomatic,
    NeedsUserAssist,
    Failed,
    Ready,
    UserConfirmed,
    Running,
}

/// <summary>A presentation step. Rows retain the original item identities and evidence.</summary>
internal sealed record ToolFlowGoalStep(string Id, string Title, string Hint,
    IReadOnlyList<ToolFlowResumeItemRow> Rows, string ActionLabel, string? SourceUrl,
    ToolFlowGoalStepState State, bool HasAutomaticItems, bool CanConfirmManual, bool IsOptional)
{
    /// <summary>A verified local tool used for a still-unverified preparation step.</summary>
    internal ToolFlowToolAccessEntry? AccessEntry { get; init; }
}

/// <summary>
/// A pure projection of the existing readiness view. It neither changes the selection nor checks
/// tools, accounts, subscription rights, files or project progress.
/// </summary>
internal sealed record ToolFlowGoalGuide(string Goal, string Stage, string Summary,
    ToolFlowGoalStep? CurrentStep, IReadOnlyList<ToolFlowGoalStep> LaterSteps,
    IReadOnlyList<ToolFlowGoalStep> ReadySteps, bool HasPendingAutomatic)
{
    /// <summary>One local entry or service entry; its availability must be rechecked before an action.</summary>
    internal ToolFlowDeliveryPresentation? Delivery { get; init; }

    /// <summary>Follow the running item without duplicating it or hiding an earlier failed step.</summary>
    internal ToolFlowGoalGuide WithActiveItem(string itemId, string? hint)
    {
        var all = (CurrentStep is null ? Enumerable.Empty<ToolFlowGoalStep>() : [CurrentStep])
            .Concat(LaterSteps).Concat(ReadySteps).ToArray();
        var active = all.FirstOrDefault(step => step.Rows.Any(row => row.ItemId == itemId));
        if (active is null) return this;
        var later = LaterSteps.Where(step => step.Id != active.Id).ToList();
        if (CurrentStep is { } previous && previous.Id != active.Id) later.Insert(0, previous);
        return this with
        {
            CurrentStep = active with { State = ToolFlowGoalStepState.Running,
                CanConfirmManual = false, Hint = hint ?? active.Hint },
            LaterSteps = later,
            ReadySteps = ReadySteps.Where(step => step.Id != active.Id).ToArray(),
        };
    }

    private enum Purpose { Other, Service, Account, Subscription, Model, Asset, Network }

    private sealed record Member(ToolFlowResumeItemRow Row, ToolFlowItem? Item, Purpose Purpose,
        bool Optional, string? SourceUrl, string? Host);

    // A name-only fallback is deliberately small. An explicit different host always takes precedence.
    private static readonly (string Name, string Host)[] NamedServices =
    [
        ("Suno", "suno.com"), ("Midjourney", "midjourney.com"),
        ("ChatGPT", "chatgpt.com"), ("Claude", "claude.ai"),
        ("Hugging Face", "huggingface.co"), ("Replicate", "replicate.com"),
        ("Leonardo", "leonardo.ai"),
    ];

    internal static ToolFlowGoalGuide Create(ToolFlowResumeView view, bool running = false,
        string? activeStep = null, IReadOnlyList<ToolFlowToolAccessEntry>? accessEntries = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        var members = view.Rows.Select(row =>
        {
            // Ambiguous or unmatched identities cannot supply a link or a grouping target.
            var matches = view.Selection.Items.Where(item => item.ItemId == row.ItemId).Take(2).ToArray();
            var item = matches.Length == 1 ? matches[0] : null;
            var source = SafeSourceUrl(item?.SourceUrl);
            return new Member(row, item, GetPurpose(row), IsExplicitlyOptional(row), source,
                source is null ? null : NormalizeHost(new Uri(source).IdnHost));
        }).ToArray();

        var groups = new List<List<Member>>();
        var groupedServices = new Dictionary<string, List<Member>>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            var service = ServiceIdentity(member, members);
            if (service is null)
            {
                groups.Add([member]);
                continue;
            }
            // Explicit optional requirements never become blockers by joining a required account.
            var groupKey = service + (member.Optional ? "|optional" : "|required");
            if (!groupedServices.TryGetValue(groupKey, out var group))
            {
                group = [];
                groupedServices.Add(groupKey, group);
                groups.Add(group);
            }
            group.Add(member);
        }

        // Only an unambiguous required Agent can host a generic model-setup step.
        // Optional helpers, duplicate identities and multi-Agent plans need explicit routing.
        var requiredAgents = members.Where(member => !member.Optional && member.Item is not null &&
            ToolFlowItemSemantics.IsAgent(member.Item)).Take(2).ToArray();
        ToolFlowToolAccessEntry? agentEntry = null;
        if (requiredAgents.Length == 1 && requiredAgents[0] is { } candidate &&
            candidate.Row.State == ToolFlowResumeItemState.InstalledOrDetected &&
            members.Count(member => member.Row.ItemId == candidate.Row.ItemId) == 1)
        {
            var target = ToolFlowItemSemantics.ReadyTarget(candidate.Item!, candidate.Row);
            var entries = (accessEntries ?? []).Where(entry =>
                string.Equals(entry.TargetKey, target, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.ExecutablePath) &&
                (entry.IsGui || ToolFlowToolAccess.IsInteractiveAgent(entry))).Take(2).ToArray();
            if (entries.Length == 1) agentEntry = entries[0];
        }
        var steps = groups.Select(group => BuildStep(group, agentEntry)).ToArray();
        var required = steps.Where(step => !step.IsOptional && !IsHandled(step))
            .OrderBy(Priority).ToArray();
        var current = required.FirstOrDefault();
        if (running && current is { HasAutomaticItems: true })
            current = current with
            {
                State = ToolFlowGoalStepState.Running,
                CanConfirmManual = false,
                Hint = string.IsNullOrWhiteSpace(activeStep)
                    ? Text("AiTask_Preparing", "正在读取清单，准备逐项检测…", "Reading the list and checking items…")
                    : activeStep,
            };

        var later = required.Skip(1).Concat(steps.Where(step => step.IsOptional && !IsHandled(step))).ToArray();
        var ready = steps.Where(IsHandled).ToArray();
        var summary = view.Rows.Count == 0
            ? Text("AiGoal_SummaryEmpty", "当前方案还没有准备清单。", "This plan has no preparation list yet.")
            : required.Length > 0
                ? Format("AiGoal_SummaryPending", "还需处理 {0} 个准备步骤",
                    "{0} preparation steps still need attention", required.Length)
                : view.Rows.Any(row => row.State == ToolFlowResumeItemState.UserReportedDone)
                    ? Text("AiDelivery_SummarySelfReported", "准备记录包含你确认的事项；实际使用尚未验证。",
                        "Preparation includes your confirmations; actual use has not been verified.")
                    : Text("AiDelivery_SummaryPrepared", "必要工具已有准备记录；账号和实际使用仍需核对。",
                        "Required tools have preparation records; account access and actual use still need checking.");
        var stage = running
            ? Text("AiTask_Preparing", "正在读取清单，准备逐项检测…", "Reading the list and checking items…")
            : current?.HasAutomaticItems == true || view.Rows.Count == 0
                ? Text("AiDelivery_StageTools", "准备工具", "Prepare tools")
                : current is not null
                    ? current.Rows.Any(row => !IsHandled(row) && GetPurpose(row) is
                        Purpose.Model or Purpose.Account or Purpose.Subscription or Purpose.Network or Purpose.Service)
                        ? Text("AiDelivery_StageAccess", "完成接入", "Complete access setup")
                        : Text("AiDelivery_StageRequirements", "完成必要准备", "Complete required preparation")
                    : Text("AiDelivery_StageTry", "可以开始试用", "Ready to try");
        var guide = new ToolFlowGoalGuide(view.Selection.ProjectGoal, stage, summary, current, later, ready,
            required.Any(step => step.HasAutomaticItems));
        return guide with { Delivery = ToolFlowDeliveryPresentation.Create(view, guide, accessEntries ?? []) };
    }

    private static ToolFlowGoalStep BuildStep(List<Member> members, ToolFlowToolAccessEntry? agentEntry)
    {
        var rows = members.Select(member => member.Row).ToArray();
        var pending = members.Where(member => !IsHandled(member.Row)).ToArray();
        var automatic = pending.Any(member => member.Row.CanContinueAutomatically);
        var state = pending.Length == 0
            ? rows.Any(row => row.State == ToolFlowResumeItemState.UserReportedDone)
                ? ToolFlowGoalStepState.UserConfirmed : ToolFlowGoalStepState.Ready
            : pending.Any(member => member.Row.State == ToolFlowResumeItemState.Failed)
                ? ToolFlowGoalStepState.Failed
                : automatic ? ToolFlowGoalStepState.PendingAutomatic : ToolFlowGoalStepState.NeedsUserAssist;
        var actionMembers = pending.Length == 0 ? members.ToArray() : pending;
        var purpose = automatic ? Purpose.Other : ActionPurpose(actionMembers);
        var name = DisplayName(members, actionMembers);
        var title = automatic ? Format("AiGoal_AutomaticTitle", "准备 {0}", "Prepare {0}", name)
            : purpose switch
            {
                Purpose.Network => Format("AiGoal_NetworkTitle", "让 {0} 可以联网", "Connect {0} to the network", name),
                Purpose.Account => Format("AiGoal_AccountTitle", "登录 {0} 并确认使用条件",
                    "Sign in to {0} and check access requirements", name),
                Purpose.Subscription => Format("AiGoal_SubscriptionTitle", "核对 {0} 的订阅要求",
                    "Check subscription requirements for {0}", name),
                Purpose.Model => Format("AiGoal_ModelTitle", "为 {0} 配置模型", "Configure a model for {0}", name),
                Purpose.Asset => Format("AiGoal_AssetTitle", "准备 {0} 所需素材", "Prepare assets for {0}", name),
                _ => Format("AiGoal_ManualTitle", "配置 {0}", "Set up {0}", name),
            };

        string hint;
        if (state == ToolFlowGoalStepState.UserConfirmed)
            hint = Text("AiGoal_ConfirmedHint", "已按你的声明收起这一步；应用尚未验证，原始记录保留在详情中。",
                "This step is handled according to your report; the app has not verified it, and the original evidence remains in details.");
        else if (state == ToolFlowGoalStepState.Ready)
            hint = Short(rows[0].StatusLine, 200);
        else if (automatic)
            hint = Text("AiGoal_AutomaticHint", "先检测已有工具，再准备尚缺的项；失败原因保留在详情中。",
                "Check existing tools, then prepare missing items; failure details remain available.");
        else
        {
            // Completed group members remain in details, but their actions are never requested again.
            hint = string.Join(LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? "; " : "；",
                pending.Select(member => Short(member.Row.ManualHint, 160))
                .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal));
            if (hint.Length == 0)
                hint = purpose == Purpose.Subscription
                    ? Format("AiGoal_SubscriptionHint", "先查看 {0} 的使用条件，确认方案是否要求订阅后再决定。",
                        "Check the terms for {0}, then decide whether the plan requires a subscription.", name)
                    : purpose == Purpose.Other
                        ? Text("AiGoal_LocalPreparationUnknown", "尚未核实本机工具或具体配置要求。请在应用中心登记已有工具位置后重新检测，或让助手补全必要操作。",
                            "The local tool or setup requirements have not been verified. Register its location in App Center and check again, or ask the assistant for the missing steps.")
                        : Format("AiGoal_ManualHint", "打开准备说明，核对 {0} 的要求并完成配置。",
                            "Open the preparation notes, check the requirements for {0}, and configure it.", name);
            hint = Short(hint, 220);
        }

        // A product's homepage is reference information, not a configuration action.
        var source = actionMembers.Where(member => member.Item is not null && ToolFlowItemSemantics.HasWebAction(member.Item))
            .Select(member => member.SourceUrl).FirstOrDefault(url => url is not null);
        if (source is null && actionMembers.Any(member => member.Item is not null && ToolFlowItemSemantics.HasWebAction(member.Item)))
            source = members.Where(member => member.Item is not null && ToolFlowItemSemantics.HasWebAction(member.Item))
                .Select(member => member.SourceUrl).FirstOrDefault(url => url is not null);
        var localSetup = pending.Length > 0 && !automatic && purpose == Purpose.Model && source is null ? agentEntry : null;
        if (localSetup is not null)
        {
            title = Format("AiGoal_LocalModelTitle", "在 {0} 中核对模型接入", "Check model access in {0}", localSetup.Name);
            var localHint = Format("AiGoal_LocalModelHint", "打开 {0}，先确认当前版本支持方案要求的模型接入方式；支持时填写服务地址、模型和凭据并测试连接。不支持则返回调整 Agent 或模型方案。",
                "Open {0} and check whether this version supports the planned model connection. If supported, enter the service address, model and credentials, then test it. Otherwise return to revise the Agent or model choice.", localSetup.Name);
            hint = pending.Any(member => !string.IsNullOrWhiteSpace(member.Row.ManualHint))
                ? hint + "\n" + localHint : localHint;
        }
        var action = pending.Length == 0
            ? Text("AiTask_Details", "查看清单", "View list")
            : automatic ? Text("AiTask_Continue", "继续准备", "Continue preparation")
            : localSetup is not null ? Text("AiGoal_OpenSetupTool", "打开 Agent 核对接入", "Open Agent to check access")
            : source is not null ? Text("AiGoal_OpenWebsite", "打开网站", "Open website")
            : Text("AiTask_Guide", "查看准备说明", "View preparation notes");
        return new(StableId(rows), title, hint, rows, action, source, state, automatic,
            pending.Length > 0 && !automatic, members.All(member => member.Optional)) { AccessEntry = localSetup };
    }

    private static int Priority(ToolFlowGoalStep step) => step.HasAutomaticItems
        ? step.Rows.Any(row => row.CanContinueAutomatically && row.State == ToolFlowResumeItemState.Failed) ? 0 : 1
        : step.State == ToolFlowGoalStepState.Failed ? 2 : 3;

    private static bool IsHandled(ToolFlowGoalStep step) => step.State is
        ToolFlowGoalStepState.Ready or ToolFlowGoalStepState.UserConfirmed;

    private static bool IsHandled(ToolFlowResumeItemRow row) => row.State is
        ToolFlowResumeItemState.InstalledOrDetected or ToolFlowResumeItemState.UserReportedDone;

    private static Purpose ActionPurpose(IReadOnlyList<Member> members)
    {
        // Access to a service can include account and subscription requirements. The hints retain both.
        foreach (var purpose in new[] { Purpose.Network, Purpose.Model, Purpose.Asset, Purpose.Subscription,
                     Purpose.Account, Purpose.Service })
            if (members.Any(member => member.Purpose == purpose)) return purpose;
        return Purpose.Other;
    }

    private static Purpose GetPurpose(ToolFlowResumeItemRow row)
    {
        var kind = row.Kind.Trim().ToLowerInvariant();
        var name = row.Name;
        var hint = row.ManualHint ?? "";
        if (kind is "asset" or "assets" or "resource" or "resources" || Matches(name, "素材|资源包|音效|贴图|asset|resource|texture"))
            return Purpose.Asset;
        if (kind is "network" or "connectivity" || Matches(name, "网络|联网|代理|(?<![a-z])vpn(?![a-z])|network|proxy")
            || Matches(hint, "(?:配置|设置|连接).{0,8}(?:网络|代理)|(?:configure|connect|set up).{0,12}(?:network|proxy|vpn)"))
            return Purpose.Network;
        if (kind == "account") return Purpose.Account;
        if (ToolFlowItemSemantics.IsModelSetup(new ToolFlowItem { ItemId = row.ItemId, Name = name, Kind = row.Kind, ManualHint = row.ManualHint }) ||
            kind == "model" || Matches(hint, "(?:配置|设置).{0,8}模型|模型.{0,8}(?:配置|设置)|configure.{0,12}model|model.{0,12}config|api\\s*(?:key|密钥)"))
            return Purpose.Model;
        if (kind is "subscription" or "membership" or "license" || Matches(name, "订阅|会员|subscription|membership"))
            return Purpose.Subscription;
        if (kind == "account" || Matches(name + " " + hint, "账号|账户|登录|注册|account|sign[ -]?in|log[ -]?in|register"))
            return Purpose.Account;
        if (kind is "service" or "website" or "web" or "platform" or "online_service") return Purpose.Service;
        return Purpose.Other;
    }

    private static bool IsExplicitlyOptional(ToolFlowResumeItemRow row)
    {
        var text = row.Name + " " + row.ManualHint;
        if (Matches(text, "可(?:以)?(?:后续|稍后)配置|can (?:configure|set up) (?:later|afterwards)") &&
            !Matches(text, "必须|不可延后|不能延后|不可稍后|不能稍后|(?<![a-z])(?:must|required)(?![a-z])|cannot defer")) return true;
        text = Regex.Replace(text, "(?:不可选|非可选|不是可选|not\\s+optional|non[- ]optional|optional\\s*[:=]\\s*false)",
            "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Matches(text, "可选|按需|(?<![a-z-])optional(?![a-z])");
    }

    private static bool IsServiceMember(Member member)
    {
        var kind = member.Row.Kind.Trim().ToLowerInvariant();
        if (member.Purpose == Purpose.Asset) return false;
        if (kind is "asset" or "assets" or "resource" or "resources" or "model" or "software" or "agent") return false;
        return kind is "service" or "website" or "web" or "platform" or "online_service" or "account"
            or "subscription" or "membership" or "license"
            || member.Purpose is Purpose.Account or Purpose.Subscription;
    }

    private static string? ServiceIdentity(Member member, IReadOnlyList<Member> members)
    {
        if (!IsServiceMember(member)) return null;
        if (member.Host is { } host) return "host:" + host;
        // An unsafe supplied URL cannot acquire trust through a familiar name or target key.
        if (!string.IsNullOrWhiteSpace(member.Item?.SourceUrl)) return null;
        if (member.Item?.InstallTargetKey?.Trim() is { Length: > 0 } target)
        {
            var hosts = members.Where(other => IsServiceMember(other) && other.Host is not null
                    && string.Equals(other.Item?.InstallTargetKey?.Trim(), target, StringComparison.OrdinalIgnoreCase))
                .Select(other => other.Host).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            return hosts.Length == 1 ? "host:" + hosts[0] : "target:" + target;
        }
        var named = NamedService(member.Row.Name);
        return named is { } service ? "host:" + service.Host : null;
    }

    private static string DisplayName(IReadOnlyList<Member> members, IReadOnlyList<Member> actionMembers)
    {
        if (members.Count > 1)
        {
            var names = members.Select(member => NamedService(member.Row.Name)).Where(service => service is not null)
                .Select(service => service!.Value.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length == 1) return names[0];
            var host = members.Select(member => member.Host).FirstOrDefault(value => value is not null);
            if (host is not null) return host;
        }
        return Short(actionMembers[0].Row.Name, 80);
    }

    private static (string Name, string Host)? NamedService(string name)
    {
        var matches = NamedServices.Where(service => Matches(name,
            "(?<![a-z0-9])" + Regex.Escape(service.Name) + "(?![a-z0-9])")).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static string? SafeSourceUrl(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Any(char.IsControl) || source.Contains('\\')
            || !Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || !uri.IsDefaultPort
            || uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns || !uri.IdnHost.Contains('.')
            || IPAddress.TryParse(uri.IdnHost, out _)) return null;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host == "localhost" || new[] { ".localhost", ".local", ".lan", ".internal", ".home", ".invalid" }
            .Any(host.EndsWith)) return null;
        return uri.AbsoluteUri;
    }

    private static string NormalizeHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static string StableId(IReadOnlyList<ToolFlowResumeItemRow> rows)
    {
        var identities = string.Concat(rows.Select(row => row.ItemId).Order(StringComparer.Ordinal)
            .Select(id => id.Length.ToString(CultureInfo.InvariantCulture) + ":" + id));
        return "goal-step:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identities)))[..24];
    }

    private static bool Matches(string text, string pattern) => Regex.IsMatch(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Short(string? value, int limit)
    {
        var text = Regex.Replace(value?.Trim() ?? "", "\\s+", " ");
        if (text.Length <= limit) return text;
        var cut = limit - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }

    private static string Text(string key, string zh, string en) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? en : zh);

    private static string Format(string key, string zh, string en, object value) =>
        string.Format(CultureInfo.CurrentCulture, Text(key, zh, en), value);
}
