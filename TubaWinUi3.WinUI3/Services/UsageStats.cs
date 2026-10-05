using System.Text.Json;

namespace TubaWinUi3.Services;

/// <summary>
/// ZXAI 2026-09-20：使用统计——卡片点击计数（用于列表按热度排序）+ AI 卡收藏。
/// 存储：%LocalAppData%\TubaWinUi3\usage_stats.json
/// （【GUI 隔离】测试模式：$ZXAI_DATA_ROOT\usage_stats.json，不读写真实用户统计。）
/// key 约定：AI 卡 = "ai:" + 卡名；图吧工具 = "tool:" + 工具路径。
/// </summary>
public static class UsageStats
{
    private sealed class Data
    {
        public Dictionary<string, int> Clicks { get; set; } = new();
        public List<string> AiFavorites { get; set; } = new();
    }

    private static string FilePath => DataRoots.TestRoot is { } testRoot
        ? System.IO.Path.Combine(testRoot, "usage_stats.json")
        : System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TubaWinUi3", "usage_stats.json");

    private static Data? _cache;

    private static Data Load()
    {
        if (_cache is not null) return _cache;
        try
        {
            if (File.Exists(FilePath))
                _cache = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
            else
                _cache = new Data();
        }
        catch
        {
            _cache = new Data();
        }
        return _cache;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_cache ?? new Data()));
        }
        catch { }
    }

    /// <summary>卡片点击 +1（AI 卡 key="ai:名称"；图吧工具 key="tool:路径"）。</summary>
    public static void RecordClick(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var d = Load();
        d.Clicks[key] = d.Clicks.TryGetValue(key, out var n) ? n + 1 : 1;
        Save();
    }

    public static int GetClicks(string key)
    {
        var d = Load();
        return d.Clicks.TryGetValue(key, out var n) ? n : 0;
    }

    /// <summary>按点击次数降序稳定排序（0 次的保持原有顺序）。</summary>
    public static List<T> OrderByUsage<T>(IEnumerable<T> items, Func<T, string> keySelector)
        => items.OrderByDescending(i => GetClicks(keySelector(i))).ToList();

    // ---- AI 卡收藏 ----

    public static bool IsAiFavorite(string cardName)
        => Load().AiFavorites.Contains(cardName, StringComparer.OrdinalIgnoreCase);

    public static void ToggleAiFavorite(string cardName)
    {
        if (string.IsNullOrWhiteSpace(cardName)) return;
        var d = Load();
        var idx = d.AiFavorites.FindIndex(x => string.Equals(x, cardName, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            d.AiFavorites.RemoveAt(idx);
        else
            d.AiFavorites.Add(cardName);
        Save();
    }

    public static IReadOnlyList<string> GetAiFavorites() => Load().AiFavorites;

    /// <summary>测试用：清空缓存（下次从磁盘重读）。</summary>
    internal static void ResetCache() => _cache = null;
}
