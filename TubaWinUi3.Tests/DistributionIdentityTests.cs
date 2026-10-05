using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【A15】发行身份防回归：本产品是独立定制发行（枕星图吧AI助手）。
/// ① 上游（luolangaga/tubatool）程序更新闸门永久关闭（ToolsBundle 与单工具更新闸门各自独立、保持关闭）；
/// ② 程序更新只走自有通道（https://zhenxingai.com/updates/stable.json），只接受本域 HTTPS x64 便携 ZIP，
///    清单无效或网络失败必须报“检查失败”，不得谎报“已是最新版本”；
/// ③ 下载完成后按清单 size + SHA-256 校验；就绪判断绑定当前清单（文件名/大小/SHA-256）并在提示前重算哈希，
///    未验证的历史残留、同大小换包与失败残留记录都不算“更新就绪”；
/// 自有通道全部用离线可注入清单测试，不发起真实网络请求。
/// </summary>
public class DistributionIdentityTests
{
    [Fact]
    public void UpstreamUpdates_AreLocked_ForIndependentDistribution()
    {
        Assert.False(UpdateService.UpstreamUpdatesEnabled,
            "独立发行不得放开上游更新闸门——详见 UpdateService.UpstreamUpdatesEnabled 注释（A15）。");
    }

    private static string NewerVersion => $"{UpdateService.CurrentVersion.Major + 1}.0.0";

    private static string Manifest(
        string version,
        string? url = null,
        string? sha = null,
        long size = 123456,
        string arch = "x64",
        string type = "portable-zip",
        string channel = "stable")
    {
        var zipName = $"TubaWinUi3-v{version}-portable.zip";
        return $$"""
        {
          "channel": "{{channel}}",
          "version": "{{version}}",
          "publishedAt": "2026-09-25T00:00:00Z",
          "notesUrl": "https://zhenxingai.com/zxai/changelog.html",
          "package": {
            "architecture": "{{arch}}",
            "type": "{{type}}",
            "url": "{{url ?? $"https://zhenxingai.com/downloads/{zipName}"}}",
            "sizeBytes": {{size}},
            "sha256": "{{sha ?? new string('a', 64)}}"
          }
        }
        """;
    }

    private static Task<UpdateCheckResult> CheckAsync(string json)
        => UpdateService.CheckOwnChannelAsync(_ => Task.FromResult<string?>(json));

