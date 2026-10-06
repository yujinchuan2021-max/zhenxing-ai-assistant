using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Tests;

public sealed class CloudToolBatchUpdateTests
{
    private static CloudToolState State(string id = "sample-tool", string version = "1", string available = "2") =>
        new(id, "示例工具", version, available, CloudToolStatus.Installed,
            EntryPath: id + "/app.exe", IsManaged: true) { HasUpdate = true };

    private static CloudToolDefinition Tool(string id = "sample-tool", string version = "2", string architecture = "x64") => new()
    {
        Id = id, Name = "示例工具", Version = version,
        Packages = [new() { Architecture = architecture, Url = "https://zhenxingai.com/downloads/tools/" + id + ".zip",
            SizeBytes = 100, Sha256 = new string('a', 64), EntryPoint = "app.exe" }]
    };

    private static CloudToolBatchUpdatePlan Plan(params CloudToolState[] states) =>
        CloudToolBatchUpdatePlanner.CreatePlan(states, states.Select(state => Tool(state.Id, state.AvailableVersion)), "x64", _ => true);

    private static CloudToolState Applied(CloudToolState state) => state with
    { Version = state.AvailableVersion, HasUpdate = false, Status = CloudToolStatus.Installed, PendingUpdate = false, Error = null };

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public void Planner_OnlySelectsInstalledManagedToolsAndExactPackage()
    {
        var installed = State();
        var legacy = State("legacy-tool") with { IsManaged = false };
        var absent = State("absent-tool") with { IsManaged = false, EntryPath = null };
        var plan = CloudToolBatchUpdatePlanner.CreatePlan([installed, legacy, absent],
            [Tool(), Tool("legacy-tool"), Tool("absent-tool")], "x64", _ => true);
        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(installed.Id, candidate.Id);
        Assert.Equal("1", candidate.InstalledVersion);
        Assert.Equal("2", candidate.TargetVersion);
        Assert.Equal(new string('a', 64), candidate.PackageSha256);
        Assert.Equal(100, candidate.SizeBytes);
        Assert.Equal("x64", candidate.PackageArchitecture);
        Assert.Equal(100, plan.TotalSizeBytes);
        Assert.Equal(CloudToolBatchUpdateSkipReason.NotManaged, Assert.Single(plan.Skipped).SkipReason);
    }

    [Fact]
    public void Planner_SameVersionHashCorrectionUsesManagerHasUpdateEvidence()
    {
        var sameVersion = State(version: "2");
        Assert.Single(Plan(sameVersion).Candidates);
        var current = sameVersion with { HasUpdate = false };
        Assert.Empty(Plan(current).Candidates);
        Assert.Equal(CloudToolBatchUpdateSkipReason.AlreadyCurrent, Assert.Single(Plan(current).Skipped).SkipReason);
    }

    [Theory]
    [InlineData(CloudToolStatus.Downloading)]
    [InlineData(CloudToolStatus.Installing)]
    [InlineData(CloudToolStatus.Updating)]
    [InlineData(CloudToolStatus.Removing)]
    public void Planner_SkipsEveryBusyStatus(CloudToolStatus status)
    {
        var plan = Plan(State() with { Status = status });
        Assert.Empty(plan.Candidates);
        Assert.Equal(CloudToolBatchUpdateSkipReason.Busy, Assert.Single(plan.Skipped).SkipReason);
    }

    [Fact]
    public void Planner_PendingOrMissingEntryDoesNotStartAnotherDownload()
    {
        Assert.Equal(CloudToolBatchUpdateSkipReason.PendingUpdate,
            Assert.Single(Plan(State() with { PendingUpdate = true }).Skipped).SkipReason);
        Assert.Equal(CloudToolBatchUpdateSkipReason.PendingUpdate,
            Assert.Single(Plan(State() with { Status = CloudToolStatus.PendingUpdate }).Skipped).SkipReason);
        var invalid = CloudToolBatchUpdatePlanner.CreatePlan([State()], [Tool()], "x64", _ => false);
        Assert.Equal(CloudToolBatchUpdateSkipReason.MissingEntry, Assert.Single(invalid.Skipped).SkipReason);
        Assert.Equal(CloudToolBatchUpdateSkipReason.NotInstalled,
            Assert.Single(Plan(State() with { EntryPath = null }).Skipped).SkipReason);
    }

