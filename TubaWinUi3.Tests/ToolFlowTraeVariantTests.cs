using TubaWinUi3.Services.AppManagement;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>Only fake installation evidence and temporary snapshots; no network, OS probe or software operation.</summary>
public sealed class ToolFlowTraeVariantTests
{
    [Theory]
    [InlineData("Trae 国内版", "agent")]
    [InlineData("Trae 中国版", "agent")]
    [InlineData("Trae 中国大陆版", "agent")]
    [InlineData("Trae（大陆版）", "agent")]
    [InlineData("TRAE CN", "agent")]
    [InlineData("TRAE-CN", "agent")]
    [InlineData("Trae China", "agent")]
    [InlineData("Trae domestic edition", "agent")]
    [InlineData("Trae", "agent 国内版")]
    [InlineData("Trae", "China version")]
    public async Task ExplicitChinaVariantNeverProbesOrReusesInternationalInstallation(string name, string kind)
    {
        foreach (var installed in new[] { false, true })
        {
            var item = Item(name, " TRAE ", kind);
            var calls = 0;
            var service = new ToolFlowPreparationService(["trae"], (key, _) =>
            {
                calls++;
                return Task.FromResult<InstalledToolEvidence?>(installed ? Evidence(key) : null);
            });
            var row = await service.EvaluateAsync(item);
            Assert.Equal(0, calls);
            Assert.Same(item, row.Item);
            Assert.False(string.IsNullOrWhiteSpace(ToolFlowAgentVariantPolicy.GetMismatchReason(item)));
            Assert.Equal(ToolFlowAgentVariantPolicy.GetMismatchReason(item), row.VariantMismatchReason);
            Assert.Equal(row.VariantMismatchReason, row.Message);
            Assert.Equal(ToolFlowPreparationState.NeedsUserAssist, row.State);
            Assert.False(row.AutomaticInstallationAllowed);
            Assert.False(row.DetectionAttempted);
            Assert.False(row.IsSatisfied);
            Assert.Null(row.ExistingTool);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunnerSkipsChinaMismatchButContinuesOtherItemsWithoutRewritingSelection(bool alreadyInstalled)
    {
        using var fixture = new SnapshotRoot();
        var bad = Item("Trae 国内版");
        var good = Item("Godot", "godot", "software");
        var store = new ToolFlowSelectionStore(fixture.Path);
        var selected = store.SelectForInstall(Plan(bad, good));
        var installer = new FakeInstaller();
        if (alreadyInstalled) installer.Present.Add("trae");
        var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selected.SubmissionId);
        var blocked = Assert.Single(result.Items, row => row.ItemId == bad.ItemId);
        Assert.Equal(ToolFlowInstallItemStatus.ManualStep, blocked.Status);
        Assert.Equal(ToolFlowAgentVariantPolicy.GetMismatchReason(bad), blocked.Message);
        Assert.Null(blocked.Delivery);
        Assert.Equal(ToolFlowInstallItemStatus.Installed, Assert.Single(result.Items, row => row.ItemId == good.ItemId).Status);
        Assert.Equal(new[] { "godot" }, installer.Installs);
        Assert.NotEmpty(installer.Probes);
        Assert.All(installer.Probes, key => Assert.Equal("godot", key));
        var saved = store.GetBySubmissionId(selected.SubmissionId)!;
        Assert.Equal(bad, saved.Items[0]);
        Assert.Equal("trae", saved.Items[0].InstallTargetKey);
        Assert.DoesNotContain(saved.Events, row => row.ItemId == bad.ItemId);
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("user-mark")]
    [InlineData("run-installed")]
    [InlineData("run-detected")]
    public void OldInternationalSuccessCannotMakeChinaVariantReady(string source)
    {
        var item = Item("TRAE CN");
        var selection = Plan(item) with
        {
            Events = source == "verified" ? [new()
            {
                Id = Guid.NewGuid().ToString("D"), ItemId = item.ItemId, Kind = ToolFlowEventKind.Verified,
                AtUtc = DateTimeOffset.UtcNow, Detail = "Previous international installation",
            }] : [],
            ItemMarks = source == "user-mark" ? [new()
            { ItemId = item.ItemId, Kind = ToolFlowItemMark.UserReportedDone, AtUtc = DateTimeOffset.UtcNow }] : [],
        };
        ToolFlowInstallResult? current = source is "run-installed" or "run-detected" ? new([new(item.ItemId,
            item.Name, source == "run-installed" ? ToolFlowInstallItemStatus.Installed : ToolFlowInstallItemStatus.AlreadyInstalled,
            "Previous international result")]) : null;
        var view = ToolFlowResume.Build(selection, current, ["trae"]);
        AssertBlocked(view, item);
        var stale = new ToolFlowPreparation([new(item, new(ToolFlowRequirementKind.ExactTarget),
            ToolFlowPreparationState.AlreadyInstalled, Evidence("trae"))
        { AutomaticInstallationAllowed = true, DetectionAttempted = true }]);
        AssertBlocked(ToolFlowResume.ApplyPreparation(view, stale), item);
        Assert.Same(selection, view.Selection);
        Assert.Same(item, Assert.Single(view.Selection.Items));
        Assert.Equal(source == "verified" ? 1 : 0, selection.Events.Count);
        Assert.Equal(source == "user-mark" ? 1 : 0, selection.ItemMarks.Count);
    }

    [Theory]
    [InlineData("Trae", "agent")]
    [InlineData("Trae 国际版", "agent")]
    [InlineData("Trae International", "agent")]
    [InlineData("Trae https://www.trae.com.cn/CN", "agent")]
    [InlineData("Trae", "agent https://www.trae.com.cn/国内版")]
    [InlineData("Trae Windows", "agent")]
    public async Task ExistingInternationalAndLegacyItemsRemainEligibleDespiteUnrelatedRegionalContext(string name, string kind)
    {
        foreach (var installed in new[] { false, true })
        {
            using var fixture = new SnapshotRoot();
            var item = Item(name, kind: kind) with
            {
                SourceUrl = "https://www.trae.com.cn/", DownloadUrl = "https://www.trae.com.cn/CN/download",
                ManualHint = "另一个方案讨论国内版、TRAE CN 和 China edition。",
            };
            Assert.Null(ToolFlowAgentVariantPolicy.GetMismatchReason(item));
            var store = new ToolFlowSelectionStore(fixture.Path);
            var selection = store.SelectForInstall(Plan(item) with
            {
                FlowText = "其他路线需要中国版；这里的原有 Trae 项目保持用户已确认的版本。",
                ProjectGoal = "讨论 TRAE CN 与国际版的区别",
            });
            var installer = new FakeInstaller();
            if (installed) installer.Present.Add("trae");
            var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId);
            Assert.Equal(installed ? ToolFlowInstallItemStatus.AlreadyInstalled : ToolFlowInstallItemStatus.Installed,
                Assert.Single(result.Items).Status);
            Assert.NotEmpty(installer.Probes);
            Assert.All(installer.Probes, key => Assert.Equal("trae", key));
            Assert.Equal(installed ? 0 : 1, installer.Installs.Count);
            Assert.Equal(item, Assert.Single(store.GetBySubmissionId(selection.SubmissionId)!.Items));
        }
    }

