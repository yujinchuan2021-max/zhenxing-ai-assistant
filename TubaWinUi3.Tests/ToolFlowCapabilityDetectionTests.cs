using System.Diagnostics;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>Capability evidence uses fake paths/metadata only; no installed tool, command or user configuration is opened.</summary>
public sealed class ToolFlowCapabilityDetectionTests
{
    [Fact]
    public async Task FfmpegCanBeDetectedWithoutBecomingAnAutomaticInstallationTarget()
    {
        var item = Item("FFmpeg", "ffmpeg");
        var service = Service(FfmpegEvidence());
        var preparation = await service.PrepareAsync([item]);
        var row = Assert.Single(preparation.Rows);
        Assert.Equal(ToolFlowPreparationState.AlreadyInstalled, row.State);
        Assert.True(row.DetectionAttempted);
        Assert.False(row.AutomaticInstallationAllowed);
        Assert.Empty(preparation.ConfirmationItems);

        var view = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(Selection(item)), preparation);
        Assert.Equal(ToolFlowResumeItemState.InstalledOrDetected, Assert.Single(view.Rows).State);
        Assert.Empty(view.PendingAutomaticItems);
        Assert.Equal("FFmpeg", view.Rows[0].ExistingToolName);
    }

    [Fact]
    public async Task ExistingWinRarAlsoRemainsAnIndependentReadOnlyTarget()
    {
        var row = await new ToolFlowPreparationService([], (key, _) =>
            Task.FromResult<InstalledToolEvidence?>(key == "winrar" ? new("winrar", "WinRAR") : null))
            .EvaluateAsync(Item("WinRAR", "winrar"));
        Assert.True(row.IsSatisfied);
        Assert.True(row.DetectionAttempted);
        Assert.False(row.AutomaticInstallationAllowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("arbitrary-command")]
    public async Task ExactLegacyFfmpegNameOnlyAddsAReadOnlyCheck(string? target)
    {
        var item = Item(" FFmpeg ", target);
        var probed = new List<string>();
        var service = new ToolFlowPreparationService([], (key, _) =>
        {
            probed.Add(key);
            return Task.FromResult<InstalledToolEvidence?>(FfmpegEvidence());
        });
        var row = await service.EvaluateAsync(item, "An unrelated executable path elsewhere in the plan.");
        Assert.Equal(new[] { "ffmpeg" }, probed);
        Assert.True(row.IsSatisfied);
        Assert.False(row.AutomaticInstallationAllowed);
        Assert.Same(item, row.Item);
        Assert.Equal(target, row.Item.InstallTargetKey);
    }

    [Theory]
    [InlineData("FFmpeg（本机已装，复用）")]
    [InlineData("FFmpeg (already installed, reuse)")]
    [InlineData("FFmpeg.exe（本机已有）")]
    [InlineData("FFmpeg CLI (existing local encoder)")]
    public async Task LegacyFfmpegParentheticalDescriptionsStillRequireActualExecutableEvidence(string name)
    {
        var item = Item(name, null);
        var probes = new List<string>();
        var missing = await new ToolFlowPreparationService([], (key, _) =>
        {
            probes.Add(key);
            return Task.FromResult<InstalledToolEvidence?>(null);
        }).EvaluateAsync(item);
        Assert.Equal(new[] { "ffmpeg" }, probes);
        Assert.True(missing.DetectionAttempted);
        Assert.False(missing.IsSatisfied);
        Assert.False(missing.AutomaticInstallationAllowed);
        Assert.Equal(ToolFlowPreparationState.NeedsUserAssist, missing.State);

        var detected = await Service(FfmpegEvidence()).EvaluateAsync(item);
        Assert.True(detected.IsSatisfied);
        Assert.False(detected.AutomaticInstallationAllowed);
        Assert.Null(detected.Item.InstallTargetKey);
        Assert.Equal(name, detected.Item.Name);
    }

    [Theory]
    [InlineData("FFmpeg", "service")]
    [InlineData("FFmpeg account", "software")]
    [InlineData("Download FFmpeg", "software")]
    [InlineData("FFmpeg installed", "software")]
    [InlineData(@"C:\FAKE\ffmpeg.exe", "software")]
    [InlineData("https://example.invalid/ffmpeg.exe", "software")]
    [InlineData("Other encoder", "software")]
    [InlineData("FFmpeg教程", "software")]
    [InlineData("FFmpeg tutorial", "software")]
    [InlineData("ffmpeg-python", "software")]
    [InlineData("ffprobe", "software")]
    [InlineData("FakeFFmpeg", "software")]
    [InlineData("FFmpegToolkit", "software")]
    [InlineData("FFmpeg插件", "software")]
    [InlineData("FFmpeg plugin", "software")]
    [InlineData("FFmpeg（本机已装）（复用）", "software")]
    [InlineData("FFmpeg (installed) (reuse)", "software")]
    [InlineData("FFmpeg（本机(已装)）", "software")]
    [InlineData("FFmpeg（本机已装)", "software")]
    [InlineData("FFmpeg（本机已装，复用）插件", "software")]
    public async Task LegacyDetectionNeverInterpretsMentionsCommandsPathsOrAccounts(string name, string kind)
    {
        var item = Item(name, null) with { Kind = kind, ManualHint = "FFmpeg is already installed" };
        var row = await new ToolFlowPreparationService([], (_, _) => throw new Exception("Must not probe"))
            .EvaluateAsync(item, "Use FFmpeg from C:\\FAKE\\ffmpeg.exe");
        Assert.Equal(ToolFlowPreparationState.NeedsUserAssist, row.State);
        Assert.False(row.DetectionAttempted);
        Assert.False(row.AutomaticInstallationAllowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative\\ffmpeg.exe")]
    [InlineData(@"C:\FAKE\other.exe")]
    public async Task AClaimOfInstalledFfmpegWithoutItsExecutableCannotSatisfyPreparation(string? path)
    {
        var row = await Service(new("ffmpeg", "FFmpeg", path)).EvaluateAsync(Item("FFmpeg", "ffmpeg"));
        Assert.False(row.IsSatisfied);
        Assert.Equal(ToolFlowPreparationState.NeedsUserAssist, row.State);
        Assert.True(row.DetectionAttempted);
        Assert.False(row.AutomaticInstallationAllowed);
    }

    [Fact]
    public async Task MissingFfmpegAndProbeErrorsNeverGrantAutomaticContinue()
    {
        var item = Item("FFmpeg", "ffmpeg") with { ManualHint = "Use the converter's component download entry." };
        foreach (var service in new[]
        {
            Service(null),
            new ToolFlowPreparationService([], (_, _) => throw new IOException("PRIVATE-ERROR-PATH")),
        })
        {
            var preparation = await service.PrepareAsync([item]);
            var view = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(Selection(item)), preparation);
            Assert.Empty(view.PendingAutomaticItems);
            Assert.False(view.Rows[0].CanContinueAutomatically);
            Assert.False(preparation.Rows[0].AutomaticInstallationAllowed);
            Assert.DoesNotContain("PRIVATE-ERROR-PATH", view.Rows[0].StatusLine);
        }
    }

    [Fact]
    public void CandidateExpansionUsesFixedFileNamesAndFullyQualifiedDirectories()
    {
        var managed = @"C:\FAKE-ROOT\data\ffmpeg\ffmpeg-x64.exe";
        var paths = new ToolFlowInstalledToolPaths(managed,
            ["relative", "", @"C:drive-relative", "\"C:\\FAKE-ROOT\\path\"", @"C:\FAKE-ROOT\path"],
            @"C:\FAKE-ROOT\links");
        Assert.Equal(new[] { managed, @"C:\FAKE-ROOT\links\ffmpeg.exe", @"C:\FAKE-ROOT\path\ffmpeg.exe" },
            ToolFlowInstalledToolProbe.FfmpegCandidates(paths));
        Assert.Empty(ToolFlowInstalledToolProbe.FfmpegCandidates(new(@"C:\FAKE\unexpected.exe", [], null)));
    }

    [Theory]
    [InlineData("FFmpeg", "", "ffmpeg.exe", true)]
    [InlineData("Jellyfin FFmpeg", "", "ffmpeg.exe", true)]
    [InlineData("", "FFmpeg command-line tool", "", true)]
    [InlineData("Other product", "", "ffmpeg.exe", false)]
    [InlineData("", "", "ffmpeg.exe", false)]
    [InlineData("FFmpeg", "FFmpeg", "ffprobe.exe", false)]
    [InlineData("FFmpegTools", "", "ffmpeg.exe", false)]
    public void FfmpegEvidenceRequiresTheActualFilesProductIdentity(string product, string description,
        string originalName, bool accepted)
    {
        var paths = new ToolFlowInstalledToolPaths(@"C:\FAKE-ROOT\ffmpeg-x64.exe", [], null);
        var evidence = ToolFlowInstalledToolProbe.ProbeFfmpeg(paths, _ => true,
            _ => new(product, description, originalName, "8.1.2"));
        Assert.Equal(accepted, evidence is not null);
        if (accepted)
        {
            Assert.Equal("ffmpeg", evidence!.TargetKey);
            Assert.Equal("8.1.2", evidence.Version);
            Assert.Equal(paths.ManagedFfmpegPath, evidence.ExecutablePath);
        }
    }

    [Fact]
    public void FakeRootMissingAndRenamedNonExecutablesDoNotCreateEvidenceOrNewFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "zxai-flow-ffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "ffmpeg.exe");
            var paths = new ToolFlowInstalledToolPaths(executable, [root], null);
            Assert.Null(ToolFlowInstalledToolProbe.ProbeFfmpeg(paths, File.Exists,
                _ => throw new Exception("Metadata is never read for a missing file")));
            File.WriteAllText(executable, "This fake file has no FFmpeg executable identity.");
            var before = File.ReadAllBytes(executable);
            Assert.Null(ToolFlowInstalledToolProbe.ProbeFfmpeg(paths, File.Exists, path =>
            {
                Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase);
                var info = FileVersionInfo.GetVersionInfo(path);
                return new(info.ProductName, info.FileDescription, info.OriginalFilename, info.ProductVersion);
            }));
            Assert.Equal(before, File.ReadAllBytes(executable));
            Assert.Equal(new[] { executable }, Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void UnusableManagedCandidateFallsBackToVerifiedPathCandidate()
    {
        var managed = @"C:\FAKE-ROOT\managed\ffmpeg-x64.exe";
        var external = @"C:\FAKE-ROOT\path\ffmpeg.exe";
        var checkedPaths = new List<string>();
        var found = ToolFlowInstalledToolProbe.ProbeFfmpeg(new(managed, [@"C:\FAKE-ROOT\path"], null),
            _ => true, path =>
            {
                checkedPaths.Add(path);
                return new(path == external ? "FFmpeg" : "Other software", "", "ffmpeg.exe", "8.0");
            });
        Assert.Equal(new[] { managed, external }, checkedPaths);
        Assert.Equal(external, found!.ExecutablePath);
    }

    [Fact]
    public void CanceledFfmpegProbeDoesNotReadFiles()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ToolFlowInstalledToolProbe.ProbeFfmpeg(
            new(@"C:\FAKE-ROOT\ffmpeg.exe", [], null),
            _ => throw new Exception("Must not read files"),
            _ => throw new Exception("Must not read metadata"), stop.Token));
    }

    private static ToolFlowPreparationService Service(InstalledToolEvidence? evidence)
        => new([], (_, _) => Task.FromResult(evidence));
    private static InstalledToolEvidence FfmpegEvidence() => new("ffmpeg", "FFmpeg", @"C:\FAKE-ROOT\ffmpeg.exe", "8.1.2");
    private static ToolFlowItem Item(string name, string? target) => new()
    {
        ItemId = Guid.NewGuid().ToString("D"), Name = name, Kind = "software", InstallTargetKey = target,
    };
    private static ToolFlowSelection Selection(ToolFlowItem item) => new()
    {
        FlowId = Guid.NewGuid().ToString("D"), SubmissionId = Guid.NewGuid().ToString("D"),
        Origin = ToolFlowOrigin.Assistant, SelectedAtUtc = DateTimeOffset.UtcNow,
        GoalDescription = "Prepare the encoder", FlowText = "Prepare the encoder", UploadEnabledAtSelection = false,
        Conversation = [], Items = [item],
    };
}
