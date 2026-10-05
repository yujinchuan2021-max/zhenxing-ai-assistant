using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Controls.AgentChat;

namespace TubaWinUi3.Services;

public static partial class MarkdownTextService
{
    public static void RenderToRichTextBlock(RichTextBlock richTextBlock, string markdown)
    {
        // 【ZXAI 字体统一】正文/代码全走 AppFonts 统一接口（换字体只改 AppFonts.cs）。
        // 必须显式赋值：FontFamily 依赖属性的默认值通常已经非空，??= 不会生效（历史坑，见 font-maintenance.md）。
        richTextBlock.FontFamily = TubaWinUi3.Services.AppFonts.WinUI;

        // 【主题统一】无外层渲染作用域时（独立调用方，如更新日志窗口），以本 RichTextBlock
        // 为容器自建作用域；有外层（AiMarkdownRenderer.Render 的容器）则登记进外层，
        // 保证主题切换时一起就地重刷。
        if (ThemeRefreshScope.Ambient is not null)
        {
            ThemeRefreshScope.ReleaseRenderedContent(richTextBlock);
            RenderCore(richTextBlock, markdown);
            return;
        }

        ToolFlowThemeResources.AddPalette(richTextBlock);
        var scope = ThemeRefreshScope.AttachRenderedContent(richTextBlock);
        using var _ = scope.Push();
        RenderCore(richTextBlock, markdown);
    }

