using System.Text.Json;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Tests;

public sealed class CloudToolValidationTests
{
    [Theory]
    [InlineData("https://evil.example/downloads/tools/a.zip")]
    [InlineData("http://zhenxingai.com/downloads/tools/a.zip")]
    [InlineData("https://zhenxingai.com/downloads/tools/../a.zip")]
    [InlineData("https://zhenxingai.com/downloads/tools/%2e%2e/a.zip")]
    [InlineData("https://zhenxingai.com/downloads/tools/a.zip?token=x")]
    [InlineData("https://zhenxingai.com/downloads/tools/a.zip#x")]
    [InlineData("https://zhenxingai.com:444/downloads/tools/a.zip")]
    [InlineData("https://other@zhenxingai.com/downloads/tools/a.zip")]
    public void PackageUrl_RejectsOtherSourcesAndPathTricks(string url) =>
        Assert.False(CloudToolValidation.IsPackageUrl(url));

    [Theory]
    [InlineData("../manual")]
    [InlineData("tool/../../manual")]
    [InlineData("CON")]
    [InlineData("sample_tool")]
    [InlineData("Sample-Tool")]
    [InlineData("tool:alternate")]
    public void ToolId_RejectsFilesystemOrUnstableIdentifiers(string id) =>
        Assert.False(CloudToolValidation.IsId(id));

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("bin/ToolInstaller.exe")]
    [InlineData("uninstall.exe")]
    [InlineData("vcredist.exe")]
    [InlineData("tool.cmd")]
    public void ExecutableEntry_DoesNotTreatInstallersOrScriptsAsPortableTools(string entry) =>
        Assert.False(CloudToolValidation.IsToolEntryPoint(entry));

    [Fact]
    public void DuplicateToolIds_RejectWholeManifest()
    {
        var tool = new CloudToolDefinition { Id = "sample-tool", Name = "工具", Category = "系统工具", Version = "1" };
        var catalog = new CloudToolCatalog { PublishedAt = "2026-10-05T01:00:00Z", Tools = [tool, tool] };
        Assert.Throws<InvalidDataException>(() => CloudToolValidation.ParseCatalog(
            JsonSerializer.Serialize(catalog, CloudToolValidation.JsonOptions), new Version(0, 1, 0)));
    }

    [Fact]
    public void UnsupportedClientVersion_RejectsManifestInsteadOfInventingAvailability()
    {
        var catalog = new CloudToolCatalog { PublishedAt = "2026-10-05T01:00:00Z", MinClientVersion = "99.0.0" };
        Assert.Throws<InvalidDataException>(() => CloudToolValidation.ParseCatalog(
            JsonSerializer.Serialize(catalog, CloudToolValidation.JsonOptions), new Version(0, 1, 0)));
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0.0")]
    [InlineData("0.1.0.0", "0.1.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("1.2.0.0", "1.2")]
    public void EquivalentMinimumVersions_AcceptMissingZeroComponents(string minimum, string client)
    {
        var catalog = new CloudToolCatalog { PublishedAt = "2026-10-05T01:00:00Z", MinClientVersion = minimum };
        var parsed = CloudToolValidation.ParseCatalog(
            JsonSerializer.Serialize(catalog, CloudToolValidation.JsonOptions), Version.Parse(client));
        Assert.Equal(minimum, parsed.MinClientVersion);
    }

    [Theory]
    [InlineData("0.1.0.1", "0.1.0")]
    [InlineData("0.1.1", "0.1.0.0")]
    [InlineData("0.2", "0.1.99.99")]
    public void HigherMinimumVersion_StillRequiresAClientUpgrade(string minimum, string client)
    {
        var catalog = new CloudToolCatalog { PublishedAt = "2026-10-05T01:00:00Z", MinClientVersion = minimum };
        Assert.Throws<InvalidDataException>(() => CloudToolValidation.ParseCatalog(
            JsonSerializer.Serialize(catalog, CloudToolValidation.JsonOptions), Version.Parse(client)));
    }

    [Theory]
    [InlineData("x64", "x86,any,x64", "x64")]
    [InlineData("x64", "x86,any", "any")]
    [InlineData("x64", "x86", "x86")]
    [InlineData("arm64", "x86,x64,any,arm64", "arm64")]
    [InlineData("arm64", "x86,x64,any", "any")]
    [InlineData("arm64", "x64,x86", "x86")]
    [InlineData("arm64", "x64", null)]
    [InlineData("x86", "x64", null)]
    [InlineData("x86", "any,x64,x86", "x86")]
    [InlineData("x86", "x64,any", "any")]
    public void PackageSelection_PrefersNativeThenAnyThenSupportedX86(string architecture,
        string packageArchitectures, string? expected)
    {
        var tool = new CloudToolDefinition { Packages = packageArchitectures.Split(',')
            .Select(a => new CloudToolPackage { Architecture = a }).ToArray() };
        Assert.Equal(expected, CloudToolValidation.SelectPackage(tool, architecture)?.Architecture);
    }
}
