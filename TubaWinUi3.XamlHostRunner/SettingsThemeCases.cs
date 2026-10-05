using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using Windows.UI;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>Real SettingsPage XBF and stock Expander templates, without settings
/// initialization, navigation, service probes, downloads or configuration changes.</summary>
internal static class SettingsThemeCases
{
    private static bool _resourcesReady;
    private static readonly string[] SectionNames =
    [
        "GeneralExpander", "AppearanceExpander", "HardwareAiExpander",
        "AiServiceExpander", "ToolsCommunityExpander", "CreditsExpander",
    ];

    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("Settings_RealPage_SectionThemesAndRemount", SectionThemesAndRemount),
    ];

    private static async Task SectionThemesAndRemount()
    {
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT")));
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        var app = Assert.IsType<TestApp>(Application.Current);
        app.EnsureSharedControlResources();
        Assert.Equal(ApplicationTheme.Dark, app.RequestedTheme);
        Assert.Null(ThemeResourceResolver.CurrentThemeKeyOverrideForTest);
        Assert.Null(ThemeResourceResolver.ExtraRootDictionariesForTest);
        if (!_resourcesReady)
        {
            // Same compiled dictionaries as App.xaml. Retain them for the shared
            // native Window: deferred template callbacks must not lose resources.
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("ms-appx:///Styles/FluentTokens.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("ms-appx:///Styles/SettingsStyles.xaml") });
            _resourcesReady = true;
        }

        await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
        {
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1180, 1100));
            var host = Assert.IsType<Grid>(XamlReader.Load("""
                <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      Background="{ThemeResource ApplicationPageBackgroundThemeBrush}" />
                """));
            host.RequestedTheme = ElementTheme.Light;
            var page = new SettingsPage(initializeSettings: false);
            var sections = SectionNames.Select(name => Assert.IsType<Expander>(page.FindName(name))).ToArray();
            var headers = sections.Select(section => Assert.IsType<StackPanel>(section.Header)).ToArray();
            var contents = sections.Select(section => section.Content).ToArray();
            host.Children.Add(page);
            root.Children.Add(host);
            Collect();
            await Settle(root);

            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                host.RequestedTheme = theme;
                Collect();
                await Settle(root);
                Assert.Equal(theme, page.ActualTheme);
                Assert.True(sections[0].IsExpanded);
                Assert.All(sections.Skip(1), section => Assert.False(section.IsExpanded));
                await AssertSections(root, host, sections, theme);
                for (var index = 0; index < sections.Length; index++)
                {
                    Assert.Same(headers[index], sections[index].Header);
                    Assert.Same(contents[index], sections[index].Content);
                }
            }
            if (Preview) await GoalGuideCases.SaveControlPreviewAsync(host, "settings-sections-light.png");

            sections[0].IsExpanded = false;
            sections[5].IsExpanded = true;
            await Settle(root);
            root.Children.Remove(host);
            await Settle(root);
            Assert.False(page.IsLoaded);
            host.RequestedTheme = ElementTheme.Dark;
            Collect();
            root.Children.Add(host);
            await Settle(root);
            foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
            {
                host.RequestedTheme = theme;
                Collect();
                await Settle(root);
                Assert.False(sections[0].IsExpanded);
                Assert.True(sections[5].IsExpanded);
                await AssertSections(root, host, sections, theme);
                for (var index = 0; index < sections.Length; index++)
                {
                    Assert.Same(headers[index], sections[index].Header);
                    Assert.Same(contents[index], sections[index].Content);
                }
                if (Preview) await GoalGuideCases.SaveControlPreviewAsync(host,
                    theme == ElementTheme.Dark ? "settings-sections-dark.png" : "settings-sections-remount-light.png");
            }
            Assert.Equal(ApplicationTheme.Dark, app.RequestedTheme);
        });
    }

    private static async Task AssertSections(Grid root, Grid host, Expander[] sections, ElementTheme theme)
    {
        var canvas = Solid(host.Background).Color;
        foreach (var section in sections)
        {
            section.ApplyTemplate();
            var header = Assert.IsType<StackPanel>(section.Header);
            // Walk the header's declared content here, not each control's native
            // implementation: FontIcon's template contains another TextBlock.
            // The actual icon and chevron are checked separately below.
            var headerContent = HeaderContent(header);
            var texts = headerContent.OfType<TextBlock>().Where(text => !string.IsNullOrWhiteSpace(text.Text)).ToArray();
            Assert.Equal(2, texts.Length);
            var icon = Assert.Single(headerContent.OfType<FontIcon>());
            var toggle = Assert.Single(VisualNodes(section).OfType<ToggleButton>()
                .Where(button => ReferenceEquals(button.Content, header)));
            Assert.True(toggle.IsEnabled);
            Assert.True(toggle.IsTabStop);
            Assert.Equal(section.IsExpanded, toggle.IsChecked);
            var chevron = Assert.Single(VisualNodes(toggle).OfType<AnimatedIcon>()
                .Where(control => control.Name == "ExpandCollapseChevron"));

            // Native template states used by pointer and keyboard activation must
            // not reintroduce the application's Dark foreground in a Light page.
            foreach (var suffix in new[] { "", "PointerOver", "Pressed", "" })
            {
                var state = (section.IsExpanded ? "Checked" : "") + suffix;
                if (state.Length == 0) state = "Normal";
                Assert.True(VisualStateManager.GoToState(toggle, state, useTransitions: false));
                await Task.Delay(25);
                root.UpdateLayout();
                Assert.Equal(theme, header.ActualTheme);
                foreach (var text in texts)
                {
                    Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0);
                    AssertContrast(Backdrop(text, host, canvas), text.Foreground, $"{section.Name}/{state}/{text.Text}");
                }
                AssertContrast(Backdrop(icon, host, canvas), icon.Foreground, $"{section.Name}/{state}/icon");
                AssertContrast(Backdrop(chevron, host, canvas), chevron.Foreground, $"{section.Name}/{state}/chevron");
            }
        }
        Console.WriteLine($"SETTINGS_THEME|{theme}|sections={sections.Length}|native-states=normal,pointer,pressed|contrast>=4.5");
    }

    private static IReadOnlyList<FrameworkElement> HeaderContent(FrameworkElement root)
    {
        var nodes = new List<FrameworkElement>();
        void Walk(FrameworkElement current)
        {
            nodes.Add(current);
            switch (current)
            {
                case Panel panel:
                    foreach (var child in panel.Children.OfType<FrameworkElement>()) Walk(child);
                    break;
                case Border { Child: FrameworkElement borderChild }:
                    Walk(borderChild);
                    break;
            }
        }
        Walk(root);
        return nodes;
    }

    private static IReadOnlyList<DependencyObject> VisualNodes(DependencyObject root)
    {
        var nodes = new List<DependencyObject>();
        void Walk(DependencyObject current)
        {
            nodes.Add(current);
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                Walk(VisualTreeHelper.GetChild(current, index));
        }
        Walk(root);
        return nodes;
    }

    private static Color Backdrop(FrameworkElement element, FrameworkElement boundary, Color canvas)
    {
        var brushes = new List<Brush>();
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null;
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
        for (var index = brushes.Count - 1; index >= 0; index--) canvas = Composite(brushes[index], canvas);
        return canvas;
    }

    private static SolidColorBrush Solid(Brush? brush) => Assert.IsType<SolidColorBrush>(brush);
    private static Color Composite(Brush? brush, Color behind)
    {
        if (brush is null) return behind;
        var solid = Solid(brush);
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
        Assert.True(ratio >= 4.5, $"{label}: {ratio:F2}:1; background={background}; foreground={Solid(foreground).Color}");
    }

    private static async Task Settle(Grid root) { await Task.Delay(300); root.UpdateLayout(); }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static bool Preview => Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1";
}
