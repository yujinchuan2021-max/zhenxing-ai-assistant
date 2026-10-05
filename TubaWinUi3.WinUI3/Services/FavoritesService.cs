using System.Text.Json;

namespace TubaWinUi3.Services;

public static class FavoritesService
{
    private const string Key = "FavoriteTools";
    private static string FavoritesPath => ConfigManager.GetFavoritesPath();
    private static List<string>? _cache;

    public static IReadOnlyList<string> GetFavorites()
    {
        if (_cache is not null)
            return _cache;

        try
        {
            if (File.Exists(FavoritesPath))
            {
                var json = File.ReadAllText(FavoritesPath);
                var stored = JsonSerializer.Deserialize<List<string>>(json) ?? [];
                _cache = stored.Select(p => PathResolver.MakeAbsolute(p)).ToList();
            }
            else
            {
                _cache = [];
            }
        }
        catch
        {
            _cache = [];
        }

        return _cache;
    }

    public static bool IsFavorite(string toolPath)
    {
        return GetFavorites().Contains(toolPath, StringComparer.OrdinalIgnoreCase);
    }

    public static void AddFavorite(string toolPath)
    {
        var list = GetFavorites()
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (list.Contains(toolPath, StringComparer.OrdinalIgnoreCase))
            return;

        list.Add(toolPath);
        _cache = list;
        Save(list);
    }

    public static void RemoveFavorite(string toolPath)
    {
        var list = GetFavorites()
            .Where(p => !p.Equals(toolPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _cache = list;
        Save(list);
    }

    public static void RemoveAll()
    {
        _cache = [];
        Save([]);
    }

    public static void ToggleFavorite(string toolPath)
    {
        if (IsFavorite(toolPath))
            RemoveFavorite(toolPath);
        else
            AddFavorite(toolPath);
    }

    /// <summary>按给定顺序保存收藏(拖拽排序后调用)。</summary>
    public static void SaveOrder(IEnumerable<string> orderedPaths)
    {
        var list = orderedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        _cache = list;
        Save(list);
    }

    public static void InvalidateCache()
    {
        _cache = null;
    }

    private static void Save(List<string> favorites)
    {
        try
        {
            var dir = Path.GetDirectoryName(FavoritesPath)!;
            Directory.CreateDirectory(dir);
            var stored = favorites.Select(p => PathResolver.MakeRelative(p)).ToList();
            var json = JsonSerializer.Serialize(stored);
            File.WriteAllText(FavoritesPath, json);
        }
        catch { }
    }
}
