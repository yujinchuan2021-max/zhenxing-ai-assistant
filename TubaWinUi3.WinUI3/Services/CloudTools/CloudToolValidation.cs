using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TubaWinUi3.Services.CloudTools;

/// <summary>Manifest and filesystem boundaries shared by online, bundled and cached data.</summary>
internal static class CloudToolValidation
{
    internal const int MaxCatalogBytes = 4 * 1024 * 1024;
    internal const int MaxReceiptBytes = 4 * 1024 * 1024;
    internal const long MaxPackageBytes = 512L * 1024 * 1024;
    internal const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaxZipEntries = 10000;
    internal const int MaxPackageMirrors = 3;
    internal const string ReceiptFile = ".zxai-cloud-install.json";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 20,
    };

    internal static bool IsId(string? value) => value is not null && value.Length <= 80 &&
        Regex.IsMatch(value, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant) &&
        !IsDeviceName(value);

    internal static bool IsSha256(string? value) => value is not null && value.Length == 64 &&
        value.All(Uri.IsHexDigit);

    internal static bool HasFileBaseline(CloudToolReceipt receipt)
    {
        if (receipt.Files is null || receipt.Files.Count is 0 or > MaxZipEntries) return false;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in receipt.Files)
        {
            if (!IsRelativePath(pair.Key) || !IsSha256(pair.Value)) return false;
            var relative = pair.Key.Replace('\\', '/');
            if (relative.Equals(ReceiptFile, StringComparison.OrdinalIgnoreCase) || !paths.Add(relative)) return false;
        }
        return paths.Contains(receipt.EntryPoint.Replace('\\', '/'));
    }

    internal static bool IsToolEntryPoint(string? value) => IsRelativePath(value) &&
        value!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        !new[] { "setup", "install", "uninstall", "redist" }.Any(word =>
            value.Replace('\\', '/').Split('/')[^1].Contains(word, StringComparison.OrdinalIgnoreCase));

    internal static bool IsPackageUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || !IsPackageHost(uri.Host) ||
            !uri.AbsolutePath.StartsWith("/downloads/tools/", StringComparison.Ordinal) ||
            !uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        // Reject normalization tricks even when System.Uri has already removed dot segments.
        var raw = value![(value!.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = raw.IndexOf('/');
        if (slash < 0) return false;
        var rawPath = raw[(slash + 1)..];
        // Backslashes and encoded delimiters can be interpreted differently by
        // origin servers and proxies. A URL is a path, never an alternate query,
        // traversal path or a double-encoded redirect instruction.
        if (rawPath.Contains('\\') || Regex.IsMatch(rawPath, "%2f|%5c|%25|%3f|%23",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        return IsRelativePath(Uri.UnescapeDataString(rawPath));
    }

    private static bool IsPackageHost(string host) => host.Equals("zhenxingai.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("download.zhenxingai.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("download-backup.zhenxingai.com", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<Uri> PackageSources(CloudToolPackage package)
    {
        if (!IsPackageUrl(package.Url) || package.Mirrors is null ||
            package.Mirrors.Length > MaxPackageMirrors || package.Mirrors.Any(url => !IsPackageUrl(url)))
            throw new InvalidDataException("工具包主源或备用源地址无效。");
        // Uri canonicalizes scheme/host and default ports; paths remain case
        // sensitive. Duplicate sources cannot consume retries or append bytes.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<Uri>();
        foreach (var url in new[] { package.Url }.Concat(package.Mirrors))
        {
            var uri = new Uri(url);
            if (seen.Add(uri.AbsoluteUri)) sources.Add(uri);
        }
        return sources;
    }

    internal static bool IsRelativePath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 240 || value.StartsWith('/') || value.StartsWith('\\'))
            return false;
        var segments = value.Replace('\\', '/').Split('/');
        return segments.All(p => p.Length is > 0 and <= 120 && p is not "." and not ".." &&
            !p.EndsWith(' ') && !p.EndsWith('.') && !IsDeviceName(p) &&
            !p.Any(c => c < 32 || "<>:\"|?*".Contains(c)));
    }

    private static bool IsDeviceName(string value)
    {
        var stem = value.Split('.')[0];
        return Regex.IsMatch(stem, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static CloudToolCatalog ParseCatalog(string json, Version clientVersion)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxCatalogBytes)
            throw new InvalidDataException("工具目录超过大小限制。");
        var catalog = JsonSerializer.Deserialize<CloudToolCatalog>(json, JsonOptions)
            ?? throw new InvalidDataException("工具目录为空。");
        if (catalog.SchemaVersion is not (1 or 2) || catalog.Revision < 0 ||
            !DateTimeOffset.TryParse(catalog.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !Version.TryParse(catalog.MinClientVersion, out var minimum) ||
            NormalizeVersion(minimum) > NormalizeVersion(clientVersion) ||
            catalog.Tools is null || catalog.Tools.Length > 10000)
            throw new InvalidDataException("工具目录版本不受支持或格式不正确。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in catalog.Tools)
        {
            if (tool is null || !IsId(tool.Id) || !ids.Add(tool.Id) ||
                !Text(tool.Name, 160, true) || !Text(tool.Category, 100, true) ||
                !Text(tool.Version, 100, true) || !Text(tool.Description, 6000) || !Text(tool.Publisher, 250) ||
                tool.Order < 0 || !Strings(tool.Tags, 40, 100) || !Strings(tool.Categories, 30, 100) ||
                tool.LegacyPath is null || (tool.LegacyPath.Length != 0 && !IsRelativePath(tool.LegacyPath)) ||
                !Text(tool.Homepage, 1500) || !Homepage(tool.Homepage) || tool.Packages is null || tool.Packages.Length > 4)
                throw new InvalidDataException("工具条目的标识、说明或路径无效。");
            var architectures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var package in tool.Packages)
            {
                if (package is null || package.Architecture is not ("x64" or "arm64" or "x86" or "any") ||
                    !architectures.Add(package.Architecture) || !IsPackageUrl(package.Url) ||
                    package.Mirrors is null || package.Mirrors.Length > MaxPackageMirrors ||
                    package.Mirrors.Any(url => !IsPackageUrl(url)) ||
                    (catalog.SchemaVersion == 1 && package.Mirrors.Length != 0) ||
                    package.SizeBytes <= 0 || package.SizeBytes > MaxPackageBytes || !IsSha256(package.Sha256) ||
                    !IsToolEntryPoint(package.EntryPoint) ||
                    !Text(package.Kind, 60, true))
                    throw new InvalidDataException("工具包来源、校验值、架构或入口无效。");
            }
        }
        return catalog;
    }

    // System.Version treats an omitted Build/Revision as -1. Manifests and assembly
    // versions use both three and four components; omitted components mean zero here.
    private static Version NormalizeVersion(Version value) =>
        new(value.Major, value.Minor, Math.Max(value.Build, 0), Math.Max(value.Revision, 0));

    private static bool Text(string? value, int max, bool required = false) => value is not null &&
        value.Length <= max && (!required || !string.IsNullOrWhiteSpace(value)) &&
        !value.Any(c => c == '\0');

    private static bool Strings(string[]? values, int maxCount, int maxLength) => values is not null &&
        values.Length <= maxCount && values.All(v => Text(v, maxLength, true));

    private static bool Homepage(string value) => value.Length == 0 ||
        (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0);

    internal static CloudToolPackage? SelectPackage(CloudToolDefinition tool, string architecture) =>
        tool.Packages.FirstOrDefault(p => p.Architecture == architecture) ??
        tool.Packages.FirstOrDefault(p => p.Architecture == "any") ??
        (architecture is "x64" or "arm64" ? tool.Packages.FirstOrDefault(p => p.Architecture == "x86") : null);

    internal static string Under(string root, string relative)
    {
        if (!IsRelativePath(relative)) throw new InvalidDataException("工具文件路径无效。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("工具路径超出管理目录。");
        CheckNoReparse(result);
        return result;
    }

    // Checking all existing ancestors also protects the root itself from junction redirection.
    internal static void CheckNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("工具目录不能包含符号链接或目录联接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void CheckTree(string directory)
    {
        CheckNoReparse(directory);
        if (!Directory.Exists(directory)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            CheckNoReparse(entry);
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }

    internal static void DeleteTree(string directory)
    {
        CheckTree(directory);
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            CheckNoReparse(file);
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(directory, recursive: true);
    }
}
