using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Tests;

// No real network, vendor execution or global app settings: each case owns a
// new temp directory and requests are answered by a local message handler.
public sealed class CloudToolMirrorTests : IDisposable
{
    private const string Primary = "https://zhenxingai.com/downloads/tools/test-tool.zip";
    private const string Backup = "https://download-backup.zhenxingai.com/downloads/tools/test-tool.zip";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-mirrors-" + Guid.NewGuid().ToString("N"));
    private readonly Transport _transport = new();
    private readonly HttpClient _http;
    private string Seed => Path.Combine(_root, "seed.json");
    private static Uri V1 => CloudToolService.OwnEndpoint;
    private static Uri V2 => CloudToolService.PreferredEndpoint;

    public CloudToolMirrorTests()
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(_transport);
    }

    private CloudToolManager Create(bool fallback = false, string architecture = "x64") => new(_root, _http, fallback ? V2 : V1, architecture,
        new Version(0, 1, 1), Seed, isInUse: _ => false, fallbackCatalogEndpoint: fallback ? V1 : null);

    private static CloudToolDefinition Tool(byte[] zip, string version = "1.0", string[]? mirrors = null) => new()
    {
        Id = "test-tool", Name = "测试工具", Category = "系统工具", Version = version,
        Packages = [new() { Architecture = "x64", Url = Primary, Mirrors = mirrors ?? [Backup],
            SizeBytes = zip.Length, Sha256 = Convert.ToHexString(SHA256.HashData(zip)), EntryPoint = "app.exe" }],
    };

    private static CloudToolCatalog Catalog(CloudToolDefinition tool, long revision = 1, int schema = 2) => new()
    { SchemaVersion = schema, Revision = revision, PublishedAt = "2026-10-07T01:00:00Z", Tools = [tool] };

    private void SeedCatalog(CloudToolCatalog catalog) => File.WriteAllText(Seed,
        JsonSerializer.Serialize(catalog, CloudToolValidation.JsonOptions));

    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes,
        HttpStatusCode status = HttpStatusCode.OK, string? media = null)
    {
        var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        if (media is not null) response.Content.Headers.ContentType = new MediaTypeHeaderValue(media);
        return response;
    }

    private static byte[] Pe(ushort machine = 0x8664)
    {
        var bytes = new byte[256];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(64));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(70), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(84), 112);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(86), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(88), machine == 0x014c ? (ushort)0x010b : (ushort)0x020b);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(216), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(220), 240);
        return bytes;
    }

    private static byte[] Zip(byte[]? program = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using var entry = zip.CreateEntry("app.exe", CompressionLevel.NoCompression).Open();
            entry.Write(program ?? Pe());
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData("404")]
    [InlineData("html-content-type")]
    [InlineData("html-disguised")]
    [InlineData("size")]
    [InlineData("sha256")]
    [InlineData("timeout")]
    [InlineData("redirect")]
    [InlineData("redirect-final-uri")]
    public async Task PrimaryFailure_AutomaticallyInstallsExactVerifiedBackup(string failure)
    {
        var zip = Zip();
        SeedCatalog(Catalog(Tool(zip)));
        _transport.Respond = (request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri == Backup) return Task.FromResult(Response(request, zip));
            if (failure == "timeout") throw new TaskCanceledException("synthetic source timeout");
            var broken = zip.ToArray();
            broken[60] ^= 1;
            var html = new byte[zip.Length];
            "<!doctype html><title>error</title>"u8.CopyTo(html);
            var response = failure switch
            {
                "404" => Response(request, [], HttpStatusCode.NotFound),
                "html-content-type" => Response(request, html, media: "text/html"),
                "html-disguised" => Response(request, html, media: "application/octet-stream"),
                "size" => Response(request, zip[..^1]),
                "sha256" => Response(request, broken),
                "redirect" => Response(request, [], HttpStatusCode.Found),
                _ => Response(request, zip),
            };
            if (failure == "redirect-final-uri") response.RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                "https://evil.example/downloads/tools/test-tool.zip");
            return Task.FromResult(response);
        };
        var manager = Create();
        var result = await manager.InstallAsync("test-tool");
        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { Primary, Backup }, _transport.Requests.Select(r => r.Url));
        Assert.Equal(Pe(), File.ReadAllBytes(manager.GetInstalledEntryPath("test-tool")!));
        var receipt = JsonSerializer.Deserialize<CloudToolReceipt>(File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(manager.GetInstalledEntryPath("test-tool"))!, CloudToolValidation.ReceiptFile)), CloudToolValidation.JsonOptions)!;
        Assert.Equal(Backup, receipt.PackageUrl);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(), receipt.Sha256);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "CloudTools", "Staging")));
    }

    [Fact]
    public async Task InterruptedPrimary_IsRemovedBeforeBackupStartsAndNeverUsesRange()
    {
        var zip = Zip();
        SeedCatalog(Catalog(Tool(zip)));
        _transport.Respond = (request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri == Backup)
            {
                Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "CloudTools", "Staging"), "*.zip", SearchOption.AllDirectories));
                return Task.FromResult(Response(request, zip));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request,
                Content = new StreamContent(new InterruptedStream(zip)) });
        };
        var manager = Create();
        Assert.True((await manager.InstallAsync("test-tool")).Success);
        Assert.Equal(Pe(), File.ReadAllBytes(manager.GetInstalledEntryPath("test-tool")!));
        Assert.All(_transport.Requests, request => Assert.Null(request.Range));
    }

    [Fact]
    public async Task DuplicateSources_DoNotRepeatAnIdenticalFailedRequest()
    {
        var zip = Zip();
        SeedCatalog(Catalog(Tool(zip, mirrors: [Primary, "https://ZHENXINGAI.COM/downloads/tools/test-tool.zip", Backup])));
        _transport.Respond = (request, _) => Task.FromResult(Response(request,
            request.RequestUri!.AbsoluteUri == Backup ? zip : [], request.RequestUri.AbsoluteUri == Backup ? HttpStatusCode.OK : HttpStatusCode.NotFound));
        Assert.True((await Create().InstallAsync("test-tool")).Success);
        Assert.Equal(new[] { Primary, Backup }, _transport.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task Cancellation_DoesNotSwitchSourceAndKeepsOldProgramConfigurationAndReceipt()
    {
        var old = Zip();
        SeedCatalog(Catalog(Tool(old, mirrors: []), schema: 1));
        _transport.Respond = (request, _) => Task.FromResult(Response(request, old));
        var manager = Create();
        Assert.True((await manager.InstallAsync("test-tool")).Success);
        var entry = manager.GetInstalledEntryPath("test-tool")!;
        var dir = Path.GetDirectoryName(entry)!;
        var receipt = File.ReadAllBytes(Path.Combine(dir, CloudToolValidation.ReceiptFile));
        File.WriteAllText(Path.Combine(dir, "settings.ini"), "keep-user-settings");
        var newerBytes = Pe(); newerBytes[^1] = 1;
        var newer = Zip(newerBytes);
        var incoming = Catalog(Tool(newer, "2.0"), 2);
        _transport.Respond = (request, _) => Task.FromResult(Response(request,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(incoming, CloudToolValidation.JsonOptions))));
        await manager.RefreshAsync();
        using var cancellation = new CancellationTokenSource();
        _transport.Requests.Clear();
        _transport.Respond = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request,
            Content = new StreamContent(new CancelStream(newer, cancellation)) });
        var result = await manager.UpdateAsync("test-tool", cancellation.Token);
        Assert.False(result.Success);
        Assert.Contains("已取消", result.Message);
        Assert.Single(_transport.Requests);
        Assert.Equal(Pe(), File.ReadAllBytes(entry));
        Assert.Equal("keep-user-settings", File.ReadAllText(Path.Combine(dir, "settings.ini")));
        Assert.Equal(receipt, File.ReadAllBytes(Path.Combine(dir, CloudToolValidation.ReceiptFile)));
    }

    [Fact]
    public async Task AllSourcesFail_DoesNotRemoveVerifiedOldInstallOrUserData()
    {
        var old = Zip();
        SeedCatalog(Catalog(Tool(old, mirrors: []), schema: 1));
        _transport.Respond = (request, _) => Task.FromResult(Response(request, old));
        var manager = Create();
        Assert.True((await manager.InstallAsync("test-tool")).Success);
        var entry = manager.GetInstalledEntryPath("test-tool")!;
        var dir = Path.GetDirectoryName(entry)!;
        var receipt = File.ReadAllBytes(Path.Combine(dir, CloudToolValidation.ReceiptFile));
        File.WriteAllText(Path.Combine(dir, "save.dat"), "progress");
        var newerBytes = Pe(); newerBytes[^1] = 2;
        var incoming = Catalog(Tool(Zip(newerBytes), "2.0"), 2);
        _transport.Respond = (request, _) => Task.FromResult(Response(request,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(incoming, CloudToolValidation.JsonOptions))));
        await manager.RefreshAsync();
        _transport.Requests.Clear();
        _transport.Respond = (request, _) => Task.FromResult(Response(request, [], HttpStatusCode.NotFound));
        var result = await manager.UpdateAsync("test-tool");
        Assert.False(result.Success);
        Assert.Contains("2 个下载源", result.Message);
        Assert.Equal(2, _transport.Requests.Count);
        Assert.Equal(Pe(), File.ReadAllBytes(entry));
        Assert.Equal(receipt, File.ReadAllBytes(Path.Combine(dir, CloudToolValidation.ReceiptFile)));
        Assert.Equal("progress", File.ReadAllText(Path.Combine(dir, "save.dat")));
        Assert.True(Assert.Single(manager.GetStates()).IsInstalled);
    }

    [Fact]
    public async Task SignedButTruncatedZip_GivesReadableErrorAndDoesNotDeploy()
    {
        var corrupt = Zip()[..^22];
        SeedCatalog(Catalog(Tool(corrupt)));
        _transport.Respond = (request, _) => Task.FromResult(Response(request, corrupt));
        var result = await Create().InstallAsync("test-tool");
        Assert.False(result.Success);
        Assert.Contains("完整有效的 ZIP", result.Message);
        Assert.DoesNotContain("Central Directory", result.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "CloudTools", "Installed", "test-tool")));
        Assert.Equal(2, _transport.Requests.Count);
    }

    [Fact]
    public async Task CatalogHashCannotHideAnEntryWithBadCrc()
    {
        var corrupt = Zip();
        for (var index = 0; index < corrupt.Length - 20; index++)
            if (corrupt.AsSpan(index, 4).SequenceEqual("PK\x01\x02"u8)) { corrupt[index + 16] ^= 1; break; }
        SeedCatalog(Catalog(Tool(corrupt)));
        _transport.Respond = (request, _) => Task.FromResult(Response(request, corrupt));
        var manager = Create();
        var result = await manager.InstallAsync("test-tool");
        Assert.False(result.Success);
        Assert.Contains("CRC", result.Message);
        Assert.False(manager.IsManaged("test-tool"));
        Assert.Equal(2, _transport.Requests.Count);
    }

    [Theory]
    [InlineData("mz-only")]
    [InlineData("wrong-machine")]
    [InlineData("outside-section")]
    public async Task SchemaTwo_RejectsFalseOrIncompatibleWindowsEntry(string defect)
    {
        var entry = defect == "mz-only" ? "MZsynthetic-not-pe"u8.ToArray() : Pe(defect == "wrong-machine" ? (ushort)0x014c : (ushort)0x8664);
        if (defect == "outside-section") BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(220), 1000);
        var zip = Zip(entry);
        SeedCatalog(Catalog(Tool(zip, mirrors: [])));
        _transport.Respond = (request, _) => Task.FromResult(Response(request, zip));
        var manager = Create();
        var result = await manager.InstallAsync("test-tool");
        Assert.False(result.Success);
        Assert.Contains("Windows 程序", result.Message);
        Assert.False(manager.IsManaged("test-tool"));
    }

    [Theory]
    [InlineData("x64", 0x8664, true)]
    [InlineData("x64", 0x014c, true)]
    [InlineData("x64", 0xaa64, false)]
    [InlineData("arm64", 0xaa64, true)]
    [InlineData("arm64", 0x014c, true)]
    [InlineData("arm64", 0x8664, false)]
    [InlineData("x86", 0x014c, true)]
    [InlineData("x86", 0x8664, false)]
    [InlineData("x86", 0xaa64, false)]
    public async Task SchemaTwoAnyPackage_RequiresNativeOrSupportedX86Entry(string architecture, int machine, bool accepted)
    {
        var zip = Zip(Pe((ushort)machine));
        var tool = Tool(zip, mirrors: []);
        tool = tool with { Packages = [tool.Packages[0] with { Architecture = "any" }] };
        SeedCatalog(Catalog(tool));
        _transport.Respond = (request, _) => Task.FromResult(Response(request, zip));
        var manager = Create(architecture: architecture);
        var result = await manager.InstallAsync("test-tool");
        Assert.Equal(accepted, result.Success);
        Assert.Equal(accepted, manager.IsManaged("test-tool"));
        if (!accepted) Assert.Contains("Windows 程序", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task UndeployedV2_FallsBackToV1AndThenSelectsV2WhenDeployed(HttpStatusCode missing)
    {
        var zip = Zip();
        var v1 = Catalog(Tool(zip, mirrors: []), schema: 1);
        var v2 = Catalog(Tool(zip), 2);
        var deployed = false;
        _transport.Respond = (request, _) =>
        {
            if (request.RequestUri == V2 && !deployed) return Task.FromResult(Response(request, [], missing));
            var response = Response(request, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                request.RequestUri == V1 ? v1 : v2, CloudToolValidation.JsonOptions)));
            response.Headers.ETag = new EntityTagHeaderValue(request.RequestUri == V1 ? "\"v1\"" : "\"v2\"");
            return Task.FromResult(response);
        };
        var manager = Create(fallback: true);
        await manager.RefreshAsync();
        Assert.Null(manager.LastRefreshError);
        Assert.Equal(V1, manager.ActiveCatalogEndpoint);
        Assert.Equal(CloudToolService.EventsEndpoint, CloudToolService.SelectEventsEndpoint(manager.ActiveCatalogEndpoint!));
        await manager.RefreshAsync();
        Assert.Equal("\"v1\"", _transport.Requests[^1].IfNoneMatch);
        deployed = true;
        await manager.RefreshAsync();
        Assert.Null(manager.LastRefreshError);
        Assert.Equal(V2, manager.ActiveCatalogEndpoint);
        Assert.Null(_transport.Requests[^1].IfNoneMatch);
        Assert.Equal(CloudToolService.PreferredEventsEndpoint, CloudToolService.SelectEventsEndpoint(manager.ActiveCatalogEndpoint!));
        _transport.Respond = (request, _) => Task.FromResult(Response(request, [], HttpStatusCode.NotModified));
        await manager.RefreshAsync();
        Assert.Null(manager.LastRefreshError);
        Assert.Equal("\"v2\"", _transport.Requests[^1].IfNoneMatch);
        Assert.Equal(2, manager.Revision);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("invalid-mirror")]
    [InlineData("401")]
    [InlineData("500")]
    [InlineData("timeout")]
    public async Task BrokenV2_DoesNotSilentlyDowngradeToV1(string defect)
    {
        var zip = Zip();
        SeedCatalog(Catalog(Tool(zip, mirrors: []), schema: 1));
        _transport.Respond = (request, _) =>
        {
            if (defect == "timeout") throw new TaskCanceledException("synthetic timeout");
            var json = defect == "invalid-mirror" ? JsonSerializer.Serialize(Catalog(Tool(zip, mirrors:
                ["https://evil.example/downloads/tools/a.zip"]), 2), CloudToolValidation.JsonOptions) : "{broken";
            var status = defect == "401" ? HttpStatusCode.Unauthorized : defect == "500" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
            return Task.FromResult(Response(request, Encoding.UTF8.GetBytes(json), status));
        };
        var manager = Create(fallback: true);
        await manager.RefreshAsync();
        Assert.NotNull(manager.LastRefreshError);
        Assert.Single(_transport.Requests);
        Assert.Equal(1, manager.Revision);
        Assert.Null(manager.ActiveCatalogEndpoint);
    }

    public void Dispose()
    {
        _http.Dispose();
        CloudToolValidation.DeleteTree(_root);
    }

    private sealed class Transport : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (request, _) => Task.FromResult(Response(request, [], HttpStatusCode.NotFound));
        internal List<(string Url, string? Range, string? IfNoneMatch)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Range?.ToString(), request.Headers.IfNoneMatch.FirstOrDefault()?.ToString()));
            return Respond(request, cancellationToken);
        }
    }

    private sealed class InterruptedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0) throw new IOException("synthetic disconnected stream");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 17)], cancellationToken);
        }
    }

    private sealed class CancelStream(byte[] bytes, CancellationTokenSource source) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0) { source.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 17)], cancellationToken);
        }
    }
}