    private static void RenderCore(RichTextBlock richTextBlock, string markdown)
    {
        richTextBlock.Blocks.Clear();
        // Plain runs and headings inherit this explicit rendering color instead of
        // the application's already materialized system brush.
        ThemeBrushBinder.Apply(richTextBlock, RichTextBlock.ForegroundProperty, ToolFlowThemeResources.PrimaryText);

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            if (HeadingRegex().IsMatch(line))
            {
                var match = HeadingRegex().Match(line);
                var level = match.Groups[1].Value.Length;
                var text = match.Groups[2].Value.Trim();
                AddHeading(richTextBlock, text, level);
                i++;
            }
            else if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* ") || line.TrimStart().StartsWith("+ "))
            {
                i = AddUnorderedList(richTextBlock, lines, i);
            }
            else if (OrderedListRegex().IsMatch(line.TrimStart()))
            {
                i = AddOrderedList(richTextBlock, lines, i);
            }
            else if (line.Trim() == "---" || line.Trim() == "***" || line.Trim() == "___")
            {
                AddHorizontalRule(richTextBlock);
                i++;
            }
            else if (line.TrimStart().StartsWith("> "))
            {
                i = AddBlockquote(richTextBlock, lines, i);
            }
            else if (line.TrimStart().StartsWith("```"))
            {
                i = AddCodeBlock(richTextBlock, lines, i);
            }
            else if (line.TrimStart().StartsWith("|") && line.TrimStart().IndexOf('|', 1) >= 0)
            {
                i = AddTable(richTextBlock, lines, i);
            }
            else
            {
                AddParagraph(richTextBlock, line);
                i++;
            }
        }
    }

    private static void AddHeading(RichTextBlock rtb, string text, int level)
    {
        var para = new Paragraph();
        var fontSize = level switch
        {
            1 => 22,
            2 => 18,
            3 => 15,
            _ => 13
        };
        var fontWeight = level <= 2 ? FontWeights.Bold : FontWeights.Normal;

        var run = new Run { Text = text, FontSize = fontSize, FontWeight = fontWeight };
        para.Inlines.Add(run);
        rtb.Blocks.Add(para);
    }

    private static int AddUnorderedList(RichTextBlock rtb, string[] lines, int start)
    {
        var i = start;
        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("- ") && !trimmed.StartsWith("* ") && !trimmed.StartsWith("+ "))
                break;

            var content = trimmed[2..];
            var para = new Paragraph { TextIndent = 0 };
            var bullet = new Run { Text = "  \u2022  " };
            ThemeBrushBinder.Apply(bullet, TextElement.ForegroundProperty, ToolFlowThemeResources.SecondaryText);
            para.Inlines.Add(bullet);
            AddInlineContent(para, content);
            rtb.Blocks.Add(para);
            i++;
        }
        return i;
    }

    private static int AddOrderedList(RichTextBlock rtb, string[] lines, int start)
    {
        var i = start;
        var number = 1;
        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimStart();
            var match = OrderedListRegex().Match(trimmed);
            if (!match.Success) break;

            var content = trimmed[match.Length..];
            var para = new Paragraph { TextIndent = 0 };
            var numRun = new Run { Text = $"  {number}.  " };
            ThemeBrushBinder.Apply(numRun, TextElement.ForegroundProperty, ToolFlowThemeResources.SecondaryText);
            para.Inlines.Add(numRun);
            AddInlineContent(para, content);
            rtb.Blocks.Add(para);
            number++;
            i++;
        }
        return i;
    }

    private static void AddHorizontalRule(RichTextBlock rtb)
    {
        var para = new Paragraph();
        var run = new Run
        {
            Text = "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            FontSize = 12
        };
        ThemeBrushBinder.Apply(run, TextElement.ForegroundProperty, ToolFlowThemeResources.Stroke);
        para.Inlines.Add(run);
        rtb.Blocks.Add(para);
    }

    private static int AddBlockquote(RichTextBlock rtb, string[] lines, int start)
    {
        var i = start;
        var sb = new System.Text.StringBuilder();
        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("> ")) break;
            sb.AppendLine(trimmed[2..]);
            i++;
        }

        var para = new Paragraph();
        var borderRun = new Run { Text = "\u2502 " };
        ThemeBrushBinder.Apply(borderRun, TextElement.ForegroundProperty, ToolFlowThemeResources.Accent);
        para.Inlines.Add(borderRun);
        AddInlineContent(para, sb.ToString().TrimEnd());
        rtb.Blocks.Add(para);
        return i;
    }

    private static int AddCodeBlock(RichTextBlock rtb, string[] lines, int start)
    {
        var i = start + 1;
        var sb = new System.Text.StringBuilder();
        while (i < lines.Length)
        {
            if (lines[i].TrimStart().StartsWith("```")) { i++; break; }
            sb.AppendLine(lines[i]);
            i++;
        }

        var para = new Paragraph();
        var codeRun = new Run
        {
            Text = sb.ToString().TrimEnd(),
            FontFamily = TubaWinUi3.Services.AppFonts.WinUI,
            FontSize = 12
        };
        ThemeBrushBinder.Apply(codeRun, TextElement.ForegroundProperty, ToolFlowThemeResources.SecondaryText);
        para.Inlines.Add(codeRun);
        rtb.Blocks.Add(para);
        return i;
    }

    private static int AddTable(RichTextBlock rtb, string[] lines, int start)
    {
        var i = start;
        var tableRows = new List<string[]>();

        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("|") || trimmed.IndexOf('|', 1) < 0) break;
            if (IsTableSeparatorRow(trimmed)) { i++; continue; }

            var cells = ParseTableRow(trimmed);
            if (cells.Length > 0)
                tableRows.Add(cells);
            i++;
        }

        if (tableRows.Count == 0) return i;

        var colCount = tableRows.Max(r => r.Length);

        foreach (var row in tableRows)
        {
            var isHeader = tableRows.IndexOf(row) == 0;
            var para = new Paragraph();

            for (int c = 0; c < row.Length && c < colCount; c++)
            {
                if (c > 0)
                {
                    var separator = new Run { Text = "  \u2502  ", FontSize = 12 };
                    ThemeBrushBinder.Apply(separator, TextElement.ForegroundProperty, ToolFlowThemeResources.Stroke);
                    para.Inlines.Add(separator);
                }

                if (isHeader)
                {
                    var bold = new Span { FontWeight = FontWeights.Bold };
                    AddInlineContent(bold, row[c]);
                    para.Inlines.Add(bold);
                }
                else
                {
                    AddInlineContent(para, row[c]);
                }
            }

            rtb.Blocks.Add(para);
        }

        return i;
    }

    private static bool IsTableSeparatorRow(string line)
    {
        var stripped = line.Replace("|", "").Replace("-", "").Replace(" ", "").Replace(":", "");
        return stripped.Length == 0 && line.Contains('-');
    }

    private static string[] ParseTableRow(string line)
    {
        var cells = new List<string>();
        var parts = line.Split('|');
        for (int i = 1; i < parts.Length - 1; i++)
            cells.Add(parts[i].Trim());
        if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[^1].TrimEnd()))
            cells.Add(parts[^1].Trim());
        return cells.ToArray();
    }

    private static void AddInlineContent(Span span, string text)
    {
        var para = new Paragraph();
        AddInlineContent(para, text);
        foreach (var inline in para.Inlines.ToList())
        {
            para.Inlines.Remove(inline);
            span.Inlines.Add(inline);
        }
    }

    private static void AddParagraph(RichTextBlock rtb, string text)
    {
        var para = new Paragraph();
        AddInlineContent(para, text);
        rtb.Blocks.Add(para);
    }

    internal static void AddInlineContent(Paragraph para, string text)
    {
        var pos = 0;
        var inlineRegex = InlineRegex();

        while (pos < text.Length)
        {
            var match = inlineRegex.Match(text, pos);
            if (!match.Success || match.Index > pos)
            {
                var plain = match.Success ? text[pos..match.Index] : text[pos..];
                if (!string.IsNullOrEmpty(plain))
                    para.Inlines.Add(new Run { Text = plain });
                if (!match.Success) break;
            }

            if (match.Groups[1].Success)
            {
                var linkText = match.Groups[1].Value;
                var linkUrl = match.Groups[2].Value;
                var hyperlink = new Hyperlink
                {
                    UnderlineStyle = UnderlineStyle.Single
                };
                var linkRun = new Run { Text = linkText };
                ThemeBrushBinder.Apply(linkRun, TextElement.ForegroundProperty, ToolFlowThemeResources.Accent);
                hyperlink.Inlines.Add(linkRun);
                if (InternalBrowserLink.Bind(hyperlink, linkUrl)) para.Inlines.Add(hyperlink);
                else para.Inlines.Add(new Run { Text = linkText });
            }
            else if (match.Groups[3].Success)
            {
                var boldText = match.Groups[3].Value;
                var bold = new Span { FontWeight = FontWeights.Bold };
                bold.Inlines.Add(new Run { Text = boldText });
                para.Inlines.Add(bold);
            }
            else if (match.Groups[4].Success)
            {
                var italicText = match.Groups[4].Value;
                var italic = new Span { FontStyle = Windows.UI.Text.FontStyle.Italic };
                italic.Inlines.Add(new Run { Text = italicText });
                para.Inlines.Add(italic);
            }
            else if (match.Groups[5].Success)
            {
                var codeText = match.Groups[5].Value;
                var code = new Span
                {
                    FontFamily = TubaWinUi3.Services.AppFonts.WinUI,
                    FontSize = 12
                };
                var codeRun = new Run { Text = codeText };
                ThemeBrushBinder.Apply(codeRun, TextElement.ForegroundProperty, ToolFlowThemeResources.Accent);
                code.Inlines.Add(codeRun);
                para.Inlines.Add(code);
            }
            else if (match.Groups[6].Success)
            {
                var url = match.Groups[6].Value;
                var hyperlink = new Hyperlink
                {
                    UnderlineStyle = UnderlineStyle.Single
                };
                var urlRun = new Run { Text = url };
                ThemeBrushBinder.Apply(urlRun, TextElement.ForegroundProperty, ToolFlowThemeResources.Accent);
                hyperlink.Inlines.Add(urlRun);
                if (InternalBrowserLink.Bind(hyperlink, url)) para.Inlines.Add(hyperlink);
                else para.Inlines.Add(new Run { Text = url });
            }

            pos = match.Index + match.Length;
        }
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\d+\.\s")]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex(@"\[(.+?)\]\((.+?)\)|\*\*(.+?)\*\*|\*(.+?)\*|`(.+?)`|<(https?://.+?)>")]
    private static partial Regex InlineRegex();
}

// ---------------------------------------------------------------------------
// 【主题统一】主题资源解析 + 动态元素主题重刷登记
//
// 背景（hermes-theme-resolution-followup / hermes-richtext-theme-followup）：
//  ① Application.Current.Resources[key] 取的是"App 当前解析值"，页面/容器局部强制
//     Light/Dark（如聊天页的 Assistant* 令牌）时会取错主题；
//  ② Markdown / 富文本里的表格、推荐卡、链接、代码、列表是一次性快照，切换主题残留旧色。
// 这里给出统一入口：
//  - ThemeResourceResolver：[主题 + 资源键] 逐层查找（元素自身 → 祖先页面/控件 → 应用级，
//    每层先查该层的主题字典（含 merged 链条）再查普通项）——绝不用"先选中一个字典再查键"
//    的写法（页面字典只含 Assistant* 键时会把查询整体带偏）；
//  - ThemeRefreshScope：动态控件弱引用登记；固定 Markdown 渲染树保留原始绑定对象，主题变化时就地重刷
//    （不重建元素 → 流式内容、滚动位置、展开状态都不受影响）；容器 Unloaded 退订。
// ---------------------------------------------------------------------------

/// <summary>资源字典的最小抽象（可脱离 XAML 单测）。</summary>
public interface IThemeResourceDictionary
{
    /// <summary>本字典自身的项（不含主题字典与 merged）。</summary>
    object? Lookup(string key);

    /// <summary>本字典针对某主题（Light/Dark）的主题字典。</summary>
    IThemeResourceDictionary? ThemeDictionary(string themeKey);

    /// <summary>本字典合并的字典链。</summary>
    IReadOnlyList<IThemeResourceDictionary> MergedDictionaries { get; }
}

/// <summary>ResourceDictionary → IThemeResourceDictionary 适配器。</summary>
internal sealed class ResourceDictionaryNode(ResourceDictionary dictionary) : IThemeResourceDictionary
{
    private readonly ResourceDictionary _dictionary = dictionary;
    internal object Identity => _dictionary;
    // A node is a short-lived snapshot for one resolver chain/refresh, never a
    // global cache. Reuse enumerated keys and child adapters across its bindings.
    private HashSet<string>? _localKeys;
    private IReadOnlyList<IThemeResourceDictionary>? _merged;
    private readonly Dictionary<string, IThemeResourceDictionary?> _themes = new(StringComparer.Ordinal);

    public object? Lookup(string key)
    {
        try
        {
            // ResourceDictionary.TryGetValue also performs WinUI's implicit theme
            // fallback. A page dictionary without this key can therefore return the
            // application's Dark brush before our explicit Light search reaches the
            // next dictionary. Only read an entry owned by this dictionary; merged
            // and theme dictionaries are traversed explicitly by the resolver.
            // Do not use Keys.Contains: the projected ICollection implementation
            // delegates to native HasKey, which includes merged/theme fallback too.
            if (_localKeys is null)
            {
                _localKeys = new(StringComparer.Ordinal);
                foreach (var candidate in _dictionary.Keys)
                    if (candidate is string localKey) _localKeys.Add(localKey);
            }
            return _localKeys.Contains(key) && _dictionary.TryGetValue(key, out var value) ? value : null;
        }
        catch { return null; }
    }

    public IThemeResourceDictionary? ThemeDictionary(string themeKey)
    {
        if (_themes.TryGetValue(themeKey, out var cached)) return cached;
        IThemeResourceDictionary? result = null;
        try
        {
            if (_dictionary.ThemeDictionaries.TryGetValue(themeKey, out var v) && v is ResourceDictionary d)
                result = new ResourceDictionaryNode(d);
        }
        catch { }
        _themes[themeKey] = result;
        return result;
    }

    public IReadOnlyList<IThemeResourceDictionary> MergedDictionaries
    {
        get
        {
            if (_merged is not null) return _merged;
            List<IThemeResourceDictionary>? list = null;
            try
            {
                foreach (var m in _dictionary.MergedDictionaries)
                {
                    if (m is null) continue;
                    (list ??= []).Add(new ResourceDictionaryNode(m));
                }
            }
            catch { }
            return _merged = list ?? (IReadOnlyList<IThemeResourceDictionary>)[];
        }
    }
}

/// <summary>
/// 【主题统一】[主题 + 资源键] → 画刷 的分层解析器。
/// 逐层（元素 → 祖先 → 应用级）按键查找，每层内先主题字典再普通项；
/// 目标元素带实际主题（ActualTheme）时以它为准，取不到再退回应用请求主题。
/// </summary>
public static class ThemeResourceResolver
{
    public const string LightKey = "Light";
    public const string DarkKey = "Dark";

    /// <summary>【测试】额外根字典（宿主没有 Application 时用于驱动同一条解析/刷色路径）。</summary>
    internal static List<IThemeResourceDictionary>? ExtraRootDictionariesForTest;

    /// <summary>【测试】强制当前主题键（模拟浅/深两态切换）。</summary>
    internal static string? CurrentThemeKeyOverrideForTest;

    public static string? ThemeKeyOf(ElementTheme? theme) => theme switch
    {
        ElementTheme.Light => LightKey,
        ElementTheme.Dark => DarkKey,
        _ => null,
    };

    /// <summary>解析当前生效的主题键：测试覆盖 → 元素实际主题 → 应用请求主题 → 浅色兜底。</summary>
    public static string ResolveThemeKey(DependencyObject? target)
    {
        if (CurrentThemeKeyOverrideForTest is { } forced) return forced;
        if (ThemeKeyOf((target as FrameworkElement)?.ActualTheme) is { } fromElement) return fromElement;
        try
        {
            if (Application.Current is { } app)
                return app.RequestedTheme == ApplicationTheme.Dark ? DarkKey : LightKey;
        }
        catch { }
        return LightKey;
    }

    /// <summary>单层查找：[主题字典（含 merged）] → [普通项（含 merged）]，均按【键】查找。</summary>
    public static bool TryResolveInDictionary(IThemeResourceDictionary dictionary, string themeKey, string key, out object? value)
    {
        value = null;
        if (dictionary is null) return false;

        if (TryFindInThemeDictionaries(dictionary, themeKey, key,
                new HashSet<object>(ReferenceEqualityComparer.Instance), out value))
            return true;

        // 独立 visited：主题那一轮已访问过的字典仍需参与普通项查找（键不同，不能被跳过）
        if (TryFindPlain(dictionary, key,
                new HashSet<object>(ReferenceEqualityComparer.Instance), out value))
            return true;

        value = null;
        return false;
    }

    /// <summary>整条链查找：链的顺序即优先级（元素自身 → 祖先 → 应用级）。</summary>
    public static bool TryResolve(IReadOnlyList<IThemeResourceDictionary> chain, string themeKey, string key, out object? value)
    {
        foreach (var dictionary in chain)
        {
            if (TryResolveInDictionary(dictionary, themeKey, key, out value)) return true;
        }
        value = null;
        return false;
    }

    /// <summary>按目标元素的实际主题解析画刷；找不到返回 null（调用方保留原值，绝不抛）。</summary>
    public static Brush? ResolveBrush(DependencyObject? target, string key, string? fallbackKey = null)
    {
        try
        {
            return ResolveBrush(BuildChain(target), ResolveThemeKey(target), key, fallbackKey);
        }
        catch { return null; }
    }

    /// <summary>按给定资源链 + 主题键解析画刷；主键缺失时尝试 fallbackKey。</summary>
    public static Brush? ResolveBrush(IReadOnlyList<IThemeResourceDictionary> chain, string themeKey, string key, string? fallbackKey = null)
    {
        try
        {
            if (TryResolve(chain, themeKey, key, out var value) && value is Brush brush) return brush;
            if (fallbackKey is not null &&
                TryResolve(chain, themeKey, fallbackKey, out var fallback) && fallback is Brush fallbackBrush)
                return fallbackBrush;
        }
        catch { }
        return null;
    }

    /// <summary>构建资源链：目标元素自身 → 逐级祖先（页面/控件局部字典） → 应用级。</summary>
    public static List<IThemeResourceDictionary> BuildChain(DependencyObject? target)
    {
        var chain = new List<IThemeResourceDictionary>();
        var node = target;
        var depth = 0;
        while (node is not null && depth++ < 64)
        {
            try
            {
                // 【主题复核·返修】不能以 Count>0 过滤：只含 ThemeDictionaries / MergedDictionaries 的
                // 局部字典 Count 为 0，但正是主题解析要用的层——一律收入链（空字典查找无成本）。
                if (node is FrameworkElement { Resources: { } resources })
                    chain.Add(new ResourceDictionaryNode(resources));
            }
            catch { }

            try { node = VisualTreeHelper.GetParent(node); }
            catch { node = null; }
        }

        chain.AddRange(RootDictionaries());
        return chain;
    }

    private static IEnumerable<IThemeResourceDictionary> RootDictionaries()
    {
        if (ExtraRootDictionariesForTest is { } extra)
        {
            foreach (var dictionary in extra) yield return dictionary;
        }

        ResourceDictionary? appResources = null;
        try { appResources = Application.Current?.Resources; }
        catch { }
        if (appResources is not null) yield return new ResourceDictionaryNode(appResources);
    }

    private static bool TryFindInThemeDictionaries(IThemeResourceDictionary dictionary, string themeKey, string key,
        HashSet<object> visited, out object? value)
    {
        value = null;
        if (!visited.Add(DictionaryIdentity(dictionary))) return false;

        if (dictionary.ThemeDictionary(themeKey) is { } themed && TryFindPlain(themed, key, visited, out value))
            return true;

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (TryFindInThemeDictionaries(merged, themeKey, key, visited, out value)) return true;
        }

        value = null;
        return false;
    }

    private static bool TryFindPlain(IThemeResourceDictionary dictionary, string key,
        HashSet<object> visited, out object? value)
    {
        value = null;
        if (!visited.Add(DictionaryIdentity(dictionary))) return false;

        if (dictionary.Lookup(key) is { } found)
        {
            value = found;
            return true;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (TryFindPlain(merged, key, visited, out value)) return true;
        }

        value = null;
        return false;
    }

    // A system resource dictionary can be shared by several merged branches.
    // Different adapters around that same native dictionary are still one node.
    private static object DictionaryIdentity(IThemeResourceDictionary dictionary)
        => dictionary is ResourceDictionaryNode native ? native.Identity : dictionary;
}

