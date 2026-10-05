using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowInstallRunnerTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(),
        "zxai-install-runner-test-" + Guid.NewGuid().ToString("N"));

    public ToolFlowInstallRunnerTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task RunsOnlySupportedSelectedItems_AndSkipsInstalledOnes()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var alreadyId = Guid.NewGuid().ToString("D");
        var selection = store.SelectForInstall(Selection(
            Item(alreadyId, "godot"), Item(Guid.NewGuid().ToString("D"), null),
            Item(Guid.NewGuid().ToString("D"), "arbitrary-command")));
        var installer = new FakeInstaller("godot") { Present = { ["godot"] = true } };
        var progress = new RecordingProgress();

        var result = await new ToolFlowInstallRunner(store, installer)
            .RunAsync(selection.SubmissionId, progress);

        Assert.Equal(1, result.AlreadyInstalledCount);
        Assert.Equal(2, result.ManualStepCount);
        Assert.Equal(0, result.InstalledCount);
        Assert.Empty(installer.InstallCalls);
        Assert.Equal(new[] { "godot" }, installer.ProbeCalls);
        Assert.Equal(3, progress.Results.Count);
        var executionEvent = Assert.Single(store.GetBySubmissionId(selection.SubmissionId)!.Events);
        Assert.Equal(alreadyId, executionEvent.ItemId);
        Assert.Equal(ToolFlowEventKind.Verified, executionEvent.Kind);

        await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId);
        Assert.Equal(2, store.GetBySubmissionId(selection.SubmissionId)!.Events.Count);
        Assert.All(store.GetBySubmissionId(selection.SubmissionId)!.Events,
            e => Assert.Equal(ToolFlowEventKind.Verified, e.Kind)); // 每轮检查保留最新的真实结果
    }

    [Fact]
    public async Task InstallSuccessRequiresPostInstallProbe_AndFailedProbeIsNotCounted()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var okId = Guid.NewGuid().ToString("D");
        var badId = Guid.NewGuid().ToString("D");
        var selection = store.SelectForInstall(Selection(Item(okId, "godot"), Item(badId, "blender")));
        var installer = new FakeInstaller("godot", "blender");
        installer.OnInstall = key =>
        {
            if (key == "godot") installer.Present[key] = true;
        };

        var result = await new ToolFlowInstallRunner(store, installer)
            .RunAsync(selection.SubmissionId);

        Assert.Equal(1, result.InstalledCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(new[] { "godot", "blender" }, installer.InstallCalls);
        var events = store.GetBySubmissionId(selection.SubmissionId)!.Events;
        Assert.Contains(events, e => e.ItemId == okId && e.Kind == ToolFlowEventKind.InstallSucceeded);
        Assert.Contains(events, e => e.ItemId == okId && e.Kind == ToolFlowEventKind.Verified);
        Assert.Contains(events, e => e.ItemId == badId && e.Kind == ToolFlowEventKind.InstallFailed);
        Assert.DoesNotContain(events, e => e.Kind is ToolFlowEventKind.DownloadStarted
            or ToolFlowEventKind.DownloadSucceeded or ToolFlowEventKind.DownloadFailed);
    }

    [Fact]
    public async Task FailedItemDoesNotPreventFollowingItem_AndNoUnselectedFlowRuns()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        bool installerAfterFailure = false;
        var installer = new FakeInstaller("godot", "blender")
        {
            OnInstall = key =>
            {
                if (key == "godot") throw new InvalidOperationException("installer failed");
                installerAfterFailure = true;
            },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ToolFlowInstallRunner(store, installer).RunAsync(Guid.NewGuid().ToString("D")));
        Assert.Empty(installer.InstallCalls);

        var selection = store.SelectForInstall(Selection(
            Item(Guid.NewGuid().ToString("D"), "godot"),
            Item(Guid.NewGuid().ToString("D"), "blender")));
        var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId);

        Assert.True(installerAfterFailure);
        Assert.Equal(2, result.FailedCount);
        Assert.Equal(2, store.GetBySubmissionId(selection.SubmissionId)!.Events.Count(e =>
            e.Kind == ToolFlowEventKind.InstallFailed));
        Assert.All(store.GetBySubmissionId(selection.SubmissionId)!.Events,
            e => Assert.DoesNotContain("installer failed", e.Detail));
    }

    [Fact]
    public async Task FailedPrecheckDoesNotPretendAnInstallWasAttempted()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var selection = store.SelectForInstall(Selection(Item(Guid.NewGuid().ToString("D"), "godot")));
        var installer = new FakeInstaller("godot")
        {
            OnProbe = _ => throw new InvalidOperationException("probe unavailable"),
        };

        var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId);

        Assert.Equal(1, result.FailedCount);
        Assert.Empty(installer.InstallCalls);
        var failed = Assert.Single(store.GetBySubmissionId(selection.SubmissionId)!.Events);
        Assert.Equal(ToolFlowEventKind.InstallFailed, failed.Kind);
        Assert.Contains("尚未启动安装", failed.Detail);
        Assert.DoesNotContain("probe unavailable", failed.Detail);
    }

    [Fact]
    public async Task ProgressReportsActualCheckingInstallationAndVerification_WithFinishedItemCounts()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var installed = Item(Guid.NewGuid().ToString("D"), "godot");
        var manual = Item(Guid.NewGuid().ToString("D"), null);
        var selection = store.SelectForInstall(Selection(installed, manual));
        var stages = new RecordingStages();
        var installer = new FakeInstaller("godot");
        installer.OnInstall = target => installer.Present[target] = true;

        var result = await new ToolFlowInstallRunner(store, installer)
            .RunAsync(selection.SubmissionId, stageProgress: stages);

        Assert.False(result.WasCanceled);
        Assert.Equal(new[]
        {
            ToolFlowInstallPhase.Checking, ToolFlowInstallPhase.Installing,
            ToolFlowInstallPhase.Verifying, ToolFlowInstallPhase.ItemCompleted,
            ToolFlowInstallPhase.ItemCompleted, ToolFlowInstallPhase.Completed,
        }, stages.Items.Select(p => p.Phase));
        Assert.All(stages.Items.Take(3), p =>
        {
            Assert.Equal(installed.ItemId, p.ItemId);
            Assert.Equal(0, p.CompletedCount);
            Assert.Null(p.ItemResult);
        });
        Assert.Equal(ToolFlowInstallItemStatus.Installed, stages.Items[3].ItemResult!.Status);
        Assert.Equal(1, stages.Items[3].CompletedCount);
        Assert.Equal(ToolFlowInstallItemStatus.ManualStep, stages.Items[4].ItemResult!.Status);
        Assert.Equal(2, stages.Items[4].CompletedCount);
        Assert.All(stages.Items, p => Assert.Equal(2, p.TotalCount));
    }

    [Fact]
    public async Task FailedInstallationReportsFailureAndContinues_WithoutClaimingVerification()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var failed = Item(Guid.NewGuid().ToString("D"), "godot");
        var existing = Item(Guid.NewGuid().ToString("D"), "blender");
        var selection = store.SelectForInstall(Selection(failed, existing));
        var stages = new RecordingStages();
        var installer = new FakeInstaller("godot", "blender")
        {
            Present = { ["blender"] = true },
            OnInstall = _ => throw new InvalidOperationException("installer unavailable"),
        };

        var result = await new ToolFlowInstallRunner(store, installer)
            .RunAsync(selection.SubmissionId, stageProgress: stages);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.AlreadyInstalledCount);
        Assert.DoesNotContain(stages.Items,
            p => p.ItemId == failed.ItemId && p.Phase == ToolFlowInstallPhase.Verifying);
        Assert.Contains(stages.Items, p => p.ItemId == failed.ItemId &&
            p.ItemResult?.Status == ToolFlowInstallItemStatus.Failed && p.CompletedCount == 1);
        Assert.Equal(ToolFlowInstallPhase.Completed, stages.Items[^1].Phase);
        Assert.Equal(2, stages.Items[^1].CompletedCount);
    }

    [Fact]
    public async Task StopBeforeStartReturnsStoppedWithoutRunningOrInventingItemResults()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var selection = store.SelectForInstall(Selection(Item(Guid.NewGuid().ToString("D"), "godot")));
        var installer = new FakeInstaller("godot");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var stages = new RecordingStages();

        var result = await new ToolFlowInstallRunner(store, installer)
            .RunAsync(selection.SubmissionId, cancellationToken: stop.Token, stageProgress: stages);

        Assert.True(result.WasCanceled);
        Assert.Empty(result.Items);
        Assert.Empty(installer.ProbeCalls);
        Assert.Empty(installer.InstallCalls);
        Assert.Empty(store.GetBySubmissionId(selection.SubmissionId)!.Events);
        var stage = Assert.Single(stages.Items);
        Assert.Equal(ToolFlowInstallPhase.Stopped, stage.Phase);
        Assert.Equal(0, stage.CompletedCount);
        Assert.Equal(1, stage.TotalCount);
    }

    [Fact]
    public async Task StopDuringInstallationFinishesCurrentVerificationAndKeepsRecord_WithoutStartingNextItem()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var current = Item(Guid.NewGuid().ToString("D"), "godot");
        var pending = Item(Guid.NewGuid().ToString("D"), "blender");
        var selection = store.SelectForInstall(Selection(current, pending));
        using var stop = new CancellationTokenSource();
        var stages = new RecordingStages();
        var installer = new FakeInstaller("godot", "blender");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        installer.OnInstallAsync = async target =>
        {
            started.SetResult();
            await finish.Task;
            installer.Present[target] = true;
        };

        var running = new ToolFlowInstallRunner(store, installer)
            .RunAsync(selection.SubmissionId, cancellationToken: stop.Token, stageProgress: stages);
        await started.Task;
        stop.Cancel();
        Assert.False(running.IsCompleted); // 正在执行的项目继续到可信检查结果，停止不强杀安装器。
        finish.SetResult();
        var result = await running;

        Assert.True(result.WasCanceled);
        Assert.Equal(ToolFlowInstallItemStatus.Installed, Assert.Single(result.Items).Status);
        Assert.Equal(new[] { "godot", "godot" }, installer.ProbeCalls);
        Assert.Equal(new[] { "godot" }, installer.InstallCalls);
        Assert.All(installer.ReceivedTokens, token => Assert.False(token.CanBeCanceled));
        var events = store.GetBySubmissionId(selection.SubmissionId)!.Events;
        Assert.Contains(events, e => e.ItemId == current.ItemId && e.Kind == ToolFlowEventKind.Verified);
        Assert.DoesNotContain(events, e => e.ItemId == pending.ItemId || e.Kind == ToolFlowEventKind.InstallFailed);
        Assert.Equal(ToolFlowInstallPhase.Stopped, stages.Items[^1].Phase);
        Assert.Equal(1, stages.Items[^1].CompletedCount);
        Assert.Equal(2, stages.Items[^1].TotalCount);
    }

    [Fact]
    public async Task StopWhileCurrentCheckFailsDoesNotTurnStopIntoFalseSuccess_AndSkipsRemainingItems()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var current = Item(Guid.NewGuid().ToString("D"), "godot");
        var selection = store.SelectForInstall(Selection(current, Item(Guid.NewGuid().ToString("D"), "blender")));
        using var stop = new CancellationTokenSource();
        var installer = new FakeInstaller("godot", "blender")
        {
            OnProbe = _ =>
            {
                stop.Cancel();
                throw new InvalidOperationException("probe failed independently");
            },
        };

        var result = await new ToolFlowInstallRunner(store, installer)
            .RunAsync(selection.SubmissionId, cancellationToken: stop.Token);

        Assert.True(result.WasCanceled);
        Assert.Equal(ToolFlowInstallItemStatus.Failed, Assert.Single(result.Items).Status);
        Assert.Equal(0, result.InstalledCount);
        Assert.Equal(new[] { "godot" }, installer.ProbeCalls);
        Assert.Empty(installer.InstallCalls);
    }

    [Fact]
    public async Task SuccessfulRecheckAfterHistoricalFailureWritesFreshVerification()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var item = Item(Guid.NewGuid().ToString("D"), "godot");
        var selection = store.SelectForInstall(Selection(item));
        var installer = new FakeInstaller("godot") { Present = { ["godot"] = true } };
        var runner = new ToolFlowInstallRunner(store, installer);
        await runner.RunAsync(selection.SubmissionId);
        store.AppendEvent(selection.SubmissionId, new ToolFlowExecutionEvent
        {
            Id = Guid.NewGuid().ToString("D"), ItemId = item.ItemId,
            Kind = ToolFlowEventKind.InstallFailed, AtUtc = DateTimeOffset.UtcNow,
            Detail = "Later attempt did not verify installation.",
        });

        var result = await runner.RunAsync(selection.SubmissionId);

        Assert.Equal(1, result.AlreadyInstalledCount);
        var events = store.GetBySubmissionId(selection.SubmissionId)!.Events;
        Assert.Equal(2, events.Count(e => e.Kind == ToolFlowEventKind.Verified));
        Assert.Equal(ToolFlowEventKind.Verified, events[^1].Kind);
        Assert.Equal(ToolFlowResumeItemState.InstalledOrDetected,
            Assert.Single(ToolFlowResume.Build(store.GetBySubmissionId(selection.SubmissionId)!).Rows).State);
    }

    [Fact]
    public async Task FailedPrecheckAfterHistoricalVerificationRemainsFailedWhenSnapshotReloads()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var item = Item(Guid.NewGuid().ToString("D"), "godot");
        var selection = store.SelectForInstall(Selection(item));
        var installer = new FakeInstaller("godot") { Present = { ["godot"] = true } };
        var runner = new ToolFlowInstallRunner(store, installer);
        await runner.RunAsync(selection.SubmissionId);
        installer.OnProbe = _ => throw new InvalidOperationException("probe unavailable");

        var result = await runner.RunAsync(selection.SubmissionId);

        Assert.Equal(1, result.FailedCount);
        Assert.Empty(installer.InstallCalls);
        var reloaded = store.GetBySubmissionId(selection.SubmissionId)!;
        Assert.Equal(ToolFlowEventKind.InstallFailed, reloaded.Events[^1].Kind);
        var row = Assert.Single(ToolFlowResume.Build(reloaded).Rows);
        Assert.Equal(ToolFlowResumeItemState.Failed, row.State);
        Assert.True(row.CanContinueAutomatically);
    }

    [Fact]
    public async Task StopRequestDuringLastItemStillReportsCompletedWhenThereAreNoRemainingItems()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var selection = store.SelectForInstall(Selection(Item(Guid.NewGuid().ToString("D"), "godot")));
        using var stop = new CancellationTokenSource();
        var stages = new RecordingStages();
        var installer = new FakeInstaller("godot")
        {
            Present = { ["godot"] = true }, OnProbe = _ => stop.Cancel(),
        };

        var result = await new ToolFlowInstallRunner(store, installer)
            .RunAsync(selection.SubmissionId, cancellationToken: stop.Token, stageProgress: stages);

        Assert.False(result.WasCanceled);
        Assert.Equal(1, result.AlreadyInstalledCount);
        Assert.Equal(ToolFlowInstallPhase.Completed, stages.Items[^1].Phase);
        Assert.Equal(1, stages.Items[^1].CompletedCount);
    }

    [Fact]
    public async Task ExistingWinRarReusesOrdinaryExtraction_WithoutAnInstallSuccessOrFalseSevenZipClaim()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var archive = ToolFlowPreparationTests.Item("7-Zip", "7zip");
        var selection = store.SelectForInstall(Selection(archive));
        var installer = new FakeInstaller("7zip");
        var runner = new ToolFlowInstallRunner(store, installer, ToolFlowPreparationTests.WinRarService());

        var result = await runner.RunAsync(selection.SubmissionId);

        Assert.Empty(installer.InstallCalls);
        Assert.Equal(1, result.ReusedInstalledCount);
        Assert.Equal(0, result.AlreadyInstalledCount);
        Assert.Equal(0, result.InstalledCount);
        var actual = Assert.Single(result.Items);
        Assert.Equal("winrar", actual.ExistingToolTargetKey);
        Assert.Equal("WinRAR", actual.ExistingToolName);
        Assert.DoesNotContain(@"C:\FAKE", actual.Message);
        var snapshot = store.GetBySubmissionId(selection.SubmissionId)!;
        Assert.Equal(archive, Assert.Single(snapshot.Items));
        var record = Assert.Single(snapshot.Events);
        Assert.Equal(ToolFlowEventKind.Verified, record.Kind);
        Assert.Equal(ToolFlowReuseEvidence.WinRarArchiveDetail, record.Detail);
        Assert.DoesNotContain(@"C:\FAKE", record.Detail);
        Assert.True(ToolFlowResume.Build(snapshot).Rows.Single().IsExistingToolReuse);
    }

    [Fact]
    public async Task PreparationSnapshotIsNotReusedAsExecutionEvidence_AndCliCannotUseWinRar()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var archive = ToolFlowPreparationTests.Item("7-Zip CLI", "7zip");
        var selection = store.SelectForInstall(Selection(archive));
        var installed = false;
        var preparation = new ToolFlowPreparationService(["7zip"], (key, _) =>
            Task.FromResult<InstalledToolEvidence?>(key == "winrar" ? new("winrar", "WinRAR")
                : installed ? new("7zip", "7-Zip", HasSevenZipCli: true) : null));
        Assert.Single((await preparation.PrepareAsync([archive])).ConfirmationItems);
        var installer = new FakeInstaller("7zip") { OnInstall = _ => installed = true };

        var result = await new ToolFlowInstallRunner(store, installer, preparation).RunAsync(selection.SubmissionId);

        Assert.Equal(new[] { "7zip" }, installer.InstallCalls);
        Assert.Equal(1, result.InstalledCount);
        Assert.Equal(0, result.ReusedInstalledCount);
    }

    [Fact]
    public async Task FailedReadOnlyEvidencePreventsInstallationAndRecordsNoRawProbeMessage()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var archive = ToolFlowPreparationTests.Item("7-Zip", "7zip");
        var selection = store.SelectForInstall(Selection(archive));
        var installer = new FakeInstaller("7zip");
        var preparation = new ToolFlowPreparationService(["7zip"], (_, _) => throw new IOException("PRIVATE-PROBE-MESSAGE"));
        var result = await new ToolFlowInstallRunner(store, installer, preparation).RunAsync(selection.SubmissionId);
        Assert.Equal(1, result.FailedCount);
        Assert.Empty(installer.InstallCalls);
        Assert.DoesNotContain("PRIVATE-PROBE-MESSAGE", result.Items[0].Message);
        Assert.DoesNotContain("PRIVATE-PROBE-MESSAGE", store.GetBySubmissionId(selection.SubmissionId)!.Events[0].Detail);
    }

    private static ToolFlowItem Item(string itemId, string? target) => new()
    {
        ItemId = itemId,
        Name = itemId,
        Kind = "software",
        InstallTargetKey = target,
    };

    private static ToolFlowSelection Selection(params ToolFlowItem[] items) => new()
    {
        FlowId = Guid.NewGuid().ToString("D"),
        SubmissionId = Guid.NewGuid().ToString("D"),
        Origin = ToolFlowOrigin.User,
        SelectedAtUtc = DateTimeOffset.UtcNow,
        FlowName = "2D 游戏开发工具流",
        ProjectGoal = "我想做一款 2D 游戏",
        GoalDescription = "做一款 2D 游戏",
        FlowText = "用户确认后的工具流。",
        UploadEnabledAtSelection = false,
        Conversation = [],
        Items = [.. items],
    };

    private sealed class FakeInstaller(params string[] knownTargets) : IToolFlowInstaller
    {
        public IReadOnlyCollection<string> KnownTargets { get; } = knownTargets;
        public Dictionary<string, bool> Present { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ProbeCalls { get; } = [];
        public List<string> InstallCalls { get; } = [];
        public List<CancellationToken> ReceivedTokens { get; } = [];
        public Action<string>? OnProbe { get; set; }
        public Action<string>? OnInstall { get; set; }
        public Func<string, Task>? OnInstallAsync { get; set; }

        public Task<bool> IsInstalledAsync(string targetKey, CancellationToken cancellationToken)
        {
            ProbeCalls.Add(targetKey);
            ReceivedTokens.Add(cancellationToken);
            OnProbe?.Invoke(targetKey);
            return Task.FromResult(Present.TryGetValue(targetKey, out var installed) && installed);
        }

        public async Task InstallAsync(string targetKey, CancellationToken cancellationToken)
        {
            InstallCalls.Add(targetKey);
            ReceivedTokens.Add(cancellationToken);
            OnInstall?.Invoke(targetKey);
            if (OnInstallAsync is not null) await OnInstallAsync(targetKey);
        }
    }

    private sealed class RecordingProgress : IProgress<ToolFlowInstallItemResult>
    {
        public List<ToolFlowInstallItemResult> Results { get; } = [];
        public void Report(ToolFlowInstallItemResult value) => Results.Add(value);
    }

    private sealed class RecordingStages : IProgress<ToolFlowInstallProgress>
    {
        public List<ToolFlowInstallProgress> Items { get; } = [];
        public void Report(ToolFlowInstallProgress value) => Items.Add(value);
    }
}