    [Theory]
    [InlineData("Trae（国内版）")]
    [InlineData("Trae (CN)")]
    [InlineData("TRAE CN")]
    public async Task ChinaNameWithoutAKeyCannotInferAnInternationalProbe(string name)
    {
        var item = Item(name, null);
        var probes = 0;
        var service = new ToolFlowPreparationService(["trae"], (key, _) =>
        { probes++; return Task.FromResult<InstalledToolEvidence?>(Evidence(key)); });
        var row = await service.EvaluateAsync(item);
        Assert.Null(ToolFlowItemSemantics.TryGetReadOnlyTarget(item));
        Assert.Equal(0, probes);
        Assert.False(row.DetectionAttempted);
        Assert.False(row.IsSatisfied);
        Assert.False(row.AutomaticInstallationAllowed);
        Assert.Equal(ToolFlowPreparationState.NeedsUserAssist, row.State);
        Assert.Null(row.ExistingTool);
        Assert.Null(item.InstallTargetKey);
    }

    [Theory]
    [InlineData("Trae")]
    [InlineData("Trae（已安装，直接复用）")]
    public async Task UnkeyedLegacyTraeKeepsReadOnlyReuseWithoutNewInstallationAuthority(string name)
    {
        var probes = new List<string>();
        var item = Item(name, null);
        var row = await new ToolFlowPreparationService(["trae"], (key, _) =>
        { probes.Add(key); return Task.FromResult<InstalledToolEvidence?>(Evidence(key)); }).EvaluateAsync(item);
        Assert.Equal(new[] { "trae" }, probes);
        Assert.Equal(ToolFlowPreparationState.AlreadyInstalled, row.State);
        Assert.True(row.IsSatisfied);
        Assert.False(row.AutomaticInstallationAllowed);
        Assert.Null(item.InstallTargetKey);
    }

