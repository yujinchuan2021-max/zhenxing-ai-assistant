using System.Text.Json;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowAutomaticDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-auto-delivery-" + Guid.NewGuid().ToString("N"));
    public ToolFlowAutomaticDeliveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task ConfirmOnceInstallsVerifiesAndDeliversAllSoftwareAroundHumanOnlyItems()
    {
        var installer = new Installer();
        var order = new List<string>();
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(Plan());
        var runner = Runner(store, installer, async (item, result, ct) =>
        {
            Assert.True(installer.Present.Contains(item.InstallTargetKey!));
            Assert.Equal(ToolFlowInstallItemStatus.Installed, result.Status);
            await Task.Yield();
            order.Add(item.InstallTargetKey!);
            return Delivered(item);
        });
        var stages = new Stages();
        var result = await runner.RunAsync(selection.SubmissionId, stageProgress: stages);
        Assert.Equal(new[] { "godot", "codex", "blender" }, order);
        Assert.Equal(3, result.InstalledCount);
        Assert.Equal(1, result.ManualStepCount);
        Assert.Equal(3, result.Items.Count(item => item.Delivery is not null));
        Assert.Equal(3, stages.Items.Count(stage => stage.Phase == ToolFlowInstallPhase.Delivering));
        Assert.Equal(ToolFlowInstallPhase.Completed, stages.Items.Last().Phase);
        Assert.All(stages.Items.Where(stage => stage.Phase == ToolFlowInstallPhase.ItemCompleted &&
            stage.ItemResult!.Status == ToolFlowInstallItemStatus.Installed), stage => Assert.NotNull(stage.ItemResult!.Delivery));
    }

    [Fact]
    public async Task ShortcutFailurePreservesVerifiedInstallAndContinuesToNextSoftware()
    {
        var installer = new Installer();
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(Plan());
        var result = await Runner(store, installer, (item, _, _) => item.InstallTargetKey == "godot"
            ? throw new IOException("desktop unavailable") : Task.FromResult(Delivered(item)))
            .RunAsync(selection.SubmissionId);
        Assert.Equal(3, result.InstalledCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(ToolFlowPostInstallDeliveryKind.Failed, result.Items[0].Delivery!.Kind);
        Assert.Equal(ToolFlowPostInstallDeliveryKind.CliReady, result.Items[2].Delivery!.Kind);
        Assert.DoesNotContain(store.GetBySubmissionId(selection.SubmissionId)!.Events,
            e => e.Kind == ToolFlowEventKind.InstallFailed);
    }

    [Fact]
    public async Task ExistingSoftwareGetsAnEntryWithoutReinstallationAndFailedInstallDoesNotGetOne()
    {
        var installer = new Installer { FailingTarget = "blender" };
        installer.Present.Add("godot");
        var delivered = new List<string>();
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(Plan());
        var result = await Runner(store, installer, (item, _, _) =>
        {
            delivered.Add(item.InstallTargetKey!);
            return Task.FromResult(Delivered(item));
        }).RunAsync(selection.SubmissionId);
        Assert.Equal(new[] { "godot", "codex" }, delivered);
        Assert.DoesNotContain("godot", installer.Installs);
        Assert.Equal(ToolFlowInstallItemStatus.AlreadyInstalled, result.Items[0].Status);
        Assert.Null(result.Items.Last().Delivery);
        Assert.Equal(1, result.FailedCount);
    }

    [Fact]
    public async Task StopFinishesTheCurrentEntryAndDoesNotStartTheFollowingItem()
    {
        using var stop = new CancellationTokenSource();
        var installer = new Installer();
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(Plan());
        var result = await Runner(store, installer, (item, _, ct) =>
        {
            stop.Cancel();
            Assert.False(ct.IsCancellationRequested);
            return Task.FromResult(Delivered(item));
        }).RunAsync(selection.SubmissionId, cancellationToken: stop.Token);
        Assert.True(result.WasCanceled);
        Assert.Single(result.Items);
        Assert.Single(installer.Installs);
        Assert.NotNull(result.Items[0].Delivery);
    }

    [Fact]
    public async Task LocalDeliveryEvidenceIsExcludedFromResultSerializationAndSavedEvents()
    {
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(Plan());
        var result = await Runner(store, new Installer(), (item, _, _) => Task.FromResult(
            Delivered(item) with { Message = "PRIVATE-LOCAL-ENTRY" })).RunAsync(selection.SubmissionId);
        Assert.DoesNotContain("PRIVATE-LOCAL-ENTRY", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("PRIVATE-LOCAL-ENTRY", JsonSerializer.Serialize(store.GetBySubmissionId(selection.SubmissionId)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ffmpeg")]
    public async Task ReadOnlyDetectedFfmpegIsDeliveredWithoutAuthorisingInstallation(string? target)
    {
        var store = new ToolFlowSelectionStore(_root);
        var item = Item("ffmpeg") with { InstallTargetKey = target, Name = "FFmpeg（本机已装，复用）" };
        var selection = store.SelectForInstall(Plan() with { Items = [item] });
        var installer = new Installer();
        var preparation = new ToolFlowPreparationService(installer.KnownTargets, (key, _) =>
            Task.FromResult<InstalledToolEvidence?>(key == "ffmpeg"
                ? new("ffmpeg", "FFmpeg", @"C:\isolated-ffmpeg\ffmpeg.exe") : null));
        var delivered = 0;
        var result = await new ToolFlowInstallRunner(store, installer, preparation, (access, state, _) =>
        {
            delivered++;
            Assert.Equal("ffmpeg", access.InstallTargetKey);
            Assert.Equal(ToolFlowInstallItemStatus.AlreadyInstalled, state.Status);
            return Task.FromResult(new ToolFlowPostInstallDeliveryResult(access.ItemId, access.Name,
                ToolFlowPostInstallDeliveryKind.CliReady, "Ready"));
        }).RunAsync(selection.SubmissionId);
        Assert.Equal(1, delivered);
        Assert.Equal(1, result.AlreadyInstalledCount);
        Assert.Equal(0, result.ManualStepCount);
        Assert.Empty(installer.Installs);
        Assert.Equal(target, store.GetBySubmissionId(selection.SubmissionId)!.Items.Single().InstallTargetKey);
        Assert.Equal(ToolFlowEventKind.Verified, store.GetBySubmissionId(selection.SubmissionId)!.Events.Single().Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrFailedReadOnlyDetectionNeverInstallsFfmpeg(bool failProbe)
    {
        var store = new ToolFlowSelectionStore(_root);
        var selection = store.SelectForInstall(Plan() with { Items = [Item("ffmpeg")] });
        var installer = new Installer();
        var preparation = new ToolFlowPreparationService(installer.KnownTargets, (_, _) =>
            failProbe ? throw new IOException("probe failed") : Task.FromResult<InstalledToolEvidence?>(null));
        var result = await new ToolFlowInstallRunner(store, installer, preparation, (_, _, _) =>
            throw new Exception("No delivery without evidence")).RunAsync(selection.SubmissionId);
        Assert.Empty(installer.Installs);
        Assert.Equal(failProbe ? ToolFlowInstallItemStatus.Failed : ToolFlowInstallItemStatus.ManualStep,
            result.Items.Single().Status);
        Assert.Null(result.Items.Single().Delivery);
    }

    private static ToolFlowInstallRunner Runner(ToolFlowSelectionStore store, Installer installer,
        Func<ToolFlowItem, ToolFlowInstallItemResult, CancellationToken, Task<ToolFlowPostInstallDeliveryResult>> deliver)
        => new(store, installer, new ToolFlowPreparationService(installer.KnownTargets, (key, _) =>
            Task.FromResult<InstalledToolEvidence?>(installer.Present.Contains(key) ? new(key, key) : null)), deliver);

    private static ToolFlowPostInstallDeliveryResult Delivered(ToolFlowItem item) => new(item.ItemId, item.Name,
        item.InstallTargetKey == "codex" ? ToolFlowPostInstallDeliveryKind.CliReady
            : ToolFlowPostInstallDeliveryKind.DesktopShortcutReady, "Ready");

    private static ToolFlowSelection Plan() => new()
    {
        FlowId = Guid.NewGuid().ToString("D"), SubmissionId = Guid.NewGuid().ToString("D"),
        Origin = ToolFlowOrigin.User, SelectedAtUtc = DateTimeOffset.UtcNow, FlowName = "Game",
        ProjectGoal = "Make a game", GoalDescription = "Make a game", FlowText = "Game tools",
        UploadEnabledAtSelection = false, Conversation = [],
        Items = [Item("godot"), new() { ItemId = Guid.NewGuid().ToString("D"), Name = "Account", Kind = "account" },
            Item("codex"), Item("blender")],
    };
    private static ToolFlowItem Item(string key) => new()
    { ItemId = Guid.NewGuid().ToString("D"), Name = key, Kind = "software", InstallTargetKey = key };

    private sealed class Installer : IToolFlowInstaller
    {
        public IReadOnlyCollection<string> KnownTargets => ["godot", "codex", "blender"];
        internal HashSet<string> Present { get; } = [];
        internal List<string> Installs { get; } = [];
        internal string? FailingTarget { get; init; }
        public Task<bool> IsInstalledAsync(string targetKey, CancellationToken ct) => Task.FromResult(Present.Contains(targetKey));
        public Task InstallAsync(string targetKey, CancellationToken ct)
        {
            Installs.Add(targetKey);
            if (targetKey == FailingTarget) throw new IOException("failed");
            Present.Add(targetKey);
            return Task.CompletedTask;
        }
    }
    private sealed class Stages : IProgress<ToolFlowInstallProgress>
    {
        internal List<ToolFlowInstallProgress> Items { get; } = [];
        public void Report(ToolFlowInstallProgress value) => Items.Add(value);
    }
}
