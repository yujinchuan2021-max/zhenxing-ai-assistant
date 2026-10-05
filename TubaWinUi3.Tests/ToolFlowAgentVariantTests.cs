using System.Reflection;
using TubaWinUi3.Services;
using TubaWinUi3.Services.AppManagement;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>Fake installers and isolated snapshots only: no process, desktop entry, network or real software probe.</summary>
public sealed class ToolFlowAgentVariantTests
{
    [Theory]
    [InlineData("codex", "Codex Desktop", "agent")]
    [InlineData("CODEX", "Codex 桌面版", "agent")]
    [InlineData("opencode", "OpenCode 桌面客户端", "agent")]
    [InlineData(" opencode ", "OpenCode GUI", "agent")]
    [InlineData("claude-code", "Claude Code 图形界面", "agent")]
    [InlineData("claude-code", "Claude Code", "desktop")]
    [InlineData("codex", "Codex", "agent-gui")]
    [InlineData("codex", "Codex 中文Desktop版", "agent")]
    [InlineData("opencode", "OpenCode 中文GUI客户端", "agent")]
    public async Task ExplicitDesktopVariantNeverUsesCliDetection(string target, string name, string kind)
    {
        foreach (var installed in new[] { false, true })
        {
            var calls = 0;
            var item = Item(target, name) with { Kind = kind };
            var service = new ToolFlowPreparationService(["codex", "opencode", "claude-code"], (key, _) =>
            {
                calls++;
                return Task.FromResult<InstalledToolEvidence?>(installed ? new(key, "Existing CLI") : null);
            });
            var row = Assert.Single((await service.PrepareAsync([item])).Rows);
            Assert.Equal(0, calls);
            Assert.Same(item, row.Item);
            Assert.Equal(ToolFlowPreparationState.NeedsUserAssist, row.State);
            Assert.False(row.AutomaticInstallationAllowed);
            Assert.False(row.DetectionAttempted);
            Assert.False(row.IsSatisfied);
            Assert.Null(row.ExistingTool);
            Assert.Equal(ToolFlowAgentVariantPolicy.GetMismatchReason(item), row.VariantMismatchReason);
            Assert.Equal(row.VariantMismatchReason, row.Message);
        }
    }

    [Theory]
    [InlineData("codex", false)]
    [InlineData("codex", true)]
    [InlineData("opencode", false)]
    [InlineData("opencode", true)]
    [InlineData("claude-code", false)]
    [InlineData("claude-code", true)]
    public async Task RunnerSkipsOnlyTheMismatchWithoutProbingInstallingOrDeliveringIt(string target, bool installed)
    {
        using var fixture = new SnapshotRoot();
        var bad = Item(target, target + " 桌面版");
        var good = Item("godot", "Godot") with { Kind = "software" };
        var store = new ToolFlowSelectionStore(fixture.Path);
        var selection = store.SelectForInstall(Plan(bad, good));
        var installer = new FakeInstaller();
        if (installed) installer.Present.Add(target);
        var deliveries = new List<string>();
        var preparation = new ToolFlowPreparationService(installer.KnownTargets, async (key, ct) =>
            await installer.IsInstalledAsync(key, ct) ? new(key, key) : null);
        var runner = new ToolFlowInstallRunner(store, installer, preparation, (item, _, _) =>
        {
            deliveries.Add(item.InstallTargetKey!);
            return Task.FromResult(new ToolFlowPostInstallDeliveryResult(item.ItemId, item.Name,
                ToolFlowPostInstallDeliveryKind.DesktopShortcutReady, "Synthetic entry only"));
        });
        var result = await runner.RunAsync(selection.SubmissionId);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(ToolFlowInstallItemStatus.ManualStep, result.Items[0].Status);
        Assert.Equal(ToolFlowAgentVariantPolicy.GetMismatchReason(bad), result.Items[0].Message);
        Assert.Null(result.Items[0].Delivery);
        Assert.Equal(ToolFlowInstallItemStatus.Installed, result.Items[1].Status);
        Assert.Equal(new[] { "godot" }, installer.Installs);
        Assert.All(installer.Probes, key => Assert.Equal("godot", key));
        Assert.Equal(new[] { "godot" }, deliveries);
        var saved = store.GetBySubmissionId(selection.SubmissionId)!;
        Assert.Equal(bad, saved.Items[0]);
        Assert.DoesNotContain(saved.Events, entry => entry.ItemId == bad.ItemId);
        Assert.Contains(saved.Events, entry => entry.ItemId == good.ItemId && entry.Kind == ToolFlowEventKind.Verified);
    }

