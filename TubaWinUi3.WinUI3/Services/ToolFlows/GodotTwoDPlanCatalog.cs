using System.Text;
using TubaWinUi3.Services;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>
/// Godot 2D 首场景的“可见方案”目录（示例模板）。
/// 由应用内置整理，不依赖 AI 回复能否生成结构化方案；所有未经本轮核实的内容
/// 必须逐项标为「待核实」，不得编造链接、价格或教程。查看与切换不启动安装。
/// </summary>
public sealed record GodotTwoDPlanItem
{
    public required string Name { get; init; }
    /// <summary>software / agent / asset / service（与服务端契约的既有字段口径一致）。</summary>
    public required string Kind { get; init; }
    /// <summary>在项目里的用途。</summary>
    public required string Purpose { get; init; }
    /// <summary>前后依赖（依赖哪一项、何时装、何时用）。</summary>
    public required string Dependency { get; init; }
    /// <summary>取得方式（应用内固定目标或手动在线获取）。</summary>
    public required string Acquisition { get; init; }
    /// <summary>账号、费用与网络条件。</summary>
    public required string Requirements { get; init; }
    /// <summary>适用依据（为什么放进这套流程）。</summary>
    public required string Applicability { get; init; }
    /// <summary>未知项；未经核实的内容一律在此说明并标注「待核实」。</summary>
    public required string Unknowns { get; init; }
    /// <summary>匹配应用内固定安装目标时为固定目标代码；否则 null（人工步骤）。</summary>
    public string? InstallTargetKey { get; init; }
}

