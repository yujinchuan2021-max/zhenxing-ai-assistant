using TubaWinUi3.Services.AppManagement;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>
/// 可见方案（Godot 2D 示例模板）聚焦检查：
/// 1 主 + 2 备选有真实结构差异；每项写明用途/依赖/取得方式/账号费用/适用依据/未知项；
/// 不得编造链接、不得把 AI Agent / 素材站固定成某个品牌；安装范围只把应用内固定目标算作自动；
/// 用户选定后能落进现有本地快照（本层只保存，不触发安装 runner——安装由 UI 确认页另行触发）。
/// 四处窄修的反证检查：按会话的提示状态、选定前分享告知、来源/OS 事实、偏好与方案冲突。
/// 全部使用假数据根：不联网、不触真实数据目录、不读写真实设置。
/// </summary>
public sealed class GodotTwoDPlanTests
{
    [Fact]
    public void CatalogIsOneMainPlusTwoStructurallyDifferentAlternatives()
    {
        var plans = GodotTwoDPlans.Catalog;
        Assert.Equal(3, plans.Count);
        Assert.Equal(new[] { "主方案", "备选一", "备选二" }, plans.Select(p => p.Badge).ToArray());

        var main = plans[0];
        var ai = plans[1];
        var free = plans[2];

        // 结构差异（不是换措辞）：主方案自绘含绘画工具；备选一引入 AI Agent 与在线服务；
        // 备选二既没有绘画工具也没有 AI 项，改用免费素材包。
        Assert.Contains(main.Items, i => i.Name.Contains("Krita"));
        Assert.DoesNotContain(main.Items, i => i.Kind is "agent" or "service");
        Assert.Contains(ai.Items, i => i.Kind == "agent");
        Assert.Contains(ai.Items, i => i.Kind == "service");
        Assert.DoesNotContain(free.Items, i => i.Name.Contains("Krita"));
        Assert.DoesNotContain(free.Items, i => i.Kind is "agent" or "service");
        Assert.Contains(free.Items, i => i.Kind == "asset" && i.Name.Contains("图像"));

        var signatures = plans.Select(p => string.Join("|", p.Items.Select(i => i.Name))).ToArray();
        Assert.Equal(3, signatures.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryItemDescribesItsUse_WithoutFabricatedLinksOrFixedBrands()
    {
        // 不得写死的外部品牌（AI Agent / 生图 / 素材站方向）；应用内既有固定目标不在此列。
        string[] forbiddenBrands =
        [
            "Blender", "Codex", "Claude", "Copilot", "ChatGPT", "Cursor",
            "Stable Diffusion", "Midjourney", "Kenney", "itch.io", "OpenGameArt",
        ];

        foreach (var plan in GodotTwoDPlans.Catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(plan.Positioning));
            Assert.False(string.IsNullOrWhiteSpace(plan.Difference));
            Assert.False(string.IsNullOrWhiteSpace(plan.CompareLine));
            Assert.NotEmpty(plan.Unknowns);
            Assert.NotEmpty(plan.Notes);

            // 不写死“本机为 Windows 11”：只允许如实读取或写“待核实/不检查”。
            var planBlob = string.Join("\n",
                plan.Positioning, plan.Difference, plan.CompareLine,
                string.Join("\n", plan.Unknowns), string.Join("\n", plan.Notes));
            Assert.DoesNotContain("Windows 11", planBlob, StringComparison.Ordinal);

            foreach (var item in plan.Items)
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Name));
                Assert.Contains(item.Kind, new[] { "software", "agent", "asset", "service" });
                Assert.False(string.IsNullOrWhiteSpace(item.Purpose));
                Assert.False(string.IsNullOrWhiteSpace(item.Dependency));
                Assert.False(string.IsNullOrWhiteSpace(item.Acquisition));
                Assert.False(string.IsNullOrWhiteSpace(item.Requirements));
                Assert.False(string.IsNullOrWhiteSpace(item.Applicability));
                Assert.False(string.IsNullOrWhiteSpace(item.Unknowns));

                // AI Agent / 素材 / 在线服务方向的项必须是「候选」，不能是固定必选。
                if (item.Kind is "agent" or "asset" or "service")
                    Assert.Contains("候选", item.Name);

                var blob = string.Join("\n",
                    item.Name, item.Purpose, item.Dependency, item.Acquisition,
                    item.Requirements, item.Applicability, item.Unknowns);
                Assert.DoesNotContain("http", blob, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("www.", blob, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Windows 11", blob, StringComparison.Ordinal);
                foreach (var brand in forbiddenBrands)
                    Assert.DoesNotContain(brand, blob, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void InstallScopeTreatsOnlyKnownFixedTargetsAsAutomatic()
    {
        var main = GodotTwoDPlans.Catalog[0];
        var known = new[] { "godot", "git", "krita", "audacity" };

        var (automatic, manual) = GodotTwoDPlans.DescribeInstallScope(main, known);
        Assert.Equal(4, automatic.Count);
        Assert.Contains(automatic, a => a.Contains("godot"));
        Assert.Contains(automatic, a => a.Contains("krita"));
        Assert.Contains(manual, m => m.Contains("素材"));

        // 不在固定目标清单里的项一律算人工步骤（能力边界外不得假称可自动安装）。
        var (automaticNone, manualAll) = GodotTwoDPlans.DescribeInstallScope(main, new[] { "godot" });
        Assert.Single(automaticNone);
        Assert.Equal(main.Items.Count - 1, manualAll.Count);

        // 目录里的固定目标必须真实存在于应用内安装器（防止把不存在的能力写成可自动安装）。
        foreach (var plan in GodotTwoDPlans.Catalog)
            foreach (var item in plan.Items.Where(i => i.InstallTargetKey is not null))
                Assert.True(
                    SystemInstaller.KnownTargets.Contains(item.InstallTargetKey!, StringComparer.OrdinalIgnoreCase),
                    $"固定目标不存在：{item.InstallTargetKey}");
    }

    [Fact]
    public void PlannedSelectionSavesIntoExistingSnapshot_WithHonestLabelsOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "zxai-godot-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var plan = GodotTwoDPlans.Catalog[0];
            var conversation = new List<ToolFlowConversationMessage>
            {
                new() { Role = "user", Content = "我想做一款 2D 游戏" },
                new() { Role = "assistant", Content = "（示例）我整理了一份任务简报与工具流方案。" },
            };
            var brief = new GodotTwoDPlanBrief
            {
                Goal = "做一款 2D 平台跳跃小游戏，先在自己的电脑上玩",
                Platform = "Windows 电脑（推荐先做这个）",
                Scale = "第一个可玩的小原型",
                AssetRoute = "自己画（像素/手绘）",
                CodeAssist = "你建议",
                GoalSource = GodotTwoDPlans.SourceUserText,
                PlatformSource = GodotTwoDPlans.SourcePreset,
                ScaleSource = GodotTwoDPlans.SourcePreset,
                AssetRouteSource = GodotTwoDPlans.SourceUserChoice,
                CodeAssistSource = GodotTwoDPlans.SourcePreset,
            };

            var selection = GodotTwoDPlans.BuildSelection(plan, brief, conversation, uploadEnabledAtSelection: false);
            Assert.Contains("2D 游戏", selection.FlowName, StringComparison.Ordinal);
            Assert.Equal(brief.Goal, selection.ProjectGoal);
            Assert.Equal(plan.Items.Count, selection.Items.Count);
            Assert.False(selection.UploadEnabledAtSelection);

            // 诚实标注：来源说明 + 未知项「待核实」都随所选方案落进快照；没有任何外部链接。
            Assert.Contains("【任务简报】", selection.FlowText, StringComparison.Ordinal);
            Assert.Contains("【工具与资源清单】", selection.FlowText, StringComparison.Ordinal);
            Assert.Contains("【本方案未知项 / 待核实】", selection.FlowText, StringComparison.Ordinal);
            Assert.DoesNotContain("http", selection.FlowText, StringComparison.OrdinalIgnoreCase);

            // 来源逐字段标注：只输入一句“我想做一款 2D 游戏”时，平台/体量等是应用预选，不是用户输入。
            Assert.Contains("（来源：来自你的描述）", selection.FlowText, StringComparison.Ordinal);
            Assert.Contains("（来源：你的选择）", selection.FlowText, StringComparison.Ordinal);
            Assert.Contains("（来源：应用预选（默认））", selection.FlowText, StringComparison.Ordinal);
            Assert.Contains("「应用预选（默认）」=应用的默认建议，不是你的输入。", selection.FlowText, StringComparison.Ordinal);

            // OS 事实：如实读取本机版本（不判定 10/11 代际），且不写死 Windows 11。
            Assert.Contains("本机系统（应用只读读取）：Windows 版本号 ", selection.FlowText, StringComparison.Ordinal);
            Assert.DoesNotContain("Windows 11", selection.FlowText, StringComparison.Ordinal);
            var host = GodotTwoDPlans.DescribeHostPlatform();
            Assert.StartsWith("Windows 版本号 ", host, StringComparison.Ordinal);
            Assert.Contains("不判定具体代际", host, StringComparison.Ordinal);

            for (var i = 0; i < plan.Items.Count; i++)
                Assert.Equal(plan.Items[i].InstallTargetKey, selection.Items[i].InstallTargetKey);

            var store = new ToolFlowSelectionStore(root);
            var saved = store.SelectForInstall(selection);
            var reloaded = store.GetBySubmissionId(saved.SubmissionId);
            Assert.NotNull(reloaded);
            Assert.Equal(saved.SubmissionId, reloaded!.SubmissionId);
            Assert.Equal(selection.FlowText, reloaded.FlowText);
            Assert.Equal(conversation.Count, reloaded.Conversation.Count);
            Assert.Equal(saved.FlowId, reloaded.FlowId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AssetRouteAnswersPickAMatchingPlan_AndOfferSuggestOrLater()
    {
        Assert.Contains(GodotTwoDPlans.SuggestOption, GodotTwoDPlans.AssetRouteOptions);
        Assert.Contains(GodotTwoDPlans.LaterOption, GodotTwoDPlans.AssetRouteOptions);
        Assert.Contains(GodotTwoDPlans.LaterOption, GodotTwoDPlans.PlatformOptions);
        Assert.Contains(GodotTwoDPlans.LaterOption, GodotTwoDPlans.ScaleOptions);
        Assert.Equal(GodotTwoDPlans.SuggestOption, GodotTwoDPlans.CodeAssistOptions[0]);

        Assert.Same(GodotTwoDPlans.Catalog[0], GodotTwoDPlans.SuggestPlanForAssetRoute("自己画（像素/手绘）"));
        Assert.Same(GodotTwoDPlans.Catalog[2], GodotTwoDPlans.SuggestPlanForAssetRoute("用免费素材包"));
        Assert.Same(GodotTwoDPlans.Catalog[1], GodotTwoDPlans.SuggestPlanForAssetRoute("用 AI 生成素材"));
        Assert.Null(GodotTwoDPlans.SuggestPlanForAssetRoute(GodotTwoDPlans.SuggestOption));
        Assert.Null(GodotTwoDPlans.SuggestPlanForAssetRoute(GodotTwoDPlans.LaterOption));

        // 从用户描述里只做关键词预选，不做承诺；未命中时按「你建议」默认。
        Assert.Equal("自己画（像素/手绘）", GodotTwoDPlans.GuessAssetRoute("我想做一个 2D 游戏，素材我自己画"));
        Assert.Equal("用免费素材包", GodotTwoDPlans.GuessAssetRoute("想做 2d 游戏 用免费素材"));
        Assert.Equal("用 AI 生成素材", GodotTwoDPlans.GuessAssetRoute("想做 2D 游戏，素材用 AI 生成"));
        Assert.Null(GodotTwoDPlans.GuessAssetRoute("我想做一款 2D 游戏"));
    }

    [Fact]
    public void TwoDGameIntentDetectionSharesTheLearningGuideRule()
    {
        Assert.True(ToolFlowLearningGuide.IsExplicit2DGameGoal("我想做一款 2D 游戏"));
        Assert.True(ToolFlowLearningGuide.IsExplicit2DGameGoal("想做2d游戏"));
        Assert.False(ToolFlowLearningGuide.IsExplicit2DGameGoal("我要做 3D 游戏"));
        Assert.False(ToolFlowLearningGuide.IsExplicit2DGameGoal("今天天气不错"));
        Assert.False(ToolFlowLearningGuide.IsExplicit2DGameGoal(null));
    }

    [Fact]
    public void SelectionConflictsAreSurfaced_WithoutSilentlyOverwritingEitherSide()
    {
        var main = GodotTwoDPlans.Catalog[0];
        var ai = GodotTwoDPlans.Catalog[1];
        var free = GodotTwoDPlans.Catalog[2];

        GodotTwoDPlanBrief Brief(string assetRoute, string codeAssist) => new()
        {
            Goal = "做一款 2D 小游戏",
            Platform = GodotTwoDPlans.PlatformOptions[0],
            Scale = GodotTwoDPlans.ScaleOptions[0],
            AssetRoute = assetRoute,
            CodeAssist = codeAssist,
        };

        // 一致组合：不提示冲突。
        Assert.Null(GodotTwoDPlans.FindSelectionConflict(main, Brief("自己画（像素/手绘）", "自己写代码")));
        Assert.Null(GodotTwoDPlans.FindSelectionConflict(ai, Brief("用 AI 生成素材", GodotTwoDPlans.SuggestOption)));
        Assert.Null(GodotTwoDPlans.FindSelectionConflict(free, Brief("用免费素材包", GodotTwoDPlans.SuggestOption)));

        // 显式选「用 AI 生成素材」却选“自己画”的主方案 → 必须给出冲突说明（拦住选定，而非暗中改谁）。
        var assetConflict = GodotTwoDPlans.FindSelectionConflict(main, Brief("用 AI 生成素材", "自己写代码"));
        Assert.NotNull(assetConflict);
        Assert.Contains("素材路线", assetConflict!, StringComparison.Ordinal);
        Assert.Contains("选定未进行", assetConflict!, StringComparison.Ordinal);

        // 显式选「需要 AI Agent 辅助」却保存无 Agent 的方案 → 必须给出冲突说明。
        var agentConflict = GodotTwoDPlans.FindSelectionConflict(main, Brief("自己画（像素/手绘）", "需要 AI Agent 辅助"));
        Assert.NotNull(agentConflict);
        Assert.Contains("AI Agent", agentConflict!, StringComparison.Ordinal);
        Assert.Null(GodotTwoDPlans.FindSelectionConflict(ai, Brief("用 AI 生成素材", "需要 AI Agent 辅助")));

        // 「你建议 / 暂不决定」不构成约束：保留建议方案，不算冲突。
        Assert.Null(GodotTwoDPlans.FindSelectionConflict(main, Brief(GodotTwoDPlans.SuggestOption, GodotTwoDPlans.SuggestOption)));
        Assert.Null(GodotTwoDPlans.FindSelectionConflict(main, Brief(GodotTwoDPlans.LaterOption, GodotTwoDPlans.SuggestOption)));
        Assert.Null(GodotTwoDPlans.FindSelectionConflict(free, Brief(GodotTwoDPlans.LaterOption, GodotTwoDPlans.LaterOption)));
    }

    [Fact]
    public void SharingNoticeTellsOfficialRecipientAndPayload_AndOptOutNeverBackfills()
    {
        // 关闭：说明不会发送、也不会日后补传（不得号称“仅本地保存”按钮，但说明要如实）。
        var off = GodotTwoDPlans.SharingNotice(switchOn: false);
        Assert.Contains("已关闭", off, StringComparison.Ordinal);
        Assert.Contains("不会发送", off, StringComparison.Ordinal);
        Assert.Contains("不会补传", off, StringComparison.Ordinal);

        var ready = GodotTwoDPlans.SharingNotice(switchOn: true);
        Assert.Contains("发送到枕星服务器", ready, StringComparison.Ordinal);
        Assert.Contains("目标", ready, StringComparison.Ordinal);
        Assert.Contains("完整方案", ready, StringComparison.Ordinal);
        Assert.Contains("可见对话", ready, StringComparison.Ordinal);
        Assert.DoesNotContain("补填地址", ready, StringComparison.Ordinal);
        Assert.Contains("设置中关闭", ready, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoredHistoryFindsFirst2DGoal_ForRecoverableEntrypoint()
    {
        Assert.Equal("我想做一款 2D 游戏",
            GodotTwoDPlans.FindFirst2DGoal(new[] { "你好", "我想做一款 2D 游戏", "再做一个小工具" }));
        Assert.Equal("我想做一款 2D 游戏",
            GodotTwoDPlans.FindFirst2DGoal(new List<string?> { null, "  ", "我想做一款 2D 游戏" }));
        Assert.Null(GodotTwoDPlans.FindFirst2DGoal(new[] { "我要做 3D 游戏", "帮我画个图" }));
        Assert.Null(GodotTwoDPlans.FindFirst2DGoal(Array.Empty<string>()));
        Assert.Null(GodotTwoDPlans.FindFirst2DGoal(null));
    }

    [Fact]
    public void HintStateIsPerConversation_NotPageInstanceOneShot()
    {
        var state = new GodotPlanHintState();

        // 非 2D 目标：不提示，也不消耗当前会话的代次。
        Assert.False(state.ShouldOffer(conversationEpoch: 7, "今天天气不错"));
        Assert.True(state.ShouldOffer(conversationEpoch: 7, "我想做一款 2D 游戏"));
        // 同一会话只提示一次（避免重复刷按钮）。
        Assert.False(state.ShouldOffer(conversationEpoch: 7, "我想做一款 2D 游戏"));
        // 新对话/切换会话（代次变化）后可再次提示——按会话管理，而不是页面实例一次性布尔。
        Assert.True(state.ShouldOffer(conversationEpoch: 8, "我想做一款 2D 游戏"));
        Assert.True(state.ShouldOffer(conversationEpoch: 9, "重开历史：我想做一款 2D 游戏"));
        Assert.False(state.ShouldOffer(conversationEpoch: 9, "再提一次 2D 游戏"));
    }
}
