using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TubaWinUi3.Services.AppManagement;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Tests;

/// <summary>Pure states and GUID-owned fake installations; no real probes, configuration, network or executable launches.</summary>
public sealed class AppCenterInventoryPolicyTests : IDisposable
{
    private readonly string _directoryName = "zxai-app-inventory-" + Guid.NewGuid().ToString("N");
    private readonly string _tempRoot = Path.GetFullPath(Path.GetTempPath());
    private readonly string _root;
    private readonly FakeTransport _transport;
    private readonly HttpClient _http;
    private static readonly Uri Endpoint = new("https://zhenxingai.com/api/toolflows/v1/tools/catalog");
    private const string PackageUrl = "https://zhenxingai.com/downloads/tools/fake/portable.zip";
    private string Seed => Path.Combine(_root, "seed.json");
    private string Legacy => Path.Combine(_root, "legacy");

    public AppCenterInventoryPolicyTests()
    {
        _root = Path.GetFullPath(Path.Combine(_tempRoot, _directoryName));
        Directory.CreateDirectory(_root);
        var zip = FakeZip();
        _transport = new FakeTransport(zip);
        _http = new HttpClient(_transport);
        File.WriteAllText(Seed, JsonSerializer.Serialize(new CloudToolCatalog
        {
            Revision = 1, PublishedAt = "2026-10-05T01:00:00Z", MinClientVersion = "0.1.0.0",
            Tools =
            [
                new() { Id = "portable-tool", Name = "Fake portable tool", Category = "系统工具", Version = "1.0",
                    LegacyPath = "fixture/portable", Packages = [new() { Architecture = "x64", Url = PackageUrl,
                        SizeBytes = zip.Length, Sha256 = Convert.ToHexString(SHA256.HashData(zip)), EntryPoint = "app.exe" }] },
                new() { Id = "manual-tool", Name = "Fake manual tool", Category = "系统工具", Version = "1.0",
                    Homepage = "https://example.com/", Packages = [] },
            ],
        }, CloudToolValidation.JsonOptions));
    }

    [Theory]
    [InlineData(CloudToolStatus.NotInstalled)]
    [InlineData(CloudToolStatus.Unsupported)]
    [InlineData(CloudToolStatus.Failed)]
    [InlineData(CloudToolStatus.Installed)]
    public void CatalogStateAndAnUnverifiedPathDoNotProveLocalInventory(CloudToolStatus status)
    {
        var state = new CloudToolState("sample", "Sample", "", "1", status,
            Error: "metadata/read error", EntryPath: "unverified-path.exe");
        Assert.False(AppCenterInventoryPolicy.ShouldIncludeCloudTool(state, hasLocalEntry: false));
    }

    [Theory]
    [InlineData(CloudToolStatus.NotInstalled)]
    [InlineData(CloudToolStatus.Unsupported)]
    [InlineData(CloudToolStatus.Failed)]
    public void VerifiedLocalEntryRemainsVisibleWithoutAnOwnedReceipt(CloudToolStatus status)
    {
        var state = new CloudToolState("sample", "Sample", "1", "1", status);
        Assert.True(AppCenterInventoryPolicy.ShouldIncludeCloudTool(state, hasLocalEntry: true));
    }

    [Fact]
    public void OwnedMissingEntryAndPersistedPendingUpdateRemainRecoverable()
    {
        var missingEntry = new CloudToolState("sample", "Sample", "1", "2", CloudToolStatus.Failed, IsManaged: true);
        Assert.True(AppCenterInventoryPolicy.ShouldIncludeCloudTool(missingEntry, false));
        Assert.True(AppCenterInventoryPolicy.ShouldIncludeCloudTool(missingEntry with
            { IsManaged = false, Status = CloudToolStatus.NotInstalled, PendingUpdate = true }, false));
    }

    [Theory]
    [InlineData(CloudToolStatus.Downloading)]
    [InlineData(CloudToolStatus.Installing)]
    [InlineData(CloudToolStatus.Updating)]
    [InlineData(CloudToolStatus.Removing)]
    [InlineData(CloudToolStatus.PendingUpdate)]
    [InlineData(CloudToolStatus.Failed)]
    [InlineData(CloudToolStatus.Unsupported)]
    public void InitiatedJobsRemainVisibleWithoutAnEntry(CloudToolStatus status)
    {
        var state = new CloudToolState("sample", "Sample", "", "1", status) { HasOperationActivity = true };
        Assert.True(AppCenterInventoryPolicy.ShouldIncludeCloudTool(state, false));
    }

    [Fact]
    public void FreshManagerKeepsUninstalledToolsInCatalogButOutOfInventory()
    {
        var manager = Create(allowNetwork: false);
        Assert.Equal(2, manager.GetCatalog().Count);
        Assert.All(manager.GetStates(), state =>
        {
            Assert.False(state.HasOperationActivity);
            Assert.False(AppCenterInventoryPolicy.ShouldIncludeCloudTool(state, File.Exists(state.EntryPath)));
        });
        Assert.Empty(_transport.Requests);
    }

    [Fact]
    public async Task CatalogRefreshErrorDoesNotCreateAnInstallationOrRetryTask()
    {
        _transport.FailCatalog = true;
        var manager = Create();
        await manager.RefreshAsync();
        Assert.NotNull(manager.LastRefreshError);
        Assert.All(manager.GetStates(), state =>
        {
            Assert.False(state.HasOperationActivity);
            Assert.False(AppCenterInventoryPolicy.ShouldIncludeCloudTool(state, File.Exists(state.EntryPath)));
        });
        Assert.Equal(2, manager.GetCatalog().Count);
        Assert.Single(_transport.Requests);
    }

