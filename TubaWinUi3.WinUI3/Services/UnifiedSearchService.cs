using TubaWinUi3.Models;

namespace TubaWinUi3.Services;

public static class UnifiedSearchService
{
    private static readonly (string Title, string Subtitle, string Glyph, string SettingKey)[] SettingsEntries =
    [
        ("应用主题", "选择浅色、深色或跟随系统", "\uE790", "Theme"),
        ("快速模式", "禁用所有动画，提升响应速度", "\uEB3F", "FastMode"),
        ("记住窗口位置和大小", "关闭后下次启动恢复位置", "\uE784", "RememberWindow"),
        ("背景图片", "导入图片作为主页面背景", "\uE91B", "Background"),
        ("检查更新", "检查是否有新版本", "\uE895", "Update"),
        ("配置管理", "管理配置文件的存储位置、导出和导入", "\uE8B7", "ConfigManager"),
        ("自定义工具管理", "管理工具分类、导入自定义工具", "\uE8B7", "CustomToolManager"),
    ];

    private static readonly (string Title, string Subtitle, string Glyph, string Action)[] QuickActions =
    [
        ("硬件信息", "查看处理器、显卡、内存等硬件信息", "\uE977", "navigate:hardware"),
        ("常用工具", "查看收藏的工具", "\uE735", "navigate:favorites"),
        ("内置工具", "无需外部文件的系统工具", "\uE90F", "navigate:builtin"),
		("设置", "应用外观和功能设置", "\uE713", "navigate:settings"),
    ];

    public static IReadOnlyList<SearchResult> Search(string query)
    {
        var normalized = query.Trim();
        if (normalized.Length == 0)
            return [];

        var results = new List<SearchResult>();

        SearchExternalTools(normalized, results);
        SearchBuiltinTools(normalized, results);
        SearchAiTools(normalized, results);
        if (!RuntimeHelper.IsMsixPackaged)
            SearchCommunityTools(normalized, results);
        SearchSettings(normalized, results);

        DeduplicateResults(results);

        // 【排序·2026-09-22】精准优先的两级排序（用户口径：先精准，再模糊；精准的在最上面）：
        //  ① 精准命中（工具名包含关键词，score ≥ 60）永远排在前面——即使描述命中的项分数条目多也不越级；
        //  ② 组内按分数降序（精确匹配 > 前缀 > 包含）；
        //  ③ 模糊命中（仅描述里提到关键词，score < 60）跟在下面，按分数降序。
        return results
            .OrderByDescending(r => r.Score >= 60)
            .ThenByDescending(r => r.Score)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(20)
            .ToList();
    }

    public static IReadOnlyList<SearchResult> GetQuickPanelItems()
    {
        var items = new List<SearchResult>();

        var recentPaths = LaunchHistoryService.GetHistory();
        foreach (var toolPath in recentPaths.Take(3))
        {
            var tool = FindToolByPath(toolPath);
            if (tool is not null)
            {
                var iconPath = tool.IconPath ?? ToolIconService.GetCachedIconPath(tool.Path);
                items.Add(new SearchResult
                {
                    Title = tool.Name,
                    Subtitle = tool.Category,
                    Glyph = tool.IconGlyph ?? "\uE8B7",
                    Kind = tool.DatabaseSource?.Equals("custom", StringComparison.OrdinalIgnoreCase) == true
                        ? SearchItemKind.CustomTool
                        : SearchItemKind.ExternalTool,
                    MatchKey = tool.Path,
                    IconPath = iconPath,
                    Category = tool.Category,
                    Score = 100
                });
            }
        }

        foreach (var qa in QuickActions)
        {
            items.Add(new SearchResult
            {
                Title = qa.Title,
                Subtitle = qa.Subtitle,
                Glyph = qa.Glyph,
                Kind = SearchItemKind.QuickAction,
                MatchKey = qa.Action,
                Score = 50
            });
        }

        return items;
    }

