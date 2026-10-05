using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>Fake metadata and paths only. No real discovery, process, Explorer, COM or desktop writes.</summary>
public sealed class ToolFlowFfmpegAccessTests
{
    private const string Root = @"C:\isolated-ffmpeg-access";
    private static readonly string Executable = Path.Combine(Root, "ffmpeg.exe");

    [Fact]
    public async Task VerifiedLegacyFfmpegGetsALocalAccessTargetWithoutChangingTheSnapshot()
    {
        var original = Item(null) with
        {
            Name = "FFmpeg（本机已装，复用）", ManualHint = "Use the existing local encoder.",
            SourceUrl = "https://ffmpeg.org/", Version = "8.0",
        };
        var preparation = await new ToolFlowPreparationService([], Probe()).EvaluateAsync(original);
        var access = ToolFlowToolAccess.AccessItem(preparation);
        Assert.Equal("ffmpeg", access.InstallTargetKey);
        Assert.Equal(original with { InstallTargetKey = "ffmpeg" }, access);
        Assert.Null(original.InstallTargetKey);
        Assert.Equal("FFmpeg（本机已装，复用）", original.Name);
        Assert.False(preparation.AutomaticInstallationAllowed);

        var entry = await Resolve(access, Probe());
        Assert.NotNull(entry);
        Assert.Equal(Executable, entry!.ExecutablePath);
        Assert.Equal(Root, entry.DirectoryPath);
        Assert.False(entry.IsGui);
    }

    [Theory]
    [InlineData((int)ToolFlowPreparationState.NeedsUserAssist, "ffmpeg", "ffmpeg.exe")]
    [InlineData((int)ToolFlowPreparationState.DetectionFailed, "ffmpeg", "ffmpeg.exe")]
    [InlineData((int)ToolFlowPreparationState.ReusedInstalledTool, "ffmpeg", "ffmpeg.exe")]
    [InlineData((int)ToolFlowPreparationState.AlreadyInstalled, "other", "ffmpeg.exe")]
    [InlineData((int)ToolFlowPreparationState.AlreadyInstalled, "ffmpeg", "ffprobe.exe")]
    public void UnverifiedOrWrongProviderEvidenceCannotSupplyAnAccessTarget(int state,
        string provider, string fileName)
    {
        var original = Item(null);
        var row = new ToolFlowPreparationItem(original, new(ToolFlowRequirementKind.ExactTarget), (ToolFlowPreparationState)state,
            new(provider, "FFmpeg", Path.Combine(Root, fileName)));
        Assert.Same(original, ToolFlowToolAccess.AccessItem(row));
    }

    [Fact]
    public async Task ResolveReadsFreshActualFileMetadataAndDoesNotTrustTheItemsName()
    {
        var reads = new List<string>();
        var entry = await Resolve(Item(" FFMPEG "), Probe(reads: reads));
        Assert.Equal(new[] { Executable }, reads);
        Assert.Equal("ffmpeg", entry!.TargetKey);
        Assert.Equal("FFmpeg", entry.Name);
        Assert.False(entry.IsGui);

        var renamedOtherProduct = await Resolve(Item("ffmpeg"), Probe(product: "Other program"));
        Assert.Null(renamedOtherProduct);
        Assert.Null(await Resolve(Item("ffmpeg"), Probe(originalName: "ffprobe.exe")));
        Assert.Null(await Resolve(Item("ffmpeg"), Probe(filePresent: false)));
    }

    [Theory]
    [InlineData("ffmpeg.exe")]
    [InlineData("ffmpeg-x64.exe")]
    [InlineData("ffmpeg-x86.exe")]
    [InlineData("ffmpeg-arm64.exe")]
    public void FixedFfmpegExecutableNamesOnlyAllowTheLocationAction(string fileName)
    {
        var path = Path.Combine(Root, fileName);
        var entry = Assert.IsType<ToolFlowToolAccessEntry>(ToolFlowToolAccess.ResolveCandidate("FFMPEG", path,
            p => p == path, p => p == Root));
        Assert.Equal("ffmpeg", entry.TargetKey);
        Assert.Equal("FFmpeg", entry.Name);
        Assert.False(entry.IsGui);
        Assert.Equal(entry, Validate(entry, entry, requireGui: false));
        Assert.Throws<InvalidOperationException>(() => Validate(entry, entry, requireGui: true));
    }

    [Theory]
    [InlineData("ffmpeg.exe")]
    [InlineData(@"C:ffmpeg.exe")]
    [InlineData(@"\\server\share\ffmpeg.exe")]
    [InlineData(@"C:\tools:stream\ffmpeg.exe")]
    [InlineData(@"C:\tools\%ENCODER%\ffmpeg.exe")]
    [InlineData("C:\\tools\\\"ffmpeg.exe")]
    [InlineData(@"C:\tools\ffprobe.exe")]
    [InlineData(@"C:\tools\FakeFFmpeg.exe")]
    [InlineData(@"C:\tools\ffmpeg.cmd")]
    public void RelativeRemoteShellAndWrongExecutableCandidatesCannotBecomeFfmpegEntries(string path)
        => Assert.Null(ToolFlowToolAccess.ResolveCandidate("ffmpeg", path, _ => true, _ => true));

