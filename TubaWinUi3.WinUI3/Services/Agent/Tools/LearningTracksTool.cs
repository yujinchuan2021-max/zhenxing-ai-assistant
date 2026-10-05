using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace TubaWinUi3.Services.Agent;

/// <summary>
/// 项目轨道（产品线目录）工具组（ZXAI 2026-09-19）：
/// ① get_project_tracks —— 列出全部项目方向 / 单轨道详情（里程碑+工具链+精准教学视频）
/// ② update_learning_progress —— 里程碑打卡（陪着用户一步步做出成品）
/// ③ get_learning_progress —— 查学习进度
/// 数据：Metadata/project-tracks.json（随包发布；视频全部人工核验存在）
/// 进度：%LocalAppData%\TubaWinUi3\learning-progress.json
/// （【GUI 隔离】测试模式：$ZXAI_DATA_ROOT\learning-progress.json，不读写真实用户进度。）
/// </summary>
public static class LearningTracksTool
{
    public static void Register()
    {
        AgentToolRegistry.Register(new AgentTool
        {
            Name = "get_project_tracks",
            DisplayName = MiscTexts.T("项目轨道库"),
            Glyph = "\uE7C1",
            RequiresConfirmation = false,
            Function = AIFunctionFactory.Create(
                (Func<string, string>)GetProjectTracks,
                new AIFunctionFactoryOptions { Name = "get_project_tracks" }),
        });

        AgentToolRegistry.Register(new AgentTool
        {
            Name = "update_learning_progress",
            DisplayName = MiscTexts.T("学习打卡"),
            Glyph = "\uE8BD",
            RequiresConfirmation = false,
            Function = AIFunctionFactory.Create(
                (Func<string, int, string, string>)UpdateLearningProgress,
                new AIFunctionFactoryOptions { Name = "update_learning_progress" }),
        });

        AgentToolRegistry.Register(new AgentTool
        {
            Name = "get_learning_progress",
            DisplayName = MiscTexts.T("查询学习进度"),
            Glyph = "\uE9D5",
            RequiresConfirmation = false,
            Function = AIFunctionFactory.Create(
                (Func<string>)GetLearningProgress,
                new AIFunctionFactoryOptions { Name = "get_learning_progress" }),
        });
    }

    /// <summary>测试注入（null=默认路径）。</summary>
    internal static string? TracksPathOverride;

    /// <summary>测试注入（null=默认路径）。</summary>
    internal static string? ProgressPathOverride;

    private static string TracksPath => TracksPathOverride
        ?? Path.Combine(AppContext.BaseDirectory, "Metadata", "project-tracks.json");

    private static string ProgressPath => ProgressPathOverride
        ?? (DataRoots.TestRoot is { } testRoot
            ? Path.Combine(testRoot, "learning-progress.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TubaWinUi3", "learning-progress.json"));