/// <summary>
/// 【主题统一】一次渲染产出的动态元素（表格/推荐卡/链接/代码/列表…）的主题重刷作用域：
/// 登记 [元素 + 属性 + 资源键]，主题变化时按元素【实际主题】就地重新解析并赋色。
/// 生命周期：容器 Loaded 订阅（并做一次按实际主题的校正刷色）、Unloaded 退订；
/// 默认登记用弱引用；固定渲染树在其作用域中保留绑定对象，避免原始 WinRT RCW
/// 被 GC 后虽然 native Run 仍显示、却丢失重刷登记。全程只改画刷，不重建元素
/// （因此流式内容、滚动位置、展开状态都保留）。
/// </summary>
public sealed class ThemeRefreshScope : IDisposable
{
    [ThreadStatic] private static ThemeRefreshScope? _ambient;
    private static readonly List<WeakReference<ThemeRefreshScope>> Live = [];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, ThemeRefreshScope> RenderedContent = new();
    private static bool _subscribed;

    private readonly record struct Entry(WeakReference<DependencyObject> Target, DependencyProperty Property, string Key, string? FallbackKey);

    private readonly WeakReference<FrameworkElement> _root;
    private FrameworkElement? _renderRoot;
    private readonly List<DependencyObject>? _retainedTargets;
    private readonly bool _useRootResources;
    private readonly List<Entry> _entries = [];
    private readonly List<FrameworkElement> _themeAncestors = [];
    private readonly Windows.Foundation.TypedEventHandler<FrameworkElement, object> _actualThemeChanged;
    private int _mountEpoch;
    private int _queuedEpoch = -1;
    private bool _loaded;
    private bool _attached;
    private bool _disposed;

