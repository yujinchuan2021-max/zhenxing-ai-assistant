using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// Code-only native controls in the empty STA TestApp. These cases never create the
/// product App/page, call a model, open a link, execute a command, or scan ToolCatalog.
/// The host strips XBF/framework templates, so this is layout/state evidence, not
/// a screenshot acceptance test of the shipped application.
/// </summary>
internal static class CardsCases
{
    public static (string Name, Func<Task> Body)[] All() =>
    [
        ("Cards_WindowBaseline", WindowBaseline),
        ("Cards_ButtonTemplateBaseline", ButtonTemplateBaseline),
        ("Cards_ExpanderTemplateBaseline", ExpanderTemplateBaseline),
        ("Cards_ResponsiveLayout", ResponsiveLayout),
        ("Cards_SelectionStates", () => { SelectionStates(); return Task.CompletedTask; }),
        ("Cards_DetailsLocalization", DetailsLocalization),
        ("Cards_ReadOnlyMarkdown", () => { ReadOnlyMarkdown(); return Task.CompletedTask; }),
    ];

    private static ToolFlowRecommendationSet Demo() => new(
        "Build a demo game", "medium",
        [Option("light"), Option("medium"), Option("heavy", available: false)],
        [new ToolFlowConversationMessage { Role = "user", Content = "Build a demo game" }]);

    private static ToolFlowRecommendationOption Option(string id, bool available = true) => new(
        id, id + " demo name", id + " summary", id + " fit", id + " cost",
        id + " requirements", [id + " warning 1", id + " warning 2"],
        [new ToolFlowItem
        {
            ItemId = id + "-agent", Name = id + " agent", Kind = "agent",
            Version = "demo version 1.2.3", SourceUrl = "https://example.com/agent",
            DownloadUrl = "https://example.com/agent/download", InstallTargetKey = "demo-agent",
        }], available, available ? "" : "demo unavailable reason", "demo only");

