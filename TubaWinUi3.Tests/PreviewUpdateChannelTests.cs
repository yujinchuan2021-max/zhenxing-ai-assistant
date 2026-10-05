using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>Offline preview update checks: no feed, package, account, browser or native UI requests.</summary>
public class PreviewUpdateChannelTests
{
    private static string Manifest(string version, string channel = "preview", string? label = "0.1.1 公开预览版",
        string? notes = "https://zhenxingai.com/updates/0.1.1.html", long size = 7)
        => JsonSerializer.Serialize(new
        {
            channel, version, releaseLabel = label, notesUrl = notes,
            publishedAt = "2026-10-05T01:00:00Z",
            package = new
            {
                architecture = UpdateService.CurrentArchitecture, type = "portable-zip",
                url = "https://zhenxingai.com/downloads/ZhenxingAI-v0.1.1-preview-20261005.zip",
                sizeBytes = size, sha256 = new string('a', 64)
            }
        });

    [Fact]
    public void ClientHasDistinctPreviewIdentity()
    {
        Assert.Equal("https://zhenxingai.com/updates/preview.json", UpdateService.OwnUpdateManifestUrl);
        Assert.Equal("preview", UpdateService.OwnChannelName);
        Assert.Equal(new Version(0, 1, 1, 0), UpdateService.CurrentVersion);
        Assert.Equal("0.1.1.0", typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.Equal("0.1.1-preview", typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        Assert.False(UpdateService.UpstreamUpdatesEnabled);
    }

    [Theory]
    [InlineData("0.1.1", "0.1.0", UpdateCheckStatus.UpdateAvailable)]
    [InlineData("0.1.1", "0.1.0.99", UpdateCheckStatus.UpdateAvailable)]
    [InlineData("0.1.1", "0.1.1", UpdateCheckStatus.UpToDate)]
    [InlineData("0.1.1.0", "0.1.1", UpdateCheckStatus.UpToDate)]
    [InlineData("0.1.1", "0.1.1.0", UpdateCheckStatus.UpToDate)]
    [InlineData("0.1.1.1", "0.1.1", UpdateCheckStatus.UpdateAvailable)]
    [InlineData("0.1.1.1", "0.1.1.1", UpdateCheckStatus.UpToDate)]
    [InlineData("0.1.1.0", "0.1.1.1", UpdateCheckStatus.UpToDate)]
    [InlineData("0.1.0.99", "0.1.1", UpdateCheckStatus.UpToDate)]
    public void VersionComparison_DoesNotRepeatEquivalentRelease_AndKeepsRevision(
        string remote, string current, UpdateCheckStatus expected)
    {
        var result = UpdateService.ParseOwnManifest(Manifest(remote), Version.Parse(current));
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == UpdateCheckStatus.UpdateAvailable, result.Update is not null);
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("0.1.1.0.1")]
    [InlineData("v0.1.1")]
    [InlineData("0.1.1-preview")]
    [InlineData("0.1.-1")]
    [InlineData("0.1.+1")]
    [InlineData("0.1. 1")]
    public void NonNumericOrIncompleteReleaseVersionsAreRejected(string version)
        => Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest(Manifest(version), new Version(0, 1, 0)).Status);