    [Theory]
    [InlineData("Codex CLI", "codex", false)]
    [InlineData("OpenCode CLI", "opencode", true)]
    [InlineData("Claude Code", "claude-code", false)]
    [InlineData("Claude 桌面版", "claude-desktop", true)]
    public async Task ExistingGuiTargetsAndExplicitCliRemainEligible(string name, string target, bool installed)
    {
        using var fixture = new SnapshotRoot();
        var item = Item(target, name) with
        {
            ManualHint = "Desktop GUI notes belong to another tool, not this item's selected variant.",
            SourceUrl = "https://example.invalid/desktop", DownloadUrl = "https://example.invalid/gui",
        };
        var installer = new FakeInstaller();
        if (installed) installer.Present.Add(target);
        var store = new ToolFlowSelectionStore(fixture.Path);
        var selection = store.SelectForInstall(Plan(item) with { FlowText = "Desktop GUI 桌面版应用方案" });
        var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId);
        Assert.Equal(installed ? ToolFlowInstallItemStatus.AlreadyInstalled : ToolFlowInstallItemStatus.Installed,
            Assert.Single(result.Items).Status);
        Assert.All(installer.Probes, key => Assert.Equal(target, key));
        Assert.Equal(installed ? 0 : 1, installer.Installs.Count);
        Assert.Null(ToolFlowAgentVariantPolicy.GetMismatchReason(item));
    }

    [Theory]
    [InlineData("Codex CLI", "codex", "agent")]
    [InlineData("Codex Windows", "codex", "agent")]
    [InlineData("Codex PC", "codex", "agent")]
    [InlineData("Codex CLI 桌面快捷方式", "codex", "agent")]
    [InlineData("OpenCode", "opencode", "software")]
    [InlineData("Codex https://example.invalid/desktop", "codex", "agent")]
    [InlineData("Desktopify", "codex", "agent")]
    [InlineData("Claude Desktop", "claude-desktop", "agent")]
    [InlineData("GitHub Desktop", "github-desktop", "software")]
    [InlineData("Godot GUI", "godot", "software")]
    [InlineData("Arbitrary Desktop", "made-up-target", "agent")]
    [InlineData("Codex Desktop", null, "agent")]
    public void PolicyDoesNotInferVariantFromOtherFieldsOrBroadenItsTargets(string name, string? target, string kind)
    {
        var item = Item(target, name) with { Kind = kind, ManualHint = "桌面版 Desktop GUI 图形界面",
            SourceUrl = "https://desktop.example.invalid/gui", DownloadUrl = "https://example.invalid/desktop.zip" };
        Assert.Null(ToolFlowAgentVariantPolicy.GetMismatchReason(item));
    }

    [Fact]
    public async Task UnknownDesktopTargetRemainsManualAndNeverBecomesAKnownInstaller()
    {
        using var fixture = new SnapshotRoot();
        var store = new ToolFlowSelectionStore(fixture.Path);
        var selection = store.SelectForInstall(Plan(Item("codex-desktop-unregistered", "Codex Desktop")));
        var installer = new FakeInstaller();
        var result = await new ToolFlowInstallRunner(store, installer).RunAsync(selection.SubmissionId);
        Assert.Equal(ToolFlowInstallItemStatus.ManualStep, Assert.Single(result.Items).Status);
        Assert.Empty(installer.Probes);
        Assert.Empty(installer.Installs);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("opencode")]
    [InlineData("claude-code")]
    public async Task DesktopMismatchCannotExposeAnExistingCliUsageEntry(string target)
    {
        var probes = 0;
        var pathChecks = 0;
        var entry = await ToolFlowToolAccess.ResolveAsync(Item(target, target + " Desktop"), (_, _) =>
        {
            probes++;
            throw new InvalidOperationException("A mismatched variant must not probe software.");
        }, _ =>
        {
            pathChecks++;
            throw new InvalidOperationException("A mismatched variant must not resolve an executable.");
        }, _ =>
        {
            pathChecks++;
            throw new InvalidOperationException("A mismatched variant must not resolve a directory.");
        });
        Assert.Null(entry);
        Assert.Equal(0, probes);
        Assert.Equal(0, pathChecks);
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("user-mark")]
    [InlineData("run-installed")]
    [InlineData("run-detected")]
    [InlineData("all")]
    public void ResumeDoesNotTreatOldCliSuccessOrSelfReportAsDesktopReadiness(string source)
    {
        var item = Item("codex", "Codex Desktop");
        var selection = Plan(item) with
        {
            Events = source is "verified" or "all" ? [Verified(item)] : [],
            ItemMarks = source is "user-mark" or "all" ? [new()
            { ItemId = item.ItemId, Kind = ToolFlowItemMark.UserReportedDone, AtUtc = DateTimeOffset.UtcNow }] : [],
        };
        ToolFlowInstallResult? current = source is "run-installed" or "run-detected" or "all"
            ? new([new(item.ItemId, item.Name, source == "run-detected"
                ? ToolFlowInstallItemStatus.AlreadyInstalled : ToolFlowInstallItemStatus.Installed, "Old CLI result")]) : null;
        var view = ToolFlowResume.Build(selection, current, ["codex"]);
        AssertBlocked(view, item);
        Assert.Same(selection, view.Selection);
        Assert.Same(item, view.Selection.Items.Single());
        Assert.Equal(source is "verified" or "all" ? 1 : 0, selection.Events.Count);
        Assert.Equal(source is "user-mark" or "all" ? 1 : 0, selection.ItemMarks.Count);
    }

    [Theory]
    [InlineData("satisfied")]
    [InlineData("missing")]
    [InlineData("changed-item")]
    public void StalePreparationCannotRestoreAConflictingDesktopItem(string source)
    {
        var item = Item("opencode", "OpenCode 桌面版");
        var selection = Plan(item);
        var oldRow = new ToolFlowResumeItemRow
        {
            ItemId = item.ItemId, Name = item.Name, Kind = item.Kind,
            State = ToolFlowResumeItemState.InstalledOrDetected, StatusLine = "Old CLI satisfied result",
            AutomaticInstallationAllowed = true, CanContinueAutomatically = true,
            CurrentRunStatus = ToolFlowInstallItemStatus.AlreadyInstalled,
            IsExistingToolReuse = true, ExistingToolName = "CLI", ExistingToolTargetKey = "opencode",
        };
        var oldView = new ToolFlowResumeView(selection, [oldRow], [oldRow], 1, 0, 0, 0);
        var evidenceItem = source == "changed-item" ? item with { Name = "OpenCode CLI" } : item;
        var preparation = new ToolFlowPreparation(source == "missing" ? [] : [new(evidenceItem,
            new(ToolFlowRequirementKind.ExactTarget), ToolFlowPreparationState.AlreadyInstalled,
            new("opencode", "OpenCode CLI")) { AutomaticInstallationAllowed = true, DetectionAttempted = true }]);
        AssertBlocked(ToolFlowResume.ApplyPreparation(oldView, preparation), item);
    }

    [Fact]
    public void OptionalDesktopMismatchDoesNotBecomeARequiredGoalStep()
    {
        foreach (var hint in new[] { "可选：以后需要时再配置。", "Optional: configure later if needed." })
        {
            var optional = Item("codex", "Codex Desktop") with { ManualHint = hint };
            var required = Item("godot", "Godot") with { Kind = "software" };
            var selection = Plan(required, optional) with { Events = [Verified(required)] };
            var original = ToolFlowResume.Build(selection, knownTargets: ["codex", "godot"]);
            var merged = ToolFlowResume.ApplyPreparation(original, new ToolFlowPreparation([]));
            foreach (var view in new[] { original, merged })
            {
                var row = Assert.Single(view.Rows, row => row.ItemId == optional.ItemId);
                Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, row.State);
                Assert.StartsWith(ToolFlowAgentVariantPolicy.GetMismatchReason(optional)!, row.ManualHint);
                Assert.Contains(hint, row.ManualHint);
                Assert.False(row.CanContinueAutomatically);
                var guide = ToolFlowGoalGuide.Create(view);
                Assert.Null(guide.CurrentStep); // The already-ready required tool has no extra blocker.
                var later = Assert.Single(guide.LaterSteps);
                Assert.True(later.IsOptional);
                Assert.Equal(optional.ItemId, Assert.Single(later.Rows).ItemId);
                Assert.DoesNotContain(guide.ReadySteps, step => step.Rows.Any(member => member.ItemId == optional.ItemId));
                Assert.Equal(hint, Assert.Single(view.Selection.Items, item => item.ItemId == optional.ItemId).ManualHint);
            }
        }
    }

    [Fact]
    public void ErrorMessageFollowsTheSelectedLanguage()
    {
        var field = typeof(LocalizationService).GetField("_currentLanguage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        try
        {
            var item = Item("codex", "Codex Desktop");
            field.SetValue(null, LocalizationService.ChineseLanguage);
            Assert.Contains("命令行", ToolFlowAgentVariantPolicy.GetMismatchReason(item));
            field.SetValue(null, LocalizationService.EnglishLanguage);
            Assert.Contains("command-line", ToolFlowAgentVariantPolicy.GetMismatchReason(item));
        }
        finally { field.SetValue(null, previous); }
    }

    [Fact]
    public void GuardTargetsMatchTheCurrentFixedCliCatalog()
    {
        foreach (var key in new[] { "codex", "opencode", "claude-code" })
        {
            Assert.Contains(key, SystemInstaller.KnownTargets);
            Assert.True(SystemInstaller.TryGetToolAccessMetadata(key, out var metadata));
            Assert.False(metadata.IsGui);
        }
        Assert.True(SystemInstaller.TryGetToolAccessMetadata("claude-desktop", out var desktop));
        Assert.True(desktop.IsGui);
        Assert.Null(ToolFlowAgentVariantPolicy.GetMismatchReason(Item("codex", "CodexDesktop")));
    }

    private static void AssertBlocked(ToolFlowResumeView view, ToolFlowItem item)
    {
        var row = Assert.Single(view.Rows);
        Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, row.State);
        Assert.Equal(ToolFlowAgentVariantPolicy.GetMismatchReason(item), row.StatusLine);
        Assert.Equal(row.StatusLine, row.ManualHint);
        Assert.False(row.AutomaticInstallationAllowed);
        Assert.False(row.CanContinueAutomatically);
        Assert.False(row.IsExistingToolReuse);
        Assert.Null(row.ExistingToolName);
        Assert.Null(row.ExistingToolTargetKey);
        Assert.Equal(ToolFlowInstallItemStatus.ManualStep, row.CurrentRunStatus);
        Assert.Empty(view.PendingAutomaticItems);
        Assert.Equal(0, view.InstalledOrDetectedCount);
        Assert.Equal(0, view.UserReportedDoneCount);
        Assert.Equal(1, view.NeedsUserAssistCount);
    }

    private static ToolFlowItem Item(string? target, string name) => new()
    { ItemId = Guid.NewGuid().ToString("D"), Name = name, Kind = "agent", InstallTargetKey = target };

    private static ToolFlowSelection Plan(params ToolFlowItem[] items) => new()
    {
        FlowId = Guid.NewGuid().ToString("D"), SubmissionId = Guid.NewGuid().ToString("D"),
        Origin = ToolFlowOrigin.User, SelectedAtUtc = DateTimeOffset.UtcNow,
        FlowName = "Synthetic selected plan", ProjectGoal = "Prepare a desktop toolchain",
        GoalDescription = "Prepare a desktop toolchain", FlowText = "Synthetic plan, no real execution.",
        UploadEnabledAtSelection = false, Conversation = [], Items = items.ToList(),
    };

    private static ToolFlowExecutionEvent Verified(ToolFlowItem item) => new()
    { Id = Guid.NewGuid().ToString("D"), ItemId = item.ItemId, Kind = ToolFlowEventKind.Verified,
        AtUtc = DateTimeOffset.UtcNow, Detail = "Previously detected CLI" };

    private sealed class FakeInstaller : IToolFlowInstaller
    {
        public IReadOnlyCollection<string> KnownTargets => ["codex", "opencode", "claude-code", "claude-desktop", "godot"];
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
            Path = System.IO.Path.Combine(_parent, "zxai-agent-variant-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path);
            if (!full.StartsWith(_parent.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(full).StartsWith("zxai-agent-variant-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a directory outside this test's temporary root.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