    /// <summary>当前线程正在渲染的作用域：渲染期间登记自动挂到它上面。</summary>
    public static ThemeRefreshScope? Ambient => _ambient;

    private ThemeRefreshScope(FrameworkElement root, bool retainRenderedContent = false, bool useRootResources = false)
    {
        _root = new WeakReference<FrameworkElement>(root);
        _useRootResources = retainRenderedContent || useRootResources;
        _actualThemeChanged = OnActualThemeChanged;
        if (retainRenderedContent)
        {
            _renderRoot = root;
            _retainedTargets = [];
        }
    }

    /// <summary>为渲染容器建立作用域（Loaded 订阅 / Unloaded 退订）。</summary>
    public static ThemeRefreshScope Attach(FrameworkElement root, bool useRootResources = false)
    {
        var scope = new ThemeRefreshScope(root, useRootResources: useRootResources);
        scope.AttachToRoot(root);
        return scope;
    }

    /// <summary>
    /// For one fixed rendering only: retain its bound RCWs until this rendering is
    /// replaced or disposed. The weak-key table owns no application-wide strong root; the live
    /// theme subscription also remains weak. Re-rendering the same root replaces its old scope.
    /// Bound elements share the rendering root's theme, including lazily attached details.
    /// </summary>
    public static ThemeRefreshScope AttachRenderedContent(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (RenderedContent.TryGetValue(root, out var previous)) previous.Dispose();
        var scope = new ThemeRefreshScope(root, retainRenderedContent: true);
        RenderedContent.Add(root, scope);
        scope.AttachToRoot(root);
        return scope;
    }