    private static void SearchExternalTools(string query, List<SearchResult> results)
    {
        try
        {
            var tools = ToolCatalog.Search(query);
            foreach (var tool in tools)
            {
                var isCustom = tool.DatabaseSource?.Equals("custom", StringComparison.OrdinalIgnoreCase) == true;
                var score = CalcScore(query, tool.Name, tool.Tags);
                // 【frecency·2026-09-22】常用工具优先（业界标配：高频使用 + 最近使用的排在前面）
                score += Math.Min(UsageStats.GetClicks("tool:" + tool.Path), 10) * 3;
                var iconPath = tool.IconPath ?? ToolIconService.GetCachedIconPath(tool.Path);
                results.Add(new SearchResult
                {
                    Title = tool.Name,
                    Subtitle = isCustom ? $"自定义 · {tool.Category}" : tool.Category,
                    Glyph = tool.IconGlyph ?? "\uE8B7",
                    Kind = isCustom ? SearchItemKind.CustomTool : SearchItemKind.ExternalTool,
                    MatchKey = tool.Path,
                    IconPath = iconPath,
                    Category = tool.Category,
                    Score = isCustom ? score + 1 : score
                });
            }
        }
        catch { }
    }

    private static void SearchBuiltinTools(string query, List<SearchResult> results)
    {
        try
        {
            foreach (var tool in BuiltinToolRegistry.Tools)
            {
                var score = CalcScore(query, tool.Name, [tool.Description, tool.Category]);
                if (score > 0)
                {
                    results.Add(new SearchResult
                    {
                        Title = tool.Name,
                        Subtitle = tool.Category,
                        Glyph = tool.Glyph,
                        Kind = SearchItemKind.BuiltinTool,
                        MatchKey = tool.Id,
                        Category = tool.Category,
                        Score = score
                    });
                }
            }
        }
        catch { }
    }

    /// <summary>ZXAI：AI 工具卡（全局搜索——图吧工具与 AI 工具一处都能搜到）。</summary>
    private static void SearchAiTools(string query, List<SearchResult> results)
    {
        try
        {
            foreach (var c in Pages.AiCategoryPage.AllToolCards())
            {
                // 【跨软件关联·2026-09-22】关联词一并参与匹配（openai→ChatGPT、ollama→LM Studio 等）
                var related = Pages.AiCategoryPage.RelatedTermsOf(c.Name);
                var score = CalcScore(query, c.Name, [c.Desc, c.CategoryTitle, string.Join(" ", related)]);
                // 【frecency·2026-09-22】常用工具优先：用户高频点击的卡在同分时排前
                if (score > 0) score += Math.Min(UsageStats.GetClicks("ai:" + c.Name), 10) * 3;
                if (score > 0)
                {
                    results.Add(new SearchResult
                    {
                        Title = c.Name,
                        Subtitle = $"AI 工具 · {c.CategoryTitle}",
                        Glyph = c.Glyph,
                        Kind = SearchItemKind.AiTool,
                        // 【修复】MatchKey 必须唯一：同分类多张卡共用一个分类键会被去重逻辑互相消掉
                        MatchKey = $"{c.CategoryKey}|{c.Name}",
                        Category = c.CategoryTitle,
                        Score = score
                    });
                }
            }
        }
        catch { }
    }

    private static void SearchSettings(string query, List<SearchResult> results)
    {
        foreach (var s in SettingsEntries)
        {
            var score = CalcScore(query, s.Title, [s.Subtitle]);
            if (score > 0)
            {
                results.Add(new SearchResult
                {
                    Title = s.Title,
                    Subtitle = s.Subtitle,
                    Glyph = s.Glyph,
                    Kind = SearchItemKind.Setting,
                    MatchKey = s.SettingKey,
                    Score = score
                });
            }
        }
    }

    private static void SearchCommunityTools(string query, List<SearchResult> results)
    {
    }

