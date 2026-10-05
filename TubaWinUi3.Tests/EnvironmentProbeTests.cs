using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Tests;

/// <summary>Only injected paths and file metadata. No registry, settings, process or OS inventory access.</summary>
public sealed class EnvironmentProbeTests
{
    [Theory]
    [InlineData("python", @"C:\FAKE\Python313\python.exe", "Python", "3.13.3")]
    [InlineData("python", @"C:\FAKE\WindowsApps\PythonSoftwareFoundation.Python.3.13_fake\python.exe", "Python", "3.13.3")]
    [InlineData("python", @"C:\FAKE\windowsapps\PythonSoftwareFoundation.Python.3.12_fake\python.exe", "Python", "3.12.10")]
    [InlineData("node", @"C:\FAKE\hermes\node\node.exe", "Node.js", "22.17.0")]
    [InlineData("node", @"C:\FAKE\node\node.exe", "Node.js JavaScript Runtime", "v22.17.0")]
    [InlineData("git", @"C:\FAKE\Git\cmd\git.exe", "Git", "2.49.0.windows.1")]
    [InlineData("git", @"C:\FAKE\Git\cmd\git.exe", "Git for Windows", "2.49.0")]
    public void RealRuntimeIdentityAndVersionAreRecognizedWithoutLaunching(string key, string path,
        string product, string actualVersion)
    {
        var valid = EnvironmentProbe.TryValidateRuntimeCandidate(key, path, _ => true,
            _ => new(product, actualVersion), out var version);
        Assert.True(valid);
        Assert.Equal(actualVersion, version);
    }

    [Theory]
    [InlineData(@"C:\FAKE\Microsoft\WindowsApps\python.exe")]
    [InlineData(@"C:\FAKE\Microsoft\windowsapps\PYTHON.EXE")]
    public void WindowsAppsRootAliasIsRejectedBeforeAnyFileMetadataLookup(string path)
    {
        Assert.False(EnvironmentProbe.TryValidateRuntimeCandidate("python", path,
            _ => throw new Exception("Alias must not be inspected"),
            _ => throw new Exception("Alias must not be inspected"), out var version));
        Assert.Equal("", version);
    }

    [Theory]
    [InlineData("python", "Python", "")]
    [InlineData("python", "Python", "Python was not found")]
    [InlineData("python", "Other app", "3.13.3")]
    [InlineData("python", "", "3.13.3")]
    [InlineData("node", "Node.js", "")]
    [InlineData("node", "Node.js", "FAILED")]
    [InlineData("node", "Other app", "22.17.0")]
    [InlineData("git", "Git", "")]
    [InlineData("git", "Git", "fatal: command failed")]
    [InlineData("git", "Other app", "2.49.0")]
    public void EmptyFailedOrWrongProductEvidenceCannotBecomeAvailable(string key, string product, string actualVersion)
    {
        var file = key == "node" ? "node.exe" : key == "git" ? "git.exe" : "python.exe";
        Assert.False(EnvironmentProbe.TryValidateRuntimeCandidate(key, @"C:\FAKE\" + file, _ => true,
            _ => new(product, actualVersion), out var version));
        Assert.Equal("", version);
    }

    [Fact]
    public void MissingRuntimeAndFailedMetadataLookupDoNotEstablishAvailability()
    {
        Assert.False(EnvironmentProbe.TryValidateRuntimeCandidate("python", @"C:\FAKE\python.exe", _ => false,
            _ => throw new Exception("Missing file must not be read"), out _));
        Assert.False(EnvironmentProbe.TryValidateRuntimeCandidate("python", @"C:\FAKE\python.exe", _ => true,
            _ => throw new IOException("FAKE-READ-FAILURE"), out _));
    }

    [Theory]
    [InlineData("python", "python.exe")]
    [InlineData("python", @"C:\FAKE\other.exe")]
    [InlineData("node", @"C:\FAKE\python.exe")]
    [InlineData("unknown", @"C:\FAKE\python.exe")]
    public void CandidateMustUseTheExpectedRuntimeAndAbsoluteExecutablePath(string key, string path)
        => Assert.False(EnvironmentProbe.TryValidateRuntimeCandidate(key, path, _ => true,
            _ => new("Python", "3.13.3"), out _));

    [Theory]
    [InlineData("python", "Python", "3.13.3", "python.exe")]
    [InlineData("node", "Node.js", "22.17.0", "node.exe")]
    public void RegisteredRuntimeUsesTheRealProductIdentity_NotTheRecommendedVersionLabel(string key,
        string product, string version, string exe)
    {
        Assert.True(SystemInstaller.TryGetToolAccessMetadata(key, out var metadata));
        Assert.NotEqual(product, metadata.Name); // Python 3.12 / Node.js LTS are recommendation labels.
        var path = @"D:\FAKE\Outside-PATH\" + exe;
        var record = new SoftRecord { Name = "A user-entered label", Path = path, Version = "FAKE-USER-VERSION", Source = "manual" };
        var found = EnvironmentProbe.FindRegisteredRuntimeCandidate(key, [record], p => [p], _ => true,
            _ => new(product, version));
        Assert.Equal(path, found);
        Assert.Equal("FAKE-USER-VERSION", record.Version);
    }

    [Fact]
    public void RegisteredAliasOrUnverifiedEntryCannotHideALaterRealRuntime()
    {
        const string alias = @"C:\FAKE\Microsoft\WindowsApps\python.exe";
        const string actual = @"D:\FAKE\Python\python.exe";
        SoftRecord[] records = [new() { Path = alias }, new() { Path = actual }];
        var found = EnvironmentProbe.FindRegisteredRuntimeCandidate("python", records, p => [p], _ => true,
            path => path == actual ? new("Python", "3.13.3") : throw new Exception("Alias must not be read"));
        Assert.Equal(actual, found);
    }

    [Theory]
    [InlineData("python", false)]
    [InlineData("node", false)]
    [InlineData("git", false)]
    [InlineData("PYTHON", false)]
    [InlineData("7zip", false)]
    [InlineData("winrar", false)]
    [InlineData("gimp", true)]
    public void OsRegistrationCannotResurrectARejectedRuntimeCandidate(string key, bool eligible)
        => Assert.Equal(eligible, SystemInstaller.CanUseOsRegistrationWithoutExecutable(key));
}
