using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>
/// 选定工具流后的离线文字入门卡。只复用随包项目轨道的里程碑，
/// 不读取或展示轨道中的视频、外部链接，也不推断已安装软件的版本。
/// </summary>
public static partial class ToolFlowLearningGuide
{
    private const string NoMatch = "当前工具流暂无可确认匹配的本地文字入门路径；不会用未经核对的视频或链接凑教程。";

    public static string Build(
        ToolFlowSelection selection,
        ToolFlowInstallResult? result,
        string? tracksPath = null)
    {
        ArgumentNullException.ThrowIfNull(selection);

        // 只认用户在选定窗口核对过的项目目标和固定安装目标；不靠工具名称猜测。
        if (!IsExplicit2DGameGoal(selection.ProjectGoal)) return MiscTexts.T(NoMatch);
        var godot = selection.Items.FirstOrDefault(item =>
            string.Equals(item.InstallTargetKey?.Trim(), "godot", StringComparison.OrdinalIgnoreCase));
        if (godot is null) return MiscTexts.T(NoMatch);

        tracksPath ??= Path.Combine(AppContext.BaseDirectory, "Metadata", "project-tracks.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(tracksPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
                return MissingCatalog();

            foreach (var track in tracks.EnumerateArray())
            {
                if (track.ValueKind != JsonValueKind.Object ||
                    !track.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String ||
                    id.GetString() != "game2d-godot")
                    continue;

                if (!track.TryGetProperty("milestones", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    return MissingCatalog();

                var milestones = entries.EnumerateArray()
                    .Where(entry => entry.ValueKind == JsonValueKind.String)
                    .Select(entry => NormalizeLine(entry.GetString() ?? ""))
                    .Where(line => line.Length > 0 && line.Length <= 300 && !ExternalReference().IsMatch(line))
                    .Take(12)
                    .ToArray();
                if (milestones.Length == 0) return MissingCatalog();

                var updatedAt = CatalogDate(root);
                var status = result?.Items.FirstOrDefault(item => item.ItemId == godot.ItemId)?.Status;
                var text = new StringBuilder();
                text.AppendLine(MiscTexts.T("Godot 2D · 本地文字入门路径"));
                text.AppendLine(MiscTexts.T("匹配依据：已确认的项目目标明确为 2D 游戏，且选定清单包含固定安装目标 godot。"));
                text.AppendLine(MiscTexts.T("Godot 状态：") + StatusText(status));
                text.AppendLine(MiscTexts.TSub($"来源：随包 Metadata/project-tracks.json 的 game2d-godot 轨道；目录更新日期：{updatedAt}。"));
                text.AppendLine(MiscTexts.T("以下仅为离线里程碑，不代表已核对本机 Godot 的实际版本，也不是逐步操作教程；视频与外部链接未展示。"));
                for (var i = 0; i < milestones.Length; i++)
                    text.AppendLine($"{i + 1}. {milestones[i]}");
                return text.ToString().TrimEnd();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return MissingCatalog();
        }

        return MissingCatalog();
    }

    /// <summary>本机判断：文本是否明确表达了「做 2D 游戏」的目标（可见方案入口复用同一判据）。</summary>
    internal static bool IsExplicit2DGameGoal(string? goal)
    {
        if (string.IsNullOrWhiteSpace(goal) || !GameWord().IsMatch(goal) ||
            ThreeDimensional().IsMatch(goal) || Negated2D().IsMatch(goal))
            return false;
        return TwoDimensional().IsMatch(goal);
    }

    private static string CatalogDate(JsonElement root)
    {
        if (root.TryGetProperty("updatedAt", out var value) && value.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return MiscTexts.T("未注明");
    }

    private static string StatusText(ToolFlowInstallItemStatus? status) => status switch
    {
        ToolFlowInstallItemStatus.Installed => MiscTexts.T("本轮安装后检测到目标；仍需用户打开项目自行验证可用性。"),
        ToolFlowInstallItemStatus.AlreadyInstalled => MiscTexts.T("安装前检测到目标；本轮未重复安装，仍需用户打开项目自行验证可用性。"),
        ToolFlowInstallItemStatus.Failed => MiscTexts.T("本轮未能验证安装成功，请先按安装结果排障。"),
        ToolFlowInstallItemStatus.ManualStep => MiscTexts.T("需要用户按清单手动处理，尚未验证。"),
        _ => MiscTexts.T("尚无本轮安装结果，不能断言已经可用。"),
    };

    private static string NormalizeLine(string value) => Regex.Replace(value.Trim(), @"\s+", " ");

    private static string MissingCatalog() =>
        MiscTexts.T("已匹配 Godot 2D 工具流，但随包文字入门目录缺失或无可用里程碑；请先查看安装结果，暂无可核对的教程。");

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])2\s*d(?![A-Za-z0-9])|二维")]
    private static partial Regex TwoDimensional();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])3\s*d(?![A-Za-z0-9])|三维")]
    private static partial Regex ThreeDimensional();

    [GeneratedRegex(@"(?i)(?:不|非|不要|别|拒绝|排除|不是)\s*(?:做|要|选|用)?\s*(?:(?<![A-Za-z0-9])2\s*d(?![A-Za-z0-9])|二维)")]
    private static partial Regex Negated2D();

    [GeneratedRegex(@"(?i)游戏|game")]
    private static partial Regex GameWord();

    [GeneratedRegex(@"(?i)[A-Za-z][A-Za-z0-9+.-]*://|www\.|bilibili|b23\.tv|youtu(?:be\.com|\.be)|(?<![A-Za-z0-9])BV[0-9A-Za-z]{10}(?![A-Za-z0-9])")]
    private static partial Regex ExternalReference();
}