    // A formerly independent RichTextBlock may be adopted by a new outer Markdown rendering.
    // Do not leave the old scope subscribed or retaining content removed by Blocks.Clear().
    internal static void ReleaseRenderedContent(FrameworkElement root)
    {
        if (RenderedContent.TryGetValue(root, out var previous) && !ReferenceEquals(previous, Ambient))
            previous.Dispose();
    }

    private void AttachToRoot(FrameworkElement root)
    {
        try
        {
            root.Loaded += OnRootLoaded;
            root.Unloaded += OnRootUnloaded;
            if (_retainedTargets is not null) root.ActualThemeChanged += _actualThemeChanged;
        }
        catch { }

        Subscribe();
        if (_retainedTargets is not null && root.IsLoaded)
        {
            _loaded = true;
            TrackThemeAncestors(root);
            QueueThemeRefresh();
        }
    }

    /// <summary>渲染期间把本作用域设为环境作用域（Dispose 恢复上一个）。</summary>
    public IDisposable Push()
    {
        var previous = _ambient;
        _ambient = this;
        return new AmbientPopper(previous);
    }

    /// <summary>登记 [元素 + 属性 + 资源键]：立即按当前主题赋色，并在主题变化时重刷。</summary>
    public void Bind(DependencyObject target, DependencyProperty property, string key, string? fallbackKey = null)
    {
        if (_disposed) return;
        try
        {
            _entries.Add(new Entry(new WeakReference<DependencyObject>(target), property, key, fallbackKey));
            _retainedTargets?.Add(target);
            var root = _renderRoot ?? (_root.TryGetTarget(out var r) ? r : null);
            var chain = ThemeResourceResolver.BuildChain(_useRootResources && root is not null ? root : target);
            Apply(target, property, chain, ResolveEntryThemeKey(target, root), key, fallbackKey);
        }
        catch { }
    }

