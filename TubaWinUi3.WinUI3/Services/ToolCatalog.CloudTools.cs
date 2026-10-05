using TubaWinUi3.Models;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Services;

public static partial class ToolCatalog
{
    /// <summary>Resolve a legacy Tools-relative path to the current managed copy, or the existing bundle.</summary>
    public static string ResolveToolPath(string relativeToolsPath)
    {
        if (!CloudToolValidation.IsRelativePath(relativeToolsPath))
            throw new ArgumentException("工具相对路径无效。", nameof(relativeToolsPath));
        return CloudToolService.ResolveLegacyToolPath(relativeToolsPath)
            ?? Path.Combine(ToolsRoot, relativeToolsPath.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar));
    }

    private static IReadOnlyList<ToolItem> MergeCloudTools(string category, IEnumerable<ToolItem> localItems)
    {
        var definitions = CloudToolService.GetCatalog()
            .Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase)
                || t.Categories.Contains(category, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (definitions.Length == 0) return localItems.ToList();
        var states = CloudToolService.GetStates().ToDictionary(s => s.Id, StringComparer.Ordinal);
        var local = localItems.ToList();
        foreach (var definition in definitions)
        {
            states.TryGetValue(definition.Id, out var state);
            var existing = local.Where(item => !item.IsBuiltinLink &&
                MatchesLegacyCloudDirectory(item.Path, definition.LegacyPath)).ToArray();
            // Installer-only/batch/script entries can remain usable in older full bundles.
            // A manifest without a known portable entry must not replace their launch card.
            if (state?.IsInstalled != true && existing.Any(item => File.Exists(item.EffectivePath))) continue;
            foreach (var item in existing) local.Remove(item);
            local.Add(CreateCloudToolItem(category, definition, state));
        }
        return local;
    }

    private static bool MatchesLegacyCloudDirectory(string file, string legacyPath)
    {
        if (string.IsNullOrWhiteSpace(legacyPath)) return false;
        foreach (var root in new[] { ToolsRoot, WritableToolsRoot })
        {
            var directory = Path.GetFullPath(Path.Combine(root, legacyPath.Replace('/', Path.DirectorySeparatorChar)));
            var full = Path.GetFullPath(file);
            if (full.Equals(directory, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static ToolItem CreateCloudToolItem(string category, CloudToolDefinition definition, CloudToolState? state)
    {
        var hasPackage = definition.Packages.Length > 0 && state?.Status != CloudToolStatus.Unsupported;
        var entry = definition.Packages.FirstOrDefault()?.EntryPoint ?? "__cloud-tool.exe";
        // Stable logical path keeps favourites/history attached to the catalogue entry across downloads.
        var virtualDirectory = string.IsNullOrEmpty(definition.LegacyPath)
            ? Path.Combine(ToolsRoot, "Cloud", definition.Id)
            : Path.Combine(ToolsRoot, definition.LegacyPath.Replace('/', Path.DirectorySeparatorChar));
        var logicalPath = Path.Combine(virtualDirectory, entry.Replace('/', Path.DirectorySeparatorChar));
        var item = new ToolItem
        {
            Name = definition.Name, Category = category, PrimaryCategory = definition.Category,
            Categories = definition.Categories.Prepend(definition.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Path = logicalPath, RelativePath = Path.GetRelativePath(ToolsRoot, logicalPath),
            Extension = state?.IsInstalled == true ? "EXE" : "待下载", CloudToolId = definition.Id,
            CloudHasPackage = hasPackage, DownloadUrl = hasPackage ? definition.Packages[0].Url : null,
            Description = definition.Description, Publisher = definition.Publisher,
            Version = state?.IsInstalled == true ? state.Version : definition.Version,
            RemoteUrl = hasPackage ? null : definition.Homepage, SortOrder = definition.Order,
            Tags = definition.Tags, IsCatalogCurated = true, IconGlyph = "\uE8F1",
        };
        item.IsFavorite = FavoritesService.IsFavorite(item.Path);
        return item;
    }

    internal static void NotifyCloudToolsChanged()
    {
        InvalidateTagsCache();
        ToolsChanged?.Invoke();
    }
}
