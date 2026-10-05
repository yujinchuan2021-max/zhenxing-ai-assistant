using TubaWinUi3.Services;
using TubaWinUi3.Services.AppManagement;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>Only synthetic package directories and empty EXE placeholders; no Python, pip or installer is executed.</summary>
public sealed class SystemInstallerPipEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-pip-evidence-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousRoot = DataRoots.TestRootOverrideForTest;
    private string Python => Path.Combine(_root, "Python312", "python.exe");
    private string Roaming => Path.Combine(_root, "Roaming");

    public SystemInstallerPipEvidenceTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Python)!);
        File.WriteAllText(Python, "synthetic file; never executed");
        DataRoots.TestRootOverrideForTest = _root;
    }

    [Theory]
    [InlineData("vllm", "vllm", "vllm")]
    [InlineData("inspect-ai", "inspect_ai", "inspect")]
    [InlineData("ragas", "ragas", "ragas")]
    [InlineData("helm", "crfm-helm", "helm")]
    [InlineData("deepeval", "deepeval", "deepeval")]
    public void FixedPackageAndConsoleEntryAreFoundWithoutAnyPathDiscovery(string target, string distribution, string command)
    {
        var directory = Path.GetDirectoryName(Python)!;
        WritePackage(Path.Combine(directory, "Lib", "site-packages"), distribution);
        var entry = WriteEntry(Path.Combine(directory, "Scripts"), command);
        var evidence = Probe(target);
        Assert.NotNull(evidence);
        Assert.Equal(target, evidence.TargetKey);
        Assert.Equal("1.2.3", evidence.Version);
        Assert.Equal(entry, evidence.ExecutablePath);
        Assert.True(ToolFlowToolAccess.TryGetCliLaunchInstruction(
            new(target, evidence.Name, entry, Path.GetDirectoryName(entry), IsGui: false), out var instruction));
        Assert.Contains(entry, instruction.Command);
    }

    [Fact]
    public void StandardUserSiteKeepsItsOwnConsoleEntry()
    {
        var userBase = Path.Combine(Roaming, "Python", "Python312");
        WritePackage(Path.Combine(userBase, "site-packages"), "inspect-ai");
        var entry = WriteEntry(Path.Combine(userBase, "Scripts"), "inspect");
        Assert.Equal(entry, Probe("inspect-ai")?.ExecutablePath);
    }

    [Fact]
    public async Task InstalledPackageWithoutAConsoleIsSatisfiedAndDoesNotPretendItsOpeningEntryIsReady()
    {
        WritePackage(Path.Combine(Path.GetDirectoryName(Python)!, "Lib", "site-packages"), "ragas");
        var evidence = Probe("ragas");
        Assert.NotNull(evidence);
        Assert.Null(evidence.ExecutablePath);
        var preparation = new ToolFlowPreparationService(["ragas"], (_, _) => Task.FromResult(evidence));
        var item = new ToolFlowItem { ItemId = "library", Name = "RAGAS", Kind = "software", InstallTargetKey = "ragas" };
        var row = await preparation.EvaluateAsync(item);
        Assert.Equal(ToolFlowPreparationState.AlreadyInstalled, row.State);
        Assert.True(row.IsSatisfied);
        var delivery = new ToolFlowPostInstallDelivery((_, _) => Task.FromResult<ToolFlowToolAccessEntry?>(null),
            (_, _) => throw new InvalidOperationException("No desktop shortcut may be created for this library."));
        var result = await delivery.DeliverAsync(item, new(item.ItemId, item.Name, ToolFlowInstallItemStatus.AlreadyInstalled, "Synthetic"));
        Assert.Equal(ToolFlowPostInstallDeliveryKind.Unavailable, result.Kind);
    }

    [Fact]
    public void VirtualEnvironmentDoesNotBorrowADisabledUserSitePackage()
    {
        var virtualPython = WriteEntry(Path.Combine(_root, "venv", "Scripts"), "python");
        File.WriteAllText(Path.Combine(_root, "venv", "pyvenv.cfg"), "include-system-site-packages = false");
        WritePackage(Path.Combine(Roaming, "Python", "Python312", "site-packages"), "inspect-ai");
        Assert.Null(SystemInstaller.ProbePipPackageFiles("inspect-ai", virtualPython, "3.12.9", Roaming));
        WritePackage(Path.Combine(_root, "venv", "Lib", "site-packages"), "inspect-ai");
        var entry = WriteEntry(Path.Combine(_root, "venv", "Scripts"), "inspect");
        Assert.Equal(entry, SystemInstaller.ProbePipPackageFiles("inspect-ai", virtualPython, "3.12.9", Roaming)?.ExecutablePath);
    }

    [Theory]
    [InlineData("Name: unrelated\nVersion: 1.2.3\n\n")]
    [InlineData("Name: inspect-ai\nVersion: 1.2.3\nName: inspect-ai\n\n")]
    [InlineData("Name: inspect-ai\nVersion: ../not-a-version\n\n")]
    [InlineData("Name: inspect-ai\n\nVersion: 1.2.3\n")]
    public void WrongOrMalformedDistributionMetadataCannotEstablishInstallation(string metadata)
    {
        var path = WritePackage(Path.Combine(Path.GetDirectoryName(Python)!, "Lib", "site-packages"), "inspect-ai");
        File.WriteAllText(path, metadata);
        WriteEntry(Path.Combine(Path.GetDirectoryName(Python)!, "Scripts"), "inspect");
        Assert.Null(Probe("inspect-ai"));
    }

    [Fact]
    public void LargeDescriptionDoesNotInvalidateALegalShortMetadataHeader()
    {
        var path = WritePackage(Path.Combine(Path.GetDirectoryName(Python)!, "Lib", "site-packages"), "inspect-ai");
        File.WriteAllText(path, "Metadata-Version: 2.1\r\nName: inspect-ai\r\nVersion: 1.2.3\r\n\r\n" + new string('x', 1024 * 1024));
        Assert.Equal("1.2.3", Probe("inspect-ai")?.Version);
    }

    [Fact]
    public void OversizedHeaderIsRejectedEvenIfItEventuallyContainsMatchingPackageIdentity()
    {
        var path = WritePackage(Path.Combine(Path.GetDirectoryName(Python)!, "Lib", "site-packages"), "inspect-ai");
        File.WriteAllText(path, "License: " + new string('x', 64 * 1024) + "\nName: inspect-ai\nVersion: 1.2.3\n\n");
        Assert.Null(Probe("inspect-ai"));
    }

    [Fact]
    public void MissingPackageAndUnrelatedConsoleFilesAreNotEvidence()
    {
        Assert.Null(Probe("inspect-ai"));
        WriteEntry(Path.Combine(Path.GetDirectoryName(Python)!, "Scripts"), "inspect");
        Assert.Null(Probe("inspect-ai"));
        WritePackage(Path.Combine(Path.GetDirectoryName(Python)!, "Lib", "site-packages"), "inspect-ai");
        File.Delete(Path.Combine(Path.GetDirectoryName(Python)!, "Scripts", "inspect.exe"));
        WriteEntry(Path.Combine(Path.GetDirectoryName(Python)!, "Scripts"), "unrelated");
        Assert.Null(Probe("inspect-ai")?.ExecutablePath);
    }

    [Fact]
    public void UnknownTargetDoesNotReadOrCreateAnyPackage()
    {
        WritePackage(Path.Combine(Path.GetDirectoryName(Python)!, "Lib", "site-packages"), "untrusted");
        Assert.Null(Probe("untrusted"));
        Assert.Null(SystemInstaller.ProbePipPackageFiles("inspect-ai", "python.exe", "3.12.9", Roaming));
        Assert.Null(SystemInstaller.ProbePipPackageFiles("inspect-ai", Path.Combine(_root, "missing", "python.exe"), "3.12.9", Roaming));
    }

    [Fact]
    public void CallerCancellationStopsFileLookup()
    {
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SystemInstaller.ProbePipPackageFiles("inspect-ai", Python, "3.12.9", Roaming, stopped.Token));
    }

    private InstalledToolEvidence? Probe(string target) => SystemInstaller.ProbePipPackageFiles(target, Python, "3.12.9", Roaming);
    private static string WritePackage(string packages, string distribution)
    {
        var directory = Path.Combine(packages, distribution.Replace('-', '_') + "-1.2.3.dist-info");
        Directory.CreateDirectory(directory);
        var metadata = Path.Combine(directory, "METADATA");
        File.WriteAllText(metadata, "Metadata-Version: 2.1\nName: " + distribution + "\nVersion: 1.2.3\n\nSynthetic package only.");
        return metadata;
    }
    private static string WriteEntry(string scripts, string command)
    {
        Directory.CreateDirectory(scripts);
        var executable = Path.Combine(scripts, command + ".exe");
        File.WriteAllText(executable, "synthetic file; never executed");
        return executable;
    }
    public void Dispose()
    {
        DataRoots.TestRootOverrideForTest = _previousRoot;
        Directory.Delete(_root, recursive: true);
    }
}
