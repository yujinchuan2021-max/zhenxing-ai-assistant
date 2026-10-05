using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Tests;

public sealed class CloudToolManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-cloud-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTransport _transport = new();
    private readonly HttpClient _http;
    private static readonly Uri Endpoint = new("https://zhenxingai.com/api/toolflows/v1/tools/catalog");
    private string Seed => Path.Combine(_root, "seed.json");
    private string Legacy => Path.Combine(_root, "legacy");

    public CloudToolManagerTests()
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(_transport);
    }

    private CloudToolManager Create(bool network = true, Func<string, bool>? running = null,
        string architecture = "x64") => new(_root, _http, Endpoint, architecture,
            new Version(0, 1, 0), Seed, Legacy, network, running ?? (_ => false));

    private CloudToolDefinition Tool(byte[] zip, string version = "1.0", string id = "sample-tool") => new()
    {
        Id = id, Name = "示例工具", Category = "系统工具", Version = version,
        LegacyPath = "系统工具/示例工具", Order = 1,
        Packages = [new() { Architecture = "x64", Url = "https://zhenxingai.com/downloads/tools/" + id + ".zip",
            SizeBytes = zip.Length, Sha256 = Convert.ToHexString(SHA256.HashData(zip)), EntryPoint = "app.exe" }],
    };

    private static CloudToolCatalog Catalog(long revision, params CloudToolDefinition[] tools) => new()
    {
        Revision = revision, PublishedAt = "2026-10-05T01:00:00Z", MinClientVersion = "0.1.0", Tools = tools,
    };

    private void SeedCatalog(CloudToolCatalog catalog) =>
        File.WriteAllText(Seed, JsonSerializer.Serialize(catalog, CloudToolValidation.JsonOptions));

    private void SetRemote(CloudToolCatalog catalog, params byte[][] zips)
    {
        _transport.Manifest = JsonSerializer.Serialize(catalog, CloudToolValidation.JsonOptions);
        for (var i = 0; i < zips.Length; i++) _transport.Packages[catalog.Tools[i].Packages[0].Url] = zips[i];
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Name);
                using var stream = entry.Open();
                stream.Write(file.Content);
            }
        return output.ToArray();
    }

    [Fact]
    public void SeedFallback_WorksOfflineAndDoesNotRequestAnything()
    {
        var zip = Zip(("app.exe", "MZlocal"u8.ToArray()));
        SeedCatalog(Catalog(1, Tool(zip)));
        var manager = Create(network: false);
        Assert.Single(manager.GetCatalog());
        Assert.Equal(CloudToolStatus.NotInstalled, Assert.Single(manager.GetStates()).Status);
        Assert.Empty(_transport.Requests);
    }

    [Fact]
    public async Task Isolation_BlocksRefreshAndInstallEvenWithFakeTransport()
    {
        var zip = Zip(("app.exe", "MZlocal"u8.ToArray()));
        SeedCatalog(Catalog(1, Tool(zip)));
        var manager = Create(network: false);
        await manager.RefreshAsync();
        Assert.False((await manager.InstallAsync("sample-tool")).Success);
        Assert.Empty(_transport.Requests);
    }

    [Fact]
    public async Task VerifiedInstall_ProducesManagedReceiptAndCanBeRemoved()
    {
        var zip = Zip(("app.exe", "MZone"u8.ToArray()), ("assets/data.txt", "data"u8.ToArray()));
        var catalog = Catalog(1, Tool(zip));
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var state = Assert.Single(manager.GetStates());
        Assert.True(state.IsManaged);
        Assert.Equal("1.0", state.Version);
        Assert.Equal("MZone", await File.ReadAllTextAsync(state.EntryPath!));
        Assert.True(Create().IsManaged("sample-tool"));
        Assert.True((await manager.RemoveAsync("sample-tool")).Success);
        Assert.False(manager.IsManaged("sample-tool"));
        Assert.Null(manager.GetInstalledEntryPath("sample-tool"));
    }

    [Fact]
    public async Task LegacyResolver_MapsOwnedDirectoryAndSecondaryExecutableWithoutRequests()
    {
        var zip = Zip(("app.exe", "MZmain"u8.ToArray()), ("bin/cli.exe", "MZsecondary"u8.ToArray()));
        var catalog = Catalog(1, Tool(zip));
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var directory = Path.GetDirectoryName(manager.GetInstalledEntryPath("sample-tool"))!;
        var requestCount = _transport.Requests.Count;

        Assert.Equal(directory, manager.ResolveLegacyToolPath("系统工具/示例工具"));
        Assert.Equal(Path.Combine(directory, "bin", "cli.exe"), manager.ResolveLegacyToolPath("系统工具\\示例工具\\bin\\cli.exe"));
        Assert.Null(manager.ResolveLegacyToolPath("系统工具/示例工具/missing.exe"));
        Assert.Null(manager.ResolveLegacyToolPath("系统工具/示例工具箱/app.exe"));
        Assert.Equal(requestCount, _transport.Requests.Count);
    }

    [Fact]
    public async Task LegacyResolver_PrefersLongestExactDirectoryPrefix()
    {
        var parentZip = Zip(("app.exe", "MZparent"u8.ToArray()), ("nested/app.exe", "MZparent nested"u8.ToArray()));
        var childZip = Zip(("app.exe", "MZchild"u8.ToArray()));
        var child = Tool(childZip, id: "nested-tool") with { LegacyPath = "系统工具/示例工具/nested" };
        var catalog = Catalog(1, Tool(parentZip), child);
        SeedCatalog(catalog);
        SetRemote(catalog, parentZip, childZip);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        Assert.True((await manager.InstallAsync("nested-tool")).Success);

        Assert.Equal(manager.GetInstalledEntryPath("nested-tool"), manager.ResolveLegacyToolPath("系统工具/示例工具/nested/app.exe"));
        Assert.Equal("MZchild", File.ReadAllText(manager.ResolveLegacyToolPath("系统工具/示例工具/nested/app.exe")!));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("系统工具/示例工具/../../escape.exe")]
    [InlineData("C:/系统工具/示例工具/app.exe")]
    [InlineData("系统工具/示例工具/app.exe:stream")]
    public async Task LegacyResolver_RejectsUnsafePathsEvenWithAnOwnedInstallation(string relative)
    {
        var zip = Zip(("app.exe", "MZowned"u8.ToArray()));
        var catalog = Catalog(1, Tool(zip));
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        Assert.Null(manager.ResolveLegacyToolPath(relative));
    }

    [Fact]
    public void LegacyResolver_DoesNotMapManualOrUnregisteredDirectories()
    {
        var zip = Zip(("app.exe", "MZpackage"u8.ToArray()));
        SeedCatalog(Catalog(1, Tool(zip)));
        var manual = Path.Combine(Legacy, "系统工具", "示例工具");
        Directory.CreateDirectory(manual);
        File.WriteAllText(Path.Combine(manual, "app.exe"), "MZmanual");
        var unregistered = Path.Combine(_root, "CloudTools", "Installed", "sample-tool");
        Directory.CreateDirectory(unregistered);
        File.WriteAllText(Path.Combine(unregistered, "app.exe"), "MZunregistered");
        var manager = Create();

        Assert.Null(manager.ResolveLegacyToolPath("系统工具/示例工具"));
        Assert.Null(manager.ResolveLegacyToolPath("系统工具/示例工具/app.exe"));
        Assert.Equal("MZmanual", File.ReadAllText(Path.Combine(manual, "app.exe")));
        Assert.Equal("MZunregistered", File.ReadAllText(Path.Combine(unregistered, "app.exe")));
        Assert.Empty(_transport.Requests);
    }

    [Fact]
    public async Task LegacyResolver_UsesCurrentOwnedDirectoryAfterEntryPointChangesOnUpdate()
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()), ("bin/cli.exe", "MZold cli"u8.ToArray()));
        var first = Catalog(1, Tool(old));
        SeedCatalog(first);
        SetRemote(first, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var directory = Path.GetDirectoryName(manager.GetInstalledEntryPath("sample-tool"))!;
        Assert.Equal(Path.Combine(directory, "app.exe"), manager.ResolveLegacyToolPath("系统工具/示例工具/app.exe"));
        var newer = Zip(("new-main.exe", "MZnew"u8.ToArray()), ("bin/cli.exe", "MZnew cli"u8.ToArray()));
        var tool = Tool(newer, "2.0");
        tool = tool with { Packages = [tool.Packages[0] with { EntryPoint = "new-main.exe" }] };
        SetRemote(Catalog(2, tool), newer);
        await manager.RefreshAsync();
        // A changed remote entry point cannot hide the still-owned old directory.
        Assert.Equal(directory, manager.ResolveLegacyToolPath("系统工具/示例工具"));
        Assert.True((await manager.UpdateAsync("sample-tool")).Success);

        Assert.Equal(directory, manager.ResolveLegacyToolPath("系统工具/示例工具"));
        Assert.Null(manager.ResolveLegacyToolPath("系统工具/示例工具/app.exe"));
        Assert.Equal("MZnew", File.ReadAllText(manager.ResolveLegacyToolPath("系统工具/示例工具/new-main.exe")!));
        Assert.Equal("MZnew cli", File.ReadAllText(manager.ResolveLegacyToolPath("系统工具/示例工具/bin/cli.exe")!));
    }

    [Fact]
    public async Task BadHashOnUpdate_PreservesPreviouslyVerifiedVersionAndEntry()
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()));
        var one = Catalog(1, Tool(old));
        SeedCatalog(one);
        SetRemote(one, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var newer = Zip(("app.exe", "MZnew"u8.ToArray()));
        var bad = Tool(newer, "2.0") with { Packages = [Tool(newer).Packages[0] with { Sha256 = new string('0', 64) }] };
        SetRemote(Catalog(2, bad), newer);
        await manager.RefreshAsync();
        Assert.False((await manager.UpdateAsync("sample-tool")).Success);
        var state = Assert.Single(manager.GetStates());
        Assert.Equal("1.0", state.Version);
        Assert.True(state.IsInstalled);
        Assert.Equal("MZold", File.ReadAllText(state.EntryPath!));
        Assert.Equal(CloudToolStatus.Failed, state.Status);
    }

    [Fact]
    public async Task Update_PreservesNewConfigurationAndSaveWithoutReplacingNewProgramFiles()
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()), ("lib.dll", "old library"u8.ToArray()));
        var first = Catalog(1, Tool(old));
        SeedCatalog(first);
        SetRemote(first, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var dir = Path.GetDirectoryName(manager.GetInstalledEntryPath("sample-tool"))!;
        File.WriteAllText(Path.Combine(dir, "user.ini"), "theme=dark");
        Directory.CreateDirectory(Path.Combine(dir, "saves"));
        File.WriteAllText(Path.Combine(dir, "saves", "save.db"), "saved progress");
        var newer = Zip(("app.exe", "MZnew"u8.ToArray()), ("lib.dll", "new library"u8.ToArray()));
        SetRemote(Catalog(2, Tool(newer, "2.0")), newer);
        await manager.RefreshAsync();

        Assert.True((await manager.UpdateAsync("sample-tool")).Success);
        Assert.Equal("MZnew", File.ReadAllText(Path.Combine(dir, "app.exe")));
        Assert.Equal("new library", File.ReadAllText(Path.Combine(dir, "lib.dll")));
        Assert.Equal("theme=dark", File.ReadAllText(Path.Combine(dir, "user.ini")));
        Assert.Equal("saved progress", File.ReadAllText(Path.Combine(dir, "saves", "save.db")));
        var receipt = JsonSerializer.Deserialize<CloudToolReceipt>(
            File.ReadAllText(Path.Combine(dir, CloudToolValidation.ReceiptFile)), CloudToolValidation.JsonOptions)!;
        Assert.Equal(2, receipt.Files!.Count);
        Assert.DoesNotContain("user.ini", receipt.Files.Keys);
        Assert.DoesNotContain("saves/save.db", receipt.Files.Keys);
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "CloudTools", "Backups")));
        Assert.Equal("MZold", File.ReadAllText(Path.Combine(backup, "app.exe")));
        Assert.Equal("saved progress", File.ReadAllText(Path.Combine(backup, "saves", "save.db")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "CloudTools", "Transactions")));
        Assert.True(Create().IsManaged("sample-tool"));
        Assert.True(Directory.Exists(backup));
    }

    [Theory]
    [InlineData("default=new")]
    [InlineData("my customized settings")]
    public async Task ModifiedPackagedConfiguration_ConflictsBeforeCommitAndKeepsOldReceipt(string incomingConfiguration)
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()), ("settings.ini", "default=old"u8.ToArray()));
        var first = Catalog(1, Tool(old));
        SeedCatalog(first);
        SetRemote(first, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var dir = Path.GetDirectoryName(manager.GetInstalledEntryPath("sample-tool"))!;
        File.WriteAllText(Path.Combine(dir, "settings.ini"), "my customized settings");
        var receiptPath = Path.Combine(dir, CloudToolValidation.ReceiptFile);
        var originalReceipt = File.ReadAllBytes(receiptPath);
        var newer = Zip(("app.exe", "MZnew"u8.ToArray()), ("settings.ini", Encoding.UTF8.GetBytes(incomingConfiguration)));
        SetRemote(Catalog(2, Tool(newer, "2.0")), newer);
        await manager.RefreshAsync();

        var result = await manager.UpdateAsync("sample-tool");
        Assert.False(result.Success);
        Assert.Contains("配置与新版本冲突，原版本保留", result.Message);
        Assert.Equal("MZold", File.ReadAllText(Path.Combine(dir, "app.exe")));
        Assert.Equal("my customized settings", File.ReadAllText(Path.Combine(dir, "settings.ini")));
        Assert.Equal(originalReceipt, File.ReadAllBytes(receiptPath));
        Assert.Equal("1.0", Assert.Single(manager.GetStates()).Version);
        Assert.True(Create().IsManaged("sample-tool"));
    }

    [Fact]
    public async Task NewUserFile_CollisionWithNewPackagePreservesOriginalDataAndReceipt()
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()));
        var first = Catalog(1, Tool(old));
        SeedCatalog(first);
        SetRemote(first, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var dir = Path.GetDirectoryName(manager.GetInstalledEntryPath("sample-tool"))!;
        File.WriteAllText(Path.Combine(dir, "user.ini"), "user created configuration");
        var receiptPath = Path.Combine(dir, CloudToolValidation.ReceiptFile);
        var originalReceipt = File.ReadAllBytes(receiptPath);
        var newer = Zip(("app.exe", "MZnew"u8.ToArray()), ("user.ini", "new package configuration"u8.ToArray()));
        SetRemote(Catalog(2, Tool(newer, "2.0")), newer);
        await manager.RefreshAsync();

        var result = await manager.UpdateAsync("sample-tool");
        Assert.False(result.Success);
        Assert.Contains("配置与新版本冲突，原版本保留", result.Message);
        Assert.Equal("user created configuration", File.ReadAllText(Path.Combine(dir, "user.ini")));
        Assert.Equal("MZold", File.ReadAllText(Path.Combine(dir, "app.exe")));
        Assert.Equal(originalReceipt, File.ReadAllBytes(receiptPath));
        Assert.Equal("1.0", Assert.Single(manager.GetStates()).Version);
        Assert.True(Create().IsManaged("sample-tool"));
    }

    [Fact]
    public async Task ModifiedPackagedConfiguration_RemovedFromNewPackageIsStillCarriedAcrossUpdates()
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()), ("settings.ini", "default"u8.ToArray()));
        var first = Catalog(1, Tool(old));
        SeedCatalog(first);
        SetRemote(first, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var dir = Path.GetDirectoryName(manager.GetInstalledEntryPath("sample-tool"))!;
        File.WriteAllText(Path.Combine(dir, "settings.ini"), "custom setting");
        File.WriteAllText(Path.Combine(dir, "user.ini"), "theme=dark");
        File.WriteAllText(Path.Combine(dir, "save.db"), "save one");

        for (var version = 2; version <= 3; version++)
        {
            var newer = Zip(("app.exe", Encoding.UTF8.GetBytes("MZversion" + version)));
            SetRemote(Catalog(version, Tool(newer, version + ".0")), newer);
            await manager.RefreshAsync();
            Assert.True((await manager.UpdateAsync("sample-tool")).Success);
            Assert.Equal("MZversion" + version, File.ReadAllText(Path.Combine(dir, "app.exe")));
            Assert.Equal("custom setting", File.ReadAllText(Path.Combine(dir, "settings.ini")));
            Assert.Equal("theme=dark", File.ReadAllText(Path.Combine(dir, "user.ini")));
            Assert.Equal("save one", File.ReadAllText(Path.Combine(dir, "save.db")));
            var receipt = JsonSerializer.Deserialize<CloudToolReceipt>(
                File.ReadAllText(Path.Combine(dir, CloudToolValidation.ReceiptFile)), CloudToolValidation.JsonOptions)!;
            Assert.Single(receipt.Files!);
            Assert.Contains("app.exe", receipt.Files!.Keys);
        }
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(_root, "CloudTools", "Backups")).Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("{\"app.exe\":\"not-a-sha256\"}")]
    [InlineData("{\"../escape\":\"0000000000000000000000000000000000000000000000000000000000000000\"}")]
    [InlineData("{\"app.exe\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"APP.exe\":\"0000000000000000000000000000000000000000000000000000000000000000\"}")]
    [InlineData("{\"app.exe\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"app.exe\":\"0000000000000000000000000000000000000000000000000000000000000000\"}")]
    public async Task MissingOrInvalidBaseline_RefusesUpdateButStillAllowsOwnedOpeningAndRemoval(string? filesJson)
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()));
        var first = Catalog(1, Tool(old));
        SeedCatalog(first);
        SetRemote(first, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var entry = manager.GetInstalledEntryPath("sample-tool")!;
        var receiptPath = Path.Combine(Path.GetDirectoryName(entry)!, CloudToolValidation.ReceiptFile);
        var receipt = JsonSerializer.Deserialize<CloudToolReceipt>(File.ReadAllText(receiptPath), CloudToolValidation.JsonOptions)!;
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(receipt with { Files = null }, CloudToolValidation.JsonOptions))!;
        node.AsObject().Remove("files");
        var coreJson = node.ToJsonString();
        // Keep the raw file object, including deliberately duplicated JSON keys.
        File.WriteAllText(receiptPath, filesJson is null ? coreJson : coreJson[..^1] + ",\"files\":" + filesJson + "}");
        var originalReceipt = File.ReadAllBytes(receiptPath);
        var newer = Zip(("app.exe", "MZnew"u8.ToArray()));
        SetRemote(Catalog(2, Tool(newer, "2.0")), newer);
        await manager.RefreshAsync();
        var packageRequests = _transport.Requests.Count(u => u.AbsolutePath.EndsWith(".zip"));

        var result = await manager.UpdateAsync("sample-tool");
        Assert.False(result.Success);
        Assert.Contains("请先备份数据", result.Message);
        Assert.Equal(packageRequests, _transport.Requests.Count(u => u.AbsolutePath.EndsWith(".zip")));
        Assert.Equal(originalReceipt, File.ReadAllBytes(receiptPath));
        Assert.Equal(entry, manager.GetInstalledEntryPath("sample-tool"));
        Assert.Equal("MZold", File.ReadAllText(entry));
        Assert.True(Create().IsManaged("sample-tool"));
        Assert.True((await manager.RemoveAsync("sample-tool")).Success);
    }

    [Fact]
    public async Task CompletedUpdateRecovery_KeepsOldBackupIncludingLateWrittenData()
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()));
        var first = Catalog(1, Tool(old));
        SeedCatalog(first);
        SetRemote(first, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var newer = Zip(("app.exe", "MZnew"u8.ToArray()));
        SetRemote(Catalog(2, Tool(newer, "2.0")), newer);
        await manager.RefreshAsync();
        Assert.True((await manager.UpdateAsync("sample-tool")).Success);
        var root = Path.Combine(_root, "CloudTools");
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(root, "Backups")));
        File.WriteAllText(Path.Combine(backup, "late-save.db"), "last write before close");
        var journal = Path.Combine(root, "Transactions", "sample-tool.json");
        File.WriteAllText(journal, JsonSerializer.Serialize(new CloudToolTransaction("sample-tool", "", Path.GetFileName(backup)),
            CloudToolValidation.JsonOptions));

        var recovered = Create();
        Assert.Equal("MZnew", File.ReadAllText(recovered.GetInstalledEntryPath("sample-tool")!));
        Assert.Equal("last write before close", File.ReadAllText(Path.Combine(backup, "late-save.db")));
        Assert.False(File.Exists(journal));
        Assert.True((await recovered.RemoveAsync("sample-tool")).Success);
        Assert.Equal("last write before close", File.ReadAllText(Path.Combine(backup, "late-save.db")));
    }

    [Fact]
    public async Task ConfigurationConflict_BlocksSameAutomaticPackageAcrossRestartButAllowsExplicitRetryAndNewHash()
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()), ("settings.ini", "default"u8.ToArray()));
        var first = Catalog(1, Tool(old));
        SeedCatalog(first);
        SetRemote(first, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var dir = Path.GetDirectoryName(manager.GetInstalledEntryPath("sample-tool"))!;
        File.WriteAllText(Path.Combine(dir, "settings.ini"), "custom settings");
        var newer = Zip(("app.exe", "MZnew"u8.ToArray()), ("settings.ini", "new default"u8.ToArray()));
        SetRemote(Catalog(2, Tool(newer, "2.0")), newer);
        await manager.RefreshAsync();
        Assert.False((await manager.UpdateAsync("sample-tool")).Success);
        var packageRequests = _transport.Requests.Count(u => u.AbsolutePath.EndsWith(".zip"));

        var restarted = Create();
        await restarted.UpdateManagedAsync();
        Assert.Equal(packageRequests, _transport.Requests.Count(u => u.AbsolutePath.EndsWith(".zip")));
        Assert.False((await restarted.UpdateAsync("sample-tool")).Success);
        Assert.Equal(packageRequests + 1, _transport.Requests.Count(u => u.AbsolutePath.EndsWith(".zip")));
        var third = Zip(("app.exe", "MZthird"u8.ToArray()), ("settings.ini", "third default"u8.ToArray()));
        SetRemote(Catalog(3, Tool(third, "3.0")), third);
        await restarted.RefreshAsync();
        await restarted.UpdateManagedAsync();
        Assert.Equal(packageRequests + 2, _transport.Requests.Count(u => u.AbsolutePath.EndsWith(".zip")));
        Assert.Equal("MZold", File.ReadAllText(Path.Combine(dir, "app.exe")));
        Assert.Equal("custom settings", File.ReadAllText(Path.Combine(dir, "settings.ini")));
    }

    [Fact]
    public async Task LargePackageBaseline_StaysReadableBeyondTheOld32KiBLimitAndCanUpdate()
    {
        var assets = Enumerable.Range(0, 400).Select(i =>
            (Name: "assets/" + new string('a', 55) + i + ".txt", Content: "asset"u8.ToArray())).ToArray();
        var old = Zip(assets.Append(("app.exe", "MZold"u8.ToArray())).ToArray());
        var first = Catalog(1, Tool(old));
        SeedCatalog(first);
        SetRemote(first, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var entry = manager.GetInstalledEntryPath("sample-tool")!;
        var receiptPath = Path.Combine(Path.GetDirectoryName(entry)!, CloudToolValidation.ReceiptFile);
        Assert.True(new FileInfo(receiptPath).Length > 32768);
        Assert.True(Create().IsManaged("sample-tool"));
        var newer = Zip(("app.exe", "MZnew"u8.ToArray()));
        SetRemote(Catalog(2, Tool(newer, "2.0")), newer);
        await manager.RefreshAsync();
        Assert.True((await manager.UpdateAsync("sample-tool")).Success);
        Assert.Equal("MZnew", File.ReadAllText(entry));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("app.exe:payload")]
    [InlineData("NUL.txt")]
    [InlineData(".zxai-cloud-install.json")]
    public async Task UntrustedZipPath_IsRejectedWithoutInstallingOrEscaping(string name)
    {
        var zip = Zip(("app.exe", "MZgood"u8.ToArray()), (name, "bad"u8.ToArray()));
        var catalog = Catalog(1, Tool(zip));
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        var manager = Create();
        Assert.False((await manager.InstallAsync("sample-tool")).Success);
        Assert.False(manager.IsManaged("sample-tool"));
        Assert.False(File.Exists(Path.Combine(_root, "escape.exe")));
        Assert.False(Directory.Exists(Path.Combine(_root, "CloudTools", "Installed", "sample-tool")));
    }

    [Fact]
    public async Task DuplicateWindowsZipPaths_RejectWholePackage()
    {
        var zip = Zip(("app.exe", "MZone"u8.ToArray()), ("APP.exe", "MZtwo"u8.ToArray()));
        var catalog = Catalog(1, Tool(zip));
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        Assert.False((await Create().InstallAsync("sample-tool")).Success);
    }

    [Fact]
    public async Task MissingEntryPoint_DoesNotRegisterSuccessfulInstall()
    {
        var zip = Zip(("other.exe", "MZother"u8.ToArray()));
        var catalog = Catalog(1, Tool(zip));
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        var manager = Create();
        Assert.False((await manager.InstallAsync("sample-tool")).Success);
        Assert.False(manager.IsManaged("sample-tool"));
    }

    [Fact]
    public async Task WrongReportedSize_IsRejectedBeforeInstallation()
    {
        var zip = Zip(("app.exe", "MZtool"u8.ToArray()));
        var tool = Tool(zip);
        tool = tool with { Packages = [tool.Packages[0] with { SizeBytes = zip.Length + 1 }] };
        var catalog = Catalog(1, tool);
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        var manager = Create();
        Assert.False((await manager.InstallAsync(tool.Id)).Success);
        Assert.False(manager.IsManaged(tool.Id));
    }

    [Fact]
    public async Task ZipSymlink_IsRejectedWithoutCreatingAReparseEntry()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var entry = archive.CreateEntry("app.exe").Open()) entry.Write("MZtool"u8);
            var link = archive.CreateEntry("link.txt");
            link.ExternalAttributes = unchecked((int)0xa1ff0000);
            using var stream = link.Open();
            stream.Write("../outside"u8);
        }
        var zip = output.ToArray();
        var catalog = Catalog(1, Tool(zip));
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        Assert.False((await Create().InstallAsync("sample-tool")).Success);
    }

    [Fact]
    public async Task CancellationDuringUpdateDownload_PreservesOriginalEntryAndReceipt()
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()));
        var catalog = Catalog(1, Tool(old));
        SeedCatalog(catalog);
        SetRemote(catalog, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var newer = Zip(("app.exe", "MZnew"u8.ToArray()));
        SetRemote(Catalog(2, Tool(newer, "2.0")), newer);
        await manager.RefreshAsync();
        using var cancel = new CancellationTokenSource();
        _transport.PackageContent = bytes => new StreamContent(new CancelReadStream(bytes, cancel));
        Assert.False((await manager.UpdateAsync("sample-tool", cancel.Token)).Success);
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal("MZold", File.ReadAllText(manager.GetInstalledEntryPath("sample-tool")!));
        Assert.Equal("1.0", Assert.Single(manager.GetStates()).Version);
        Assert.True(Create().IsManaged("sample-tool"));
    }

    [Fact]
    public async Task RunningTool_BecomesPendingAndIsNotDownloadedRepeatedly()
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()));
        var first = Catalog(1, Tool(old));
        SeedCatalog(first);
        SetRemote(first, old);
        var running = false;
        var manager = Create(running: _ => running);
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var newer = Zip(("app.exe", "MZnew"u8.ToArray()));
        SetRemote(Catalog(2, Tool(newer, "2.0")), newer);
        await manager.RefreshAsync();
        running = true;
        await manager.UpdateManagedAsync();
        await manager.UpdateManagedAsync();
        var state = Assert.Single(manager.GetStates());
        Assert.Equal(CloudToolStatus.PendingUpdate, state.Status);
        Assert.Equal("1.0", state.Version);
        Assert.True(state.PendingUpdate);
        Assert.Single(_transport.Requests.Where(u => u.AbsolutePath.EndsWith(".zip")));
        Assert.Equal(CloudToolStatus.PendingUpdate, Assert.Single(Create().GetStates()).Status);
        running = false;
        Assert.True((await manager.UpdateAsync("sample-tool")).Success);
        Assert.Equal("2.0", Assert.Single(manager.GetStates()).Version);
    }

    [Fact]
    public async Task LegacyCopy_IsShownButNeverAutomaticallyUpdatedOrRemoved()
    {
        var zip = Zip(("app.exe", "MZdownload"u8.ToArray()));
        var tool = Tool(zip);
        var catalog = Catalog(1, tool);
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        var legacyDir = Path.Combine(Legacy, "系统工具", "示例工具");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "app.exe"), "MZlegacy");
        var manager = Create();
        var state = Assert.Single(manager.GetStates());
        Assert.True(state.IsInstalled);
        Assert.False(state.IsManaged);
        Assert.Empty(state.Version); // We have not verified a legacy executable's actual version.
        await manager.UpdateManagedAsync();
        Assert.Empty(_transport.Requests);
        Assert.False((await manager.RemoveAsync(tool.Id)).Success);
        Assert.True((await manager.UpdateAsync(tool.Id)).Success);
        Assert.True(Assert.Single(manager.GetStates()).IsManaged);
        Assert.Equal("MZlegacy", File.ReadAllText(Path.Combine(legacyDir, "app.exe")));
    }

    [Fact]
    public async Task UpdateManaged_OnlyUpdatesManagedCopiesAndDetectsSameVersionHashChange()
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()));
        var first = Catalog(1, Tool(old), Tool(old, id: "unused-tool"));
        SeedCatalog(first);
        SetRemote(first, old, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var newer = Zip(("app.exe", "MZnew"u8.ToArray()));
        SetRemote(Catalog(2, Tool(newer), Tool(newer, id: "unused-tool")), newer, newer);
        await manager.RefreshAsync();
        await manager.UpdateManagedAsync();
        Assert.Equal("MZnew", File.ReadAllText(manager.GetInstalledEntryPath("sample-tool")!));
        Assert.False(manager.IsManaged("unused-tool"));
        Assert.DoesNotContain(_transport.Requests, u => u.AbsolutePath.EndsWith("unused-tool.zip"));
    }

    [Fact]
    public async Task ForeignDirectory_CannotBeInstalledOverOrRemoved()
    {
        var zip = Zip(("app.exe", "MZdownload"u8.ToArray()));
        var catalog = Catalog(1, Tool(zip));
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        var foreign = Path.Combine(_root, "CloudTools", "Installed", "sample-tool");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "manual");
        var manager = Create();
        Assert.False((await manager.InstallAsync("sample-tool")).Success);
        Assert.False((await manager.RemoveAsync("sample-tool")).Success);
        Assert.Equal("manual", File.ReadAllText(Path.Combine(foreign, "keep.txt")));
        Assert.Empty(_transport.Requests);
    }

    [Fact]
    public async Task CrashAfterOldDirectoryWasMoved_RestoresReceiptAndOldVersion()
    {
        var zip = Zip(("app.exe", "MZold"u8.ToArray()));
        var catalog = Catalog(1, Tool(zip));
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        Assert.True((await Create().InstallAsync("sample-tool")).Success);
        var root = Path.Combine(_root, "CloudTools");
        var backupName = "sample-tool-" + Guid.NewGuid().ToString("N");
        var backup = Path.Combine(root, "Backups", backupName);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        Directory.Move(Path.Combine(root, "Installed", "sample-tool"), backup);
        var txDir = Path.Combine(root, "Transactions");
        Directory.CreateDirectory(txDir);
        File.WriteAllText(Path.Combine(txDir, "sample-tool.json"), JsonSerializer.Serialize(
            new CloudToolTransaction("sample-tool", "", backupName), CloudToolValidation.JsonOptions));
        var recovered = Create();
        Assert.True(recovered.IsManaged("sample-tool"));
        Assert.Equal("MZold", File.ReadAllText(recovered.GetInstalledEntryPath("sample-tool")!));
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public async Task InterruptedUpdateAfterOldMove_RecoversConfigurationSaveAndOriginalReceipt()
    {
        var old = Zip(("app.exe", "MZold"u8.ToArray()));
        var first = Catalog(1, Tool(old));
        SeedCatalog(first);
        SetRemote(first, old);
        var manager = Create();
        Assert.True((await manager.InstallAsync("sample-tool")).Success);
        var target = Path.GetDirectoryName(manager.GetInstalledEntryPath("sample-tool"))!;
        File.WriteAllText(Path.Combine(target, "user.ini"), "theme=dark");
        Directory.CreateDirectory(Path.Combine(target, "saves"));
        File.WriteAllText(Path.Combine(target, "saves", "save.db"), "user game progress");
        var originalReceipt = File.ReadAllBytes(Path.Combine(target, CloudToolValidation.ReceiptFile));
        var root = Path.Combine(_root, "CloudTools");
        var backupName = "sample-tool-" + Guid.NewGuid().ToString("N");
        var backup = Path.Combine(root, "Backups", backupName);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        Directory.Move(target, backup);
        var stageName = "sample-tool-" + Guid.NewGuid().ToString("N");
        var stage = Path.Combine(root, "Staging", stageName);
        Directory.CreateDirectory(Path.Combine(stage, "payload"));
        File.WriteAllText(Path.Combine(stage, "payload", "app.exe"), "MZunfinished-new-version");
        var journal = Path.Combine(root, "Transactions", "sample-tool.json");
        File.WriteAllText(journal, JsonSerializer.Serialize(new CloudToolTransaction("sample-tool", stageName, backupName),
            CloudToolValidation.JsonOptions));

        var recovered = Create();
        Assert.True(recovered.IsManaged("sample-tool"));
        Assert.Equal("1.0", Assert.Single(recovered.GetStates()).Version);
        Assert.Equal("MZold", File.ReadAllText(recovered.GetInstalledEntryPath("sample-tool")!));
        Assert.Equal("theme=dark", File.ReadAllText(Path.Combine(target, "user.ini")));
        Assert.Equal("user game progress", File.ReadAllText(Path.Combine(target, "saves", "save.db")));
        Assert.Equal(originalReceipt, File.ReadAllBytes(Path.Combine(target, CloudToolValidation.ReceiptFile)));
        Assert.False(Directory.Exists(backup));
        Assert.False(Directory.Exists(stage));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task OlderOrInvalidRemoteCatalog_PreservesCacheAndBundledCards()
    {
        var zip = Zip(("app.exe", "MZold"u8.ToArray()));
        SeedCatalog(Catalog(5, Tool(zip)));
        var manager = Create();
        SetRemote(Catalog(4));
        await manager.RefreshAsync();
        Assert.Equal(5, manager.Revision);
        Assert.Single(manager.GetCatalog());
        Assert.NotNull(manager.LastRefreshError);
        _transport.Manifest = "{broken";
        await manager.RefreshAsync();
        Assert.Single(manager.GetCatalog());
        Assert.NotNull(manager.LastRefreshError);
    }

    [Fact]
    public async Task CatalogEtag_UsesConditionalRequestAndDoesNotRewriteOn304()
    {
        var zip = Zip(("app.exe", "MZold"u8.ToArray()));
        SeedCatalog(Catalog(1, Tool(zip)));
        SetRemote(Catalog(2, Tool(zip)), zip);
        _transport.Etag = "\"revision-2\"";
        var manager = Create();
        await manager.RefreshAsync();
        var cache = Path.Combine(_root, "CloudTools", "catalog.json");
        var sentinel = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(cache, sentinel);
        var savedTime = File.GetLastWriteTimeUtc(cache);
        _transport.NotModified = true;
        await manager.RefreshAsync();
        Assert.Equal("\"revision-2\"", _transport.LastIfNoneMatch);
        Assert.Null(manager.LastRefreshError);
        Assert.Equal(2, manager.Revision);
        Assert.Equal(savedTime, File.GetLastWriteTimeUtc(cache));
    }

    [Fact]
    public async Task Unsolicited304WithoutValidCatalog_IsARefreshFailure()
    {
        _transport.NotModified = true;
        var manager = Create();
        await manager.RefreshAsync();
        Assert.Empty(manager.GetCatalog());
        Assert.NotNull(manager.LastRefreshError);
        Assert.False(File.Exists(Path.Combine(_root, "CloudTools", "catalog.json")));
    }

    [Fact]
    public void CorruptedCache_FallsBackToValidBundledCatalog()
    {
        var zip = Zip(("app.exe", "MZold"u8.ToArray()));
        SeedCatalog(Catalog(1, Tool(zip)));
        Directory.CreateDirectory(Path.Combine(_root, "CloudTools"));
        File.WriteAllText(Path.Combine(_root, "CloudTools", "catalog.json"), "{bad}");
        Assert.Single(Create().GetCatalog());
    }

    [Fact]
    public async Task NoPortablePackage_ExposesOfficialSiteWithoutDownloading()
    {
        var tool = new CloudToolDefinition { Id = "site-tool", Name = "官网工具", Category = "系统工具",
            Version = "1", Homepage = "https://example.com/" };
        SeedCatalog(Catalog(1, tool));
        var manager = Create();
        var state = Assert.Single(manager.GetStates());
        Assert.Equal(CloudToolStatus.Unsupported, state.Status);
        Assert.Contains("来源网页", state.Error);
        Assert.False((await manager.InstallAsync(tool.Id)).Success);
        Assert.Empty(_transport.Requests);
    }

    [Fact]
    public async Task WrongArchitecture_DoesNotDownloadAnIncompatibleExecutable()
    {
        var zip = Zip(("app.exe", "MZold"u8.ToArray()));
        SeedCatalog(Catalog(1, Tool(zip)));
        Assert.False((await Create(architecture: "arm64").InstallAsync("sample-tool")).Success);
        Assert.Empty(_transport.Requests);
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public async Task CompatibleX86Package_IsInstalledOn64BitWindows(string architecture)
    {
        var zip = Zip(("app.exe", "MZx86"u8.ToArray()));
        var tool = Tool(zip);
        tool = tool with { Packages = [tool.Packages[0] with { Architecture = "x86" }] };
        var catalog = Catalog(1, tool);
        SeedCatalog(catalog);
        SetRemote(catalog, zip);
        var manager = Create(architecture: architecture);
        Assert.True((await manager.InstallAsync(tool.Id)).Success);
        Assert.True(Assert.Single(manager.GetStates()).IsManaged);
        Assert.Equal("MZx86", File.ReadAllText(manager.GetInstalledEntryPath(tool.Id)!));
    }

    public void Dispose()
    {
        _http.Dispose();
        // This fixture owns this exact fresh temporary root; it contains no real settings/data.
        CloudToolValidation.DeleteTree(_root);
    }

    private sealed class FakeTransport : HttpMessageHandler
    {
        internal string Manifest { get; set; } = "";
        internal string? Etag { get; set; }
        internal bool NotModified { get; set; }
        internal string? LastIfNoneMatch { get; private set; }
        internal Func<byte[], HttpContent>? PackageContent { get; set; }
        internal Dictionary<string, byte[]> Packages { get; } = new(StringComparer.Ordinal);
        internal List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            if (request.RequestUri == Endpoint)
            {
                LastIfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
                if (NotModified) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)
                    { RequestMessage = request });
            }
            HttpContent content = request.RequestUri == Endpoint
                ? new StringContent(Manifest, Encoding.UTF8, "application/json")
                : PackageContent?.Invoke(Packages[request.RequestUri!.AbsoluteUri]) ??
                  new ByteArrayContent(Packages[request.RequestUri!.AbsoluteUri]);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
            if (request.RequestUri == Endpoint && Etag is not null)
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(Etag);
            return Task.FromResult(response);
        }
    }

    private sealed class CancelReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position > 0) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 8)], ct);
        }
    }
}