    /// <summary>就地重刷全部登记元素（不重建元素）。</summary>
    public void RefreshAll()
    {
        if (_disposed) return;
        _entries.RemoveAll(e => !e.Target.TryGetTarget(out _));

        FrameworkElement? root = _renderRoot ?? (_root.TryGetTarget(out var r) ? r : null);
        List<IThemeResourceDictionary>? sharedChain = null;
        if (root is not null)
        {
            try { sharedChain = ThemeResourceResolver.BuildChain(root); }
            catch { sharedChain = null; }
        }

        foreach (var entry in _entries)
        {
            if (!entry.Target.TryGetTarget(out var target)) continue;
            try
            {
                var chain = sharedChain ?? ThemeResourceResolver.BuildChain(target);
                Apply(target, entry.Property, chain, ResolveEntryThemeKey(target, root), entry.Key, entry.FallbackKey);
            }
            catch { }
        }
    }

    /// <summary>退订：停止接收主题变化，保留渲染登记以便同一控件重新挂载。</summary>
    public void Detach()
    {
        _loaded = false;
        _mountEpoch++;
        _queuedEpoch = -1;
        UntrackThemeAncestors();
        if (!_attached) return;
        _attached = false;
        Live.RemoveAll(w => !w.TryGetTarget(out var s) || ReferenceEquals(s, this));
        RefreshSubscription();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        if (_root.TryGetTarget(out var root))
        {
            try
            {
                root.Loaded -= OnRootLoaded;
                root.Unloaded -= OnRootUnloaded;
                if (_retainedTargets is not null) root.ActualThemeChanged -= _actualThemeChanged;
            }
            catch { }
            if (RenderedContent.TryGetValue(root, out var current) && ReferenceEquals(current, this))
                RenderedContent.Remove(root);
        }
        _entries.Clear();
        _retainedTargets?.Clear();
        _renderRoot = null;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        Subscribe();
        if (_retainedTargets is not null && _root.TryGetTarget(out var root))
        {
            _loaded = true;
            TrackThemeAncestors(root);
        }
        // 元素此刻才真正进入可视树：ActualTheme / 页面级字典此时才有效，做一次校正刷色。
        RefreshAll();
        QueueThemeRefresh();
    }

