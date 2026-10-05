using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TubaWinUi3.Services.AppManagement;

/// <summary>一条"已登记安装"的软件记录。</summary>
public sealed class SoftRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>来源：ai=AI助手登记 / manual=用户手动登记 / sandbox=沙箱安装（保留）。</summary>
    public string Source { get; set; } = "manual";
    public string Note { get; set; } = "";
    /// <summary>winget 包 Id（系统安装条目；用于一键卸载/升级）。</summary>
    public string WingetId { get; set; } = "";
    public DateTime InstalledAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 软件登记表（installed.json，位于 %LocalAppData%\TubaWinUi3\）：
/// 【GUI 隔离】测试模式落在 $ZXAI_DATA_ROOT\installed.json，不读写真实登记表。
/// 「应用中心」的数据源之一——凡登记过的软件（AI 助手安装 / 用户手动登记）都会出现在应用中心。
/// 由 AI 助手工具 register_installed_software 与「应用中心」页面共同读写。
/// </summary>
internal static class SoftwareRegistry
{
    private static readonly object Gate = new();

    public static readonly string FilePath = DataRoots.TestRoot is { } testRoot
        ? Path.Combine(testRoot, "installed.json")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TubaWinUi3", "installed.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static List<SoftRecord> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return [];
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<List<SoftRecord>>(json, JsonOpts) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    /// <summary>登记一条（同路径覆盖更新）。返回该条记录。</summary>
    public static SoftRecord Add(string name, string path, string version = "", string source = "manual", string note = "", string wingetId = "")
    {
        lock (Gate)
        {
            var list = Load();
            var normalized = (path ?? "").Trim().TrimEnd('\\', '/');
            var existing = list.FirstOrDefault(x =>
                string.Equals((x.Path ?? "").TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new SoftRecord { Id = Guid.NewGuid().ToString("N")[..12] };
                list.Add(existing);
            }
            existing.Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(normalized) : name;
            existing.Path = path ?? "";
            existing.Version = version ?? "";
            existing.Source = source;
            if (!string.IsNullOrWhiteSpace(wingetId)) existing.WingetId = wingetId;
            if (!string.IsNullOrWhiteSpace(note)) existing.Note = note;
            existing.InstalledAt = DateTime.Now;
            Save(list);
            return existing;
        }
    }

    /// <summary>移除记录（只删登记，不动文件）。</summary>
    public static bool Remove(string id)
    {
        lock (Gate)
        {
            var list = Load();
            var removed = list.RemoveAll(x => x.Id == id) > 0;
            if (removed) Save(list);
            return removed;
        }
    }

    private static void Save(List<SoftRecord> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, JsonOpts));
        }
        catch
        {
        }
    }
}
