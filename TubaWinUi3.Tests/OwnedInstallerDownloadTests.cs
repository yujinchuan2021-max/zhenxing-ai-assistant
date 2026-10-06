using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

[Collection("GlobalConfigTests")]
public sealed class OwnedInstallerDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-owned-installer-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousRoot = DataRoots.TestRootOverrideForTest;
    private readonly StubTransport _transport = new();
    private readonly HttpClient _http;
    private readonly OwnedInstallerDownloadManager _manager;
    private OwnedWindowsPackage Package => new()
    {
        Architecture = "x64", ExecutableArchitecture = "x86", Version = "1.2.3", Type = "installer-exe",
        Url = "https://zhenxingai.com/downloads/installers/example/1.2.3/setup.exe",
        SizeBytes = _transport.Payload.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(_transport.Payload)).ToLowerInvariant()
    };

    public OwnedInstallerDownloadTests()
    {
        Directory.CreateDirectory(_root);
        DataRoots.TestRootOverrideForTest = _root;
        _http = new HttpClient(_transport);
        _manager = new(_root, _http, "x64");
        _transport.Payload = Pe("x86");
        _transport.Catalog = Catalog(Package);
    }

    private static string Catalog(OwnedWindowsPackage package, long revision = 1) => JsonSerializer.Serialize(new OwnedInstallerCatalog
    {
        SchemaVersion = 1, Revision = revision, PublishedAt = "2026-10-07T00:00:00Z",
        Tools = [new() { Id = "example", Name = "示例软件", Packages = [package] }]
    }, OwnedInstallerDownloadManager.JsonOptions);

    internal static byte[] Pe(string architecture = "x64")
    {
        var result = new byte[1024];
        result[0] = (byte)'M'; result[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(60), 128);
        "PE\0\0"u8.CopyTo(result.AsSpan(128));
        var machine = architecture switch { "x86" => 0x14C, "arm64" => 0xAA64, _ => 0x8664 };
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(132), (ushort)machine);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(134), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(148), 240);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(150), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(152), architecture == "x86" ? (ushort)0x10B : (ushort)0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(128 + 24 + 240 + 16), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(128 + 24 + 240 + 20), 512);
        return result;
    }

    [Fact]
    public async Task TargetX64MayUseActualX86BootstrapperWithoutChangingTarget()
    {
        var package = await _manager.ResolveAsync("example");
        Assert.Equal("x64", package!.Architecture);
        var path = await _manager.DownloadAsync("example", package);
        WindowsDownloadValidation.Validate(path, package.SizeBytes, "x86");
        Assert.Equal(_transport.Payload, File.ReadAllBytes(path));
        Assert.Equal(2, _transport.Requests.Count);
    }

    [Fact]
    public async Task HtmlResponseCannotReplaceAnExistingInstaller()
    {
        var package = await _manager.ResolveAsync("example");
        var target = await _manager.DownloadAsync("example", package!);
        var before = File.ReadAllBytes(target);
        _transport.Payload = "<!doctype html><title>not a download</title>"u8.ToArray();
        _transport.PackageType = "text/html";
        await Assert.ThrowsAsync<InvalidDataException>(() => _manager.DownloadAsync("example", package!));
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(target)!, ".zxai-download-*"));
    }

    [Fact]
    public async Task WrongHashWithValidPeAndCorrectSizeCannotReplacePrevious()
    {
        var package = await _manager.ResolveAsync("example");
        var path = await _manager.DownloadAsync("example", package!);
        var before = File.ReadAllBytes(path);
        _transport.Payload[^1] = 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => _manager.DownloadAsync("example", package!));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task WrongExecutableArchitectureDoesNotCommitFile()
    {
        _transport.Payload = Pe("arm64");
        _transport.Catalog = Catalog(Package); // Declares an x86 bootstrapper.
        var package = await _manager.ResolveAsync("example");
        await Assert.ThrowsAsync<InvalidDataException>(() => _manager.DownloadAsync("example", package!));
        Assert.False(File.Exists(Path.Combine(_manager.Destination("example", package!), "setup.exe")));
    }

    [Fact]
    public async Task SizeDeclarationIsNotReplacedByServerContentLength()
    {
        var package = await _manager.ResolveAsync("example");
        _transport.Payload = _transport.Payload[..900];
        await Assert.ThrowsAsync<InvalidDataException>(() => _manager.DownloadAsync("example", package!));
    }

    [Fact]
    public async Task MissingCatalogAlonePermitsOfficialFallback()
    {
        _transport.CatalogStatus = HttpStatusCode.NotFound;
        Assert.Null(await _manager.ResolveAsync("example"));
        _transport.CatalogStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<InvalidDataException>(() => _manager.ResolveAsync("example"));
    }

    [Fact]
    public async Task HtmlCatalogNeverFallsBackAsIfCatalogWereAbsent()
    {
        _transport.Catalog = "<!doctype html>";
        _transport.CatalogType = "text/html";
        await Assert.ThrowsAsync<InvalidDataException>(() => _manager.ResolveAsync("example"));
    }

    [Fact]
    public async Task MissingArm64PackageDoesNotSelectX64OrX86Package()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _manager.ResolveAsync("example", architecture: "arm64"));
    }

    [Fact]
    public async Task CachedCatalogCanBeReusedDuringNetworkFailureAfterRestart()
    {
        var package = await _manager.ResolveAsync("example");
        _transport.CatalogStatus = HttpStatusCode.ServiceUnavailable;
        var restarted = new OwnedInstallerDownloadManager(_root, _http, "x64");
        Assert.Equal(package, await restarted.ResolveAsync("example"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task CatalogRollbackOrSameRevisionMutationCannotReplaceLastGood(long revision, bool mutate)
    {
        await _manager.ResolveAsync("example");
        var original = File.ReadAllText(Path.Combine(_root, "downloads", "ManagedInstallers", "catalog.json"));
        _transport.Catalog = Catalog(mutate ? Package with { Version = "99.0" } : Package, revision);
        var restarted = new OwnedInstallerDownloadManager(_root, _http, "x64");
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.ResolveAsync("example"));
        Assert.Equal(original, File.ReadAllText(Path.Combine(_root, "downloads", "ManagedInstallers", "catalog.json")));
    }

    [Theory]
    [InlineData("https://github.com/example/setup.exe")]
    [InlineData("https://zhenxingai.com/downloads/installers/../setup.exe")]
    [InlineData("https://zhenxingai.com/downloads/installers/%2e%2e/setup.exe")]
    [InlineData("https://zhenxingai.com/downloads/installers/setup.exe?url=evil")]
    public void NonOwnedOrAmbiguousUrlsAreRejected(string url)
    {
        Assert.Throws<InvalidDataException>(() => OwnedInstallerDownloadManager.Parse(Catalog(Package with { Url = url })));
    }

    [Fact]
    public void DuplicateOrUnknownManifestFieldsAreRejected()
    {
        var json = Catalog(Package);
        Assert.Throws<InvalidDataException>(() => OwnedInstallerDownloadManager.Parse(json.Replace("\"revision\":1", "\"revision\":1,\"revision\":2")));
        Assert.Throws<JsonException>(() => OwnedInstallerDownloadManager.Parse(json.Replace("\"revision\":1", "\"revision\":1,\"command\":\"run\"")));
    }

    [Fact]
    public async Task RestoredProcessorKeepsOriginalShaSizeArchitectureAndType()
    {
        var package = await _manager.ResolveAsync("example");
        var path = await _manager.DownloadAsync("example", package!);
        var restored = Assert.IsType<OwnedDownloadPostProcessor>(PostProcessorRegistry.Find(
            PostProcessorRegistry.GetKey(new OwnedDownloadPostProcessor("example", package!))));
        Assert.Equal(package, restored.Package);
        var calls = 0;
        InstallerLaunchProcessor.LaunchOverrideForTests = _ => { calls++; return true; };
        await restored.ExecuteAsync(path, Path.GetDirectoryName(path)!, null, CancellationToken.None);
        Assert.Equal(1, calls);
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => restored.ExecuteAsync(path, Path.GetDirectoryName(path)!, null, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("owned-windows-v1:not-base64")]
    [InlineData("owned-windows-v1:eyJJZCI6IngifQ==")]
    [InlineData("owned-windows-v1:eyJpZCI6IngiLCJwYWNrYWdlIjpudWxsfQ==")]
    public void InvalidRestoredBindingCannotBecomeAnInstallerProcessor(string key) => Assert.Null(PostProcessorRegistry.Find(key));

    [Fact]
    public async Task CommonInstallerLauncherRejectsHtmlAndTruncatedMzBeforeAnyProcess()
    {
        var path = Path.Combine(_root, "invalid.exe");
        var calls = 0;
        InstallerLaunchProcessor.LaunchOverrideForTests = _ => { calls++; return true; };
        foreach (var bytes in new[] { "<!doctype html>"u8.ToArray(), "MZbroken"u8.ToArray(), Pe()[..600] })
        {
            File.WriteAllBytes(path, bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => new InstallerLaunchProcessor().ExecuteAsync(path, _root, null, CancellationToken.None));
        }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task InstallerLaunchFailureIsNotReportedAsSuccess()
    {
        var path = Path.Combine(_root, "setup.exe");
        File.WriteAllBytes(path, Pe());
        InstallerLaunchProcessor.LaunchOverrideForTests = _ => false;
        await Assert.ThrowsAsync<IOException>(() => new InstallerLaunchProcessor().ExecuteAsync(path, _root, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(".msix")]
    [InlineData(".appx")]
    [InlineData(".msixbundle")]
    [InlineData(".appxbundle")]
    public async Task WindowsPackageContainerWithManifestAndBlockMapRetainsInstallFlow(string extension)
    {
        var path = Path.Combine(_root, "example" + extension);
        WriteWindowsPackage(path, blockMap: true);
        var calls = 0;
        InstallerLaunchProcessor.LaunchOverrideForTests = _ => { calls++; return true; };
        await new InstallerLaunchProcessor().ExecuteAsync(path, _root, null, CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RenamedZipOrHtmlCannotMasqueradeAsMsix()
    {
        var path = Path.Combine(_root, "example.msix");
        File.WriteAllText(path, "<!doctype html><title>not an application package</title>");
        await Assert.ThrowsAsync<InvalidDataException>(() => WindowsDownloadValidation.ValidateAsync(path));
        WriteWindowsPackage(path, blockMap: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => WindowsDownloadValidation.ValidateAsync(path));
    }

    [Fact]
    public async Task WindowsPackageWithDamagedEntryCrcCannotStartInstaller()
    {
        var path = Path.Combine(_root, "example.msix");
        WriteWindowsPackage(path, blockMap: true);
        var bytes = File.ReadAllBytes(path);
        var central = -1;
        for (var i = 0; i < bytes.Length - 4; i++)
            if (bytes.AsSpan(i, 4).SequenceEqual(new byte[] { 0x50, 0x4B, 0x01, 0x02 })) { central = i; break; }
        Assert.True(central >= 0);
        bytes[central + 16] ^= 0xff;
        File.WriteAllBytes(path, bytes);
        var calls = 0;
        InstallerLaunchProcessor.LaunchOverrideForTests = _ => { calls++; return true; };
        await Assert.ThrowsAsync<InvalidDataException>(() => new InstallerLaunchProcessor().ExecuteAsync(path, _root, null, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    private static void WriteWindowsPackage(string path, bool blockMap)
    {
        var bundle = Path.GetExtension(path).EndsWith("bundle", StringComparison.Ordinal);
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry(bundle ? "AppxMetadata/AppxBundleManifest.xml" : "AppxManifest.xml").Open()))
            writer.Write(bundle ? "<Bundle xmlns=\"http://schemas.microsoft.com/appx/2013/bundle\" />"
                : "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" />");
        if (blockMap)
            using (var writer = new StreamWriter(archive.CreateEntry("AppxBlockMap.xml").Open()))
                writer.Write("<BlockMap xmlns=\"http://schemas.microsoft.com/appx/2010/blockmap\" />");
    }

    [Fact]
    public void AssetSelectionDoesNotSilentlyUseHistoricalX64ForArm64OrX86()
    {
        GitHubAssetInfo[] assets = [new("UniGetUI.Installer.exe", "https://example.com/installer.exe", 1)];
        Assert.Null(GitHubReleaseService.FindBestAsset(assets, "arm64", AssetMatchStrategy.UniGetUI));
        Assert.Null(GitHubReleaseService.FindBestAsset(assets, "x86", AssetMatchStrategy.UniGetUI));
        Assert.NotNull(GitHubReleaseService.FindBestAsset(assets, "x64", AssetMatchStrategy.UniGetUI));
        assets = [new("Optimizer-Windows-x64.exe", "https://example.com/optimizer.exe", 1)];
        Assert.Null(GitHubReleaseService.FindBestAsset(assets, "arm64", AssetMatchStrategy.OptimizerDuck));
    }

    public void Dispose()
    {
        InstallerLaunchProcessor.LaunchOverrideForTests = null;
        DataRoots.TestRootOverrideForTest = _previousRoot;
        _http.Dispose();
        Directory.Delete(_root, true);
    }

    private sealed class StubTransport : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        internal string Catalog { get; set; } = "";
        internal byte[] Payload { get; set; } = [];
        internal string CatalogType { get; set; } = "application/json";
        internal string PackageType { get; set; } = "application/octet-stream";
        internal HttpStatusCode CatalogStatus { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var isCatalog = request.RequestUri == OwnedInstallerDownloadManager.Endpoint;
            var response = new HttpResponseMessage(isCatalog ? CatalogStatus : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = isCatalog ? new StringContent(Catalog) : new ByteArrayContent(Payload)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(isCatalog ? CatalogType : PackageType);
            return Task.FromResult(response);
        }
    }
}