    [Fact]
    public void Planner_NoNameFallbackAndAmbiguousIdsAreExcluded()
    {
        var absent = CloudToolBatchUpdatePlanner.CreatePlan([State()], [Tool("other-id")], "x64", _ => true);
        Assert.Equal(CloudToolBatchUpdateSkipReason.CatalogMissing, Assert.Single(absent.Skipped).SkipReason);
        var duplicates = CloudToolBatchUpdatePlanner.CreatePlan([State()], [Tool(), Tool()], "x64", _ => true);
        Assert.Equal(CloudToolBatchUpdateSkipReason.AmbiguousIdentity, Assert.Single(duplicates.Skipped).SkipReason);
        Assert.Equal(CloudToolBatchUpdateSkipReason.AmbiguousIdentity, Assert.Single(Plan(State(), State()).Skipped).SkipReason);
    }

    [Fact]
    public void Planner_RejectsUnsupportedOrUnverifiedSourcesAndVersionDrift()
    {
        var noArchitecture = CloudToolBatchUpdatePlanner.CreatePlan([State()], [Tool(architecture: "arm64")], "x64", _ => true);
        Assert.Equal(CloudToolBatchUpdateSkipReason.UnsupportedPackage, Assert.Single(noArchitecture.Skipped).SkipReason);
        var vendor = Tool() with { Packages = [Tool().Packages[0] with { Url = "https://github.com/vendor/download.zip" }] };
        var noOwnedSource = CloudToolBatchUpdatePlanner.CreatePlan([State()], [vendor], "x64", _ => true);
        Assert.Equal(CloudToolBatchUpdateSkipReason.UnsupportedPackage, Assert.Single(noOwnedSource.Skipped).SkipReason);
        var installer = Tool() with { Packages = [Tool().Packages[0] with { Kind = "installer-exe" }] };
        Assert.Empty(CloudToolBatchUpdatePlanner.CreatePlan([State()], [installer], "x64", _ => true).Candidates);
        var drift = CloudToolBatchUpdatePlanner.CreatePlan([State()], [Tool(version: "3")], "x64", _ => true);
        Assert.Equal(CloudToolBatchUpdateSkipReason.CatalogChanged, Assert.Single(drift.Skipped).SkipReason);
    }

    [Fact]
    public void Planner_UsesSameArchitectureFallbackAsManager()
    {
        var x86Fallback = CloudToolBatchUpdatePlanner.CreatePlan([State()], [Tool(architecture: "x86")], "arm64", _ => true);
        Assert.Equal("x86", Assert.Single(x86Fallback.Candidates).PackageArchitecture);
        var any = CloudToolBatchUpdatePlanner.CreatePlan([State()], [Tool(architecture: "any")], "x86", _ => true);
        Assert.Equal("any", Assert.Single(any.Candidates).PackageArchitecture);
    }

    [Fact]
    public async Task RepeatedClicksJoinOneGlobalTaskAndProgressIsRealManagerState()
    {
        var state = State(); var started = Gate(); var release = Gate(); var calls = 0;
        var coordinator = new CloudToolBatchUpdateCoordinator(async (candidate, token) =>
        {
            Interlocked.Increment(ref calls); Assert.Equal(new string('a', 64), candidate.PackageSha256);
            state = state with { Status = CloudToolStatus.Updating, Progress = 42 }; started.SetResult();
            await release.Task.WaitAsync(token); state = Applied(state); return new(true, "完成", state);
        }, _ => state, _ => true);
        var task = coordinator.StartOrJoinAsync(Plan(state));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(Enumerable.Range(0, 20).Select(_ => coordinator.StartOrJoinAsync(Plan(State()))), joined => Assert.Same(task, joined));
        Assert.Equal(42, coordinator.Snapshot.Progress);
        Assert.Equal(CloudToolBatchUpdateOutcome.InProgress, coordinator.Snapshot.Current!.Outcome);
        release.SetResult(); var result = await task;
        Assert.Equal(1, calls); Assert.Equal(1, result.SucceededCount); Assert.Equal(100, result.Progress); Assert.False(result.IsRunning);
    }