    [Description("获取【项目轨道库】：产品线目录（26 个可做的项目方向：游戏开发/网站/小程序/App/AI应用/AI绘画/数据分析/办公自动化/爬虫/单片机/视频剪辑/3D建模…）。用法：① keyword 留空=列出全部方向（用户问'我能做什么/有什么项目/想学点什么'时调用）② keyword 传轨道 id 或关键词（如 'game2d-godot'/'游戏'/'小程序'）=返回该轨道详情（里程碑+工具链+精准教学视频）。用户选定方向后，按里程碑陪跑，每完成一步用 update_learning_progress 打卡。")]
    public static string GetProjectTracks(string keyword)
    {
        try
        {
            if (!File.Exists(TracksPath)) return "轨道库文件缺失（Metadata/project-tracks.json 未随包发布）。";
            var doc = JsonNode.Parse(File.ReadAllText(TracksPath, Encoding.UTF8));
            var tracks = doc?["tracks"]?.AsArray();
            if (tracks is null || tracks.Count == 0) return "轨道库为空。";

            keyword = (keyword ?? "").Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                var sb = new StringBuilder($"项目轨道库（共 {tracks.Count} 个方向，用 get_project_tracks(keyword=轨道id) 看详情）：\n");
                foreach (var t in tracks)
                    sb.AppendLine($"- [{t?["id"]}] {t?["name"]}（{t?["category"]}·{t?["difficulty"]}）{t?["desc"]}");
                sb.AppendLine("\n选定方向后：展示里程碑和教学视频，陪着用户一步步做，每步完成后打卡。");
                return sb.ToString();
            }

            JsonNode? hit = null;
            foreach (var t in tracks)
                if (string.Equals((string?)t?["id"], keyword, StringComparison.OrdinalIgnoreCase)) { hit = t; break; }
            if (hit is null)
            {
                foreach (var t in tracks)
                {
                    var hay = $"{t?["id"]}{t?["name"]}{t?["category"]}{t?["desc"]}";
                    if (hay.Contains(keyword, StringComparison.OrdinalIgnoreCase)) { hit = t; break; }
                }
            }
            if (hit is null)
                return $"没有匹配 “{keyword}” 的轨道。可用：{string.Join("、", tracks.Select(t => (string?)t?["id"]))}";

            var sb2 = new StringBuilder();
            sb2.AppendLine($"【{hit["name"]}】（{hit["category"]}·{hit["difficulty"]}）");
            sb2.AppendLine((string?)hit["desc"] ?? "");
            var ms = hit["milestones"]?.AsArray();
            sb2.AppendLine("\n🎯 里程碑（陪着用户一步步做）：");
            for (var i = 0; i < (ms?.Count ?? 0); i++) sb2.AppendLine($"  {i}. {ms![i]}");
            sb2.AppendLine("\n🧰 工具链：");
            foreach (var tool in hit["tools"]?.AsArray() ?? []) sb2.AppendLine($"  - {tool}");
            sb2.AppendLine("\n🎬 精准教学视频（B站，原样引用标题和链接，别改）：");
            foreach (var v in hit["videos"]?.AsArray() ?? [])
            {
                var note = (string?)v?["note"];
                sb2.AppendLine($"  - {v?["title"]}：https://www.bilibili.com/video/{v?["bvid"]}/"
                               + (string.IsNullOrEmpty(note) ? "" : $"（{note}）"));
            }

            var prog = FindProgress((string?)hit["id"] ?? "");
            var doneCount = prog?["done"]?.AsArray()?.Count ?? 0;
            if (prog is not null && doneCount > 0)
            {
                sb2.AppendLine($"\n📌 用户进度：已完成 {doneCount}/{ms?.Count ?? 0} 步。");
                var next = NextMilestoneIndex(prog, ms?.Count ?? 0);
                sb2.AppendLine(next < 0
                    ? "🎉 该轨道里程碑已全部完成！可以祝贺用户，并建议下一个挑战。"
                    : $"下一步（第 {next} 步）：{ms?[next]}");
            }
            return sb2.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"读取轨道库失败：{ex.Message}";
        }
    }

    [Description("学习里程碑打卡：用户完成轨道的某一步后调用（比如'我做出来了/第一步完成了'），记录进度并返回下一步提示。track_id=轨道 id（如 game2d-godot），milestone=完成的里程碑序号（从 0 开始，看轨道详情里的编号），note=可选备注（用户的成果/卡点）。")]
    public static string UpdateLearningProgress(string track_id, int milestone, string note)
    {
        try
        {
            var doc = JsonNode.Parse(File.ReadAllText(TracksPath, Encoding.UTF8));
            var tracks = doc?["tracks"]?.AsArray();
            JsonNode? track = null;
            foreach (var t in tracks ?? [])
                if (string.Equals((string?)t?["id"], (track_id ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) { track = t; break; }
            if (track is null) return $"未找到轨道 {track_id}，先用 get_project_tracks 列一下可用 id。";
            var ms = track["milestones"]?.AsArray();
            var total = ms?.Count ?? 0;
            if (milestone < 0 || milestone >= total) return $"milestone 序号超出范围（0-{total - 1}）。";

            var root = LoadProgressRoot();
            var records = root["records"]?.AsArray() ?? new JsonArray();
            JsonNode? rec = null;
            foreach (var r in records)
                if (string.Equals((string?)r?["trackId"], (string?)track["id"], StringComparison.OrdinalIgnoreCase)) { rec = r; break; }
            if (rec is null)
            {
                rec = new JsonObject
                {
                    ["trackId"] = (string?)track["id"],
                    ["trackName"] = (string?)track["name"],
                    ["started"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    ["done"] = new JsonArray(),
                    ["log"] = new JsonArray(),
                };
                records.Add(rec);
            }
            var done = rec["done"]!.AsArray();
            if (!done.Any(x => (int?)x == milestone)) done.Add(milestone);
            var sorted = done.Select(x => (int?)x ?? -1).Where(x => x >= 0).OrderBy(x => x).ToList();
            var newDone = new JsonArray();
            foreach (var d in sorted) newDone.Add(d);
            rec["done"] = newDone;
            rec["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            if (!string.IsNullOrWhiteSpace(note))
                rec["log"]!.AsArray().Add(new JsonObject { ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), ["milestone"] = milestone, ["note"] = note });

            root["records"] = records;
            SaveProgressRoot(root);

            var doneCount = newDone.Count;
            var next = NextMilestoneIndex(rec, total);
            var sb = new StringBuilder($"✅ 已打卡：{track["name"]} 第 {milestone} 步「{ms?[milestone]}」（进度 {doneCount}/{total}）。\n");
            sb.Append(next < 0
                ? "🎉 这个轨道的里程碑全部完成了！请祝贺用户，建议把作品发出去/分享，并推荐下一个挑战。"
                : $"下一步（第 {next} 步）：{ms?[next]}\n（跟用户确认这一步怎么做，别一次讲太多。）");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"打卡失败：{ex.Message}";
        }
    }

    [Description("查询用户的学习进度（所有在学的项目轨道、完成了多少步、下一步是什么）。用户问'我学到哪了/进度如何'时调用。")]
    public static string GetLearningProgress()
    {
        try
        {
            var root = LoadProgressRoot();
            var records = root["records"]?.AsArray();
            if (records is null || records.Count == 0) return "用户还没有开始任何学习轨道。（可以先 get_project_tracks 推荐方向。）";
            var sb = new StringBuilder("用户的学习进度：\n");
            foreach (var r in records)
            {
                var doneCount = r?["done"]?.AsArray()?.Count ?? 0;
                sb.AppendLine($"- {r?["trackName"]}（{r?["trackId"]}）：已完成 {doneCount} 步，最近更新 {r?["updated"]}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"查询失败：{ex.Message}";
        }
    }

    // ---------- 辅助 ----------

    private static JsonObject LoadProgressRoot()
    {
        try
        {
            if (File.Exists(ProgressPath))
                return JsonNode.Parse(File.ReadAllText(ProgressPath, Encoding.UTF8)) as JsonObject ?? new JsonObject();
        }
        catch { }
        return new JsonObject { ["records"] = new JsonArray() };
    }

    private static void SaveProgressRoot(JsonObject root)
    {
        var dir = Path.GetDirectoryName(ProgressPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ProgressPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));
    }

    private static JsonNode? FindProgress(string trackId)
    {
        try
        {
            var records = LoadProgressRoot()["records"]?.AsArray();
            foreach (var r in records ?? [])
                if (string.Equals((string?)r?["trackId"], trackId, StringComparison.OrdinalIgnoreCase)) return r;
        }
        catch { }
        return null;
    }

    private static int NextMilestoneIndex(JsonNode? record, int total)
    {
        var done = new HashSet<int>();
        foreach (var x in record?["done"]?.AsArray() ?? []) done.Add((int?)x ?? -1);
        for (var i = 0; i < total; i++) if (!done.Contains(i)) return i;
        return -1;
    }
}
