using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;

namespace TubaWinUi3.Services;

/// <summary>
/// ZXAI 2026-09-22：AI 工具<b>厂商图标</b>加载器（由 fetch-ai-icons.py 从各厂商官网抓取，
/// 存放于 Assets/AiIcons/&lt;工具名&gt;.png）。卡片图标双轨：有厂商图标 → 图片；无 → 回退 FontIcon。
/// </summary>
public static class AiIconService
{
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    /// <summary>取工具厂商图标；不存在返回 null（调用方回退 FontIcon）。</summary>
    public static ImageSource? GetIcon(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(toolName, out var cached)) return cached;
            ImageSource? src = null;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AiIcons", Sanitize(toolName) + ".png");
                if (File.Exists(path))
                    src = new BitmapImage(new Uri(path));
            }
            catch
            {
                // 无效图片不影响卡片显示（回退 Glyph）
            }
            Cache[toolName] = src;
            return src;
        }
    }

    /// <summary>文件名清洗（须与 fetch-ai-icons.py 的 sanitize 保持一致）。</summary>
    public static string Sanitize(string name)
    {
        var invalid = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars).Trim();
    }
}
