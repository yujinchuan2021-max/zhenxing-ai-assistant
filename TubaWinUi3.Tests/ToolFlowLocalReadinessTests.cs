using System.Text.Json;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>Saved-plan fixtures and injected evidence only. No OS lookup, files, launch, or installer.</summary>
public sealed class ToolFlowLocalReadinessTests
{
    [Theory]
    [InlineData("Godot Engine（已安装，直接复用）", "2D游戏引擎")]
    [InlineData("Godot（已安装直接复用）", "游戏引擎")]
    [InlineData("Godot Engine", "software")]
    public async Task VerifiedLegacyGodotIsReadyWithoutRewritingTheConfirmedPlan(string name, string kind)
    {
        var item = Item("engine", name, kind, source: "https://godotengine.org/");
        var selection = Selection(item);
        var before = JsonSerializer.Serialize(selection);
        var requested = new List<string>();
        var preparation = await Service((key, _) =>
        {
            requested.Add(key);
            return Task.FromResult<InstalledToolEvidence?>(Evidence(key));
        }).PrepareAsync(selection.Items, selection.FlowText);

        var detected = Assert.Single(preparation.Rows);
        Assert.Equal(new[] { "godot" }, requested);
        Assert.Equal(ToolFlowPreparationState.AlreadyInstalled, detected.State);
        Assert.True(detected.DetectionAttempted);
        Assert.False(detected.AutomaticInstallationAllowed);
        Assert.Empty(preparation.ConfirmationItems);
        Assert.Equal("godot", ToolFlowToolAccess.AccessItem(detected).InstallTargetKey);
        Assert.Null(item.InstallTargetKey);

        var view = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(selection), preparation);
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: [Gui("godot")]);
        Assert.Equal(ToolFlowResumeItemState.InstalledOrDetected, Assert.Single(view.Rows).State);
        Assert.Empty(view.PendingAutomaticItems);
        Assert.Null(guide.CurrentStep);
        Assert.Single(guide.ReadySteps);
        Assert.Equal(ToolFlowDeliveryAction.OpenTool, guide.Delivery!.Action);
        Assert.Equal("godot", guide.Delivery.AccessEntry!.TargetKey);
        Assert.Null(guide.Delivery.SourceUrl);
        Assert.Equal(before, JsonSerializer.Serialize(selection));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("no-path")]
    [InlineData("wrong-product")]
    public async Task AReuseLabelWithoutUsableEvidenceDoesNotBecomeReadyOrGainInstallPermission(string evidenceKind)
    {
        var item = Item("engine", "Godot Engine（已安装，直接复用）", "2D游戏引擎");
        var evidence = evidenceKind switch
        {
            "missing" => null,
            "no-path" => new InstalledToolEvidence("godot", "Godot"),
            _ => Evidence("trae"),
        };
        var row = await Service((_, _) => Task.FromResult(evidence)).EvaluateAsync(item);
        Assert.False(row.IsSatisfied);
        Assert.False(row.AutomaticInstallationAllowed);
        Assert.NotEqual(ToolFlowPreparationState.PendingAutomatic, row.State);
    }

    [Fact]
    public async Task ProbeFailureStaysUnconfirmedWithoutPermittingAnInferredInstallation()
    {
        var row = await Service((_, _) => Task.FromException<InstalledToolEvidence?>(new IOException("Synthetic probe failure")))
            .EvaluateAsync(Item("engine", "Godot Engine", "游戏引擎"));
        Assert.Equal(ToolFlowPreparationState.DetectionFailed, row.State);
        Assert.False(row.IsSatisfied);
        Assert.False(row.AutomaticInstallationAllowed);
    }

    [Theory]
    [InlineData("unknown-target")]
    [InlineData("trae")]
    public async Task AnExplicitDifferentTargetCannotBecomeGodotThroughANameFallback(string key)
    {
        var row = await Service((target, _) => Task.FromResult<InstalledToolEvidence?>(Evidence(target)))
            .EvaluateAsync(Item("engine", "Godot Engine（已安装，直接复用）", "2D游戏引擎", key));
        Assert.False(row.IsSatisfied);
    }

    [Theory]
    [InlineData("account", "Godot 账号注册")]
    [InlineData("model", "Godot 模型接入")]
    [InlineData("service", "Godot 在线服务")]
    [InlineData("subscription", "Godot 订阅")]
    public async Task AccessRequirementsCannotBeCompletedByAnExecutableEvenWithASoftwareKey(string kind, string name)
    {
        var item = Item("access", name, kind, "godot", "https://godotengine.org/");
        var preparation = await Service((key, _) => Task.FromResult<InstalledToolEvidence?>(Evidence(key)))
            .PrepareAsync([item]);
        var row = Assert.Single(preparation.Rows);
        Assert.False(row.IsSatisfied);
        Assert.False(row.AutomaticInstallationAllowed);
        var view = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(Selection(item)), preparation);
        Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, Assert.Single(view.Rows).State);
        Assert.Empty(view.PendingAutomaticItems);
        Assert.NotNull(ToolFlowGoalGuide.Create(view).CurrentStep);
    }

    [Theory]
    [InlineData("Godot 素材库")]
    [InlineData("Godot 插件")]
    [InlineData("Godot 使用教程")]
    [InlineData("Godot + Trae")]
    public async Task RelatedNamesCannotBorrowTheInstalledEngineIdentity(string name)
    {
        var probes = 0;
        var row = await Service((key, _) =>
        {
            probes++;
            return Task.FromResult<InstalledToolEvidence?>(Evidence(key));
        }).EvaluateAsync(Item("related", name, "software"));
        Assert.False(row.IsSatisfied);
        Assert.False(row.AutomaticInstallationAllowed);
        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task AReadyDescriptiveAgentLeadsDeliveryBeforeTheInstalledEngine()
    {
        var selection = Selection(Godot(), Trae()); // Engine first must not dictate the next action.
        var view = await ReadyView(selection);
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: [Gui("godot"), Gui("trae")]);
        Assert.Null(guide.CurrentStep);
        Assert.Equal(ToolFlowDeliveryAction.OpenTool, guide.Delivery!.Action);
        Assert.Equal("agent", guide.Delivery.ItemId);
        Assert.Equal("trae", guide.Delivery.AccessEntry!.TargetKey);
        Assert.Null(guide.Delivery.SourceUrl);
    }

    [Fact]
    public async Task ModelSetupOpensTheSelectedLocalAgentWithoutClaimingSetupIsComplete()
    {
        var model = Item("model", "Trae 模型接入", "model", source: "https://www.trae.ai/") with
            { ManualHint = "在 Trae 中选择模型并填写自己的 API Key。" };
        var selection = Selection(Godot(), Trae(), model);
        var before = JsonSerializer.Serialize(selection);
        var view = await ReadyView(selection);
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: [Gui("godot"), Gui("trae")]);
        var current = Assert.IsType<ToolFlowGoalStep>(guide.CurrentStep);
        Assert.Equal("model", Assert.Single(current.Rows).ItemId);
        Assert.Equal(ToolFlowGoalStepState.NeedsUserAssist, current.State);
        Assert.False(current.HasAutomaticItems);
        Assert.True(current.CanConfirmManual);
        Assert.Equal("trae", current.AccessEntry!.TargetKey);
        Assert.Null(current.SourceUrl);
        Assert.Equal(ToolFlowDeliveryAction.None, guide.Delivery!.Action);
        Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, view.Rows.Single(row => row.ItemId == "model").State);
        Assert.Equal(before, JsonSerializer.Serialize(selection));
    }

    [Fact]
    public async Task DescriptiveModelSetupRetainsTheUsersDeferredInstructionsAndLocalAgentEntry()
    {
        const string hint = "可后续配置，先用 Trae 内置模型；后续按所选服务说明填写 API Key。";
        var model = Item("model", "自有国内模型 API 接入（可后续配置，先用 Trae 内置模型）",
            "模型接入（人工配置）", source: "https://platform.deepseek.com/") with { ManualHint = hint };
        var selection = Selection(Godot(), Trae(), model);
        var before = JsonSerializer.Serialize(selection);
        var view = await ReadyView(selection);
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: [Gui("godot"), Gui("trae")]);
        var steps = guide.LaterSteps.Concat(guide.CurrentStep is { } current ? [current] : []);
        var setup = Assert.Single(steps.Where(step => step.Rows.Any(row => row.ItemId == "model")));
        Assert.Null(guide.CurrentStep);
        Assert.True(setup.IsOptional);
        Assert.Equal(ToolFlowGoalStepState.NeedsUserAssist, setup.State);
        Assert.False(setup.HasAutomaticItems);
        Assert.Equal("trae", setup.AccessEntry!.TargetKey);
        Assert.Null(setup.SourceUrl);
        Assert.Contains(hint, setup.Hint);
        Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, view.Rows.Single(row => row.ItemId == "model").State);
        Assert.DoesNotContain(guide.ReadySteps, step => step.Rows.Any(row => row.ItemId == "model"));
        Assert.Equal(ToolFlowDeliveryAction.OpenTool, guide.Delivery!.Action);
        Assert.Equal("trae", guide.Delivery.AccessEntry!.TargetKey);
        Assert.Equal(before, JsonSerializer.Serialize(selection));
    }

    [Fact]
    public async Task ARequiredSignupKeepsItsWebsiteEvenWhenTheAgentIsInstalled()
    {
        const string signup = "https://platform.deepseek.com/";
        var account = Item("account", "DeepSeek 账号注册", "account", source: signup) with
            { ManualHint = "注册并登录服务，创建自己的 API Key。" };
        var guide = ToolFlowGoalGuide.Create(await ReadyView(Selection(Godot(), Trae(), account)),
            accessEntries: [Gui("godot"), Gui("trae")]);
        var current = Assert.IsType<ToolFlowGoalStep>(guide.CurrentStep);
        Assert.Equal("account", Assert.Single(current.Rows).ItemId);
        Assert.Equal(ToolFlowGoalStepState.NeedsUserAssist, current.State);
        Assert.Equal(signup, current.SourceUrl);
        Assert.Null(current.AccessEntry);
        Assert.Equal(ToolFlowDeliveryAction.None, guide.Delivery!.Action);
    }

    private static ToolFlowPreparationService Service(Func<string, CancellationToken, Task<InstalledToolEvidence?>> probe) =>
        new(["godot", "trae"], probe);

    private static async Task<ToolFlowResumeView> ReadyView(ToolFlowSelection selection)
    {
        var preparation = await Service((key, _) => Task.FromResult<InstalledToolEvidence?>(Evidence(key)))
            .PrepareAsync(selection.Items, selection.FlowText);
        return ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(selection), preparation);
    }

    private static InstalledToolEvidence Evidence(string key) =>
        new(key, key == "godot" ? "Godot" : "Trae", @"C:\FixtureTools\" + key + @"\" + key + ".exe", "4.7.2");
    private static ToolFlowToolAccessEntry Gui(string key) =>
        new(key, key == "godot" ? "Godot" : "Trae", @"C:\FixtureTools\" + key + @"\" + key + ".exe", @"C:\FixtureTools\" + key, true);
    private static ToolFlowItem Godot() => Item("engine", "Godot Engine（已安装，直接复用）", "2D游戏引擎", source: "https://godotengine.org/");
    private static ToolFlowItem Trae() => Item("agent", "Trae", "AI 编程 Agent", "trae", "https://www.trae.ai/");
    private static ToolFlowItem Item(string id, string name, string kind, string? target = null, string? source = null) =>
        new() { ItemId = id, Name = name, Kind = kind, InstallTargetKey = target, SourceUrl = source };
    private static ToolFlowSelection Selection(params ToolFlowItem[] items) => new()
    {
        FlowId = "fixture-local-ready-flow", SubmissionId = "fixture-local-ready-submission",
        Origin = ToolFlowOrigin.Assistant, SelectedAtUtc = DateTimeOffset.Parse("2026-10-05T02:00:00Z"),
        FlowName = "Godot + Trae", ProjectGoal = "准备制作一款 2D 游戏的工具流", GoalDescription = "Synthetic goal",
        FlowText = "Godot 与 Trae 准备完成后进入所选 Agent；接入步骤单独核对。",
        UploadEnabledAtSelection = false, Conversation = [], Items = items.ToList(),
    };
}