    private static void DeduplicateResults(List<SearchResult> results)
    {
        var builtinIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            if (r.Kind == SearchItemKind.ExternalTool)
            {
                var tool = ToolCatalog.GetAllToolsCached()
                    .FirstOrDefault(t => t.Path.Equals(r.MatchKey, StringComparison.OrdinalIgnoreCase));
                if (tool?.IsBuiltinLink == true && !string.IsNullOrWhiteSpace(tool.BuiltinToolId))
                    builtinIds.Add(tool.BuiltinToolId);
            }
        }

        var seen = new HashSet<(SearchItemKind, string)>();
        for (var i = results.Count - 1; i >= 0; i--)
        {
            var r = results[i];
            if (r.Kind == SearchItemKind.BuiltinTool && builtinIds.Contains(r.MatchKey))
            {
                results.RemoveAt(i);
                continue;
            }
            var key = (r.Kind, r.MatchKey);
            if (!seen.Add(key))
                results.RemoveAt(i);
        }
    }

    private static ToolItem? FindToolByPath(string toolPath)
    {
        try
        {
            return ToolCatalog.GetAllToolsCached()
                .FirstOrDefault(t => t.Path.Equals(toolPath, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    internal static double CalcScore(string query, string primary, IReadOnlyList<string>? secondary)
    {
        // 【多词搜索·2026-09-22】查询按空格拆词（如 "codex 切换"）：
        // 每个词都要命中（AND），分数为各词得分之和——保证组合查询给出一致的精确结果。
        var words = query.Split([' ', '\u3000'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return 0;
        if (words.Length == 1) return CalcScoreSingle(words[0], primary, secondary);

        var total = 0.0;
        foreach (var w in words)
        {
            var s = CalcScoreSingle(w, primary, secondary);
            if (s <= 0) return 0;
            total += s;
        }
        return total;
    }

    private static double CalcScoreSingle(string query, string primary, IReadOnlyList<string>? secondary)
    {
        var score = 0.0;

        if (primary.Equals(query, StringComparison.CurrentCultureIgnoreCase))
            score += 100;
        else if (primary.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
            score += 80;
        else if (primary.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            score += 60;
        else
            score += SubsequenceScore(query, primary);   // 【fzf 式·2026-09-22】子序列模糊（如 ccs → CC Switch）

        if (secondary is not null)
        {
            foreach (var text in secondary)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    score += 20;
            }
        }

        return score;
    }

    /// <summary>
    /// 【fzf 式模糊匹配·2026-09-22】查询字符按顺序出现在名称中即可（不要求连续）。
    /// 业界通行做法（fzf / VS Code / Sublime）：词首 + 连续匹配加分、跨度过大视为弱匹配丢弃。
    /// 例："ccs" → "CC Switch"（C-C-S 命中词首）；"ors" → "orders"。
    /// 返回 40..59 区间（永远低于连续包含 60，保证精确依旧优先）。
    /// </summary>
    internal static double SubsequenceScore(string query, string primary)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(primary)) return 0;
        if (query.Length < 2) return 0;   // 单字符不做子序列（太泛）

        var q = query.ToLowerInvariant();
        var p = primary;
        int ti = 0, first = -1, last = -1, consecutive = 0, boundary = 0;
        for (int i = 0; i < p.Length && ti < q.Length; i++)
        {
            if (char.ToLowerInvariant(p[i]) != q[ti]) continue;
            if (first < 0) first = i;
            // 词首判定：字符串开头 / 空格、-、_、· 之后 / 大写字母（驼峰）
            if (i == 0 || p[i - 1] == ' ' || p[i - 1] == '-' || p[i - 1] == '_' || p[i - 1] == '·' || char.IsUpper(p[i]))
                boundary++;
            if (last >= 0 && i == last + 1) consecutive++;
            last = i;
            ti++;
        }
        if (ti < q.Length) return 0;          // 有字符没匹配上 → 不是子序列

        var span = last - first + 1;
        if (span > q.Length * 4 + 8) return 0; // 跨度太散（字母从各处乱凑）→ 丢弃，防止垃圾结果

        var score = 40.0 + boundary * 3 + consecutive * 2;
        return Math.Min(score, 59);           // 封顶 59：永远低于"连续包含 60"
    }
}