    private static void RequireIsolatedData()
    {
        var declared = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(declared), "Cards cases require a fresh ZXAI_DATA_ROOT.");
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.Equal(Path.GetFullPath(declared!), Path.GetFullPath(DataRoots.EffectiveTestRoot!), ignoreCase: true);
    }

    private static async Task ResponsiveLayout()
    {
        Step("responsive/isolation-check");
        RequireIsolatedData();
        Step("responsive/isolation-ready");
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        Step("responsive/theme-ready");
        var control = new ToolFlowCardsControl(Demo()) { Width = 1020, HorizontalAlignment = HorizontalAlignment.Left };
        Step("responsive/cards-created");
        await InOffscreenWindow(control, async () =>
        {
            Assert.Equal(3, control.Cards.Count);
            Assert.Equal(3, control.ColumnDefinitions.Count);
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(0, Grid.GetRow(control.Cards[i]));
                Assert.Equal(i, Grid.GetColumn(control.Cards[i]));
                Assert.Contains(control.Cards[i].OptionId + " demo name", Texts(control.Cards[i]));
                Assert.Contains(control.Cards[i].OptionId + " summary", Texts(control.Cards[i]));
                Assert.Contains(control.Cards[i].OptionId + " fit", Texts(control.Cards[i]));
                Assert.Contains(control.Cards[i].OptionId + " cost", Texts(control.Cards[i]));
                Assert.Contains(control.Cards[i].OptionId + " requirements", Texts(control.Cards[i]));
            }

            var sameCards = control.Cards.ToArray();
            control.Width = 360;
            control.UpdateLayout();
            await Task.Yield();
            control.UpdateLayout();
            Assert.Equal(1, control.ColumnDefinitions.Count);
            Assert.Equal(3, control.RowDefinitions.Count);
            for (var i = 0; i < 3; i++)
            {
                Assert.Same(sameCards[i], control.Cards[i]);
                Assert.Equal(i, Grid.GetRow(control.Cards[i]));
                Assert.Equal(0, Grid.GetColumn(control.Cards[i]));
            }
        });
    }

    private static async Task WindowBaseline()
    {
        RequireIsolatedData();
        // Same empty application and window path as the real card cases, with
        // only core XAML primitives. No default control templates or AppFonts.
        var text = new TextBlock { Text = "native window baseline" };
        var root = new Grid { Width = 1020, Children = { text } };
        Step("baseline/primitives-created");
        await InOffscreenWindow(root, () =>
        {
            Assert.True(root.ActualWidth > 0, "Baseline was not laid out.");
            Assert.True(text.ActualWidth > 0, "Baseline text was not laid out.");
            return Task.CompletedTask;
        });
    }

    private static async Task ButtonTemplateBaseline()
    {
        RequireIsolatedData();
        var button = new Button { Content = "demo" };
        var root = new Grid { Width = 1020, Children = { button } };
        Step("button-baseline/created");
        await InOffscreenWindow(root, () =>
        {
            Assert.True(button.ActualWidth > 0, "Button baseline was not laid out.");
            Assert.Equal("demo", button.Content);
            return Task.CompletedTask;
        });
    }

    private static async Task ExpanderTemplateBaseline()
    {
        RequireIsolatedData();
        var text = new TextBlock { Text = "demo detail" };
        var expander = new Expander { Header = "demo", Content = text };
        var root = new Grid { Width = 1020, Children = { expander } };
        Step("expander-baseline/created");
        await InOffscreenWindow(root, () =>
        {
            Assert.True(expander.ActualWidth > 0, "Expander baseline was not laid out.");
            Assert.Equal("demo", expander.Header);
            Assert.Same(text, expander.Content);
            return Task.CompletedTask;
        });
    }

    private static void SelectionStates()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var cards = new ToolFlowCardsControl(Demo()).Cards;
        var normal = cards[0];
        Assert.False(normal.SelectButton.IsEnabled); // initial/streaming
        normal.SetActionState(ready: false, busy: false, failed: true);
        Assert.False(normal.SelectButton.IsEnabled);
        normal.SetActionState(ready: true, busy: false, failed: true);
        Assert.False(normal.SelectButton.IsEnabled); // contradictory flags fail closed
        normal.SetActionState(ready: true, busy: true);
        Assert.False(normal.SelectButton.IsEnabled);
        normal.SetActionState(ready: true, busy: false);
        Assert.True(normal.SelectButton.IsEnabled);
        normal.SetActionState(ready: true, busy: false, selected: true);
        Assert.False(normal.SelectButton.IsEnabled);
        cards[2].SetActionState(ready: true, busy: false);
        Assert.False(cards[2].SelectButton.IsEnabled); // unavailable remains readable
        Assert.NotNull(Demo().ToProposal("light"));
        Assert.Null(Demo().ToProposal("heavy"));
    }

    private static async Task DetailsLocalization()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        // Change only the in-memory fallback language; do not initialize Localizer,
        // save a setting or broadcast to any product service.
        var field = typeof(LocalizationService).GetField("_currentLanguage", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Localization fallback field missing.");
        var oldLanguage = (string?)field.GetValue(null);
        try
        {
            field.SetValue(null, LocalizationService.ChineseLanguage);
            var control = new ToolFlowCardsControl(Demo()) { Width = 1020 };
            var card = control.Cards[1];
            card.SetActionState(ready: true, busy: false);
            await InOffscreenWindow(control, async () =>
            {
                var expander = Flatten(card).OfType<Expander>().Single();
                Assert.False(expander.IsExpanded);
                expander.IsExpanded = true;
                await Task.Yield();
                control.UpdateLayout();
                Assert.True(expander.IsExpanded);
                var details = Assert.IsType<StackPanel>(expander.Content);
                var option = Demo().Options[1];
                foreach (var value in new[] { option.Summary, option.Fit, option.Cost, option.Requirements,
                    "medium agent", "demo version 1.2.3", "medium warning 1", "medium warning 2" })
                    Assert.Contains(value, Texts(details));
                var links = Flatten(details).OfType<HyperlinkButton>().ToArray();
                Assert.Equal(2, links.Length);
                // Web destinations are handled by InternalBrowserLink; setting
                // NavigateUri would also launch the operating system browser.
                Assert.All(links, link => Assert.Null(link.NavigateUri));
                Assert.All(links, link => Assert.Equal("example.com", link.Content));
                Assert.Equal("https", new Uri(option.Items[0].SourceUrl!).Scheme);
                Assert.Equal("https", new Uri(option.Items[0].DownloadUrl!).Scheme);
                Assert.Contains("中量", Texts(card));

                field.SetValue(null, LocalizationService.EnglishLanguage);
                control.ApplyLocalization();
                Assert.Same(expander, Flatten(card).OfType<Expander>().Single());
                Assert.Same(details, expander.Content);
                Assert.True(expander.IsExpanded);
                Assert.True(card.SelectButton.IsEnabled);
                Assert.Equal("Plan details", expander.Header);
                Assert.Contains("Medium", Texts(card));
                Assert.Equal("Choose Medium", card.SelectButton.Content);
                Assert.Contains("medium requirements", Texts(details));
            });
        }
        finally { field.SetValue(null, oldLanguage); }
    }

    private static void ReadOnlyMarkdown()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        // allowActions:false must also bypass local ToolCatalog lookup, even when
        // a recommended tool name happens to match a bundled executable.
        var root = AiMarkdownRenderer.Render("""
            [RECOMMEND_TOOL] CPU-Z | reason=demo recommendation

            [ACTION] {"kind":"run_command","desc":"demo command description","command":"should-never-run"}
            """, allowActions: false);
        var nodes = Flatten(root);
        Assert.Empty(nodes.OfType<Button>());
        Assert.Contains("CPU-Z", Texts(root));
        Assert.DoesNotContain("should-never-run", string.Join("\n", Texts(root).Concat(nodes.OfType<Run>().Select(x => x.Text))));
    }

    private static async Task InOffscreenWindow(FrameworkElement control, Func<Task> verify)
    {
        // Keep one owned native Window alive through the suite. Closing WinUI's
        // last Window between async cases can end the dispatcher while a later
        // case is awaiting layout, or invalidate cached native template owners.
        await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
        {
            var loaded = new TaskCompletionSource();
            control.Loaded += (_, _) => loaded.TrySetResult();
            Step("window/shared-host/" + control.GetType().Name);
            root.Children.Add(control);
            Step("window/content-attached");
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1150, 900));
            Step("window/moved-offscreen");
            window.AppWindow.Show(false);
            Step("window/show-called");
            Assert.True(await Task.WhenAny(loaded.Task, Task.Delay(8000)) == loaded.Task, "Cards root did not load.");
            Step("window/loaded");
            await Task.Yield();
            control.UpdateLayout();
            Step("window/layout-updated");
            await verify();
        });
    }

    private static void Step(string stage)
    {
        Program.Diag("cards-step " + stage);
        Console.WriteLine("CARDS_STEP|" + stage);
        Console.Out.Flush();
    }

    private static string[] Texts(DependencyObject root) => Flatten(root).OfType<TextBlock>().Select(x => x.Text).ToArray();

    private static IReadOnlyList<DependencyObject> Flatten(DependencyObject root)
    {
        var nodes = new List<DependencyObject>();
        void Walk(DependencyObject node)
        {
            nodes.Add(node);
            switch (node)
            {
                case Panel panel: foreach (var child in panel.Children) Walk(child); break;
                case Border border when border.Child is { } child: Walk(child); break;
                case Expander expander:
                    if (expander.Header is DependencyObject header) Walk(header);
                    if (expander.Content is DependencyObject content) Walk(content);
                    break;
                case ContentControl control when control.Content is DependencyObject child: Walk(child); break;
                case RichTextBlock rich: foreach (var block in rich.Blocks) Walk(block); break;
                case Paragraph paragraph: foreach (var inline in paragraph.Inlines) Walk(inline); break;
                case Span span: foreach (var inline in span.Inlines) Walk(inline); break;
            }
        }
        Walk(root);
        return nodes;
    }
}