    private void OnRootUnloaded(object sender, RoutedEventArgs e) => Detach();

    // Page.RequestedTheme changes do not have to broadcast ThemeService.ThemeChanged.
    // Cached descendants also need not raise their inherited event after remount.
    // Listen only to this rendering's current ancestors, and repaint after propagation.
    private void TrackThemeAncestors(FrameworkElement root)
    {
        UntrackThemeAncestors();
        for (var parent = VisualTreeHelper.GetParent(root); parent is not null;
            parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not FrameworkElement element) continue;
            _themeAncestors.Add(element);
            element.ActualThemeChanged += _actualThemeChanged;
        }
    }

    private void UntrackThemeAncestors()
    {
        foreach (var element in _themeAncestors) element.ActualThemeChanged -= _actualThemeChanged;
        _themeAncestors.Clear();
    }

    private void OnActualThemeChanged(FrameworkElement _, object __) => QueueThemeRefresh();

    private void QueueThemeRefresh()
    {
        if (_disposed || !_attached || !_loaded || _queuedEpoch == _mountEpoch || !_root.TryGetTarget(out var root)) return;
        var epoch = _mountEpoch;
        _queuedEpoch = epoch;
        if (!root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (_disposed || epoch != _mountEpoch) return;
            _queuedEpoch = -1;
            if (_attached && _loaded && _root.TryGetTarget(out var current) && current.IsLoaded) RefreshAll();
        })) _queuedEpoch = -1;
    }

    private void Subscribe()
    {
        if (_disposed || _attached) return;
        _attached = true;
        Live.Add(new WeakReference<ThemeRefreshScope>(this));
        RefreshSubscription();
    }

    private static void RefreshSubscription()
    {
        Live.RemoveAll(w => !w.TryGetTarget(out _));
        if (Live.Count == 0)
        {
            if (_subscribed)
            {
                try { ThemeService.ThemeChanged -= OnThemeChanged; } catch { }
                _subscribed = false;
            }
            return;
        }

        if (_subscribed) return;
        try
        {
            ThemeService.ThemeChanged += OnThemeChanged;
            _subscribed = true;
        }
        catch { }
    }

    private static void OnThemeChanged(ElementTheme theme) => BroadcastRefresh();

    /// <summary>主题变化的统一广播（离线验证可直接触发同一条重刷路径）。</summary>
    internal static void BroadcastRefresh()
    {
        Live.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var weak in Live.ToArray())
        {
            if (weak.TryGetTarget(out var scope)) scope.RefreshAll();
        }

        if (Live.Count == 0) RefreshSubscription();
    }

    internal static int LiveScopeCountForTest => Live.Count;
    internal static bool SubscribedForTest => _subscribed;
    internal int BindingCountForTest => _entries.Count;
    internal int RetainedTargetCountForTest => _retainedTargets?.Count ?? 0;
    internal bool IsDisposedForTest => _disposed;

    /// <summary>【测试】按渲染容器找作用域（验证 Unloaded/退订语义）。</summary>
    internal static ThemeRefreshScope? FindScopeForTest(FrameworkElement root)
    {
        foreach (var weak in Live)
        {
            if (weak.TryGetTarget(out var scope) &&
                scope._root.TryGetTarget(out var scopeRoot) &&
                ReferenceEquals(scopeRoot, root))
                return scope;
        }
        return null;
    }

    /// <summary>【测试】释放全部存活作用域，避免跨用例串扰。</summary>
    internal static void ResetForTest()
    {
        foreach (var rendering in RenderedContent.ToArray()) rendering.Value.Dispose();
        foreach (var weak in Live.ToArray())
        {
            if (weak.TryGetTarget(out var scope)) scope.Dispose();
        }
        Live.Clear();
        RefreshSubscription();
    }

    private string ResolveEntryThemeKey(DependencyObject target, FrameworkElement? root)
    {
        if (ThemeResourceResolver.CurrentThemeKeyOverrideForTest is { } forced) return forced;
        // A TextBlock inside a closed Expander can still report the Application's
        // theme. It belongs to this fixed rendering, not to a separate theme island.
        if (_useRootResources && root is not null)
            return ThemeResourceResolver.ThemeKeyOf(root.ActualTheme) ?? ThemeResourceResolver.ResolveThemeKey(root);
        return ThemeResourceResolver.ThemeKeyOf((target as FrameworkElement)?.ActualTheme)
            ?? ThemeResourceResolver.ThemeKeyOf(root?.ActualTheme)   // Run/Span 等无实际主题：随渲染容器
            ?? ThemeResourceResolver.ResolveThemeKey(target);
    }

    private static void Apply(DependencyObject target, DependencyProperty property,
        IReadOnlyList<IThemeResourceDictionary> chain, string themeKey, string key, string? fallbackKey)
    {
        var brush = ThemeResourceResolver.ResolveBrush(chain, themeKey, key, fallbackKey);
        if (brush is null) return;
        try { target.SetValue(property, brush); } catch { }
    }

    private sealed class AmbientPopper(ThemeRefreshScope? previous) : IDisposable
    {
        private readonly ThemeRefreshScope? _previous = previous;
        public void Dispose() => _ambient = _previous;
    }
}

/// <summary>
/// 【主题统一】"元素 + 属性 + 资源键"赋色的唯一入口：
/// 处于渲染作用域内则登记（主题变化时重刷），否则一次性赋色（按元素实际主题解析）。
/// </summary>
internal static class ThemeBrushBinder
{
    internal static void Apply(DependencyObject target, DependencyProperty property, string key, string? fallbackKey = null)
    {
        if (ThemeRefreshScope.Ambient is { } scope)
        {
            scope.Bind(target, property, key, fallbackKey);
            return;
        }

        ApplyUntracked(target, property, key, fallbackKey);
    }

    /// <summary>
    /// 一次性赋色（不登记重刷）：用于【运行期状态色】（执行中/成功/失败等由交互决定、且会被
    /// 后续代码覆盖的颜色），避免主题重刷把状态色冲回资源色。
    /// </summary>
    internal static void ApplyUntracked(DependencyObject target, DependencyProperty property, string key, string? fallbackKey = null)
    {
        try
        {
            var brush = ThemeResourceResolver.ResolveBrush(target, key, fallbackKey);
            if (brush is not null) target.SetValue(property, brush);
        }
        catch { }
    }
}
