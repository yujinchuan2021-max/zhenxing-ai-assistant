using System.Text.Json;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Tests;

[Collection("GlobalConfigTests")]
public sealed class CloudToolCatalogIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldDataRoot = DataRoots.TestRootOverrideForTest;
    private readonly HttpClient _http = new(new RejectNetwork());
    private string Tools => Path.Combine(_root, "Tools");
    private string Metadata => Path.Combine(_root, "Metadata");

    public CloudToolCatalogIntegrationTests()
    {
        Directory.CreateDirectory(Metadata);
        File.WriteAllText(Path.Combine(Metadata, "tools.json"), """
            {"tools":[{"match":"Sample","category":"云工具","categories":["跨分类"],"downloadUrl":"gc:Tools/云工具/Sample"},
            {"match":"内置检测","builtin":"stress-test","categories":["系统工具"]}]}
            """);
        DataRoots.TestRootOverrideForTest = Path.Combine(_root, "data");
        ToolCatalog.SetToolsRootForBuild(Tools);
        ToolMetadataService.SetMetadataRootForTests(Metadata);
        ToolCatalog.OnToolsChanged();
        if (BuiltinToolRegistry.GetById("stress-test") is null) BuiltinToolRegistry.RegisterDefaults();
    }

    private void Seed(bool downloadable = true, string homepage = "https://example.com/",
        CloudToolPackage[]? packages = null, bool cached = false)
    {
        var catalog = new CloudToolCatalog
        {
            Revision = 1, PublishedAt = "2026-10-05T01:00:00Z", Tools = [new()
            {
                Id = "sample-tool", Name = "示例工具", Category = "云工具", Categories = ["跨分类"],
                Version = "1", LegacyPath = "云工具/Sample", Homepage = homepage, Tags = ["检测"],
                Packages = packages ?? (downloadable ? [new() { Architecture = "x64", Url = "https://zhenxingai.com/downloads/tools/sample.zip",
                    SizeBytes = 8, Sha256 = new string('a', 64), EntryPoint = "app.exe" }] : []),
            }],
        };
        var seed = Path.Combine(Metadata, "cloud-tools.json");
        if (cached)
        {
            File.WriteAllText(seed, JsonSerializer.Serialize(catalog with { Tools = [] }, CloudToolValidation.JsonOptions));
            var cache = Path.Combine(_root, "CloudTools", "catalog.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            File.WriteAllText(cache, JsonSerializer.Serialize(catalog with { Revision = 2 }, CloudToolValidation.JsonOptions));
        }
        else File.WriteAllText(seed, JsonSerializer.Serialize(catalog, CloudToolValidation.JsonOptions));
        CloudToolService.OverrideForTests = new CloudToolManager(_root, _http, CloudToolService.OwnEndpoint,
            "x64", new Version(0, 1, 0), seed, Tools, allowNetwork: false);
        ToolCatalog.OnToolsChanged();
    }

    [Fact]
    public void EmptyToolFoldersStillShowCloudCategoriesCardsAndNativePlacement()
    {
        Seed();
        Assert.False(Directory.Exists(Tools));
        Assert.Contains("云工具", ToolCatalog.GetCategories());
        Assert.Contains("跨分类", ToolCatalog.GetCategories());
        var tool = Assert.Single(ToolCatalog.GetTools("云工具"));
        Assert.Equal("sample-tool", tool.CloudToolId);
        Assert.Equal("下载", tool.LaunchButtonText);
        Assert.False(tool.CanSendToDesktop);
        Assert.Single(ToolCatalog.GetTools("跨分类"));
        Assert.Contains(ToolCatalog.GetTools("系统工具"), t => t.IsBuiltinLink);
        Assert.Contains("检测", ToolCatalog.GetAllTags());
        Assert.Contains(ToolCatalog.Search("示例"), t => t.CloudToolId == "sample-tool");
    }

    [Fact]
    public void LegacyExecutableIsReusedWithoutDuplicateOrOwnedRemoval()
    {
        var exe = Path.Combine(Tools, "云工具", "Sample", "app.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, "MZsample"u8.ToArray());
        Seed();
        var tool = Assert.Single(ToolCatalog.GetTools("云工具"));
        Assert.Equal("sample-tool", tool.CloudToolId);
        Assert.Equal(exe, tool.EffectivePath);
        Assert.Equal("打开", tool.LaunchButtonText);
        Assert.False(CloudToolService.IsManaged("sample-tool"));
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void ManualCloudEntryDoesNotHideAnExistingLegacyLaunchCard()
    {
        var exe = Path.Combine(Tools, "云工具", "Sample", "app.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, "MZsample"u8.ToArray());
        Seed(downloadable: false);
        var tool = Assert.Single(ToolCatalog.GetTools("云工具"));
        Assert.Null(tool.CloudToolId);
        Assert.Equal(exe, tool.EffectivePath);
    }

    [Fact]
    public void ImageOnlyLegacyDirectoryUsesOneCloudDownloadCard()
    {
        var directory = Path.Combine(Tools, "云工具", "Sample");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "thumbnail.png"), [0]);
        Seed();
        var tool = Assert.Single(ToolCatalog.GetTools("云工具"));
        Assert.Equal("sample-tool", tool.CloudToolId);
        Assert.Equal("下载", tool.LaunchButtonText);
    }

    [Fact]
    public void ManualEntryWithoutLocalFilesRemainsVisibleWithSourceAction()
    {
        Seed(downloadable: false);
        var tool = Assert.Single(ToolCatalog.GetTools("云工具"));
        Assert.Equal("sample-tool", tool.CloudToolId);
        Assert.Equal("官网获取", tool.LaunchButtonText);
        Assert.Equal("网站", tool.Extension);
        Assert.Equal("网站", tool.ExtensionDisplay);
        Assert.Equal("https://example.com/", tool.RemoteUrl);
        Assert.False(tool.CloudHasPackage);
        Assert.False(tool.NeedsDownload);
        Assert.Null(tool.DownloadUrl);
    }

    [Fact]
    public void EmptyCloudPlaceholderIsHiddenAcrossCategorySearchAndTags()
    {
        var directory = Path.Combine(Tools, "云工具", "Sample");
        Directory.CreateDirectory(directory);
        var image = Path.Combine(directory, "thumbnail.png");
        File.WriteAllBytes(image, [0]);
        Seed(downloadable: false, homepage: "");
        Assert.Empty(ToolCatalog.GetTools("云工具"));
        Assert.Empty(ToolCatalog.GetTools("跨分类"));
        Assert.DoesNotContain(ToolCatalog.Search("示例"), tool => tool.CloudToolId == "sample-tool");
        Assert.DoesNotContain("检测", ToolCatalog.GetAllTags());
        Assert.True(File.Exists(image));
    }

    [Fact]
    public void CachedTagsFollowTheCurrentVisibleCatalogueAfterAnAvailabilityChange()
    {
        Seed();
        Assert.Contains("检测", ToolCatalog.GetAllTags());
        Seed(downloadable: false, homepage: "");
        Assert.Empty(ToolCatalog.GetTools("云工具"));
        Assert.DoesNotContain("检测", ToolCatalog.GetAllTags());
    }

    [Fact]
    public void EmptyCachedCloudPlaceholderIsFilteredWithoutRewritingItsCache()
    {
        Seed(downloadable: false, homepage: "", cached: true);
        var cache = Path.Combine(_root, "CloudTools", "catalog.json");
        var contents = File.ReadAllBytes(cache);
        var modified = File.GetLastWriteTimeUtc(cache);
        Assert.Equal(2, CloudToolService.OverrideForTests!.Revision);
        Assert.Empty(ToolCatalog.GetTools("云工具"));
        Assert.DoesNotContain(ToolCatalog.Search("示例"), tool => tool.CloudToolId == "sample-tool");
        Assert.Equal(contents, File.ReadAllBytes(cache));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(cache));
    }

    [Fact]
    public void EmptyCloudPlaceholderRetainsAnActualLocalLaunchCard()
    {
        var exe = Path.Combine(Tools, "云工具", "Sample", "app.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, "MZsample"u8.ToArray());
        Seed(downloadable: false, homepage: "");
        var tool = Assert.Single(ToolCatalog.GetTools("云工具"));
        Assert.Null(tool.CloudToolId);
        Assert.Equal(exe, tool.EffectivePath);
        Assert.Equal("打开", tool.LaunchButtonText);
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void ManagedEntryStaysUsableAfterItsCatalogueDownloadSourceIsRemoved()
    {
        var installed = SeedManaged(("sample-tool", "云工具/Sample", "app.exe", []));
        Seed(downloadable: false, homepage: "");
        var tool = Assert.Single(ToolCatalog.GetTools("云工具"));
        Assert.Equal("sample-tool", tool.CloudToolId);
        Assert.Equal(Path.Combine(installed["sample-tool"], "app.exe"), tool.EffectivePath);
        Assert.Equal("打开", tool.LaunchButtonText);
        Assert.Equal("EXE", tool.Extension);
        Assert.True(tool.CanSendToDesktop);
    }

    [Theory]
    [InlineData("incompatible", false)]
    [InlineData("installer", false)]
    [InlineData("incompatible", true)]
    [InlineData("installer", true)]
    public void UnsupportedPackagesRequireAValidManualHomepage(string kind, bool hasHomepage)
    {
        var architecture = kind == "incompatible"
            ? UpdateService.CurrentArchitecture == "arm64" ? "x64" : "arm64"
            : UpdateService.CurrentArchitecture;
        var package = new CloudToolPackage
        {
            Architecture = architecture, Url = "https://zhenxingai.com/downloads/tools/sample.zip",
            SizeBytes = 8, Sha256 = new string('a', 64), EntryPoint = "app.exe",
            Kind = kind == "installer" ? "installer" : "portable-zip",
        };
        Seed(homepage: hasHomepage ? "https://example.com/" : "", packages: [package]);
        var tools = ToolCatalog.GetTools("云工具");
        if (!hasHomepage) Assert.Empty(tools);
        else
        {
            var tool = Assert.Single(tools);
            Assert.False(tool.CloudHasPackage);
            Assert.Null(tool.DownloadUrl);
            Assert.Equal("官网获取", tool.LaunchButtonText);
            Assert.Equal("网站", tool.Extension);
        }
    }

    [Fact]
    public void DownloadCardUsesTheCompatiblePackageRatherThanTheFirstPackage()
    {
        var otherArchitecture = UpdateService.CurrentArchitecture == "arm64" ? "x64" : "arm64";
        var packages = new[]
        {
            new CloudToolPackage { Architecture = otherArchitecture, Url = "https://zhenxingai.com/downloads/tools/other.zip",
                SizeBytes = 8, Sha256 = new string('a', 64), EntryPoint = "other.exe" },
            new CloudToolPackage { Architecture = UpdateService.CurrentArchitecture, Url = "https://zhenxingai.com/downloads/tools/current.zip",
                SizeBytes = 8, Sha256 = new string('b', 64), EntryPoint = "current.exe" },
        };
        Seed(packages: packages);
        var tool = Assert.Single(ToolCatalog.GetTools("云工具"));
        Assert.True(tool.CloudHasPackage);
        Assert.Equal(packages[1].Url, tool.DownloadUrl);
        Assert.EndsWith("current.exe", tool.Path);
        Assert.Equal("下载", tool.LaunchButtonText);
        Assert.Equal("待下载", tool.Extension);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("hash")]
    [InlineData("entry")]
    [InlineData("size")]
    [InlineData("kind")]
    public void DownloadAvailabilityRejectsInvalidPackageMetadata(string field)
    {
        var package = new CloudToolPackage
        {
            Architecture = "x64", Url = "https://zhenxingai.com/downloads/tools/sample.zip",
            SizeBytes = 8, Sha256 = new string('a', 64), EntryPoint = "app.exe",
        };
        package = field switch
        {
            "url" => package with { Url = "https://example.com/sample.zip" },
            "hash" => package with { Sha256 = "" },
            "entry" => package with { EntryPoint = "setup.exe" },
            "size" => package with { SizeBytes = 0 },
            "kind" => package with { Kind = "installer" },
            _ => package,
        };
        Assert.Null(ToolCatalog.GetDownloadableCloudPackage(new CloudToolDefinition { Packages = [package] }, "x64"));
    }

    [Fact]
    public void LegacyPathResolverKeepsTheOldPathUntilAValidManagedCopyExists()
    {
        Seed();
        var relative = "云工具/Sample/app.exe";
        var legacy = Path.Combine(Tools, "云工具", "Sample", "app.exe");
        Assert.Equal(legacy, ToolCatalog.ResolveToolPath(relative));
        Assert.Throws<ArgumentException>(() => ToolCatalog.ResolveToolPath("../app.exe"));
        var installed = SeedManaged(("sample-tool", "云工具/Sample", "app.exe", ["cli.exe"]));
        Assert.Equal(installed["sample-tool"], ToolCatalog.ResolveToolPath("云工具/Sample"));
        Assert.Equal(Path.Combine(installed["sample-tool"], "cli.exe"), ToolCatalog.ResolveToolPath("云工具/Sample/cli.exe"));
        Assert.Equal(Path.Combine(Tools, "云工具", "Sample", "missing.exe"), ToolCatalog.ResolveToolPath("云工具/Sample/missing.exe"));
    }

    [Fact]
    public void NativeHardwareAndBenchmarkLookupsUseDownloadedToolsAndTheirHelpers()
    {
        var installed = SeedManaged(
            ("cpu-z", "处理器工具/CPUZ", "cpuz_x64.exe", []),
            ("furmark", "烤鸡工具/FurMark_win64", "furmark.exe", []),
            ("diskmark", "硬盘工具/CrystalDiskMark", "DiskMark64.exe", ["CdmResource/DiskSpd/DiskSpd64.exe"]),
            ("latency", "处理器工具/C2CLatency", "C2CLatency.exe", []));
        Assert.False(Directory.Exists(Tools));
        Assert.Equal(Path.Combine(installed["cpu-z"], "cpuz_x64.exe"), CpuzInfoService.FindCpuzExe());
        Assert.Equal(Path.Combine(installed["furmark"], "furmark.exe"), PerformanceBenchmarkService.FindFurMarkExe());
        Assert.Equal(Path.Combine(installed["diskmark"], "CdmResource", "DiskSpd", "DiskSpd64.exe"), PerformanceBenchmarkService.FindDiskSpdExe());
        Assert.Equal(Path.Combine(installed["latency"], "C2CLatency.exe"), PerformanceBenchmarkService.FindCoreToCoreLatencyExe());
    }

    [Fact]
    public void AutorunsLookupKeepsCliAndGuiAsSeparateFilesInTheManagedPackage()
    {
        var installed = SeedManaged(("autoruns", "其他工具/Autoruns", "Autoruns64.exe", ["autorunsc64.exe"]));
        Assert.Equal(Path.Combine(installed["autoruns"], "autorunsc64.exe"), StartupManagerTool.FindAutorunsc());
        Assert.Equal(Path.Combine(installed["autoruns"], "Autoruns64.exe"), StartupManagerTool.FindAutorunsGui());
    }

    [Fact]
    public void AgentCliContextUsesTheManagedFileAndDoesNotClaimMissingToolsAreReady()
    {
        var doc = Path.Combine(_root, "cli.md");
        File.WriteAllText(doc, """
            ## 处理器工具
            ### CPU-Z —— 硬件检测
            **路径**：`处理器工具\CPUZ\cpuz_x64.exe`
            """);
        Seed();
        var catalog = new CliToolboxCatalog(doc);
        Assert.Contains("未准备，先在应用中心查看获取方式", catalog.BuildIndexContext());
        var installed = SeedManaged(("cpu-z", "处理器工具/CPUZ", "cpuz_x64.exe", []));
        var exe = Path.Combine(installed["cpu-z"], "cpuz_x64.exe");
        Assert.Equal(exe, catalog.ResolveExePath(catalog.Find("CPU-Z")!.ExecutablePath!));
        Assert.Contains("已准备；实际路径：" + exe, catalog.BuildIndexContext());
    }

    private Dictionary<string, string> SeedManaged(params (string Id, string Legacy, string Entry, string[] Helpers)[] specs)
    {
        var definitions = specs.Select(spec => new CloudToolDefinition
        {
            Id = spec.Id, Name = spec.Id, Category = "云工具", Version = "1", LegacyPath = spec.Legacy,
            Packages = [new() { Architecture = "x64", Url = $"https://zhenxingai.com/downloads/tools/{spec.Id}/portable.zip",
                SizeBytes = 8, Sha256 = new string('a', 64), EntryPoint = spec.Entry }],
        }).ToArray();
        var seed = Path.Combine(Metadata, "cloud-tools.json");
        File.WriteAllText(seed, JsonSerializer.Serialize(new CloudToolCatalog
        {
            Revision = 1, PublishedAt = "2026-10-05T01:00:00Z", Tools = definitions,
        }, CloudToolValidation.JsonOptions));
        var installed = new Dictionary<string, string>();
        foreach (var (spec, tool) in specs.Zip(definitions))
        {
            var directory = Path.Combine(_root, "CloudTools", "Installed", spec.Id);
            installed.Add(spec.Id, directory);
            var files = new Dictionary<string, string>();
            foreach (var relative in spec.Helpers.Prepend(spec.Entry))
            {
                var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bytes = "MZsample"u8.ToArray();
                File.WriteAllBytes(path, bytes);
                files.Add(relative, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
            }
            var package = tool.Packages[0];
            File.WriteAllText(Path.Combine(directory, CloudToolValidation.ReceiptFile), JsonSerializer.Serialize(new CloudToolReceipt
            {
                Id = spec.Id, Name = tool.Name, Version = tool.Version, Architecture = "x64", Sha256 = package.Sha256,
                PackageUrl = package.Url, PackageSize = package.SizeBytes, EntryPoint = spec.Entry, Files = files,
            }, CloudToolValidation.JsonOptions));
        }
        CloudToolService.OverrideForTests = new CloudToolManager(_root, _http, CloudToolService.OwnEndpoint,
            "x64", new Version(0, 1, 0), seed, Tools, allowNetwork: false);
        ToolCatalog.OnToolsChanged();
        return installed;
    }

    public void Dispose()
    {
        CloudToolService.OverrideForTests = null;
        ToolMetadataService.SetMetadataRootForTests(null);
        ToolCatalog.SetToolsRootForBuild(null);
        ToolCatalog.OnToolsChanged();
        DataRoots.TestRootOverrideForTest = _oldDataRoot;
        _http.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Integration test attempted networking.");
    }
}
