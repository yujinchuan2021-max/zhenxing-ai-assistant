using System.IO.Compression;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TubaWinUi3.Models;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Services;

/// <summary>Compatibility installer for explicitly selected upstream community packages.</summary>
internal static class CommunityPackageInstaller
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallLocks = new(StringComparer.OrdinalIgnoreCase);
    internal static Func<string, string, string, IProgress<ToolDownloadProgress>?, CancellationToken, Task<string>>?
        DownloadOverrideForTests { get; set; }

    internal static Task<string> DownloadAsync(string url, string directory, string fileName,
        IProgress<ToolDownloadProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName ||
            !CloudToolValidation.IsRelativePath(fileName))
            throw new InvalidDataException("下载文件名无效。");
        if (DataRoots.EffectiveTestRoot is not null && DownloadOverrideForTests is null)
            throw new InvalidOperationException("隔离验证不会连接真实社区下载源。");
        return DownloadOverrideForTests is { } download
            ? download(url, directory, fileName, progress, ct)
            : ToolDownloaderService.DownloadToFileAsync(url, directory, fileName, progress, ct);
    }

    internal static async Task<string> InstallAsync(CommunityTool tool, string? source,
        IProgress<ToolDownloadProgress>? progress, CancellationToken ct)
    {
        var toolsRoot = ToolCatalog.WritableToolsRoot ?? throw new InvalidOperationException("无法找到工具目录。");
        foreach (var segment in new[] { tool.Category, tool.Id })
            if (!CloudToolValidation.IsRelativePath(segment) || segment.Contains('/') || segment.Contains('\\'))
                throw new InvalidDataException("工具安装目录无效。");
        var parent = CloudToolValidation.Under(toolsRoot, tool.Category);
        var target = CloudToolValidation.Under(parent, tool.Id);
        Directory.CreateDirectory(parent);
        CloudToolValidation.CheckNoReparse(parent);
        var workspace = CloudToolValidation.Under(parent, ".zxai-community-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(workspace, "stage");
        var backup = Path.Combine(workspace, "previous");
        var installLock = InstallLocks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));
        await installLock.WaitAsync(ct).ConfigureAwait(false);
        var movedPrevious = false;
        var safeToClean = true;
        try
        {
            var sources = new List<string?>();
            if (!string.IsNullOrWhiteSpace(source)) sources.Add(source);
            // Use only the sources explicitly described by this plugin, never guessed proxies.
            foreach (var candidate in CommunityToolService.GetAllDownloadUrls(tool))
                if (!sources.Contains(candidate.Url, StringComparer.Ordinal)) sources.Add(candidate.Url);
            if (sources.Count == 0) sources.Add(null);
            Exception? lastFailure = null;
            for (var index = 0; index < sources.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                stage = Path.Combine(workspace, "stage-" + index);
                Directory.CreateDirectory(stage);
                try
                {
                    await CommunityToolService.DownloadLegacyPackageAsync(tool, sources[index], stage,
                        Path.Combine(workspace, "download-" + index), progress, ct).ConfigureAwait(false);
                    ValidateEntrance(stage, tool.LaunchTarget);
                    CloudToolValidation.CheckTree(stage);
                    lastFailure = null;
                    break;
                }
                catch (Exception ex) when (ex is InvalidDataException or HttpRequestException)
                {
                    lastFailure = ex;
                }
            }
            if (lastFailure is not null)
            {
                var reason = lastFailure.Message.Any(c => c > 127) ? lastFailure.Message : "来源失效或工具包损坏。";
                throw new InvalidDataException($"已尝试 {sources.Count} 个社区来源，未取得有效工具包；原有工具已保留。{reason}", lastFailure);
            }
            ct.ThrowIfCancellationRequested();
            CloudToolValidation.CheckNoReparse(target);
            if (Directory.Exists(target))
            {
                CloudToolValidation.CheckTree(target);
                Directory.Move(target, backup);
                movedPrevious = true;
                safeToClean = false;
            }
            try { Directory.Move(stage, target); }
            catch
            {
                if (movedPrevious && !Directory.Exists(target))
                {
                    Directory.Move(backup, target);
                    safeToClean = true;
                }
                throw;
            }
            safeToClean = true;
            ToolCatalog.InvalidateTagsCache();
            return target;
        }
        finally
        {
            // Only the fresh, randomly named workspace is ours to clean up.
            // Preserve the backup if a filesystem error prevented rollback.
            try
            {
                if (safeToClean)
                {
                    try { CloudToolValidation.DeleteTree(workspace); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            finally { installLock.Release(); }
        }
    }

    internal static bool IsExecutable(string path)
    {
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            CloudToolValidation.CheckNoReparse(path);
            using var file = File.OpenRead(path);
            return file.ReadByte() == 'M' && file.ReadByte() == 'Z';
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    internal static async Task VerifyBlobAsync(string path, string? gitBlobSha, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gitBlobSha)) return;
        if (gitBlobSha.Length != 40 || !gitBlobSha.All(Uri.IsHexDigit))
            throw new InvalidDataException("社区包的文件校验信息无效。");
        await using var source = File.OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(Encoding.UTF8.GetBytes($"blob {source.Length}\0"));
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0) sha.AppendData(buffer.AsSpan(0, count));
        if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(gitBlobSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("社区包校验不一致，原有工具已保留。");
    }

    internal static void ValidateEntrance(string directory, string? launchTarget)
    {
        string entrance;
        if (!string.IsNullOrWhiteSpace(launchTarget))
        {
            if (!CloudToolValidation.IsRelativePath(launchTarget) || !launchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("社区包的主程序路径无效。");
            entrance = CloudToolValidation.Under(directory, launchTarget);
            if (!File.Exists(entrance))
                throw new InvalidDataException("下载包缺少指定的主程序，原有工具已保留。");
        }
        else
        {
            var executables = Directory.GetFiles(directory, "*.exe", SearchOption.AllDirectories);
            if (executables.Length != 1)
                throw new InvalidDataException("下载包需要明确指定可打开的主程序，原有工具已保留。");
            entrance = executables[0];
        }
        using var file = File.OpenRead(entrance);
        if (file.ReadByte() != 'M' || file.ReadByte() != 'Z')
            throw new InvalidDataException("下载内容不是有效的 Windows 程序，原有工具已保留。");
    }

    internal static async Task ExtractAsync(string archivePath, string destination, string? gitBlobSha, CancellationToken ct)
    {
        await using var source = File.OpenRead(archivePath);
        if (source.Length is < 4 or > CloudToolValidation.MaxPackageBytes)
            throw new InvalidDataException("工具包大小无效，原有工具已保留。");
        var prefix = new byte[4];
        await source.ReadExactlyAsync(prefix, ct);
        if (!prefix.AsSpan().SequenceEqual("PK\u0003\u0004"u8))
            throw new InvalidDataException("下载源返回了网页或非 ZIP 内容，请刷新目录或更换来源；原有工具已保留。");
        source.Position = 0;
        if (!string.IsNullOrWhiteSpace(gitBlobSha))
        {
            if (gitBlobSha.Length != 40 || !gitBlobSha.All(Uri.IsHexDigit))
                throw new InvalidDataException("社区包的文件校验信息无效。");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            sha.AppendData(Encoding.UTF8.GetBytes($"blob {source.Length}\0"));
            var hashBuffer = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(hashBuffer, ct)) > 0) sha.AppendData(hashBuffer.AsSpan(0, count));
            if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(gitBlobSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("社区包校验不一致，原有工具已保留。");
            source.Position = 0;
        }
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count is 0 or > CloudToolValidation.MaxZipEntries)
            throw new InvalidDataException("ZIP 文件数量无效。");
        long expanded = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            var isDirectory = relative.EndsWith('/');
            relative = relative.TrimEnd('/');
            if (!CloudToolValidation.IsRelativePath(relative) || !paths.Add(relative) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("ZIP 中包含无效路径、重复文件或链接。");
            expanded = checked(expanded + entry.Length);
            if (expanded > CloudToolValidation.MaxExpandedBytes) throw new InvalidDataException("ZIP 解压大小超限。");
            var path = CloudToolValidation.Under(destination, relative);
            if (isDirectory) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long copied = 0;
            uint crc = uint.MaxValue;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                copied = checked(copied + read);
                if (copied > entry.Length) throw new InvalidDataException("ZIP 文件长度不一致。");
                for (var i = 0; i < read; i++) crc = CrcTable[(crc ^ buffer[i]) & 0xff] ^ (crc >> 8);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (copied != entry.Length || ~crc != entry.Crc32)
                throw new InvalidDataException("ZIP 文件损坏或下载不完整，原有工具已保留。");
        }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }).ToArray();
}