    [Theory]
    [InlineData("stable")]
    [InlineData("beta")]
    [InlineData("")]
    public void OtherChannelsCannotBecomePreviewUpdates(string channel)
        => Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest(Manifest("0.1.1", channel), new Version(0, 1, 0)).Status);

    [Fact]
    public void PreviewMetadata_AndOwnNotesUrlSurviveParsing()
    {
        var update = UpdateService.ParseOwnManifest(Manifest("0.1.1"), new Version(0, 1, 0)).Update!;
        Assert.Equal("preview", update.ReleaseChannel);
        Assert.Equal("0.1.1 公开预览版", update.ReleaseLabel);
        Assert.Equal(update.ReleaseLabel, UpdateService.GetReleaseDisplayName(update));
        Assert.Equal("https://zhenxingai.com/updates/0.1.1.html", UpdateService.GetUpdateNotesUrl(update));
    }

    [Theory]
    [InlineData("https://github.com/yujinchuan2021-max/zhenxing-ai-assistant/releases")]
    [InlineData("http://zhenxingai.com/updates/0.1.1.html")]
    [InlineData("https://zhenxingai.com.evil.example/r10-1.html")]
    [InlineData("https://name:password@zhenxingai.com/r10-1.html")]
    public void ForeignOrUnsafeNotesUseOfficialDownloadFallback(string notes)
    {
        var update = UpdateService.ParseOwnManifest(Manifest("0.1.1", notes: notes), new Version(0, 1, 0)).Update!;
        Assert.Equal(UpdateService.OwnDownloadPageUrl, UpdateService.GetUpdateNotesUrl(update));
        Assert.Equal(UpdateService.OwnDownloadPageUrl, UpdateService.GetUpdateNotesUrl(UpdateWithAsset(notes: notes)));
    }

    [Fact]
    public void MissingChannelAndPackage_DoNotPretendCurrentVersionIsLatest()
    {
        Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest("{\"version\":\"0.1.1\"}", new Version(0, 1, 0)).Status);
        Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest("{\"channel\":\"preview\",\"version\":\"0.1.1\"}", new Version(0, 1, 0)).Status);
        Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest("{\"channel\":\"preview\",\"version\":\"0.1.1\"}", new Version(0, 1, 1)).Status);
    }

    [Fact]
    public void SameVersionWithInvalidPackage_DoesNotReportUpToDate()
    {
        Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest(Manifest("0.1.1", size: 0), new Version(0, 1, 1)).Status);
        var foreign = Manifest("0.1.1").Replace("https://zhenxingai.com/downloads/", "https://example.com/downloads/");
        Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest(foreign, new Version(0, 1, 1)).Status);
    }

    private static string MutatePackage(string field, JsonNode? value, bool remove = false)
    {
        var manifest = JsonNode.Parse(Manifest("0.1.1"))!.AsObject();
        var package = manifest["package"]!.AsObject();
        if (remove) package.Remove(field);
        else package[field] = value;
        return manifest.ToJsonString();
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.1.1")]
    [InlineData("0.1.2")]
    public void EmptyPackage_IsFailedForNewSameAndOlderVersions(string current)
    {
        var manifest = JsonNode.Parse(Manifest("0.1.1"))!.AsObject();
        manifest["package"] = new JsonObject();
        var result = UpdateService.ParseOwnManifest(manifest.ToJsonString(), Version.Parse(current));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Null(result.Update);
    }

    [Theory]
    [InlineData("architecture")]
    [InlineData("type")]
    public void MissingBlankNullOrNonStringPackageIdentity_IsAlwaysFailed(string field)
    {
        var invalid = new[]
        {
            MutatePackage(field, null, remove: true),
            MutatePackage(field, JsonValue.Create("")),
            MutatePackage(field, JsonValue.Create("  ")),
            MutatePackage(field, null),
            MutatePackage(field, JsonValue.Create(42))
        };
        foreach (var current in new[] { new Version(0, 1, 0), new Version(0, 1, 1), new Version(0, 1, 2) })
            foreach (var json in invalid)
                Assert.Equal(UpdateCheckStatus.Failed, UpdateService.ParseOwnManifest(json, current).Status);
    }

    [Theory]
    [InlineData("architecture", "mips")]
    [InlineData("type", "arbitrary-installer")]
    public void UnknownPackageIdentity_IsNotTreatedAsUnsupportedButValid(string field, string value)
        => Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest(MutatePackage(field, JsonValue.Create(value)), new Version(0, 1, 1)).Status);

    [Theory]
    [InlineData("url", "https://example.com/downloads/test.zip")]
    [InlineData("url", "http://zhenxingai.com/downloads/test.zip")]
    [InlineData("url", "https://zhenxingai.com/downloads/test.exe")]
    [InlineData("sha256", "not-a-hash")]
    public void ForeignArchitecture_DoesNotBypassPackageValidation(string field, string value)
    {
        var manifest = JsonNode.Parse(MutatePackage(field, JsonValue.Create(value)))!.AsObject();
        manifest["package"]!["architecture"] = UpdateService.CurrentArchitecture == "arm64" ? "x64" : "arm64";
        foreach (var current in new[] { new Version(0, 1, 0), new Version(0, 1, 1), new Version(0, 1, 2) })
            Assert.Equal(UpdateCheckStatus.Failed, UpdateService.ParseOwnManifest(manifest.ToJsonString(), current).Status);
    }

    [Fact]
    public void KnownForeignPlatformAndInstallerForm_AreManualOnlyAfterCompleteValidation()
    {
        var manifest = JsonNode.Parse(Manifest("0.1.1"))!.AsObject();
        manifest["package"]!["architecture"] = UpdateService.CurrentArchitecture == "arm64" ? "x64" : "arm64";
        var foreign = UpdateService.ParseOwnManifest(manifest.ToJsonString(), new Version(0, 1, 0));
        Assert.Equal(UpdateCheckStatus.ManualDownload, foreign.Status);
        Assert.Empty(foreign.Update!.Assets);

        manifest["package"]!["type"] = "installer-exe";
        manifest["package"]!["url"] = "https://zhenxingai.com/downloads/test.exe";
        var installer = UpdateService.ParseOwnManifest(manifest.ToJsonString(), new Version(0, 1, 0));
        Assert.Equal(UpdateCheckStatus.ManualDownload, installer.Status);
        Assert.Empty(installer.Update!.Assets);
        Assert.Equal(UpdateCheckStatus.UpToDate,
            UpdateService.ParseOwnManifest(manifest.ToJsonString(), new Version(0, 1, 1)).Status);

        foreach (var url in new[] { "https://example.com/test.exe", "https://zhenxingai.com/downloads/test.zip" })
        {
            manifest["package"]!["url"] = url;
            Assert.Equal(UpdateCheckStatus.Failed,
                UpdateService.ParseOwnManifest(manifest.ToJsonString(), new Version(0, 1, 1)).Status);
        }
        manifest["package"]!["url"] = "https://zhenxingai.com/downloads/test.exe";
        manifest["package"]!["sizeBytes"] = 0;
        Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest(manifest.ToJsonString(), new Version(0, 1, 0)).Status);
        manifest["package"]!["sizeBytes"] = 7;
        manifest["package"]!["sha256"] = "bad";
        Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest(manifest.ToJsonString(), new Version(0, 1, 1)).Status);
    }

    [Fact]
    public void ControlCharactersInReleaseLabel_AreRejected()
        => Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest(Manifest("0.1.1", label: "0.1.1\nStable"), new Version(0, 1, 0)).Status);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MissingPositivePackageSize_IsRejected(long size)
        => Assert.Equal(UpdateCheckStatus.Failed,
            UpdateService.ParseOwnManifest(Manifest("0.1.1", size: size), new Version(0, 1, 0)).Status);

    private static UpdateInfo UpdateWithAsset(string url = "https://zhenxingai.com/downloads/test.zip",
        string name = "test.zip", long size = 7, string notes = "https://zhenxingai.com/download")
        => new()
        {
            Version = "0.1.1", ReleaseChannel = "preview", HtmlUrl = notes,
            PublishedAt = DateTimeOffset.UtcNow,
            Assets = [new() { Name = name, BrowserDownloadUrl = url, Size = size, Sha256 = new string('a', 64) }]
        };

    [Theory]
    [InlineData("https://example.com/downloads/test.zip", "test.zip", 7)]
    [InlineData("http://zhenxingai.com/downloads/test.zip", "test.zip", 7)]
    [InlineData("https://zhenxingai.com/downloads/test.zip", "different.zip", 7)]
    [InlineData("https://zhenxingai.com/downloads/test.exe", "test.exe", 7)]
    [InlineData("https://zhenxingai.com/downloads/test.zip", "test.zip", 0)]
    public void DownloadEntry_RejectsUnboundAssetsBeforeQueueing(string url, string name, long size)
        => Assert.Null(UpdateService.AutoDownloadUpdate(UpdateWithAsset(url, name, size)));

    [Theory]
    [InlineData("0.1.1", "0.1.1.0", true)]
    [InlineData("0.1.1.1", "0.1.1.0", false)]
    [InlineData("0.1.1", null, false)]
    [InlineData("0.1.0.99", "0.1.1", false)]
    public void SkippedVersionComparison_UsesTheSameNormalization(string left, string? right, bool equal)
        => Assert.Equal(equal, UpdateService.IsSameVersion(left, right));

    [Fact]
    public async Task VerifiedRecord_RecognizesEquivalentVersion_ButStillChecksFileHash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zxai-preview-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = UpdateService.TempDirOverrideForTest;
        UpdateService.TempDirOverrideForTest = dir;
        try
        {
            var bytes = new byte[] { 0x50, 0x4b, 3, 4, 1, 2, 3 };
            var path = Path.Combine(dir, "test.zip");
            File.WriteAllBytes(path, bytes);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            UpdateInfo Info(string version) => new()
            {
                Version = version, ReleaseChannel = "preview", HtmlUrl = UpdateService.OwnDownloadPageUrl,
                PublishedAt = DateTimeOffset.UtcNow,
                Assets = [new() { Name = "test.zip", BrowserDownloadUrl = "https://zhenxingai.com/downloads/test.zip", Size = bytes.Length, Sha256 = sha }]
            };
            UpdateService.WriteVerifiedUpdateRecord(Info("0.1.1"), path);
            Assert.True(await UpdateService.IsUpdateReadyAsync(Info("0.1.1.0")));
            Assert.False(await UpdateService.IsUpdateReadyAsync(Info("0.1.1.1")));
            File.WriteAllBytes(path, [0x50, 0x4b, 3, 4, 9, 9, 9]);
            Assert.False(await UpdateService.IsUpdateReadyAsync(Info("0.1.1.0")));
            Assert.False(await UpdateService.VerifyFileAsync(path, 0, sha));
        }
        finally
        {
            UpdateService.TempDirOverrideForTest = previous;
            Directory.Delete(dir, true);
        }
    }
}
