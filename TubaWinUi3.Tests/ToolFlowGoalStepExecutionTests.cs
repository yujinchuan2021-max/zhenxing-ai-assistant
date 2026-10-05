using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowGoalStepExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-goal-step-" + Guid.NewGuid().ToString("N"));
    public ToolFlowGoalStepExecutionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task ConfirmedPlanRunsAllSelectedSoftwareWithoutStoppingAtAccountsOrOptionalGroups()
    {
        var store = new ToolFlowSelectionStore(_root);
        var plan = Plan();
        // A human-only step between two software items cannot stop the automatic chain.
        plan = plan with { Items = [plan.Items[0], plan.Items[2], plan.Items[1]] };
        var selection = store.SelectForInstall(plan);
        var installer = new FakeInstaller();
        var stages = new Stages();
        var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId,
            stageProgress: stages);
        Assert.Equal(new[] { "godot", "blender" }, installer.Installed);
        Assert.Equal(2, result.InstalledCount);
        Assert.Equal(1, result.ManualStepCount);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(ToolFlowInstallPhase.Completed, stages.Items.Last().Phase);
        Assert.All(stages.Items, stage => Assert.Equal(3, stage.TotalCount));

        // A repeat confirmation resumes safely by reusing software, never reinstalling it.
        var resumed = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId);
        Assert.Equal(2, resumed.AlreadyInstalledCount);
        Assert.Equal(2, installer.Installed.Count);
    }

    [Fact]
    public async Task ConfirmedPythonRunsBeforeItsSelectedDependentWithoutAddingSoftware()
    {
        var store = new ToolFlowSelectionStore(_root);
        var plan = Plan();
        plan = plan with { Items = [
            new() { ItemId = "11111111-1111-1111-1111-111111111111", Name = "vLLM", Kind = "software", InstallTargetKey = "vllm" },
            new() { ItemId = "22222222-2222-2222-2222-222222222222", Name = "Python", Kind = "software", InstallTargetKey = "python" },
            plan.Items[0]] };
        var selection = store.SelectForInstall(plan);
        var installer = new FakeInstaller();
        var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId);
        Assert.Equal(new[] { "python", "vllm", "godot" }, installer.Installed);
        Assert.Equal(3, result.InstalledCount);
        Assert.Equal(new[] { "vllm", "python", "godot" }, store.GetBySubmissionId(selection.SubmissionId)!.Items.Select(item => item.InstallTargetKey));
    }

    [Fact]
    public async Task ExplicitDependentRetryDoesNotExpandAuthorizationToItsRuntime()
    {
        var store = new ToolFlowSelectionStore(_root);
        var plan = Plan() with { Items = [
            new() { ItemId = "11111111-1111-1111-1111-111111111111", Name = "vLLM", Kind = "software", InstallTargetKey = "vllm" },
            new() { ItemId = "22222222-2222-2222-2222-222222222222", Name = "Python", Kind = "software", InstallTargetKey = "python" }] };
        var selection = store.SelectForInstall(plan);
        var installer = new FakeInstaller();
        await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId, itemIds: [selection.Items[0].ItemId]);
        Assert.Equal(new[] { "vllm" }, installer.Installed);
        Assert.DoesNotContain("python", installer.Probed);
    }

    [Fact]
    public async Task ExplicitStepDoesNotProbeInstallOrRecordFollowingAndOptionalItems()
    {
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(Plan());
        var installer = new FakeInstaller();
        var stages = new Stages();
        var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId,
            stageProgress: stages, itemIds: [selection.Items[0].ItemId]);
        Assert.Single(result.Items);
        Assert.Equal(selection.Items[0].ItemId, result.Items[0].ItemId);
        Assert.Equal(ToolFlowInstallItemStatus.Installed, result.Items[0].Status);
        Assert.Equal(new[] { "godot" }, installer.Installed.ToArray());
        Assert.All(installer.Probed, key => Assert.Equal("godot", key));
        Assert.All(stages.Items, stage => Assert.Equal(1, stage.TotalCount));
        Assert.All(store.GetBySubmissionId(selection.SubmissionId)!.Events,
            e => Assert.Equal(selection.Items[0].ItemId, e.ItemId));
        Assert.Equal(selection.Items, store.GetBySubmissionId(selection.SubmissionId)!.Items);
    }

    [Fact]
    public async Task UnknownStepIsRejectedBeforeAnyPreparationOrSideEffect()
    {
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(Plan());
        var installer = new FakeInstaller();
        await Assert.ThrowsAsync<ArgumentException>(() => new ToolFlowInstallRunner(store, installer)
            .RunAsync(selection.SubmissionId, itemIds: ["not-in-plan"]));
        Assert.Empty(installer.Probed);
        Assert.Empty(installer.Installed);
        Assert.Empty(store.GetBySubmissionId(selection.SubmissionId)!.Events);
    }

    [Fact]
    public async Task EmptyStepDoesNotFallBackToRunningTheWholePlan()
    {
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(Plan());
        var installer = new FakeInstaller();
        var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId, itemIds: []);
        Assert.Empty(result.Items);
        Assert.Empty(installer.Probed);
        Assert.Empty(installer.Installed);
        Assert.Empty(store.GetBySubmissionId(selection.SubmissionId)!.Events);
    }

    [Fact]
    public async Task DuplicateStepIdsDoNotExecuteTheSameItemTwice()
    {
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(Plan());
        var installer = new FakeInstaller();
        var id = selection.Items[0].ItemId;
        var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId, itemIds: [id, id]);
        Assert.Single(result.Items);
        Assert.Single(installer.Installed);
    }

    private static ToolFlowSelection Plan() => new()
    {
        FlowId = Guid.NewGuid().ToString("D"), SubmissionId = Guid.NewGuid().ToString("D"),
        Origin = ToolFlowOrigin.User, SelectedAtUtc = DateTimeOffset.UtcNow, FlowName = "Small game",
        ProjectGoal = "Create a small game", GoalDescription = "Create a small game", FlowText = "Fake engine and optional art editor",
        UploadEnabledAtSelection = false, Conversation = [],
        Items = [new() { ItemId = Guid.NewGuid().ToString("D"), Name = "Godot", Kind = "software", InstallTargetKey = "godot" },
            new() { ItemId = Guid.NewGuid().ToString("D"), Name = "Blender (optional)", Kind = "software", InstallTargetKey = "blender" },
            new() { ItemId = Guid.NewGuid().ToString("D"), Name = "Model access", Kind = "account", ManualHint = "Sign in and check access" }],
    };

    private sealed class FakeInstaller : IToolFlowInstaller
    {
        public IReadOnlyCollection<string> KnownTargets => ["godot", "blender", "python", "vllm"];
        public List<string> Probed { get; } = [];
        public List<string> Installed { get; } = [];
        public Task<bool> IsInstalledAsync(string targetKey, CancellationToken cancellationToken)
        { Probed.Add(targetKey); return Task.FromResult(Installed.Contains(targetKey)); }
        public Task InstallAsync(string targetKey, CancellationToken cancellationToken)
        { Installed.Add(targetKey); return Task.CompletedTask; }
    }
    private sealed class Stages : IProgress<ToolFlowInstallProgress>
    {
        internal List<ToolFlowInstallProgress> Items { get; } = [];
        public void Report(ToolFlowInstallProgress value) => Items.Add(value);
    }
}
