using System.IO.Compression;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Services;

/// <summary>Checks complete ZIP contents before a queue item can be processed.</summary>
internal static class DownloadArchiveValidation
{
    // The client update includes its private runtime, so it exceeds the portable-tool limit.
    private const int MaxEntries = 100_000;
    private const long MaxExpandedBytes = 8L * 1024 * 1024 * 1024;
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
        return crc;
    }).ToArray();

    internal static async Task ValidateAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is 0 or > MaxEntries)
            throw new InvalidDataException("压缩包文件数量无效，未进行安装。");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[81920];
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var isDirectory = name.EndsWith('/');
            var relative = isDirectory ? name.TrimEnd('/') : name;
            if (!CloudToolValidation.IsRelativePath(relative) || !explicitPaths.Add(relative)
                || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("压缩包包含不安全或重复路径，未进行安装。");
            var parts = relative.Split('/');
            var parent = "";
            for (var i = 0; i < parts.Length - 1; i++)
            {
                parent = parent.Length == 0 ? parts[i] : parent + "/" + parts[i];
                if (files.Contains(parent))
                    throw new InvalidDataException("压缩包文件与目录冲突，未进行安装。");
                directories.Add(parent);
            }
            if (isDirectory)
            {
                if (files.Contains(relative) || entry.Length != 0)
                    throw new InvalidDataException("压缩包目录条目无效，未进行安装。");
                directories.Add(relative);
            }
            else
            {
                if (directories.Contains(relative) || !files.Add(relative))
                    throw new InvalidDataException("压缩包文件与目录冲突，未进行安装。");
                if (entry.Length > MaxExpandedBytes - expanded)
                    throw new InvalidDataException("压缩包展开大小超过限制，未进行安装。");
                expanded += entry.Length;
            }

            using var input = entry.Open();
            long readTotal = 0;
            var crc = uint.MaxValue;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                readTotal += read;
                if (readTotal > entry.Length)
                    throw new InvalidDataException("压缩包条目大小不一致，未进行安装。");
                for (var i = 0; i < read; i++) crc = CrcTable[(crc ^ buffer[i]) & 0xff] ^ (crc >> 8);
            }
            if (readTotal != entry.Length || ~crc != entry.Crc32)
                throw new InvalidDataException("压缩包条目不完整或 CRC 校验失败，未进行安装。");
        }
    }
}
