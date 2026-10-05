using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Ai;
using TubaWinUi3.Services.ToolFlows;
using Windows.UI;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>Real inherited-theme events, original native wrappers and restored message rendering.
/// No forced theme-key override, hand-issued theme broadcast, model, installation or navigation.</summary>
internal static class ReadabilityCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("Readability_LocalDictionaryKeepsExplicitThemeLookup", LocalDictionaryLookup),
        ("Readability_CardsMarkdown_RealThemesAndRemount", CardsMarkdown),
        ("Readability_RealPageMessagesAndHistory", PageMessages),
    ];

    private const string Markdown = """
        ## 准备你的工具

        先核对模型接入，再打开项目。

        - 复用已有工具
        - 查看[官方说明](https://example.com/guide)

        | 工具 | 用途 |
        | --- | --- |
        | 桌面 Agent | 处理项目 |

        `示例代码文字`
        """;

    private static ToolFlowRecommendationSet Demo() => new("开发一个小型 2D 游戏", "medium",
        [Option("light"), Option("medium"), Option("heavy")],
        [new() { Role = "user", Content = "开发一个小型 2D 游戏" }]);

    private static ToolFlowRecommendationOption Option(string id) => new(id,
        id + " 桌面开发方案", id + " 简短方案说明", id + " 适用范围", id + " 成本说明",
        id + " 使用条件", [id + " 需要核对账号"],
        [new() { ItemId = id + "-agent", Name = id + " 桌面 Agent", Kind = "agent", Version = "测试版本",
            SourceUrl = "https://example.com/agent", DownloadUrl = "https://example.com/download" }],
        true, "", "只读合成方案，禁止执行。");

    private static Task LocalDictionaryLookup() => WithResources(() =>
    {
        // These are real WinUI dictionaries under App.Dark, not the pure resolver
        // dictionary doubles. A local miss must remain a miss even when WinUI could
        // otherwise supply the application's brush through implicit lookup.
        var local = new ResourceDictionary { ["ReadabilityLocalMarker"] = "local" };
        var node = new ResourceDictionaryNode(local);
        Assert.Null(node.Lookup("TextFillColorSecondaryBrush"));
        Assert.Equal("local", node.Lookup("ReadabilityLocalMarker"));

        var merged = new ResourceDictionary { ["ReadabilityMergedMarker"] = "merged" };
        merged.ThemeDictionaries["Light"] = new ResourceDictionary { ["ReadabilityThemeMarker"] = "light" };
        merged.ThemeDictionaries["Dark"] = new ResourceDictionary { ["ReadabilityThemeMarker"] = "dark" };
        merged.ThemeDictionaries["HighContrast"] = new ResourceDictionary { ["ReadabilityThemeMarker"] = "contrast" };
        local.MergedDictionaries.Add(merged);
        var ownedKeys = new List<string>();
        foreach (var key in local.Keys) ownedKeys.Add(key?.ToString() ?? "<null>");
        Console.WriteLine("READABILITY_LOCAL_KEYS|" + string.Join("|", ownedKeys));
        Assert.Equal(new[] { "ReadabilityLocalMarker" }, ownedKeys);
        Assert.Null(node.Lookup("ReadabilityMergedMarker"));
        Assert.Null(node.Lookup("ReadabilityThemeMarker"));
        foreach (var (theme, expected) in new[] { ("Light", "light"), ("Dark", "dark"), ("HighContrast", "contrast") })
        {
            Assert.True(ThemeResourceResolver.TryResolveInDictionary(node, theme, "ReadabilityThemeMarker", out var themed));
            Assert.Equal(expected, themed);
            Assert.True(ThemeResourceResolver.TryResolveInDictionary(node, theme, "ReadabilityMergedMarker", out var plain));
            Assert.Equal("merged", plain);
        }

        // WinUI's public merged collection enforces single ownership. Model a
        // repeated encounter by wrapping the same real native dictionary twice,
        // then reuse the traversal's visited set across the two encounters.
        var leaf = new ResourceDictionary { ["ReadabilityLeaf"] = "leaf" };
        var parent = new ResourceDictionary();
        parent.MergedDictionaries.Add(leaf);
        var nativeNodes = new[] { parent, leaf };
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var search = typeof(ThemeResourceResolver).GetMethod("TryFindPlain", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] arguments = [new ResourceDictionaryNode(parent), "ReadabilityMissing", visited, null];
        Assert.False(Assert.IsType<bool>(search.Invoke(null, arguments)));
        Assert.Equal(nativeNodes.Length, visited.Count);
        foreach (var dictionary in nativeNodes) Assert.Contains(dictionary, visited);
        arguments[0] = new ResourceDictionaryNode(parent);
        Assert.False(Assert.IsType<bool>(search.Invoke(null, arguments)));
        Assert.Equal(nativeNodes.Length, visited.Count);
        Console.WriteLine($"READABILITY_NATIVE_IDENTITY|adapters=2|native={nativeNodes.Length}|visited={visited.Count}");

        var owner = new Grid { RequestedTheme = ElementTheme.Light };
        var ownerScope = ToolFlowThemeResources.Attach(owner);
        try
        {
            var text = new TextBlock { Text = "尚未挂载的准备步骤" };
            Assert.False(owner.IsLoaded);
            Assert.Null(VisualTreeHelper.GetParent(text));
            ToolFlowThemeResources.BindText(ownerScope, text, ToolFlowThemeResources.PrimaryText);
            Assert.Equal(Color.FromArgb(255, 0x2B, 0x2B, 0x30), Solid(text.Foreground));
        }
        finally { ownerScope.Dispose(); }
        return Task.CompletedTask;
    });

    private static Task CardsMarkdown() => WithResources(async () =>
    {
        Trace("cards-before-window");
        await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
        {
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1180, 1100));
            var host = new Grid { RequestedTheme = ElementTheme.Light, Padding = new Thickness(20) };
            ToolFlowThemeResources.AddPalette(host);
            var content = new StackPanel { Spacing = 18 };
            var scroll = new ScrollViewer { Content = content, HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            host.Children.Add(scroll);
            Trace("cards-before-create");
            var cards = new ToolFlowCardsControl(Demo());
            foreach (var card in cards.Cards) card.SetActionState(ready: true, busy: false);
            Trace("cards-created-before-markdown");
            var markdown = AiMarkdownRenderer.Render(Markdown, allowActions: false);
            content.Children.Add(cards);
            content.Children.Add(markdown);
            Collect(); // Before enumeration can pin the original wrappers; the app is Dark here.
            Trace("cards-before-attach");
            root.Children.Add(host);
            await Settle(root);
            Trace("cards-attached");
            Assert.Equal(ApplicationTheme.Dark, Application.Current.RequestedTheme);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                Trace("cards-theme-" + theme);
                host.RequestedTheme = theme;
                Collect();
                await Settle(root);
                PaintCanvas(host);
                AssertCards(cards, host, theme);
                AssertMarkdown(markdown, host, theme);
            }

            // Details are created after the card inherited Light. Their unattached
            // TextBlocks must not temporarily use the application's Dark palette.
            var middle = cards.Cards[1];
            var expander = Nodes(middle).OfType<Expander>().Single();
            expander.IsExpanded = true;
            Trace("cards-details-expanded");
            Collect();
            await Settle(root);
            var details = Assert.IsType<StackPanel>(expander.Content);
            Assert.Contains(Nodes(details).OfType<TextBlock>(), text => text.Text == "medium 桌面 Agent");
            AssertCards(cards, host, ElementTheme.Light);
            if (Preview) await GoalGuideCases.SaveControlPreviewAsync(host, "readability-cards-markdown-light.png");

            var textBefore = Text(markdown);
            root.Children.Remove(host);
            Trace("cards-detached");
            await Settle(root);
            Assert.False(host.IsLoaded);
            host.RequestedTheme = ElementTheme.Dark;
            root.Children.Add(host);
            await Settle(root);
            Trace("cards-remounted");
            foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
            {
                host.RequestedTheme = theme;
                Collect();
                await Settle(root);
                PaintCanvas(host);
                AssertCards(cards, host, theme);
                AssertMarkdown(markdown, host, theme);
                Assert.Same(details, expander.Content);
                Assert.True(expander.IsExpanded);
                Assert.Equal(textBefore, Text(markdown));
                Assert.True(middle.SelectButton.IsEnabled);
            }

            content.Children.Remove(markdown);
            await Settle(root);
            var oldRun = Nodes(markdown).OfType<Run>().First(run => run.Text.Contains("先核对"));
            var oldColor = Solid(oldRun.Foreground);
            markdown = AiMarkdownRenderer.Render(Markdown + "\n\n重新恢复的正文。", allowActions: false);
            Collect();
            content.Children.Add(markdown);
            host.RequestedTheme = ElementTheme.Dark;
            await Settle(root);
            PaintCanvas(host);
            AssertMarkdown(markdown, host, ElementTheme.Dark);
            Assert.Equal(oldColor, Solid(oldRun.Foreground)); // detached render no longer repaints
            host.RequestedTheme = ElementTheme.Light;
            await Settle(root);
            PaintCanvas(host);
            AssertMarkdown(markdown, host, ElementTheme.Light);
            if (Preview) await GoalGuideCases.SaveControlPreviewAsync(host, "readability-cards-markdown-remount-light.png");
        });
    });

    private static Task PageMessages() => WithResources(async () =>
    {
        var resolver = AgentEngine.RuntimeResolverOverrideForTest;
        var probe = typeof(AgentEngine).GetField("_probeStarted", BindingFlags.Static | BindingFlags.NonPublic)!;
        var state = typeof(AgentEngine).GetField("_dshState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldProbe = probe.GetValue(null);
        var oldState = state.GetValue(null);
        var runtimeCalls = 0;
        AiAgentPage? page = null;
        try
        {
            AgentEngine.RuntimeResolverOverrideForTest = () =>
            { runtimeCalls++; throw new InvalidOperationException("Readability fixtures cannot start a real runtime."); };
            probe.SetValue(null, 1);
            state.SetValue(null, 2);
            await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
            {
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1320, 1100));
                page = new AiAgentPage(compact: false, autoLoadLatest: false) { RequestedTheme = ElementTheme.Light };
                root.Children.Add(page);
                await Settle(root);
                Invoke(page, "AddUserBubble", "我想开发一个小型 2D 游戏。");
                Invoke(page, "AddRestoredAssistant", Markdown, "", true);
                Invoke(page, "AddSystemBubble", "已应用技能：枕星目标助手。准备记录不代表目标已完成。");
                Invoke(page, "AddErrorBubble", "只读测试错误提示，未执行任何安装。");
                var messages = Assert.IsType<StackPanel>(page.FindName("MsgPanel"));
                Collect();
                await Settle(root);
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
                {
                    page.RequestedTheme = theme;
                    Collect();
                    await Settle(root);
                    AssertMessageColors(page, messages, theme);
                }
                if (Preview) await GoalGuideCases.SaveControlPreviewAsync(page, "readability-real-page-messages-light.png");

                // Use the same real entry used when old display history is restored.
                Invoke(page, "AddRestoredAssistant", CardReply(), "", true);
                Collect();
                await Settle(root);
                var cards = Assert.Single(Nodes(messages).OfType<ToolFlowCardsControl>());
                Assert.Equal(3, cards.Cards.Count);
                AssertCards(cards, page, ElementTheme.Light);
                root.Children.Remove(page);
                await Settle(root);
                page.RequestedTheme = ElementTheme.Dark;
                root.Children.Add(page);
                await Settle(root);
                AssertMessageColors(page, messages, ElementTheme.Dark);
                AssertCards(cards, page, ElementTheme.Dark);
                page.RequestedTheme = ElementTheme.Light;
                Collect();
                await Settle(root);
                AssertMessageColors(page, messages, ElementTheme.Light);
                AssertCards(cards, page, ElementTheme.Light);
                if (Preview) await GoalGuideCases.SaveControlPreviewAsync(page, "readability-real-page-restored-cards-light.png");
                root.Children.Remove(page);
                page.Unload();
                page = null;
            });
            Assert.Equal(0, runtimeCalls);
        }
        finally
        {
            page?.Unload();
            probe.SetValue(null, oldProbe);
            state.SetValue(null, oldState);
            AgentEngine.RuntimeResolverOverrideForTest = resolver;
        }
    });

    private static string CardReply() => "推荐中量方案。\n```toolflow-options\n" + JsonSerializer.Serialize(new
    {
        schema = 1, goal = Demo().Goal, recommended = "medium", options = Demo().Options.Select(option => new
        {
            id = option.Id, name = option.Name, summary = option.Summary, fit = option.Fit,
            cost = option.Cost, requirements = option.Requirements, warnings = option.Warnings,
            items = option.Items.Select(item => new { name = item.Name, type = item.Kind, version = item.Version,
                sourceUrl = item.SourceUrl, downloadUrl = item.DownloadUrl, installTargetKey = "" }),
        }),
    }) + "\n```";

    private static void AssertCards(ToolFlowCardsControl cards, FrameworkElement host, ElementTheme theme)
    {
        Assert.Equal(theme, cards.ActualTheme);
        foreach (var card in cards.Cards)
        {
            Assert.Equal(theme, card.ActualTheme);
            var surface = Assert.IsType<Border>(Assert.Single(card.Children));
            Assert.Equal(BrandCanvas(theme), Solid(surface.Background));
            var texts = Nodes(card).OfType<TextBlock>().Where(text => !string.IsNullOrWhiteSpace(text.Text)).ToArray();
            Assert.Contains(texts, text => text.Text == card.OptionId + " 桌面开发方案");
            Assert.Contains(texts, text => text.Text == card.OptionId + " 适用范围");
            Assert.Contains(texts, text => text.Text == card.OptionId + " 桌面 Agent");
            Assert.Contains(texts, text => text.Text == card.OptionId + " 需要核对账号");
            foreach (var text in texts) AssertContrast(Backdrop(text, card, Canvas(host)), text.Foreground,
                $"card {text.Text} [host={theme}, card={card.ActualTheme}, text={text.ActualTheme}]");
        }
    }

    private static void AssertMarkdown(StackPanel markdown, FrameworkElement host, ElementTheme theme)
    {
        Assert.Equal(theme, markdown.ActualTheme);
        var runs = Nodes(markdown).OfType<Run>().Where(run => !string.IsNullOrWhiteSpace(run.Text)).ToArray();
        Assert.Contains(runs, run => run.Text.Contains("先核对"));
        Assert.Contains(runs, run => run.Text == "官方说明");
        foreach (var run in runs) AssertContrast(Canvas(host), run.Foreground, "markdown " + run.Text);
        foreach (var text in Nodes(markdown).OfType<TextBlock>().Where(text => !string.IsNullOrWhiteSpace(text.Text)))
            AssertContrast(Backdrop(text, markdown, Canvas(host)), text.Foreground, "table " + text.Text);
    }

    private static void AssertMessageColors(AiAgentPage page, StackPanel messages, ElementTheme theme)
    {
        Assert.Equal(theme, page.ActualTheme);
        foreach (var bubble in messages.Children.OfType<Border>())
        {
            var backdrop = Composite(bubble.Background, Canvas(page));
            foreach (var text in Nodes(bubble).OfType<TextBlock>().Where(text => !string.IsNullOrWhiteSpace(text.Text)))
            {
                AssertContrast(Backdrop(text, bubble, Canvas(page)), text.Foreground,
                    $"message {text.Text} [page={theme}, bubble={bubble.ActualTheme}, text={text.ActualTheme}]");
            }
            foreach (var run in Nodes(bubble).OfType<Run>().Where(run => !string.IsNullOrWhiteSpace(run.Text)))
                AssertContrast(backdrop, run.Foreground, "message markdown " + run.Text);
        }
    }

    private static void PaintCanvas(Grid host) => host.Background = ThemeResourceResolver.ResolveBrush(host, ToolFlowThemeResources.Canvas);
    private static Color Canvas(FrameworkElement host) => Solid(ThemeResourceResolver.ResolveBrush(host,
        host is AiAgentPage ? "AssistantCanvasBrush" : ToolFlowThemeResources.Canvas));
    private static Color BrandCanvas(ElementTheme theme) => theme == ElementTheme.Dark
        ? Color.FromArgb(255, 0x21, 0x21, 0x23) : Color.FromArgb(255, 0xFC, 0xFB, 0xF9);
    private static Color Solid(Brush? brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static Color Backdrop(FrameworkElement text, FrameworkElement boundary, Color canvas)
    {
        var brushes = new List<Brush>();
        for (DependencyObject? parent = VisualTreeHelper.GetParent(text); parent is not null;
            parent = VisualTreeHelper.GetParent(parent))
        {
            var brush = parent switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                Control control => control.Background,
                _ => null,
            };
            if (brush is not null) brushes.Add(brush);
            if (ReferenceEquals(parent, boundary)) break;
        }
        for (var i = brushes.Count - 1; i >= 0; i--) canvas = Composite(brushes[i], canvas);
        return canvas;
    }
    private static Color Composite(Brush? brush, Color behind)
    {
        if (brush is null) return behind;
        var solid = Assert.IsType<SolidColorBrush>(brush);
        var foreground = solid.Color;
        var alpha = foreground.A / 255d * solid.Opacity;
        byte Blend(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
        return Color.FromArgb(255, Blend(foreground.R, behind.R), Blend(foreground.G, behind.G), Blend(foreground.B, behind.B));
    }
    private static void AssertContrast(Color background, Brush? foreground, string label)
    {
        static double L(Color color)
        {
            static double C(byte channel) { var n = channel / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
        }
        var a = L(background);
        var b = L(Composite(foreground, background));
        var ratio = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        Assert.True(ratio >= 4.5, $"{label}: {ratio:F2}:1 contrast; background={background}; foreground={Solid(foreground)}");
    }
    private static string Text(DependencyObject root) => string.Join("|", Nodes(root).Select(node => node switch
    { TextBlock text => text.Text, Run run => run.Text, _ => "" }).Where(value => value.Length > 0));
    private static IReadOnlyList<DependencyObject> Nodes(DependencyObject root)
    {
        var result = new List<DependencyObject>();
        void Walk(DependencyObject node)
        {
            result.Add(node);
            switch (node)
            {
                case Panel panel: foreach (var panelChild in panel.Children) Walk(panelChild); break;
                case Border border when border.Child is { } borderChild: Walk(borderChild); break;
                case Expander expander:
                    if (expander.Header is DependencyObject expanderHeader) Walk(expanderHeader);
                    if (expander.Content is DependencyObject expanderContent) Walk(expanderContent);
                    break;
                case ContentControl control when control.Content is DependencyObject controlContent: Walk(controlContent); break;
                case RichTextBlock rich: foreach (var block in rich.Blocks) Walk(block); break;
                case Paragraph paragraph: foreach (var paragraphInline in paragraph.Inlines) Walk(paragraphInline); break;
                case Span span: foreach (var spanInline in span.Inlines) Walk(spanInline); break;
            }
        }
        Walk(root);
        return result;
    }
    private static async Task WithResources(Func<Task> body,
        [System.Runtime.CompilerServices.CallerMemberName] string caseName = "")
    {
        Trace(caseName + "-resources-enter");
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT")));
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        Assert.Equal(ApplicationTheme.Dark, Application.Current.RequestedTheme);
        Assert.Null(ThemeResourceResolver.CurrentThemeKeyOverrideForTest);
        Assert.Null(ThemeResourceResolver.ExtraRootDictionariesForTest);
        await body();
    }
    private static void Trace(string step) { Console.WriteLine("READABILITY_STAGE|" + step); Console.Out.Flush(); }
    private static object? Invoke(AiAgentPage page, string name, params object?[] args) =>
        (typeof(AiAgentPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing fixture entry: " + name)).Invoke(page, args);
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static async Task Settle(Grid root) { await Task.Delay(300); root.UpdateLayout(); }
    private static bool Preview => Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1";
}