    [Fact]
    public void MissingFileOrDirectoryCannotBeUsedAsAStandaloneFfmpegLocation()
    {
        Assert.Null(ToolFlowToolAccess.ResolveCandidate("ffmpeg", Executable, _ => false, _ => true));
        Assert.Null(ToolFlowToolAccess.ResolveCandidate("ffmpeg", Executable, _ => true, _ => false));
        Assert.Null(ToolFlowToolAccess.ResolveCandidate("ffmpeg", Root, _ => false, _ => true));
    }

    [Fact]
    public void LocalPathNormalizationPreservesTheSameVerifiedTarget()
    {
        var path = Path.Combine(Root, "unused", "..", "ffmpeg.exe");
        var entry = Assert.IsType<ToolFlowToolAccessEntry>(ToolFlowToolAccess.ResolveCandidate("ffmpeg", path,
            p => p == Executable, p => p == Root));
        Assert.Equal(Executable, entry.ExecutablePath);
        Assert.False(entry.IsGui);
    }

    [Fact]
    public async Task LocationRevalidationChecksFreshIdentityAndExactlyTheDisplayedPath()
    {
        var reads = new List<string>();
        var probe = Probe(reads: reads);
        var entry = (await Resolve(Item("ffmpeg"), probe))!;
        var current = await ToolFlowToolAccess.RevalidateAsync(entry, false, probe, Exists, DirectoryExists);
        Assert.Equal(entry, current);
        Assert.Equal(new[] { Executable, Executable }, reads);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ToolFlowToolAccess.RevalidateAsync(
            entry, false, Probe(product: "Replaced executable"), Exists, DirectoryExists));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ToolFlowToolAccess.RevalidateAsync(
            entry, false, Probe(filePresent: false), _ => false, DirectoryExists));
        var changed = Path.Combine(Root, "replacement", "ffmpeg.exe");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ToolFlowToolAccess.RevalidateAsync(
            entry, false, Probe(path: changed), p => p == changed,
            p => p == Path.GetDirectoryName(changed)));
    }

    [Fact]
    public async Task FfmpegGuiActionsRejectEvenForgedGuiFlagsBeforeAnyProbeOrAction()
    {
        var forged = new ToolFlowToolAccessEntry("ffmpeg", "FFmpeg", Executable, Root, IsGui: true);
        var probes = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ToolFlowToolAccess.RevalidateAsync(forged, true,
            (_, _) => { probes++; throw new Exception("GUI prohibition must run before discovery"); },
            _ => throw new Exception("Must not inspect files"), _ => throw new Exception("Must not inspect directories")));
        Assert.Equal(0, probes);

        // Public action paths use the same early guard; neither can reach discovery, process launch or COM.
        await Assert.ThrowsAsync<InvalidOperationException>(() => ToolFlowToolAccess.OpenToolAsync(forged));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ToolFlowToolAccess.CreateShortcutAsync(forged));
    }

    [Fact]
    public async Task MissingLocalAccessTargetAndArbitraryPathsDoNotTriggerFfmpegDiscovery()
    {
        Task<InstalledToolEvidence?> MustNotProbe(string _, CancellationToken __)
            => throw new Exception("Must not infer a local access target from names or URLs");
        Assert.Null(await Resolve(Item(null), MustNotProbe));
        Assert.Null(await Resolve(Item(Executable), MustNotProbe));
    }

    [Fact]
    public async Task CanceledFfmpegAccessCannotProduceAnEntry()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolFlowToolAccess.ResolveAsync(
            Item("ffmpeg"), (_, _) => throw new Exception("Must not probe"), Exists, DirectoryExists, stop.Token));
    }

    private static ToolFlowItem Item(string? target) => new()
    {
        ItemId = "encoder", Name = "FFmpeg (claimed installed)", Kind = "software", InstallTargetKey = target,
    };
    private static Func<string, CancellationToken, Task<InstalledToolEvidence?>> Probe(string product = "FFmpeg",
        string originalName = "ffmpeg.exe", bool filePresent = true, string? path = null, List<string>? reads = null)
        => (key, ct) =>
        {
            Assert.Equal("ffmpeg", key);
            var candidate = path ?? Executable;
            return Task.FromResult(ToolFlowInstalledToolProbe.ProbeFfmpeg(new(candidate, [], null),
                p => filePresent && p == candidate, p =>
                {
                    reads?.Add(p);
                    return new(product, "", originalName, "8.0");
                }, ct));
        };
    private static Task<ToolFlowToolAccessEntry?> Resolve(ToolFlowItem item,
        Func<string, CancellationToken, Task<InstalledToolEvidence?>> probe)
        => ToolFlowToolAccess.ResolveAsync(item, probe, Exists, DirectoryExists);
    private static bool Exists(string path) => path == Executable;
    private static bool DirectoryExists(string path) => path == Root;
    private static ToolFlowToolAccessEntry Validate(ToolFlowToolAccessEntry requested,
        ToolFlowToolAccessEntry current, bool requireGui)
        => ToolFlowToolAccess.ValidateActionTarget(requested, current, requireGui,
            p => p == current.ExecutablePath, p => p == current.DirectoryPath);
}
