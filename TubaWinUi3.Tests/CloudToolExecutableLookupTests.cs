using TubaWinUi3.Controls;

namespace TubaWinUi3.Tests;

/// <summary>GUID-owned fake files only; never scans real program folders or executes a tool.</summary>
public sealed class CloudToolExecutableLookupTests : IDisposable
{
    private readonly string _directoryName = "zxai-executable-lookup-" + Guid.NewGuid().ToString("N");
    private readonly string _root;

    public CloudToolExecutableLookupTests()
    {
        _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), _directoryName));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void MissingDirectoriesReturnNullWithoutThrowing()
    {
        var missing = Path.Combine(_root, "missing");
        Assert.Null(CloudToolExecutableLookup.FindInRoots([missing], ["prime95.exe"]));
    }

    [Fact]
    public void RecursiveLookupReturnsTheExactExecutableName()
    {
        var entry = FakeFile("nested", "native", "prime95.exe");
        FakeFile("nested", "prime95-helper.exe");
        Assert.Equal(entry, CloudToolExecutableLookup.FindInRoots([_root], ["PRIME95.EXE"]));
    }

    [Fact]
    public void HelperAndOtherExecutableNamesDoNotMatch()
    {
        FakeFile("prime95-helper.exe");
        FakeFile("other.exe");
        Assert.Null(CloudToolExecutableLookup.FindInRoots([_root], ["prime95.exe", "prime95"]));
    }

    [Fact]
    public void BareStemOnlyReturnsExeNotAnExtensionlessFile()
    {
        FakeFile("prime95");
        Assert.Null(CloudToolExecutableLookup.FindInRoots([_root], ["prime95"]));
        var entry = FakeFile("prime95.exe");
        Assert.Equal(entry, CloudToolExecutableLookup.FindInRoots([_root], ["prime95"]));
    }

    [Fact]
    public void InvalidAndMissingRootsDoNotPreventTryingRemainingRoots()
    {
        var notDirectory = FakeFile("not-a-directory");
        var entry = FakeFile("valid", "prime95.exe");
        Assert.Equal(entry, CloudToolExecutableLookup.FindInRoots(
            [notDirectory, Path.Combine(_root, "missing"), Path.Combine(_root, "valid")], ["prime95.exe"]));
    }

    private string FakeFile(params string[] relative)
    {
        var path = Path.Combine(new[] { _root }.Concat(relative).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fake entry, never executed");
        return path;
    }

    public void Dispose()
    {
        // Resolve and verify this exact fixture target before deleting its fake tree.
        var fullRoot = Path.GetFullPath(_root);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(fullRoot) != _directoryName)
            throw new InvalidOperationException("Executable lookup fixture escaped its temporary root.");
        if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, recursive: true);
    }
}
