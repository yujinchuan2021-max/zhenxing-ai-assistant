using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>Only GUID-owned fake files; no real tool directory is scanned or executed.</summary>
[Collection("GlobalConfigTests")]
public sealed class ToolLaunchTargetTests : IDisposable
{
    private readonly string _directoryName = "zxai-launch-target-" + Guid.NewGuid().ToString("N");
    private readonly string _root;
    private readonly string _tools;

    public ToolLaunchTargetTests()
    {
        _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), _directoryName));
        _tools = Path.Combine(_root, "Tools");
        var metadata = Path.Combine(_root, "Metadata");
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, "tools.json"), """
            {"tools":[
                {"match":"FurMark","launchTarget":"FurMark_GUI.exe"},
                {"match":"hwinfo","launchTarget":"HWiNFO64.exe"},
                {"match":"3DMark","launchTarget":"3DMark.exe"},
                {"match":"DDU","launchTarget":"Display Driver Uninstaller.exe"},
                {"match":"NestedPath","launchTarget":"bin/main.exe"},
                {"match":"Pattern","launchTarget":"main-*.exe"},
                {"match":"Legacy"}
            ]}
            """);
        ToolCatalog.SetToolsRootForBuild(_tools);
        ToolMetadataService.SetMetadataRootForTests(metadata);
        ToolCatalog.OnToolsChanged();
    }

    [Theory]
    [InlineData("FurMark")]
    [InlineData("hwinfo")]
    [InlineData("3DMark")]
    [InlineData("DDU")]
    public void MissingDeclaredMainWithOnlyOneHelperReturnsNull(string tool)
    {
        FakeFile(tool, "runtime/helper.exe");
        Assert.Null(ToolCatalog.FindPrimaryLaunchable(ToolDirectory(tool)));
    }

    [Theory]
    [InlineData("FurMark")]
    [InlineData("hwinfo")]
    [InlineData("3DMark")]
    [InlineData("DDU")]
    public void MissingDeclaredMainDoesNotSelectDirectOrNestedHelpers(string tool)
    {
        FakeFile(tool, "jabswitch.exe");
        FakeFile(tool, "license/slinfo.exe");
        Assert.Null(ToolCatalog.FindPrimaryLaunchable(ToolDirectory(tool)));
    }

    [Theory]
    [InlineData("FurMark", "FurMark_GUI.exe")]
    [InlineData("hwinfo", "HWiNFO64.exe")]
    [InlineData("3DMark", "3DMark.exe")]
    [InlineData("DDU", "Display Driver Uninstaller.exe")]
    public void DeclaredMainIsFoundInVendorSubfolder(string tool, string entry)
    {
        FakeFile(tool, "helper.exe");
        var main = FakeFile(tool, "vendor/bin/" + entry);
        Assert.Equal(main, ToolCatalog.FindPrimaryLaunchable(ToolDirectory(tool)));
    }

    [Fact]
    public void RelativeDeclaredPathUsesItsExactEntry()
    {
        FakeFile("NestedPath", "helper.exe");
        var main = FakeFile("NestedPath", "bin/main.exe");
        Assert.Equal(main, ToolCatalog.FindPrimaryLaunchable(ToolDirectory("NestedPath")));
    }

    [Fact]
    public void MissingRelativeDeclaredPathDoesNotSearchAnotherFolderOrSelectHelper()
    {
        FakeFile("NestedPath", "helper.exe");
        FakeFile("NestedPath", "another/main.exe");
        Assert.Null(ToolCatalog.FindPrimaryLaunchable(ToolDirectory("NestedPath")));
    }

    [Fact]
    public void ExistingFilenamePatternStillFindsItsDeclaredMain()
    {
        FakeFile("Pattern", "helper.exe");
        var main = FakeFile("Pattern", "nested/main-win.exe");
        Assert.Equal(main, ToolCatalog.FindPrimaryLaunchable(ToolDirectory("Pattern")));
    }

    [Fact]
    public void NoDeclaredMainKeepsLegacySingleExecutableDiscovery()
    {
        var entry = FakeFile("Legacy", "Legacy.exe");
        Assert.Equal(entry, ToolCatalog.FindPrimaryLaunchable(ToolDirectory("Legacy")));
    }

    private string ToolDirectory(string tool) => Path.Combine(_tools, "FakeCategory", tool);

    private string FakeFile(string tool, string relative)
    {
        var path = Path.Combine(ToolDirectory(tool), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fake entry, never executed");
        return path;
    }

    public void Dispose()
    {
        ToolMetadataService.SetMetadataRootForTests(null);
        ToolCatalog.SetToolsRootForBuild(null);
        ToolCatalog.OnToolsChanged();
        var fullRoot = Path.GetFullPath(_root);
        var tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(fullRoot) != _directoryName)
            throw new InvalidOperationException("Launch target fixture escaped its temporary root.");
        if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, recursive: true);
    }
}
