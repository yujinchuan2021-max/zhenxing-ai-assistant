using System.Diagnostics;
using System.Security.Cryptography;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Services;

/// <summary>Direct downloads commit only after content validation; a failed retry keeps the previous file.</summary>
internal static class StagedWindowsDownload
{
    internal static async Task<string> DownloadAsync(string url, string destinationDirectory, string fileName,
        IProgress<ToolDownloadProgress>? progress = null, CancellationToken ct = default,
        long expectedSize = 0, string? architecture = null, HttpClient? transport = null, string? sha256 = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source) || source.Scheme != "https" || source.UserInfo.Length != 0)
            throw new InvalidDataException("安装包下载来源无效。");
        if (!CloudToolValidation.IsRelativePath(fileName) || fileName.Contains('/') || fileName.Contains('\\'))
            throw new InvalidDataException("安装包文件名无效。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        ct = timeout.Token;
        if (DataRoots.EffectiveTestRoot is not null && transport is null)
            throw new InvalidOperationException("隔离验证不会连接真实安装包来源。");
        if (!DataRoots.IsIsolationTargetPhysicallySafe(destinationDirectory, out var reason))
            throw new InvalidDataException($"安装包保存位置不安全：{reason}");
        CloudToolValidation.CheckNoReparse(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var target = CloudToolValidation.Under(destinationDirectory, fileName);
        var stage = CloudToolValidation.Under(destinationDirectory, ".zxai-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var downloaded = CloudToolValidation.Under(stage, fileName);
        using var ownedClient = transport is null ? HttpClientFactory.CreateIpv4Preferred(TimeSpan.FromMinutes(20)) : null;
        var client = transport ?? ownedClient!;
        try
        {
            using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (sha256 is not null && response.RequestMessage?.RequestUri is { } final && final != source)
                throw new InvalidDataException("国内安装包不接受重定向，原有文件已保留。");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"安装包来源暂不可用（HTTP {(int)response.StatusCode}），原有文件已保留。", null, response.StatusCode);
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not null && (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("html", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("下载来源返回了网页或错误信息，原有文件已保留。");
            var length = response.Content.Headers.ContentLength;
            if (length is > WindowsDownloadValidation.MaxFileBytes || (expectedSize > 0 && length is not null && length != expectedSize))
                throw new InvalidDataException("下载文件大小与声明不一致，原有文件已保留。");
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(downloaded, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                var clock = Stopwatch.StartNew();
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > WindowsDownloadValidation.MaxFileBytes || (expectedSize > 0 && total > expectedSize))
                        throw new InvalidDataException("下载内容超出声明大小，原有文件已保留。");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    var maximum = expectedSize > 0 ? expectedSize : length ?? 0;
                    progress?.Report(new(total, maximum, maximum > 0 ? total * 100d / maximum : 0,
                        total * 8d / Math.Max(clock.Elapsed.TotalSeconds, .001) / 1_000_000, null));
                }
                if (length is not null && total != length)
                    throw new InvalidDataException("安装包下载不完整，原有文件已保留。");
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            WindowsDownloadValidation.Validate(downloaded, expectedSize, architecture);
            if (sha256 is not null)
            {
                if (!CloudToolValidation.IsSha256(sha256)) throw new InvalidDataException("安装包校验信息无效。");
                await using var input = File.OpenRead(downloaded);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包 SHA-256 校验失败，原有文件已保留，尚未运行安装程序。");
            }
            ct.ThrowIfCancellationRequested();
            CloudToolValidation.CheckNoReparse(target);
            File.Move(downloaded, target, overwrite: true);
            return target;
        }
        finally
        {
            try { CloudToolValidation.DeleteTree(stage); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