    [Fact]
    public async Task FailedInitialInstallIsAnExplicitRetryTaskRatherThanAMetadataError()
    {
        _transport.FailPackage = true;
        var manager = Create();
        Assert.False((await manager.InstallAsync("portable-tool")).Success);
        var state = Assert.Single(manager.GetStates(), state => state.Id == "portable-tool");
        Assert.Equal(CloudToolStatus.Failed, state.Status);
        Assert.True(state.HasOperationActivity);
        Assert.False(state.IsManaged);
        Assert.Null(state.EntryPath);
        Assert.True(AppCenterInventoryPolicy.ShouldIncludeCloudTool(state, false));
    }

    [Fact]
    public async Task UnsupportedUserAttemptIsDistinguishedFromTheSameUninstalledCatalogEntry()
    {
        var manager = Create(allowNetwork: false);
        var initial = Assert.Single(manager.GetStates(), state => state.Id == "manual-tool");
        Assert.Equal(CloudToolStatus.Unsupported, initial.Status);
        Assert.False(AppCenterInventoryPolicy.ShouldIncludeCloudTool(initial, false));
        Assert.False((await manager.InstallAsync("manual-tool")).Success);
        var attempted = Assert.Single(manager.GetStates(), state => state.Id == "manual-tool");
        Assert.Equal(CloudToolStatus.Unsupported, attempted.Status);
        Assert.True(attempted.HasOperationActivity);
        Assert.True(AppCenterInventoryPolicy.ShouldIncludeCloudTool(attempted, false));
        Assert.Empty(_transport.Requests);
    }

    [Fact]
    public async Task MissingOwnedEntryIsVisibleForRecoveryAndRemovalHidesOnlyTheOwnedCopy()
    {
        var manager = Create();
        Assert.True((await manager.InstallAsync("portable-tool")).Success);
        var installed = Assert.Single(manager.GetStates(), state => state.Id == "portable-tool");
        Assert.True(AppCenterInventoryPolicy.ShouldIncludeCloudTool(installed, File.Exists(installed.EntryPath)));
        var entry = Path.GetFullPath(installed.EntryPath!);
        Assert.True(entry.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        File.Delete(entry); // Only the GUID-owned fake executable is removed; it was never executed.
        var broken = Assert.Single(manager.GetStates(), state => state.Id == "portable-tool");
        Assert.True(broken.IsManaged);
        Assert.Null(broken.EntryPath);
        Assert.True(AppCenterInventoryPolicy.ShouldIncludeCloudTool(broken, false));
        Assert.True((await manager.RemoveAsync("portable-tool")).Success);
        var removed = Assert.Single(manager.GetStates(), state => state.Id == "portable-tool");
        Assert.False(removed.HasOperationActivity);
        Assert.False(AppCenterInventoryPolicy.ShouldIncludeCloudTool(removed, false));
        Assert.Contains(manager.GetCatalog(), tool => tool.Id == "portable-tool");
        Assert.False(AppCenterInventoryPolicy.ShouldIncludeCloudTool(
            Assert.Single(Create(allowNetwork: false).GetStates(), state => state.Id == "portable-tool"), false));
    }

    [Fact]
    public void LegacyBundledEntryIsVisibleWithoutClaimingOwnershipOrDownloading()
    {
        var entry = Path.Combine(Legacy, "fixture", "portable", "app.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        File.WriteAllBytes(entry, "MZfake bundled tool"u8.ToArray());
        var manager = Create(allowNetwork: false);
        var state = Assert.Single(manager.GetStates(), state => state.Id == "portable-tool");
        Assert.False(state.IsManaged);
        Assert.False(state.HasOperationActivity);
        Assert.Equal(entry, state.EntryPath);
        Assert.True(AppCenterInventoryPolicy.ShouldIncludeCloudTool(state, File.Exists(state.EntryPath)));
        Assert.Empty(_transport.Requests);
    }

    private CloudToolManager Create(bool allowNetwork = true) => new(_root, _http, Endpoint,
        "x64", new Version(0, 1, 0, 0), Seed, Legacy, allowNetwork, isInUse: _ => false);

    private static byte[] FakeZip()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using var entry = archive.CreateEntry("app.exe").Open();
            entry.Write("MZfake executable, never launched"u8);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        _http.Dispose();
        var full = Path.GetFullPath(_root);
        var parent = _tempRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(full) != _directoryName)
            throw new InvalidOperationException("Inventory fixture escaped its temporary root.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private sealed class FakeTransport(byte[] zip) : HttpMessageHandler
    {
        internal bool FailCatalog { get; set; }
        internal bool FailPackage { get; set; }
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            if (request.RequestUri == Endpoint)
                return Task.FromResult(new HttpResponseMessage(FailCatalog ? HttpStatusCode.BadGateway : HttpStatusCode.NotModified)
                    { RequestMessage = request });
            if (request.RequestUri?.AbsoluteUri != PackageUrl)
                throw new InvalidOperationException("Unexpected fake request; no network handler is available.");
            return Task.FromResult(new HttpResponseMessage(FailPackage ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent(zip) });
        }
    }
}