public sealed record GodotTwoDPlan
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>主方案 / 备选一 / 备选二。</summary>
    public required string Badge { get; init; }
    /// <summary>一句话定位。</summary>
    public required string Positioning { get; init; }
    /// <summary>与其它方案的实际差异（不是换措辞）。</summary>
    public required string Difference { get; init; }
    /// <summary>素材路线摘要。</summary>
    public required string AssetRoute { get; init; }
    /// <summary>对比行：素材 / 账号依赖 / 费用口径。</summary>
    public required string CompareLine { get; init; }
    public required IReadOnlyList<GodotTwoDPlanItem> Items { get; init; }
    /// <summary>方案级未知项。</summary>
    public required IReadOnlyList<string> Unknowns { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// 用户在可见方案里核对和修改过的任务简报字段。
/// 每个字段都带来源标注：如实区分「来自你的描述」/「你的选择」/「应用预选（默认）」——
/// 只输入了一句「我想做一款 2D 游戏」时，平台/体量/素材/代码辅助只是应用预选，不是用户输入。
/// </summary>
public sealed record GodotTwoDPlanBrief
{
    public required string Goal { get; init; }
    public required string Platform { get; init; }
    public required string Scale { get; init; }
    public required string AssetRoute { get; init; }
    public required string CodeAssist { get; init; }

    public string GoalSource { get; init; } = GodotTwoDPlans.SourceUserText;
    public string PlatformSource { get; init; } = GodotTwoDPlans.SourcePreset;
    public string ScaleSource { get; init; } = GodotTwoDPlans.SourcePreset;
    public string AssetRouteSource { get; init; } = GodotTwoDPlans.SourcePreset;
    public string CodeAssistSource { get; init; } = GodotTwoDPlans.SourcePreset;
}

public static class GodotTwoDPlans
{
    /// <summary>“你建议”选项：允许用户不亲自决定，由应用按稳妥默认处理。</summary>
    public const string SuggestOption = "你建议";
    public const string LaterOption = "暂不决定";

    /// <summary>简报字段来源标注（如实区分：用户说的 / 用户改的 / 应用默认建议）。</summary>
    public const string SourceUserText = "来自你的描述";
    public const string SourceUserChoice = "你的选择";
    public const string SourcePreset = "应用预选（默认）";

    public static IReadOnlyList<string> AssetRouteOptions { get; } =
        ["自己画（像素/手绘）", "用免费素材包", "用 AI 生成素材", SuggestOption, LaterOption];

    public static IReadOnlyList<string> PlatformOptions { get; } =
        ["Windows 电脑（推荐先做这个）", "先做网页试玩版", LaterOption];

    public static IReadOnlyList<string> ScaleOptions { get; } =
        ["第一个可玩的小原型", "小巧但完整的小游戏", LaterOption];

    public static IReadOnlyList<string> CodeAssistOptions { get; } =
        [SuggestOption, "需要 AI Agent 辅助", "自己写代码", LaterOption];

    public const string DefaultGoal = "做一款 2D 游戏（先完成一个可玩的小原型）";

    /// <summary>主方案 + 最多两个有实际差异的备选。</summary>
    public static IReadOnlyList<GodotTwoDPlan> Catalog { get; } = BuildCatalog();

    /// <summary>素材路线答案 → 更适合的方案（“你建议/暂不决定”不改变推荐）。</summary>
    public static GodotTwoDPlan? SuggestPlanForAssetRoute(string? route) => route switch
    {
        "自己画（像素/手绘）" => Catalog[0],
        "用 AI 生成素材" => Catalog[1],
        "用免费素材包" => Catalog[2],
        _ => null,
    };

    public static string KindLabel(string kind) => kind switch
    {
        "software" => GodotFlowTexts.T("软件工具"),
        "agent" => GodotFlowTexts.T("AI Agent（候选，按需）"),
        "asset" => GodotFlowTexts.T("素材 / 资源（候选）"),
        "service" => GodotFlowTexts.T("在线服务（候选，按需）"),
        _ => kind,
    };

    /// <summary>固定安装范围：自动处理的项与需人工的项，供方案卡片与安装确认页共用。</summary>
    public static (IReadOnlyList<string> Automatic, IReadOnlyList<string> Manual) DescribeInstallScope(
        GodotTwoDPlan plan, IReadOnlyCollection<string>? knownTargets = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var known = new HashSet<string>(knownTargets ?? SystemInstallerKnownTargets(),
            StringComparer.OrdinalIgnoreCase);
        var automatic = new List<string>();
        var manual = new List<string>();
        foreach (var item in plan.Items)
        {
            var target = item.InstallTargetKey?.Trim();
            if (!string.IsNullOrEmpty(target) && known.Contains(target))
                automatic.Add($"GodotFlowTexts.T(item.Name) → {target}");
            else
                manual.Add(GodotFlowTexts.T(item.Name));
        }
        return (automatic, manual);
    }

    /// <summary>从用户描述里预判素材路线（仅关键词命中时预选，否则按“你建议”）。</summary>
    public static string? GuessAssetRoute(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return null;
        var text = userText;
        if (text.Contains("自己画") || text.Contains("手绘") || text.Contains("自己画的"))
            return "自己画（像素/手绘）";
        if (text.Contains("免费") && (text.Contains("素材") || text.Contains("素材包")))
            return "用免费素材包";
        if (text.Contains("生成") && System.Text.RegularExpressions.Regex.IsMatch(text, "(?i)(?<![A-Za-z])ai(?![A-Za-z])"))
            return "用 AI 生成素材";
        return null;
    }

    /// <summary>由方案与简报构建完整的工具流文本（保存到本地快照、用于后续分享与复核）。</summary>
    public static string BuildFlowText(GodotTwoDPlan plan, GodotTwoDPlanBrief brief)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(brief);
        var text = new StringBuilder();
        text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_8", "2D 游戏 · {0}（{1}）"), GodotFlowTexts.T(plan.Name), GodotFlowTexts.T(plan.Badge)));
        text.AppendLine(GodotFlowTexts.T("由应用内置示例模板整理；未经本轮核实的内容均标注「待核实」。"));
        text.AppendLine();
        text.AppendLine(GodotFlowTexts.T("【任务简报】"));
        text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_13", "· 目标：{0}（来源：{1}）"), GodotFlowTexts.T(brief.Goal), GodotFlowTexts.T(brief.GoalSource)));
        text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_12", "· 目标平台：{0}（来源：{1}）"), GodotFlowTexts.T(brief.Platform), GodotFlowTexts.T(brief.PlatformSource)));
        text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_11", "· 游戏体量：{0}（来源：{1}）"), GodotFlowTexts.T(brief.Scale), GodotFlowTexts.T(brief.ScaleSource)));
        text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_14", "· 素材路线：{0}（来源：{1}）"), GodotFlowTexts.T(brief.AssetRoute), GodotFlowTexts.T(brief.AssetRouteSource)));
        text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_10", "· 代码辅助：{0}（来源：{1}）"), GodotFlowTexts.T(brief.CodeAssist), GodotFlowTexts.T(brief.CodeAssistSource)));
        text.AppendLine(GodotFlowTexts.T("· 预算口径：免费开源优先；有账号或费用要求的项已逐项标注，价格不编造。"));
        text.AppendLine();
        text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_16", "【方案定位】{0}"), GodotFlowTexts.T(plan.Positioning)));
        text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_15", "【与其它方案的差异】{0}"), GodotFlowTexts.T(plan.Difference)));
        text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_17", "【素材路线】{0}"), GodotFlowTexts.T(plan.AssetRoute)));
        text.AppendLine();
        text.AppendLine(GodotFlowTexts.T("【工具与资源清单】"));
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            text.AppendLine($"{i + 1}. {GodotFlowTexts.T(item.Name)}（{GodotFlowTexts.T(KindLabel(item.Kind))}）");
            text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_4", "   用途：{0}"), GodotFlowTexts.T(item.Purpose)));
            text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_1", "   依赖：{0}"), GodotFlowTexts.T(item.Dependency)));
            text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_2", "   取得方式：{0}"), GodotFlowTexts.T(item.Acquisition)));
            text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_5", "   账号/费用/网络：{0}"), GodotFlowTexts.T(item.Requirements)));
            text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_6", "   适用依据：{0}"), GodotFlowTexts.T(item.Applicability)));
            text.AppendLine(string.Format(LocalizationService.L("GodotFlow_T_3", "   未知项：{0}"), GodotFlowTexts.T(item.Unknowns)));
        }
        text.AppendLine();
        if (plan.Unknowns.Count > 0)
        {
            text.AppendLine(GodotFlowTexts.T("【本方案未知项 / 待核实】"));
            foreach (var unknown in plan.Unknowns) text.AppendLine("· " + GodotFlowTexts.T(unknown));
            text.AppendLine();
        }
        text.AppendLine(GodotFlowTexts.T("【说明】"));
        foreach (var note in plan.Notes) text.AppendLine("· " + GodotFlowTexts.T(note));
        text.AppendLine("· " + GodotFlowTexts.T("本机系统（应用只读读取）：") + DescribeHostPlatform() + GodotFlowTexts.T("。"));
        foreach (var line in ProvenanceLines()) text.AppendLine("· " + line);
        return text.ToString().TrimEnd();
    }

    public static IReadOnlyList<string> ProvenanceLines() =>
    [
        GodotFlowTexts.T("字段来源见简报各行标注：「来自你的描述」=你在对话里说过的；「你的选择」=你在本页改的；「应用预选（默认）」=应用的默认建议，不是你的输入。"),
        GodotFlowTexts.T("来自本应用（只读）：本机系统版本读取结果；以及「该安装目标是否属于应用内固定安装器可处理范围」的能力判断。本页不检查这些工具是否已安装。"),
        GodotFlowTexts.T("未经本轮核实（请按「待核实」对待）：各软件的具体版本与下载体积、AI 服务价格与额度、素材站与素材包条款、系统兼容性细节、教程与外部链接（本模板不提供链接）。"),
        GodotFlowTexts.T("应用无法替你做：注册、登录、付费、系统提权与交互式安装步骤。"),
    ];

    /// <summary>只读读取本机系统版本并如实展示（不判定 10/11 代际，不冒充兼容性结论）。</summary>
    public static string DescribeHostPlatform()
    {
        var version = Environment.OSVersion.Version;
        return string.Format(LocalizationService.L("GodotFlow_T_9", "Windows 版本号 {0}.{1}.{2}（应用只读读取；不判定具体代际，兼容性以官方为准）"), version.Major, version.Minor, version.Build);
    }

    /// <summary>
    /// 选定前的分享说明：说明官方接收方与上报内容，关闭后不补传。
    /// 不改变上报行为——默认上传是用户先前定的产品决策，这里只做透明告知，不偷偷停掉。
    /// </summary>
    public static string SharingNotice(bool switchOn)
    {
        if (!switchOn)
            return GodotFlowTexts.T("分享：当前已关闭——本次选定只会保存在本机、不会发送；日后重新开启也不会补传这一次。可在设置中随时开启。");
        return LocalizationService.L("GodotFlow_ShareToOfficial",
            "分享已开启：选定后，项目目标、工具流名称、完整方案和本次可见对话会发送到枕星服务器，用于分析与改进推荐。无论是否安装都会分享，可在设置中关闭。");
    }

    /// <summary>
    /// 偏好与所选方案的一致性检查（显式偏好不得与方案结构矛盾；「你建议/暂不决定」不构成约束）。
    /// 返回 null = 一致；否则返回给用户的冲突说明——要求调整偏好或方案，不自动覆盖用户的选择。
    /// </summary>
    public static string? FindSelectionConflict(GodotTwoDPlan plan, GodotTwoDPlanBrief brief)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(brief);
        var routePlan = SuggestPlanForAssetRoute(brief.AssetRoute);
        if (routePlan is not null && !ReferenceEquals(routePlan, plan))
            return GodotFlowTexts.T("偏好与方案不一致：素材路线选了「") + GodotFlowTexts.T(brief.AssetRoute) + GodotFlowTexts.T("」，与它匹配的是「") + GodotFlowTexts.T(routePlan.Badge) + " · " +
                   GodotFlowTexts.T(routePlan.Name) + GodotFlowTexts.T("」；当前选中的是「") + GodotFlowTexts.T(plan.Badge) + " · " + GodotFlowTexts.T(plan.Name) + GodotFlowTexts.T("」。请改选匹配的方案，或把素材路线改为「") +
                   GodotFlowTexts.T(SuggestOption) + GodotFlowTexts.T("」。选定未进行。");
        if (brief.CodeAssist == "需要 AI Agent 辅助" && !plan.Items.Any(i => i.Kind == "agent"))
            return GodotFlowTexts.T("偏好与方案不一致：代码辅助选了「需要 AI Agent 辅助」，但「") + GodotFlowTexts.T(plan.Name) + GodotFlowTexts.T("」里没有 AI Agent 项。请改选「") +
                   GodotFlowTexts.T(Catalog[1].Badge) + " · " + GodotFlowTexts.T(Catalog[1].Name) + GodotFlowTexts.T("」，或把代码辅助改为「") + GodotFlowTexts.T(SuggestOption) + GodotFlowTexts.T("」/「自己写代码」。选定未进行。");
        return null;
    }

    /// <summary>从历史文本里找第一个明确的「2D 游戏」目标（重新打开历史时恢复方案入口用）。</summary>
    public static string? FindFirst2DGoal(IEnumerable<string?>? texts)
    {
        if (texts is null) return null;
        foreach (var text in texts)
            if (!string.IsNullOrWhiteSpace(text) && ToolFlowLearningGuide.IsExplicit2DGameGoal(text))
                return text.Trim();
        return null;
    }

    /// <summary>
    /// 把用户选定的一套方案整理成现有本地工具流快照（只保存，不安装）。
    /// 调用方是明确的 UI 选择动作；上传资格在选定时由分享开关固定进快照。
    /// </summary>
    public static ToolFlowSelection BuildSelection(
        GodotTwoDPlan plan,
        GodotTwoDPlanBrief brief,
        IReadOnlyList<ToolFlowConversationMessage> conversation,
        bool uploadEnabledAtSelection)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(conversation);
        var goal = string.IsNullOrWhiteSpace(brief.Goal) ? DefaultGoal : brief.Goal.Trim();
        return new ToolFlowSelection
        {
            FlowId = Guid.NewGuid().ToString("D"),
            SubmissionId = Guid.NewGuid().ToString("D"),
            Origin = ToolFlowOrigin.Assistant,
            SelectedAtUtc = DateTimeOffset.UtcNow,
            FlowName = string.Format(LocalizationService.L("GodotFlow_T_7", "2D 游戏 · {0}"), GodotFlowTexts.T(plan.Name)),
            ProjectGoal = goal,
            GoalDescription = goal,
            FlowText = BuildFlowText(plan, brief),
            Conversation = [.. conversation],
            Items = [.. plan.Items.Select(item => new ToolFlowItem
            {
                ItemId = Guid.NewGuid().ToString("D"),
                Name = GodotFlowTexts.T(item.Name),
                Kind = item.Kind,
                Version = null,
                SourceUrl = null,
                DownloadUrl = null,
                InstallTargetKey = item.InstallTargetKey,
                // 人工项把保存时的具体操作提示一并存进快照（恢复界面按快照展示，不重新编造）。
                ManualHint = item.InstallTargetKey is null ? BuildManualHint(item) : null,
            })],
            UploadEnabledAtSelection = uploadEnabledAtSelection,
        };
    }

    /// <summary>人工项的具体操作提示 = 取得方式 + 账号/费用/网络条件（来自目录，随快照保存）。</summary>
    private static string BuildManualHint(GodotTwoDPlanItem item)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(item.Acquisition)) parts.Add(GodotFlowTexts.T(item.Acquisition.Trim()));
        if (!string.IsNullOrWhiteSpace(item.Requirements)) parts.Add(GodotFlowTexts.T(item.Requirements.Trim()));
        var hint = string.Join("；", parts);
        return hint.Length <= 400 ? hint : hint[..400] + "…";
    }

    private static IReadOnlyCollection<string> SystemInstallerKnownTargets()
        => TubaWinUi3.Services.AppManagement.SystemInstaller.KnownTargets.ToArray();

    private static IReadOnlyList<GodotTwoDPlan> BuildCatalog()
    {
        var godot = GodotItem();
        var git = GitItem();
        var krita = KritaItem();
        var audacity = AudacityItem();
        var freeAudio = FreeAudioItem();
        var freeArt = FreeArtItem();

        var main = new GodotTwoDPlan
        {
            Id = "godot-2d-selfbuild",
            Name = "稳妥自建（免费开源）",
            Badge = "主方案",
            Positioning = "全免费开源、不依赖额外账号，素材自己画；最可控，但前期要学一点像素画。",
            Difference = "与备选一相比不依赖 AI 服务账号与网络；与备选二相比素材风格由你自己决定（而不是素材包给什么用什么）。",
            AssetRoute = "图像素材自己画；音效可先用免费素材包（可选）。",
            CompareLine = "素材：自绘 · 账号依赖：无 · 费用：全免费（具体版本待核实）",
            Items = [godot, git, krita, audacity, freeAudio],
            Unknowns =
            [
                "各软件的具体版本与下载体积由 winget 源决定，本轮未核实。",
                "Krita 的学习成本因人而异；是否顺手需要你实际试一下。",
                "若使用免费音效素材，其许可条款需要逐个核实（署名/商用限制）。",
            ],
            Notes =
            [
                "安装顺序建议：先装 Godot 验证能打开，再装其余工具。",
                "2D 项目默认不需要 3D 建模工具；仅当你的素材路线确实需要 3D 素材时再另行加入并核实。",
                "本方案里非「固定目标」的项都由你按说明手动完成，应用不代做。",
            ],
        };

        var aiAccelerated = new GodotTwoDPlan
        {
            Id = "godot-2d-ai-accelerated",
            Name = "AI 加速（代码与素材借助 AI）",
            Badge = "备选一",
            Positioning = "出素材、写代码最快；适合不会画画、希望 AI 帮忙写代码的人。",
            Difference = "与主方案相比引入 AI 编程 Agent 与 AI 生图工具两条外部依赖（账号/费用/网络），换来更快的素材与代码产出；AI 项全是候选，按需加入、不固定品牌。",
            AssetRoute = "图像素材以 AI 生成为主，再用绘画工具局部修改；音效可先用免费素材包。",
            CompareLine = "素材：AI 生成 · 账号依赖：AI 服务（候选） · 费用：未知（待核实）",
            Items = [godot, git, AiCodingAgentItem(), AiImageServiceItem(), krita, freeAudio],
            Unknowns =
            [
                "AI 服务的账号、价格、额度与生成素材的商用条款均待核实（本模板不编造价格）。",
                "AI 生成素材的分辨率与像素规范能否直接用于 Godot 项目，需要实际验证。",
                "所选 AI Agent 是否已安装、是否支持你的用法，以 AI 库与一键安装的实际状态为准。",
            ],
            Notes =
            [
                "AI 编程 Agent 与 AI 生图工具都是候选：可以直接用你现有的账号与服务，也可以先跳过，之后再补。",
                "把 AI 当助手而不是替代品：它写的代码也需要你运行验证。",
                "2D 项目默认不需要 3D 建模工具；仅当素材路线确实需要 3D 时再另行加入并核实。",
            ],
        };

        var freeAssetStart = new GodotTwoDPlan
        {
            Id = "godot-2d-free-assets",
            Name = "免费素材起步（不学画画）",
            Badge = "备选二",
            Positioning = "直接用现成免费素材包搭出可玩原型，先验证玩法、后补美术。",
            Difference = "与主方案相比不安装绘画工具、不需要自绘素材；与备选一相比不依赖 AI 生成服务——代价是风格受素材包限制，且要遵守各素材的许可条款。",
            AssetRoute = "图像与音效都先用免费素材包（许可需逐个核实）。",
            CompareLine = "素材：免费素材包 · 账号依赖：无（部分站点可能需要，待核实） · 费用：未知（待核实）",
            Items = [godot, git, freeArt, freeAudio, audacity],
            Unknowns =
            [
                "具体素材站、素材包及其许可（署名、商用限制）待核实；本模板不提供链接、不编造素材名称。",
                "现成素材的风格统一性有限，正式作品可能需要替换或润色。",
                "各软件的具体版本与下载体积由 winget 源决定，本轮未核实。",
            ],
            Notes =
            [
                "这条路线适合先跑通「安装 → 导入素材 → 做出可玩原型」的流程，再决定要不要学画画或转 AI 素材。",
                "使用任何素材前先核实其许可条款；发布前逐项确认署名与商用要求。",
                "2D 项目默认不需要 3D 建模工具；仅当素材路线确实需要 3D 时再另行加入并核实。",
            ],
        };

        return [main, aiAccelerated, freeAssetStart];
    }

    private static GodotTwoDPlanItem GodotItem() => new()
    {
        Name = "Godot 4（游戏引擎）",
        Kind = "software",
        Purpose = "2D 游戏的引擎与编辑器：搭建场景、写脚本、运行与导出。整套工具流的开发核心。",
        Dependency = "无前置，建议最先安装——其它工具都围绕它工作。",
        Acquisition = "应用内固定安装目标 godot（由应用内安装器经 winget 处理，不依赖外部网址）。",
        Requirements = "免费开源、无需账号；安装下载需联网（体积以实际安装为准，未核实）。",
        Applicability = "官方提供 Windows 桌面版（可运行于 Windows 10 及以上；具体兼容性以官方为准）。本页不检查本机系统版本与是否已安装。",
        Unknowns = "winget 源当前提供的具体版本号待核实；首个项目的上手步骤见安装后的本地上手卡。",
        InstallTargetKey = "godot",
    };

    private static GodotTwoDPlanItem GitItem() => new()
    {
        Name = "Git（版本控制）",
        Kind = "software",
        Purpose = "给项目做版本控制：每次改动可回滚，为以后备份与协作打底。",
        Dependency = "无安装依赖；建议在开始大量改代码前装好。",
        Acquisition = "应用内固定安装目标 git。",
        Requirements = "免费；本地使用不需要账号与联网（以后要推到托管平台时才需要账号，到时候再引导）。",
        Applicability = "官方支持 Windows；无论走哪条素材路线都推荐保留。",
        Unknowns = "是否需要现在就配置提交用的用户名/邮箱，由你决定（不改系统全局配置也能用）。",
        InstallTargetKey = "git",
    };

    private static GodotTwoDPlanItem KritaItem() => new()
    {
        Name = "Krita（像素与 2D 绘画）",
        Kind = "software",
        Purpose = "画像素画、立绘与 UI 素材，导出 PNG 后导入 Godot 使用。",
        Dependency = "安装无前置；使用时按「先画素材 → 再导入 Godot」的顺序。",
        Acquisition = "应用内固定安装目标 krita（同类开源绘画工具可替代；此处选用应用内已有固定目标）。",
        Requirements = "免费开源、无需账号。",
        Applicability = "官方提供 Windows 版；2D 素材不需要 3D 建模，绘画工具足够。",
        Unknowns = "鼠标绘制还是数位板、学习成本如何，因人而异（需要你实际体验）。",
        InstallTargetKey = "krita",
    };

    private static GodotTwoDPlanItem AudacityItem() => new()
    {
        Name = "Audacity（音频处理，可选）",
        Kind = "software",
        Purpose = "处理音效与简单录音：裁剪、调音量、转格式。",
        Dependency = "可以最后装；没有它也能先做出无声原型。",
        Acquisition = "应用内固定安装目标 audacity。",
        Requirements = "免费、无需账号。",
        Applicability = "官方支持 Windows；用免费音效或自己录声音时都用得上。",
        Unknowns = "若完全使用现成音效包且不做处理，这一项可以跳过。",
        InstallTargetKey = "audacity",
    };

    private static GodotTwoDPlanItem FreeAudioItem() => new()
    {
        Name = "免费音效素材（候选）",
        Kind = "asset",
        Purpose = "为原型提供占位/临时音效，先让游戏「有声音」，正式音效以后再替换。",
        Dependency = "无安装依赖；下载后在 Godot 里导入即可用。",
        Acquisition = "在线素材站点获取（候选方向：CC0 音效库、开源游戏素材站）——具体站点与条款待核实，本模板不提供链接。",
        Requirements = "多数免费但许可各异（可能有署名/商用限制），需逐个核实；需联网。",
        Applicability = "任何 2D 项目起步阶段都能用于占位。",
        Unknowns = "具体站点、素材与许可细节待核实。",
    };

    private static GodotTwoDPlanItem FreeArtItem() => new()
    {
        Name = "免费图像素材包（候选）",
        Kind = "asset",
        Purpose = "提供角色、场景、UI 等现成图像素材，不学画画也能先做出可玩原型。",
        Dependency = "无安装依赖；下载解压后在 Godot 里导入。",
        Acquisition = "在线素材站点获取（候选方向：CC0 游戏素材库）——具体站点与条款待核实，本模板不提供链接。",
        Requirements = "多数免费；许可各异（署名/商用限制），发布前需逐项确认；部分站点可能要注册（待核实）。",
        Applicability = "美术零基础、想先验证玩法的路线。",
        Unknowns = "素材风格统一性、分辨率与许可细节待核实。",
    };

    private static GodotTwoDPlanItem AiCodingAgentItem() => new()
    {
        Name = "AI 编程 Agent（候选，按需）",
        Kind = "agent",
        Purpose = "辅助写与修改 GDScript、解释报错、生成小功能；不是必需品。",
        Dependency = "建议在 Godot 就位后再引入，让它对着真实项目工作；可随时加、随时停。",
        Acquisition = "使用你现有或你选择的 AI Agent（不固定品牌）；应用 AI 库里已收录的可作候选，是否已安装以实际为准。",
        Requirements = "需要可用的账号与额度；可能有费用（价格与额度待核实）；需联网。",
        Applicability = "适合想加速写代码、或没有编程经验需要陪跑的用户。",
        Unknowns = "选哪一个 Agent、账号与费用怎么算，由你决定并需自行核实。",
    };

    private static GodotTwoDPlanItem AiImageServiceItem() => new()
    {
        Name = "AI 生图工具（候选，按需）",
        Kind = "service",
        Purpose = "生成像素风/概念图素材，再交给绘画工具做局部修改。",
        Dependency = "产出的素材经绘画工具（或直接）导入 Godot；建议先试免费额度再决定是否长期用。",
        Acquisition = "在线 AI 绘画服务（候选，不固定品牌）——服务可用性与条款待核实，本模板不提供链接。",
        Requirements = "需要账号；多数有免费额度或订阅制（价格与商用条款待核实）；需联网。",
        Applicability = "不会画画、希望快速得到大量素材草稿的路线。",
        Unknowns = "生成素材的分辨率与像素规范适配、商用许可，都需要实际验证。",
    };
}

/// <summary>
/// 「可见方案」入口提示的按会话状态：按会话代次（页面 _displayEpoch）管理，
/// 而不是页面实例级一次性布尔——新对话/切换会话后代次变化，可再次提示；
/// 重新打开含 2D 目标的历史时按当前代次恢复入口。非 2D 目标的文本不消耗代次。
/// </summary>
internal sealed class GodotPlanHintState
{
    private int _offeredEpoch = int.MinValue;

    public bool ShouldOffer(int conversationEpoch, string? userText)
    {
        if (conversationEpoch == _offeredEpoch) return false;
        if (!ToolFlowLearningGuide.IsExplicit2DGameGoal(userText)) return false;
        _offeredEpoch = conversationEpoch;
        return true;
    }
}