    [Fact]
    public async Task ChinaMismatchDoesNotExposeInternationalLaunchEntry()
    {
        var item = Item("Trae 国内版");
        // Fail before ResolveAsync if its policy regresses; a failing test must
        // never fall through into the fixed target's real OS lookup.
        Assert.NotNull(ToolFlowAgentVariantPolicy.GetMismatchReason(item));
        var access = await ToolFlowToolAccess.ResolveAsync(item,
            (_, _) => throw new InvalidOperationException("No software probe is allowed."),
            _ => throw new InvalidOperationException("No file lookup is allowed."),
            _ => throw new InvalidOperationException("No directory lookup is allowed."));
        Assert.Null(access);
    }

    [Fact]
    public async Task RegionalLabelsDoNotGrantAnUnregisteredChinaInstaller()
    {
        using var fixture = new SnapshotRoot();
        var item = Item("Trae 国内版", "trae-cn");
        Assert.Null(ToolFlowAgentVariantPolicy.GetMismatchReason(item));
        Assert.Null(ToolFlowAgentVariantPolicy.GetMismatchReason(Item("Godot 中国版", "godot", "software")));
        var store = new ToolFlowSelectionStore(fixture.Path);
        var selection = store.SelectForInstall(Plan(item));
        var installer = new FakeInstaller();
        var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId);
        Assert.Equal(ToolFlowInstallItemStatus.ManualStep, Assert.Single(result.Items).Status);
        Assert.Empty(installer.Probes);
        Assert.Empty(installer.Installs);
        Assert.DoesNotContain("trae-cn", SystemInstaller.KnownTargets);
        Assert.DoesNotContain("ByteDance.Trae.CN", SystemInstaller.KnownTargets);
    }

    private static void AssertBlocked(ToolFlowResumeView view, ToolFlowItem item)
    {
        var row = Assert.Single(view.Rows);
        Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, row.State);
        Assert.Equal(ToolFlowAgentVariantPolicy.GetMismatchReason(item), row.StatusLine);
        Assert.False(row.AutomaticInstallationAllowed);
        Assert.False(row.CanContinueAutomatically);
        Assert.False(row.IsExistingToolReuse);
        Assert.Null(row.ExistingToolTargetKey);
        Assert.Equal(0, view.InstalledOrDetectedCount);
        Assert.Equal(0, view.UserReportedDoneCount);
        Assert.Empty(view.PendingAutomaticItems);
        var guide = ToolFlowGoalGuide.Create(view);
        Assert.NotNull(guide.CurrentStep);
        Assert.DoesNotContain(guide.ReadySteps, step => step.Rows.Any(member => member.ItemId == item.ItemId));
    }

    private static InstalledToolEvidence Evidence(string key) => new(key, "Trae international",
        @"C:\SyntheticTools\Trae\Trae.exe");
    private static ToolFlowItem Item(string name, string? target = "trae", string kind = "agent") => new()
    { ItemId = Guid.NewGuid().ToString("D"), Name = name, Kind = kind, InstallTargetKey = target };
    private static ToolFlowSelection Plan(params ToolFlowItem[] items) => new()
    {
        FlowId = Guid.NewGuid().ToString("D"), SubmissionId = Guid.NewGuid().ToString("D"), Origin = ToolFlowOrigin.User,
        SelectedAtUtc = DateTimeOffset.UtcNow, FlowName = "Synthetic regional variant fixture", ProjectGoal = "Prepare the selected tools",
        GoalDescription = "Synthetic only", FlowText = "No real execution", UploadEnabledAtSelection = false,
        Conversation = [], Items = items.ToList(),
    };
    private sealed class FakeInstaller : IToolFlowInstaller
    {
        public IReadOnlyCollection<string> KnownTargets => ["trae", "godot"];
        internal HashSet<string> Present { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Probes { get; } = [];
        internal List<string> Installs { get; } = [];
        public Task<bool> IsInstalledAsync(string targetKey, CancellationToken cancellationToken)
        { Probes.Add(targetKey); return Task.FromResult(Present.Contains(targetKey)); }
        public Task InstallAsync(string targetKey, CancellationToken cancellationToken)
        { Installs.Add(targetKey); Present.Add(targetKey); return Task.CompletedTask; }
    }
    private sealed class SnapshotRoot : IDisposable
    {
        private readonly string _parent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        internal string Path { get; }
        internal SnapshotRoot()
        {
            Path = System.IO.Path.Combine(_parent, "zxai-trae-variant-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path);
            if (!full.StartsWith(_parent.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(full).StartsWith("zxai-trae-variant-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a directory outside this fixture's temporary root.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
