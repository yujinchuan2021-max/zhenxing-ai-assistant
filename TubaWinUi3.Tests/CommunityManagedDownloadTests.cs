using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TubaWinUi3.Models;
using TubaWinUi3.Services;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Tests;

[Collection("GlobalConfigTests")]
public sealed class CommunityManagedDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-community-flow-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldRoot = DataRoots.TestRootOverrideForTest;
    private readonly CommunityDataSource _oldSource = CommunityToolService.CurrentSource;
    private readonly PackageTransport _transport = new();
    private readonly HttpClient _http;
    private string Seed => Path.Combine(_root, "seed.json");
    private string Tools => Path.Combine(_root, "Tools");

    public CommunityManagedDownloadTests()
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(_transport);
        DataRoots.TestRootOverrideForTest = _root;
        ToolCatalog.SetToolsRootForBuild(Tools);
        CommunityToolService.CurrentSource = CommunityDataSource.Zhenxing;
    }

    private void SeedManaged()
    {
        _transport.Package = Zip(("app.exe", "MZsynthetic"u8.ToArray()));
        var catalog = new CloudToolCatalog
        {
            Revision = 1, PublishedAt = "2026-10-07T00:00:00Z", Tools =
            [new() { Id = "example-tool", Name = "受管工具", Category = "检测工具", Version = "1",
                Packages = [new() { Architecture = UpdateService.CurrentArchitecture,
                    Url = PackageTransport.Url, SizeBytes = _transport.Package.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(_transport.Package)), EntryPoint = "app.exe" }] },
             new() { Id = "manual-tool", Name = "需从官网获取", Category = "检测工具", Version = "1", Homepage = "https://example.com/" },
             new() { Id = "other-platform", Name = "其他架构", Category = "检测工具", Version = "1",
                Packages = [new() { Architecture = UpdateService.CurrentArchitecture == "x64" ? "arm64" : "x64",
                    Url = PackageTransport.Url, SizeBytes = _transport.Package.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(_transport.Package)), EntryPoint = "app.exe" }] }]
        };
        File.WriteAllText(Seed, JsonSerializer.Serialize(catalog, CloudToolValidation.JsonOptions));
        CloudToolService.OverrideForTests = new CloudToolManager(_root, _http, CloudToolService.OwnEndpoint,
            UpdateService.CurrentArchitecture, new Version(0, 1, 1), Seed, Tools, allowNetwork: true);
    }

    [Fact]
    public async Task DefaultOwnedListUsesCacheAndOnlyAdvertisesInstallablePackages()
    {
        SeedManaged();
        var tool = Assert.Single(await CommunityToolService.GetPluginsAsync());
        Assert.Equal("example-tool", tool.CloudToolId);
        Assert.True(tool.UsesManagedDownload);
        Assert.Same(tool, await CommunityToolService.LoadToolDetailAsync(tool));
        Assert.Empty(_transport.Requests);
    }

    [Fact]
    public async Task CommunityDownloadUsesCatalogAndReceiptRegardlessOfWindowSelectedUrl()
    {
        SeedManaged();
        var tool = Assert.Single(await CommunityToolService.GetPluginsAsync());
        await CommunityToolService.InstallPluginAsync(tool, "https://github.com/wrong/file.zip", null);
        Assert.Equal([PackageTransport.Url], _transport.Requests);
        Assert.True(CloudToolService.IsManaged(tool.CloudToolId!));
        Assert.Equal(CommunityToolInstallStatus.Installed, CommunityToolService.CheckInstallStatus(tool));
        Assert.EndsWith("app.exe", CommunityToolService.GetLocalPath(tool)!);
        Assert.True(File.Exists(Path.Combine(_root, "CloudTools", "Installed", tool.CloudToolId!, CloudToolValidation.ReceiptFile)));
    }

    [Fact]
    public async Task HtmlFromOwnedOriginFailsWithoutAdvertisingInstallation()
    {
        SeedManaged();
        var tool = Assert.Single(await CommunityToolService.GetPluginsAsync());
        _transport.Package = "<!DOCTYPE html><title>AtomGit</title>"u8.ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => CommunityToolService.InstallPluginAsync(tool, null));
        Assert.Null(CommunityToolService.GetLocalPath(tool));
        Assert.Equal(CommunityToolInstallStatus.NotInstalled, CommunityToolService.CheckInstallStatus(tool));
    }

    private CommunityTool Legacy() => new()
    { Id = "legacy-sample", Name = "兼容工具", Category = "社区工具", File = "tool.zip", LaunchTarget = "app.exe" };

    private string Existing(CommunityTool tool)
    {
        var path = Path.Combine(Tools, tool.Category, tool.Id, "app.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, "MZprevious"u8.ToArray());
        return path;
    }

    private void FakeLegacyDownload(byte[] content)
    {
        CommunityPackageInstaller.DownloadOverrideForTests = async (_, directory, file, _, ct) =>
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, file);
            await File.WriteAllBytesAsync(path, content, ct);
            return path;
        };
    }

    [Fact]
    public async Task UpstreamHtmlPreservesPreviousVersionAndReportsUsefulReason()
    {
        var tool = Legacy();
        var previous = Existing(tool);
        FakeLegacyDownload("<!DOCTYPE html><title>AtomGit</title>"u8.ToArray());
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CommunityToolService.InstallPluginAsync(tool, "https://example.com/tool.zip", null));
        Assert.Contains("网页", error.Message);
        Assert.Equal("MZprevious"u8.ToArray(), File.ReadAllBytes(previous));
    }

    [Fact]
    public async Task MissingDeclaredEntranceDoesNotPromoteHelperExeOrDeletePrevious()
    {
        var tool = Legacy();
        var previous = Existing(tool);
        FakeLegacyDownload(Zip(("jabswitch.exe", "MZhelper"u8.ToArray())));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CommunityToolService.InstallPluginAsync(tool, "https://example.com/tool.zip", null));
        Assert.Equal("MZprevious"u8.ToArray(), File.ReadAllBytes(previous));
    }

    [Fact]
    public async Task BadZipCrcWithCompleteDownloadPreservesPrevious()
    {
        var tool = Legacy();
        var previous = Existing(tool);
        var zip = Zip(("app.exe", "MZreplacement"u8.ToArray()));
        var central = Find(zip, [0x50, 0x4b, 0x01, 0x02]);
        zip[central + 16] ^= 0xff;
        FakeLegacyDownload(zip);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CommunityToolService.InstallPluginAsync(tool, "https://example.com/tool.zip", null));
        Assert.Equal("MZprevious"u8.ToArray(), File.ReadAllBytes(previous));
    }

    [Fact]
    public async Task ValidUpstreamPackageReplacesPreviousOnlyAfterValidation()
    {
        var tool = Legacy();
        var previous = Existing(tool);
        FakeLegacyDownload(Zip(("app.exe", "MZreplacement"u8.ToArray())));
        var result = await CommunityToolService.InstallPluginAsync(tool, "https://example.com/tool.zip", null);
        Assert.Equal(Path.GetDirectoryName(previous), result);
        Assert.Equal("MZreplacement"u8.ToArray(), File.ReadAllBytes(previous));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(result)!, ".zxai-community-*"));
    }

    [Fact]
    public void DownloadResidueDoesNotCountAsInstalled()
    {
        var tool = Legacy();
        var directory = Path.Combine(Tools, tool.Category, tool.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "failed.zip"), "html");
        File.WriteAllText(Path.Combine(directory, "helper.exe"), "MZhelper");
        Assert.Equal(CommunityToolInstallStatus.NotInstalled, CommunityToolService.CheckInstallStatus(tool));
        Assert.Null(CommunityToolService.GetLocalPath(tool));
    }

    [Fact]
    public async Task ZipTraversalNeverWritesOutsideStageOrDeletesPrevious()
    {
        var tool = Legacy();
        var previous = Existing(tool);
        FakeLegacyDownload(Zip(("../escape.exe", "MZescaped"u8.ToArray())));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CommunityToolService.InstallPluginAsync(tool, "https://example.com/tool.zip", null));
        Assert.Equal("MZprevious"u8.ToArray(), File.ReadAllBytes(previous));
        Assert.Empty(Directory.GetFiles(_root, "escape.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UpstreamHtmlFallsBackWithFreshStageAndPreservesPreviousUntilValid()
    {
        var tool = new CommunityTool { Id = "legacy-sample", Name = "兼容工具", Category = "社区工具",
            RepoPath = "plugins/社区工具/legacy-sample", File = "tool.zip", LaunchTarget = "app.exe" };
        var previous = Existing(tool);
        var urls = new List<string>();
        CommunityPackageInstaller.DownloadOverrideForTests = async (url, directory, file, _, ct) =>
        {
            Assert.Equal("MZprevious"u8.ToArray(), File.ReadAllBytes(previous));
            urls.Add(url);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, file);
            await File.WriteAllBytesAsync(path, urls.Count == 1 ? "<!doctype html>"u8.ToArray() :
                Zip(("app.exe", "MZreplacement"u8.ToArray())), ct);
            return path;
        };
        await CommunityToolService.InstallPluginAsync(tool, null);
        Assert.Equal(2, urls.Count);
        Assert.Contains("gitcode.com", urls[0]);
        Assert.Contains("githubusercontent.com", urls[1]);
        Assert.Equal("MZreplacement"u8.ToArray(), File.ReadAllBytes(previous));
    }

    [Fact]
    public async Task SingleExeUsesBlobHashAndIsNotTreatedAsZip()
    {
        var tool = new CommunityTool { Id = "legacy-sample", Name = "兼容工具", Category = "社区工具",
            RepoPath = "plugins/社区工具/legacy-sample", File = "app.exe", LaunchTarget = "app.exe" };
        var payload = "MZreplacement"u8.ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"blob {payload.Length}\0"));
        hash.AppendData(payload);
        tool.FileSha = Convert.ToHexString(hash.GetHashAndReset());
        FakeLegacyDownload(payload);
        await CommunityToolService.InstallPluginAsync(tool, null);
        Assert.Equal(payload, File.ReadAllBytes(CommunityToolService.GetLocalPath(tool)!));
        tool.FileSha = new string('a', 40);
        await Assert.ThrowsAsync<InvalidDataException>(() => CommunityToolService.InstallPluginAsync(tool, null));
        Assert.Equal(payload, File.ReadAllBytes(CommunityToolService.GetLocalPath(tool)!));
    }

    [Fact]
    public void HistoricalHtmlExeIsNotInstalled()
    {
        var tool = Legacy();
        var previous = Existing(tool);
        File.WriteAllText(previous, "<!doctype html>");
        Assert.Null(CommunityToolService.GetLocalPath(tool));
        Assert.Equal(CommunityToolInstallStatus.NotInstalled, CommunityToolService.CheckInstallStatus(tool));
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, data) in files)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(data);
            }
        return output.ToArray();
    }

    private static int Find(byte[] source, byte[] target)
    {
        for (var i = 0; i <= source.Length - target.Length; i++)
            if (source.AsSpan(i, target.Length).SequenceEqual(target)) return i;
        throw new InvalidOperationException("Test ZIP lacks central directory.");
    }

    public void Dispose()
    {
        CloudToolService.OverrideForTests = null;
        CommunityPackageInstaller.DownloadOverrideForTests = null;
        CommunityToolService.CurrentSource = _oldSource;
        CommunityToolService.InvalidateCache();
        ToolCatalog.SetToolsRootForBuild(null);
        DataRoots.TestRootOverrideForTest = _oldRoot;
        _http.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class PackageTransport : HttpMessageHandler
    {
        internal const string Url = "https://zhenxingai.com/downloads/tools/example.zip";
        internal byte[] Package = [];
        internal List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            Assert.Equal(Url, url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(Package), RequestMessage = request });
        }
    }
}
