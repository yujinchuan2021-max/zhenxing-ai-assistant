using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

public sealed class ToolRootDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-tool-root-" + Guid.NewGuid().ToString("N"));

    public ToolRootDiscoveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MissingPackageToolsNeverAdoptsAnAncestorVendorInstallation()
    {
        var app = Path.Combine(_root, "client", "src");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(Path.Combine(_root, "Tools", "3DMark", "jre"));
        File.WriteAllText(Path.Combine(_root, "client", "枕星图吧AI助手.exe"), "launcher fixture");

        Assert.Equal(Path.Combine(app, "Tools"), ToolCatalog.ResolvePortableToolsRoot(app));
        Assert.True(Directory.Exists(Path.Combine(_root, "Tools", "3DMark", "jre")));
        Assert.False(Directory.Exists(Path.Combine(app, "Tools")));
    }

    [Fact]
    public void ExecutableDirectoryToolsTakesPrecedenceOverPackageAndAncestors()
    {
        var app = Path.Combine(_root, "client", "src");
        Directory.CreateDirectory(Path.Combine(app, "Tools"));
        Directory.CreateDirectory(Path.Combine(_root, "client", "Tools"));
        Directory.CreateDirectory(Path.Combine(_root, "Tools"));
        File.WriteAllText(Path.Combine(_root, "client", "枕星图吧AI助手.exe"), "launcher fixture");

        Assert.Equal(Path.Combine(app, "Tools"), ToolCatalog.ResolvePortableToolsRoot(app));
    }

    [Theory]
    [InlineData("src", "枕星图吧AI助手.exe")]
    [InlineData("SRC", "图吧工具箱WinUI3.exe")]
    public void RootToolsIsAcceptedOnlyForRecognizedPortablePackage(string executableDirectory, string launcher)
    {
        var app = Path.Combine(_root, "client", executableDirectory);
        var tools = Path.Combine(_root, "client", "Tools");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(_root, "client", launcher), "launcher fixture");

        Assert.Equal(tools, ToolCatalog.ResolvePortableToolsRoot(app));
    }

    [Theory]
    [InlineData("src", false)]
    [InlineData("bin", true)]
    public void NearbyToolsWithoutMatchingPackageLayoutIsIgnored(string executableDirectory, bool hasLauncher)
    {
        var app = Path.Combine(_root, "client", executableDirectory);
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(Path.Combine(_root, "client", "Tools"));
        if (hasLauncher) File.WriteAllText(Path.Combine(_root, "client", "枕星图吧AI助手.exe"), "launcher fixture");

        Assert.Equal(Path.Combine(app, "Tools"), ToolCatalog.ResolvePortableToolsRoot(app));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