    [Fact]
    public async Task OneFailureContinuesAndDoesNotDeleteTheOldPackage()
    {
        var path = Path.Combine(Path.GetTempPath(), "zxai-batch-preservation-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "original local user data");
        try
        {
            var states = new Dictionary<string, CloudToolState> { ["first-tool"] = State("first-tool"), ["second-tool"] = State("second-tool") };
            var calls = new List<string>();
            var coordinator = new CloudToolBatchUpdateCoordinator((candidate, _) =>
            {
                calls.Add(candidate.Id);
                if (candidate.Id == "first-tool") return Task.FromResult(new CloudToolOperationResult(false, "合成校验失败，旧包保留", states[candidate.Id]));
                states[candidate.Id] = Applied(states[candidate.Id]);
                return Task.FromResult(new CloudToolOperationResult(true, "完成", states[candidate.Id]));
            }, id => states[id], _ => true);
            var result = await coordinator.StartOrJoinAsync(Plan(states.Values.ToArray()));
            Assert.Equal(["first-tool", "second-tool"], calls); Assert.Equal(1, result.FailedCount); Assert.Equal(1, result.SucceededCount);
            Assert.Equal("1", states["first-tool"].Version); Assert.Equal("original local user data", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExceptionsAndThrowingObserversDoNotAbortOtherItems()
    {
        var states = new Dictionary<string, CloudToolState> { ["first-tool"] = State("first-tool"), ["second-tool"] = State("second-tool") };
        var coordinator = new CloudToolBatchUpdateCoordinator((candidate, _) =>
        {
            if (candidate.Id == "first-tool") throw new IOException("合成适配器错误");
            states[candidate.Id] = Applied(states[candidate.Id]); return Task.FromResult(new CloudToolOperationResult(true, "完成", states[candidate.Id]));
        }, id => states[id], _ => true);
        coordinator.Changed += (_, _) => throw new InvalidOperationException("合成订阅者错误");
        var result = await coordinator.StartOrJoinAsync(Plan(states.Values.ToArray()));
        Assert.Equal(1, result.FailedCount); Assert.Equal(1, result.SucceededCount);
    }

    [Fact]
    public async Task ChangedLocalStateIsSkippedBeforeAdapterStarts()
    {
        var state = State(); var plan = Plan(state); state = state with { AvailableVersion = "3" }; var calls = 0;
        var coordinator = new CloudToolBatchUpdateCoordinator((_, _) => { calls++; throw new Exception(); }, _ => state, _ => true);
        var result = await coordinator.StartOrJoinAsync(plan);
        Assert.Equal(0, calls); Assert.Equal(CloudToolBatchUpdateSkipReason.CatalogChanged, Assert.Single(result.Items).SkipReason);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task SuccessRequiresAppliedVersionAndUsableEntry(bool targetVersion, bool hasUpdate, bool missingEntry)
    {
        var state = State(); var plan = Plan(state);
        var usableEntry = true;
        var coordinator = new CloudToolBatchUpdateCoordinator((_, _) =>
        {
            state = state with { Version = targetVersion ? "2" : "1", HasUpdate = hasUpdate };
            usableEntry = !missingEntry;
            return Task.FromResult(new CloudToolOperationResult(true, "适配器说成功", state));
        }, _ => state, _ => usableEntry);
        var result = await coordinator.StartOrJoinAsync(plan);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(1, result.FailedCount);
    }

    [Fact]
    public async Task SameVersionCorrectionCanSucceedOnlyWithAppliedHashEvidence()
    {
        var state = State(version: "2"); var plan = Plan(state);
        var coordinator = new CloudToolBatchUpdateCoordinator((candidate, _) =>
        {
            Assert.Equal(candidate.InstalledVersion, candidate.TargetVersion); state = Applied(state);
            return Task.FromResult(new CloudToolOperationResult(true, "修订已应用", state));
        }, _ => state, _ => true);
        Assert.Equal(1, (await coordinator.StartOrJoinAsync(plan)).SucceededCount);
    }

    [Fact]
    public async Task CancelStopsFurtherStartsAndPreservesCompletedItems()
    {
        var states = new Dictionary<string, CloudToolState> { ["first-tool"] = State("first-tool"), ["second-tool"] = State("second-tool"), ["third-tool"] = State("third-tool") };
        var started = Gate(); var calls = new List<string>();
        var coordinator = new CloudToolBatchUpdateCoordinator(async (candidate, token) =>
        {
            calls.Add(candidate.Id);
            if (candidate.Id == "first-tool") { states[candidate.Id] = Applied(states[candidate.Id]); return new(true, "完成", states[candidate.Id]); }
            started.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new Exception("不应执行");
        }, id => states[id], _ => true);
        var task = coordinator.StartOrJoinAsync(Plan(states.Values.ToArray()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); var cancel = coordinator.CancelAsync();
        Assert.Same(task, cancel); var result = await cancel;
        Assert.Equal(["first-tool", "second-tool"], calls); Assert.Equal(1, result.SucceededCount); Assert.Equal(2, result.SkippedCount);
        Assert.True(result.CancellationRequested); Assert.False(result.IsRunning); Assert.Equal("1", states["third-tool"].Version);
    }

    [Theory]
    [InlineData("immediate", false)]
    [InlineData("reconcile", false)]
    [InlineData("withdrawal", false)]
    [InlineData("immediate", true)]
    [InlineData("reconcile", true)]
    [InlineData("withdrawal", true)]
    public async Task EveryCompletionPathRequiresConfirmedPackageHashEvenWhenVersionMatches(
        string completionPath, bool expectedHashInstalled)
    {
        var state = State(version: "2");
        var plan = Plan(state);
        var receiptSha = expectedHashInstalled ? new string('a', 64) : new string('b', 64);
        var hashChecks = 0;
        void CompleteWithNewerCatalogAvailable()
        {
            // The confirmed version is applied, while a subsequently refreshed catalog
            // offers another package. HasUpdate alone cannot identify the applied SHA.
            state = state with { Version = "2", AvailableVersion = "3", HasUpdate = true,
                Status = CloudToolStatus.Installed, PendingUpdate = false };
        }
        var coordinator = new CloudToolBatchUpdateCoordinator((_, _) =>
        {
            if (completionPath == "immediate")
            {
                CompleteWithNewerCatalogAvailable();
                return Task.FromResult(new CloudToolOperationResult(true, "适配器完成", state));
            }
            state = state with { PendingUpdate = true, Status = CloudToolStatus.PendingUpdate };
            return Task.FromResult(new CloudToolOperationResult(false, "等待退出", state));
        }, _ => state, _ => true,
        _ => { CompleteWithNewerCatalogAvailable(); return Task.FromResult(false); },
        candidate =>
        {
            hashChecks++;
            Assert.Equal("2", candidate.TargetVersion);
            return candidate.PackageSha256 == receiptSha;
        });

        await coordinator.StartOrJoinAsync(plan);
        if (completionPath == "reconcile")
        {
            CompleteWithNewerCatalogAvailable(); coordinator.RefreshPendingOutcomes();
        }
        else if (completionPath == "withdrawal") await coordinator.CancelAsync();

        var result = coordinator.Snapshot;
        Assert.True(hashChecks > 0);
        Assert.Equal(expectedHashInstalled ? 1 : 0, result.SucceededCount);
        Assert.Equal(expectedHashInstalled ? 0 : 1, result.FailedCount);
        Assert.Equal(0, result.PendingUpdateCount);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public async Task AlreadyCancelledTokenStartsNoAdapter()
    {
        using var source = new CancellationTokenSource(); source.Cancel(); var calls = 0;
        var coordinator = new CloudToolBatchUpdateCoordinator((_, _) => { calls++; throw new Exception(); }, _ => State(), _ => true);
        var result = await coordinator.StartOrJoinAsync(Plan(State()), source.Token);
        Assert.Equal(0, calls); Assert.Equal(1, result.SkippedCount); Assert.True(result.CancellationRequested);
    }

    [Fact]
    public async Task PendingIsNeverSuccessAndCannotBeOverwrittenByNewBatch()
    {
        var state = State(); var calls = 0;
        var coordinator = new CloudToolBatchUpdateCoordinator((_, _) =>
        {
            calls++; state = state with { PendingUpdate = true, Status = CloudToolStatus.PendingUpdate };
            return Task.FromResult(new CloudToolOperationResult(false, "等待退出", state));
        }, _ => state, _ => true);
        var result = await coordinator.StartOrJoinAsync(Plan(state));
        Assert.Equal(1, result.PendingUpdateCount); Assert.Equal(0, result.SucceededCount); Assert.False(result.IsRunning);
        var kept = await coordinator.StartOrJoinAsync(Plan(State()));
        Assert.Equal(result.Id, kept.Id); Assert.Equal(1, calls);
        state = Applied(state); coordinator.RefreshPendingOutcomes();
        Assert.Equal(1, coordinator.Snapshot.SucceededCount); Assert.Equal(0, coordinator.Snapshot.PendingUpdateCount);
    }

    [Fact]
    public async Task DeferredFailureAndTemporaryReadErrorAreReportedTruthfully()
    {
        var state = State(); var throwRead = false;
        var coordinator = new CloudToolBatchUpdateCoordinator((_, _) =>
        {
            state = state with { PendingUpdate = true, Status = CloudToolStatus.PendingUpdate };
            return Task.FromResult(new CloudToolOperationResult(false, "等待退出", state));
        }, _ => throwRead ? throw new IOException("暂时读失败") : state, _ => true);
        await coordinator.StartOrJoinAsync(Plan(state)); throwRead = true; coordinator.RefreshPendingOutcomes();
        Assert.Equal(1, coordinator.Snapshot.PendingUpdateCount);
        throwRead = false; state = state with { PendingUpdate = false, Status = CloudToolStatus.Failed, Error = "更新校验失败，原版本保留" };
        coordinator.RefreshPendingOutcomes(); Assert.Equal(1, coordinator.Snapshot.FailedCount); Assert.Equal(0, coordinator.Snapshot.SucceededCount);
    }

    [Fact]
    public async Task EndedPendingCanBeCancelledAndCleanupJoinsRepeatedClicks()
    {
        var state = State(); var cleanupStarted = Gate(); var cleanupRelease = Gate(); var cancelled = 0;
        CloudToolBatchUpdateCoordinator? coordinator = null;
        coordinator = new((_, _) =>
        {
            state = state with { PendingUpdate = true, Status = CloudToolStatus.PendingUpdate };
            return Task.FromResult(new CloudToolOperationResult(false, "等待退出", state));
        }, _ => state, _ => true, async candidate =>
        {
            Interlocked.Increment(ref cancelled); cleanupStarted.SetResult(); await cleanupRelease.Task;
            Assert.Equal(new string('a', 64), candidate.PackageSha256);
            state = state with { PendingUpdate = false, Status = CloudToolStatus.Installed };
            coordinator!.RefreshPendingOutcomes(); return true;
        });
        await coordinator.StartOrJoinAsync(Plan(state));
        var cancel = coordinator.CancelAsync(); await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(cancel, coordinator.CancelAsync()); Assert.Same(cancel, coordinator.StartOrJoinAsync(Plan(State())));
        cleanupRelease.SetResult(); var result = await cancel;
        Assert.Equal(1, cancelled); Assert.Equal(1, result.SkippedCount); Assert.Equal(0, result.PendingUpdateCount);
        Assert.Equal(0, result.FailedCount); Assert.False(result.IsRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedWithdrawalKeepsPendingAndNeverCancelsAnotherOwner(bool throws)
    {
        var state = State(); var cancelled = 0;
        var coordinator = new CloudToolBatchUpdateCoordinator((_, _) =>
        {
            state = state with { PendingUpdate = true, Status = CloudToolStatus.PendingUpdate };
            return Task.FromResult(new CloudToolOperationResult(false, "等待退出", state));
        }, _ => state, _ => true, _ =>
        {
            cancelled++; if (throws) throw new IOException("合成撤销失败"); return Task.FromResult(false);
        });
        await coordinator.StartOrJoinAsync(Plan(state)); var result = await coordinator.CancelAsync();
        Assert.Equal(1, cancelled); Assert.Equal(1, result.PendingUpdateCount); Assert.Equal(0, result.SucceededCount);
        Assert.Equal(0, result.SkippedCount); Assert.True(state.PendingUpdate); Assert.Contains("撤销", Assert.Single(result.Items).Message);
    }

    [Fact]
    public async Task CancellationWithdrawsEarlierDeferredItemBeforeBatchFinishes()
    {
        var states = new Dictionary<string, CloudToolState> { ["first-tool"] = State("first-tool"), ["second-tool"] = State("second-tool") };
        var secondStarted = Gate(); var withdrawn = new List<string>();
        var coordinator = new CloudToolBatchUpdateCoordinator(async (candidate, token) =>
        {
            if (candidate.Id == "first-tool")
            {
                states[candidate.Id] = states[candidate.Id] with { PendingUpdate = true, Status = CloudToolStatus.PendingUpdate };
                return new(false, "等待退出", states[candidate.Id]);
            }
            secondStarted.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new Exception("不应执行");
        }, id => states[id], _ => true, candidate =>
        {
            withdrawn.Add(candidate.Id); states[candidate.Id] = states[candidate.Id] with { PendingUpdate = false, Status = CloudToolStatus.Installed };
            return Task.FromResult(true);
        });
        var run = coordinator.StartOrJoinAsync(Plan(states.Values.ToArray()));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); var result = await coordinator.CancelAsync();
        Assert.Same(result, await run);
        Assert.Equal(["first-tool"], withdrawn); Assert.Equal(2, result.SkippedCount); Assert.Equal(0, result.PendingUpdateCount);
    }

    [Fact]
    public async Task AlreadyAppliedDeferredUpdateIsPreservedDuringCancellation()
    {
        var state = State();
        var coordinator = new CloudToolBatchUpdateCoordinator((_, _) =>
        {
            state = state with { PendingUpdate = true, Status = CloudToolStatus.PendingUpdate };
            return Task.FromResult(new CloudToolOperationResult(false, "等待退出", state));
        }, _ => state, _ => true, _ => { state = Applied(state); return Task.FromResult(false); });
        await coordinator.StartOrJoinAsync(Plan(state));
        var result = await coordinator.CancelAsync();
        Assert.Equal(1, result.SucceededCount); Assert.Equal(0, result.SkippedCount); Assert.Equal("2", state.Version);
    }

    [Fact]
    public async Task ADeferredFailureDuringWithdrawalDoesNotRemainFalselyPending()
    {
        var state = State();
        var coordinator = new CloudToolBatchUpdateCoordinator((_, _) =>
        {
            state = state with { PendingUpdate = true, Status = CloudToolStatus.PendingUpdate };
            return Task.FromResult(new CloudToolOperationResult(false, "等待退出", state));
        }, _ => state, _ => true, _ =>
        {
            state = state with { PendingUpdate = false, Status = CloudToolStatus.Failed, Error = "确认目标已经变化，旧版本保留" };
            return Task.FromResult(false);
        });
        await coordinator.StartOrJoinAsync(Plan(state));
        var result = await coordinator.CancelAsync();
        Assert.Equal(1, result.FailedCount); Assert.Equal(0, result.PendingUpdateCount); Assert.Equal(0, result.SucceededCount);
    }
}
