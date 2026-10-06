using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Services;

/// <summary>Content checks before opening downloaded Windows executables or installers.
/// This proves file structure, not publisher identity or that installation completed.</summary>
internal static class WindowsDownloadValidation
{
    internal const long MaxFileBytes = 2L * 1024 * 1024 * 1024;

    internal static async Task ValidateAsync(string path, long expectedSize = 0, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".msix" or ".appx" or ".msixbundle" or ".appxbundle"))
        {
            Validate(path, expectedSize);
            return;
        }
        CloudToolValidation.CheckNoReparse(path);
        var size = new FileInfo(path).Length;
        if (size is < 64 or > MaxFileBytes || (expectedSize > 0 && size != expectedSize))
            throw new InvalidDataException("Windows 应用包大小不符，尚未启动安装。");
        await DownloadArchiveValidation.ValidateAsync(path, ct).ConfigureAwait(false);
        using var archive = ZipFile.OpenRead(path);
        var bundle = extension is ".msixbundle" or ".appxbundle";
        CheckXml(bundle ? "AppxMetadata/AppxBundleManifest.xml" : "AppxManifest.xml", bundle ? "Bundle" : "Package");
        CheckXml("AppxBlockMap.xml", "BlockMap");

        void CheckXml(string entryName, string root)
        {
            var entry = archive.GetEntry(entryName);
            if (entry is null || entry.Length is < 1 or > 1024 * 1024)
                throw new InvalidDataException("Windows 应用包缺少有效的清单或块校验元数据，尚未启动安装。");
            try
            {
                using var stream = entry.Open();
                using var xml = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
                xml.MoveToContent();
                if (xml.LocalName != root || !xml.NamespaceURI.StartsWith("http://schemas.microsoft.com/appx/", StringComparison.Ordinal))
                    throw new InvalidDataException("Windows 应用包元数据格式无效，尚未启动安装。");
                while (xml.Read()) ct.ThrowIfCancellationRequested();
            }
            catch (XmlException ex) { throw new InvalidDataException("Windows 应用包元数据损坏，尚未启动安装。", ex); }
        }
    }

    internal static bool IsValid(string path, long expectedSize = 0, string? architecture = null)
    {
        try { Validate(path, expectedSize, architecture); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { return false; }
    }

    internal static void Validate(string path, long expectedSize = 0, string? architecture = null)
    {
        CloudToolValidation.CheckNoReparse(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 64 or > MaxFileBytes || expectedSize < 0 ||
            (expectedSize > 0 && file.Length != expectedSize))
            throw new InvalidDataException("下载文件大小不符或不完整，尚未运行安装程序，请重新下载。");

        Span<byte> header = stackalloc byte[64];
        file.ReadExactly(header);
        if (path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<byte> signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
            // Windows Installer packages use the compound document container.
            var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var major = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
            var fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[44..]);
            var directorySector = BinaryPrimitives.ReadUInt32LittleEndian(header[48..]);
            if (!header[..8].SequenceEqual(signature) || header[28] != 0xFE || header[29] != 0xFF ||
                !((major == 3 && sectorShift == 9) || (major == 4 && sectorShift == 12)) ||
                BinaryPrimitives.ReadUInt16LittleEndian(header[32..]) != 6 ||
                file.Length < 2L * (1L << sectorShift) || file.Length % (1L << sectorShift) != 0 ||
                fatSectors == 0 || fatSectors >= file.Length / (1L << sectorShift) ||
                directorySector >= file.Length / (1L << sectorShift) - 1)
                throw InvalidContent();
            return;
        }
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || header[0] != 'M' || header[1] != 'Z')
            throw InvalidContent();

        var offset = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
        if (offset < 64 || offset > file.Length - 24) throw InvalidContent();
        file.Position = offset;
        Span<byte> pe = stackalloc byte[24];
        file.ReadExactly(pe);
        if (!pe[..4].SequenceEqual("PE\0\0"u8)) throw InvalidContent();
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]);
        var sections = BinaryPrimitives.ReadUInt16LittleEndian(pe[6..]);
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(pe[20..]);
        var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(pe[22..]);
        if (machine is not (0x14C or 0x8664 or 0xAA64) || sections is 0 or > 96 ||
            optionalSize < 96 || (characteristics & 0x0002) == 0 || (characteristics & 0x2000) != 0 ||
            (long)offset + 24 + optionalSize + sections * 40 > file.Length)
            throw InvalidContent();

        Span<byte> magic = stackalloc byte[2];
        file.ReadExactly(magic);
        var optionalMagic = BinaryPrimitives.ReadUInt16LittleEndian(magic);
        if ((machine == 0x14C && optionalMagic != 0x10B) ||
            (machine != 0x14C && (optionalMagic != 0x20B || optionalSize < 112)))
            throw InvalidContent();
        if (architecture is not null && machine != (architecture switch
            { "x86" => 0x14C, "x64" => 0x8664, "arm64" => 0xAA64, _ => throw new InvalidDataException("下载架构信息无效。") }))
            throw new InvalidDataException("下载程序架构与选择不一致，尚未运行，请选择适合当前系统的版本。");

        file.Position = (long)offset + 24 + optionalSize;
        Span<byte> section = stackalloc byte[40];
        for (var index = 0; index < sections; index++)
        {
            file.ReadExactly(section);
            var bytes = BinaryPrimitives.ReadUInt32LittleEndian(section[16..]);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(section[20..]);
            if (bytes > 0 && (start == 0 || (long)start + bytes > file.Length))
                throw new InvalidDataException("下载的 Windows 程序被截断，尚未运行安装程序，请重新下载。");
        }
    }

    private static InvalidDataException InvalidContent() => new(
        "下载源返回了网页、错误信息或无效的 Windows 程序，尚未运行安装程序；请刷新来源后重试。");
}