    [Fact]
    public async Task OwnChannel_SameVersion_ReportsUpToDate()
    {
        var result = await CheckAsync(Manifest(UpdateService.CurrentVersion.ToString()));

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task OwnChannel_NewerVersion_ReportsUpdateWithValidatedPackage()
    {
        var sha = new string('b', 64);
        var result = await CheckAsync(Manifest(NewerVersion, sha: sha, size: 710781926));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        var update = result.Update!;
        Assert.Equal(NewerVersion, update.Version);

        var asset = Assert.Single(update.Assets);
        Assert.Equal($"TubaWinUi3-v{NewerVersion}-portable.zip", asset.Name);
        Assert.Equal("https://zhenxingai.com/downloads/" + asset.Name, asset.BrowserDownloadUrl);
        Assert.Equal(710781926, asset.Size);
        Assert.Equal(sha, asset.Sha256);
    }

    [Fact]
    public async Task OwnChannel_InvalidOrForeignUrl_IsRejected_NotUpToDate()
    {
        // http / 外域 / 非 zip / 路径穿越 / 非默认端口：一律“检查失败”，不得当成更新或“已是最新”
        foreach (var bad in new[]
        {
            "http://zhenxingai.com/downloads/TubaWinUi3.zip",
            "https://example.com/downloads/TubaWinUi3.zip",
            "https://zhenxingai.com/downloads/TubaWinUi3.exe",
            "https://zhenxingai.com/downloads/..%2Fevil.zip",
            "https://zhenxingai.com:8443/downloads/TubaWinUi3.zip"
        })
        {
            var result = await CheckAsync(Manifest(NewerVersion, url: bad));
            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
            Assert.Null(result.Update);
        }
    }

    [Fact]
    public async Task OwnChannel_InvalidHash_IsRejected()
    {
        foreach (var bad in new[] { "not-a-sha", new string('a', 63), new string('z', 64), string.Empty })
        {
            var result = await CheckAsync(Manifest(NewerVersion, sha: bad));
            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        }
    }

    [Fact]
    public async Task OwnChannel_ArchOrTypeMismatch_AsksManualDownload()
    {
        var arch = await CheckAsync(Manifest(NewerVersion, arch: "arm64"));
        Assert.Equal(UpdateCheckStatus.ManualDownload, arch.Status);
        Assert.NotNull(arch.Update);
        Assert.Empty(arch.Update!.Assets);   // 不给出无法工作的自动下载按钮

        var type = await CheckAsync(Manifest(NewerVersion, type: "installer-exe"));
        Assert.Equal(UpdateCheckStatus.ManualDownload, type.Status);
    }

    [Fact]
    public async Task OwnChannel_FetchFailure_ReportsFailed_NotUpToDate()
    {
        var result = await UpdateService.CheckOwnChannelAsync(_ => Task.FromResult<string?>(null));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Null(result.Update);

        var throwing = await UpdateService.CheckOwnChannelAsync(
            _ => throw new HttpRequestException("net down"));
        Assert.Equal(UpdateCheckStatus.Failed, throwing.Status);
    }

    [Fact]
    public async Task VerifyFile_ChecksSizeAndSha256()
    {
        var path = Path.Combine(Path.GetTempPath(), "zxai-verify-" + Guid.NewGuid().ToString("N") + ".zip");
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 };
        File.WriteAllBytes(path, bytes);
        try
        {
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.True(await UpdateService.VerifyFileAsync(path, bytes.Length, sha));
            Assert.False(await UpdateService.VerifyFileAsync(path, bytes.Length + 1, sha));             // 大小不符
            Assert.False(await UpdateService.VerifyFileAsync(path, bytes.Length, new string('c', 64))); // 哈希不符
            Assert.False(await UpdateService.VerifyFileAsync(path, bytes.Length, "not-a-sha"));          // 非法哈希
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdateReadiness_IsBoundToCurrentManifest_AndRehashesFile()
    {
        // 就绪判断必须绑定当前清单（文件名/大小/SHA-256），提示前重算哈希：
        // 历史残留、同大小换包、同版本换包、失败残留记录都不得被当作“已校验”。
        var dir = Path.Combine(Path.GetTempPath(), "zxai-update-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prev = UpdateService.TempDirOverrideForTest;
        UpdateService.TempDirOverrideForTest = dir;
        try
        {
            var zipName = "TubaWinUi3-v9.9.9-portable.zip";
            var filePath = Path.Combine(dir, zipName);
            var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4 };
            File.WriteAllBytes(filePath, bytes);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            UpdateInfo Manifest(long size, string sha256) => new()
            {
                Version = "9.9.9",
                HtmlUrl = "https://zhenxingai.com/zxai/",
                PublishedAt = DateTimeOffset.UtcNow,
                Assets =
                [
                    new UpdateAsset
                    {
                        Name = zipName,
                        BrowserDownloadUrl = "https://zhenxingai.com/downloads/" + zipName,
                        Size = size,
                        Sha256 = sha256
                    }
                ]
            };

            var manifest = Manifest(bytes.Length, sha);

            // ① 只有残留文件、没有验证记录：不算就绪
            Assert.False(UpdateService.IsUpdateAlreadyDownloaded(manifest));
            Assert.False(await UpdateService.IsUpdateReadyAsync(manifest));

            // ② 通过大小 + SHA-256 校验并落记录后：就绪
            UpdateService.WriteVerifiedUpdateRecord(manifest, filePath);
            Assert.True(UpdateService.IsUpdateAlreadyDownloaded(manifest));
            Assert.True(await UpdateService.IsUpdateReadyAsync(manifest));

            // ③ 同大小换包（长度不变、内容被替换）：异步全检重算哈希必须识破
            //   （同步快检只看文件名/大小，因此“提示已校验”必须走 IsUpdateReadyAsync）
            File.WriteAllBytes(filePath, new byte[] { 0x50, 0x4B, 0x03, 0x04, 9, 9, 9, 9 });
            Assert.True(UpdateService.IsUpdateAlreadyDownloaded(manifest));
            Assert.False(await UpdateService.IsUpdateReadyAsync(manifest));

            // ④ 文件恢复原样后：记录绑定当前清单仍成立 → 就绪
            File.WriteAllBytes(filePath, bytes);
            Assert.True(await UpdateService.IsUpdateReadyAsync(manifest));

            // ⑤ 同版本换包后的新清单（大小/SHA 变了）：旧记录绑定不上，不得显示“已校验”
            Assert.False(UpdateService.IsUpdateAlreadyDownloaded(Manifest(bytes.Length + 10, sha)));
            Assert.False(UpdateService.IsUpdateAlreadyDownloaded(Manifest(bytes.Length, new string('d', 64))));
            Assert.False(await UpdateService.IsUpdateReadyAsync(Manifest(bytes.Length, new string('d', 64))));

            // ⑥ 下载失败清理同名旧记录后：不再可能被误认
            UpdateService.InvalidateVerifiedUpdateRecordFor(zipName);
            Assert.False(UpdateService.IsUpdateAlreadyDownloaded(manifest));
            Assert.False(await UpdateService.IsUpdateReadyAsync(manifest));

            // ⑦ 非便携 ZIP 资产（.exe 条目）一律不就绪
            var exeManifest = new UpdateInfo
            {
                Version = "9.9.9",
                HtmlUrl = string.Empty,
                PublishedAt = DateTimeOffset.UtcNow,
                Assets =
                [
                    new UpdateAsset
                    {
                        Name = "setup.exe",
                        BrowserDownloadUrl = "https://zhenxingai.com/downloads/setup.exe",
                        Size = bytes.Length,
                        Sha256 = sha
                    }
                ]
            };
            Assert.False(UpdateService.IsUpdateAlreadyDownloaded(exeManifest));
            Assert.False(await UpdateService.IsUpdateReadyAsync(exeManifest));
        }
        finally
        {
            UpdateService.TempDirOverrideForTest = prev;
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
