using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Ai;
using Windows.UI;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>Exercise the real conversation-menu handler and native popup templates.
/// The metadata is synthetic; no rename/delete action, model or installation runs.</summary>
internal static class ConversationMenuThemeCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("ConversationMenu_ActualPopupThemesAndReopen", ActualPopupThemesAndReopen),
    ];

    private static async Task ActualPopupThemesAndReopen()
    {
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT")));
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        Assert.Equal(ApplicationTheme.Dark, Application.Current.RequestedTheme);
        Assert.Null(ThemeResourceResolver.CurrentThemeKeyOverrideForTest);
        Assert.Null(ThemeResourceResolver.ExtraRootDictionariesForTest);

        var resolver = AgentEngine.RuntimeResolverOverrideForTest;
        var probe = typeof(AgentEngine).GetField("_probeStarted", BindingFlags.Static | BindingFlags.NonPublic)!;
        var state = typeof(AgentEngine).GetField("_dshState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldProbe = probe.GetValue(null);
        var oldState = state.GetValue(null);
        var runtimeCalls = 0;
        AiAgentPage? page = null;
        Popup? popup = null;
        try
        {
            AgentEngine.RuntimeResolverOverrideForTest = () =>
            { runtimeCalls++; throw new InvalidOperationException("Menu fixtures cannot start a real runtime."); };
            probe.SetValue(null, 1);
            state.SetValue(null, 2);
            await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
            {
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1100, 780));
                page = new AiAgentPage(compact: false, autoLoadLatest: false) { RequestedTheme = ElementTheme.Light };
                var meta = new ConversationMeta { Id = "native-menu-no-persist", Title = "菜单主题验收", CreatedAt = new DateTime(2026, 10, 5) };
                var itemType = typeof(AiAgentPage).GetNestedType("ConversationListItem", BindingFlags.NonPublic)!;
                var listItem = Activator.CreateInstance(itemType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, args: [meta.Id, meta.Title, "10-05 09:00", meta], culture: null);
                var anchor = new Button
                {
                    Content = "…", DataContext = listItem, Width = 36, Height = 32,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(150, 100, 0, 0),
                };
                var pageRoot = Assert.IsType<Grid>(page.Content);
                pageRoot.Children.Add(anchor);
                // These are independent native ThemeResource expressions attached
                // to the visible owner's tree, not the production menu's binding
                // helper and not our managed resource resolver.
                var normalReference = ThemeReference("MenuFlyoutItemForeground");
                var hoverReference = ThemeReference("MenuFlyoutItemForegroundPointerOver");
                pageRoot.Children.Add(normalReference);
                pageRoot.Children.Add(hoverReference);
                root.Children.Add(page);
                await Settle(root);
                Assert.True(anchor.IsLoaded && anchor.XamlRoot is not null);

                // Keep the same actual popup open while its owner changes theme.
                // No test-side resource replacement or theme broadcast is allowed.
                popup = await OpenMenu(page, anchor, root);
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
                {
                    page.RequestedTheme = theme;
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    await Settle(root);
                    Assert.True(popup.IsOpen);
                    await AssertPopup(popup, anchor, theme, root, "live", normalReference, hoverReference);
                }
                popup.IsOpen = false;
                await Settle(root);

                // Each ordinary click constructs a fresh menu. Reopening must use
                // the current owner rather than the application's startup theme.
                foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
                {
                    page.RequestedTheme = theme;
                    await Settle(root);
                    popup = await OpenMenu(page, anchor, root);
                    await AssertPopup(popup, anchor, theme, root, "reopen", normalReference, hoverReference);
                    popup.IsOpen = false;
                    await Settle(root);
                }
                root.Children.Remove(page);
                page.Unload();
                page = null;
            });
            Assert.Equal(0, runtimeCalls);
        }
        finally
        {
            if (popup is not null) popup.IsOpen = false;
            page?.Unload();
            probe.SetValue(null, oldProbe);
            state.SetValue(null, oldState);
            AgentEngine.RuntimeResolverOverrideForTest = resolver;
        }
    }

    private static async Task<Popup> OpenMenu(AiAgentPage page, Button anchor, Grid root)
    {
        var handler = typeof(AiAgentPage).GetMethod("ConversationMenuButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(page, [anchor, new RoutedEventArgs()]);
        await Settle(root);
        return Assert.Single(VisualTreeHelper.GetOpenPopupsForXamlRoot(anchor.XamlRoot)
            .Where(candidate => candidate.IsOpen && candidate.Child is { } child &&
                Nodes(child).OfType<MenuFlyoutItem>().Count() == 2));
    }

    private static async Task AssertPopup(Popup popup, Button anchor, ElementTheme theme, Grid root, string phase,
        TextBlock normalReference, TextBlock hoverReference)
    {
        Assert.Equal(theme, anchor.ActualTheme);
        var child = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
        var presenter = Assert.Single(Nodes(child).OfType<MenuFlyoutPresenter>());
        Assert.Equal(theme, presenter.ActualTheme);
        Console.WriteLine($"CONVERSATION_MENU_PRESENTER|{phase}|{theme}|background={Describe(presenter.Background)}|systemBackdrop={presenter.SystemBackdrop?.GetType().Name ?? popup.SystemBackdrop?.GetType().Name ?? "null"}");
        var items = Nodes(child).OfType<MenuFlyoutItem>().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains(items, item => item.Text is "重命名" or "Rename");
        Assert.Contains(items, item => item.Text is "删除" or "Delete");
        foreach (var item in items)
        {
            Assert.Equal(theme, item.ActualTheme);
            Assert.True(item.IsEnabled);
            foreach (var visualState in new[] { "Normal", "PointerOver", "Normal" })
            {
                Assert.True(VisualStateManager.GoToState(item, visualState, useTransitions: false),
                    "The real menu-item template did not expose " + visualState);
                await Settle(root);
                var text = Assert.Single(Nodes(item).OfType<TextBlock>().Where(text => text.Text == item.Text));
                Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0);
                Assert.Equal(theme, text.ActualTheme);
                var background = Backdrop(text, child, out var materialBackground);
                var foreground = Assert.IsType<SolidColorBrush>(text.Foreground);
                var reference = visualState == "PointerOver" ? hoverReference : normalReference;
                Assert.True(reference.IsLoaded);
                Assert.Equal(theme, reference.ActualTheme);
                var expectedForeground = Assert.IsType<SolidColorBrush>(reference.Foreground);
                Assert.Equal(expectedForeground.Color, foreground.Color);
                Assert.Equal(expectedForeground.Opacity, foreground.Opacity);
                if (background.A == 255 && !materialBackground)
                {
                    var contrast = Contrast(background, Blend(foreground.Color, foreground.Opacity, background));
                    Console.WriteLine($"CONVERSATION_MENU|{phase}|{theme}|{visualState}|{item.Text}|background={background}|foreground={foreground.Color}|nativeReference={expectedForeground.Color}|contrast={contrast:F2}");
                    Assert.True(contrast >= 4.5,
                        $"Conversation menu {phase}/{theme}/{visualState}/{item.Text}: {contrast:F2}:1; bg={background}, fg={foreground.Color}");
                }
                else
                {
                    Assert.NotNull(presenter.SystemBackdrop ?? popup.SystemBackdrop);
                    Console.WriteLine($"CONVERSATION_MENU|{phase}|{theme}|{visualState}|{item.Text}|background={background}|foreground={foreground.Color}|nativeReference={expectedForeground.Color}|contrast=unavailable-compositor-material");
                }
            }
        }
    }

    private static Color Backdrop(FrameworkElement text, FrameworkElement boundary, out bool materialBackground)
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
            Console.WriteLine($"CONVERSATION_MENU_BACKGROUND|{parent.GetType().Name}|name={(parent as FrameworkElement)?.Name}|theme={(parent as FrameworkElement)?.ActualTheme}|brush={Describe(brush)}");
            if (brush is not null) brushes.Add(brush);
            if (ReferenceEquals(parent, boundary)) break;
        }
        var background = Color.FromArgb(0, 0, 0, 0);
        materialBackground = false;
        for (var i = brushes.Count - 1; i >= 0; i--)
        {
            var brush = brushes[i];
            Color color;
            if (brush is AcrylicBrush acrylic)
            {
                // A compositor material is not measurable through its XAML
                // fallback metadata; retain it only for diagnostics.
                color = acrylic.FallbackColor;
                materialBackground = true;
            }
            else color = Assert.IsType<SolidColorBrush>(brush).Color;
            background = Blend(color, brush.Opacity, background);
        }
        return background;
    }

    private static TextBlock ThemeReference(string key) => Assert.IsType<TextBlock>(XamlReader.Load(
        "<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "Text=\"Native theme reference\" Width=\"1\" Height=\"1\" Opacity=\"0\" IsHitTestVisible=\"False\" " +
        "Foreground=\"{ThemeResource " + key + "}\" />"));

    private static string Describe(Brush? brush) => brush switch
    {
        null => "null",
        SolidColorBrush solid => $"Solid({solid.Color},opacity={solid.Opacity})",
        AcrylicBrush acrylic => $"Acrylic(fallback={acrylic.FallbackColor},tint={acrylic.TintColor},opacity={acrylic.Opacity})",
        _ => brush.GetType().Name,
    };

    private static Color Blend(Color front, double opacity, Color back)
    {
        var a = front.A / 255d * opacity;
        var remaining = back.A / 255d * (1 - a);
        var total = a + remaining;
        byte C(byte f, byte b) => total == 0 ? (byte)0 : (byte)Math.Round((f * a + b * remaining) / total);
        return Color.FromArgb((byte)Math.Round(total * 255), C(front.R, back.R), C(front.G, back.G), C(front.B, back.B));
    }

    private static double Contrast(Color first, Color second)
    {
        static double L(Color color)
        {
            static double C(byte channel) { var n = channel / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
        }
        var a = L(first); var b = L(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject node)
    {
        yield return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Nodes(VisualTreeHelper.GetChild(node, i))) yield return child;
    }

    private static async Task Settle(Grid root) { await Task.Delay(160); root.UpdateLayout(); }
}
