using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Ai;
using Windows.UI;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>Real page controls and native interaction templates, without sending
/// a message or initializing settings. Run in both Dark and Light host processes.</summary>
internal static class HomeAppearanceThemeCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("Home_QuickCards_InteractionThemesAndRemount", QuickCards),
        ("Settings_ThemeRadio_InteractionThemesAndRemount", AppearanceChoices),
    ];

    private static async Task QuickCards()
    {
        RequireResources();
        var resolver = AgentEngine.RuntimeResolverOverrideForTest;
        var probe = typeof(AgentEngine).GetField("_probeStarted", BindingFlags.Static | BindingFlags.NonPublic)!;
        var state = typeof(AgentEngine).GetField("_dshState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldProbe = probe.GetValue(null);
        var oldState = state.GetValue(null);
        var calls = 0;
        AiAgentPage? page = null;
        try
        {
            AgentEngine.RuntimeResolverOverrideForTest = () =>
            { calls++; throw new InvalidOperationException("Appearance fixtures cannot start a real runtime."); };
            probe.SetValue(null, 1);
            state.SetValue(null, 2);
            await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
            {
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1180, 1050));
                var host = Canvas();
                host.RequestedTheme = OppositeAppTheme;
                page = new AiAgentPage(compact: false, autoLoadLatest: false);
                host.Children.Add(page);
                root.Children.Add(host);
                Collect(); // Before retaining or enumerating any generated card wrappers.
                await Settle(root, 700); // The welcome entrance animation must have finished.
                var panel = Assert.IsType<StackPanel>(page.FindName("QuickPillPanel"));
                var buttons = panel.Children.Cast<Button>().ToArray();
                Assert.Equal(3, buttons.Length);
                var titles = buttons.Select(button => button.Tag?.ToString()).ToArray();
                foreach (var theme in Themes)
                {
                    foreach (var button in buttons)
                        Assert.True(VisualStateManager.GoToState(button, "PointerOver", false));
                    host.RequestedTheme = theme;
                    Collect();
                    await Settle(root);
                    Assert.Equal(theme, page.ActualTheme);
                    foreach (var button in buttons)
                    {
                        CheckQuickCard(button, host, theme, "held-pointer-during-theme-change");
                        await Exercise(button, root, stateName => CheckQuickCard(button, host, theme, stateName));
                    }
                    if (Preview && theme == ElementTheme.Light)
                        await GoalGuideCases.SaveControlPreviewAsync(page, "home-quick-cards-interactions-light.png");
                }
                root.Children.Remove(host);
                await Settle(root);
                host.RequestedTheme = OppositeAppTheme;
                Collect();
                root.Children.Add(host);
                await Settle(root, 700);
                // Loaded rebuilds the welcome cards. Validate the visible new
                // controls, not detached wrappers from before the remount.
                var remountedButtons = panel.Children.Cast<Button>().ToArray();
                Assert.Equal(3, remountedButtons.Length);
                Assert.Equal(titles, remountedButtons.Select(button => button.Tag?.ToString()).ToArray());
                Assert.All(remountedButtons, button => Assert.True(button.IsLoaded));
                foreach (var button in remountedButtons)
                    await Exercise(button, root, stateName => CheckQuickCard(button, host, OppositeAppTheme, "remount-" + stateName));
                root.Children.Remove(host);
                page.Unload();
                page = null;
            });
            Assert.Equal(0, calls);
        }
        finally
        {
            page?.Unload();
            probe.SetValue(null, oldProbe);
            state.SetValue(null, oldState);
            AgentEngine.RuntimeResolverOverrideForTest = resolver;
        }
    }

    private static async Task AppearanceChoices()
    {
        RequireResources();
        // Use the same compiled styles as the app. The shared native Window's
        // resources remain present for deferred template callbacks between cases.
        foreach (var path in new[] { "FluentTokens", "SettingsStyles" })
        {
            var source = new Uri($"ms-appx:///Styles/{path}.xaml");
            if (!Application.Current.Resources.MergedDictionaries.Any(dictionary => dictionary.Source == source))
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = source });
        }
        await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
        {
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1180, 1050));
            var host = Canvas();
            host.RequestedTheme = OppositeAppTheme;
            var page = new SettingsPage(initializeSettings: false);
            Assert.IsType<Expander>(page.FindName("GeneralExpander")).IsExpanded = false;
            Assert.IsType<Expander>(page.FindName("AppearanceExpander")).IsExpanded = true;
            host.Children.Add(page);
            root.Children.Add(host);
            Collect();
            await Settle(root, 500);
            var choices = Assert.IsType<RadioButtons>(page.FindName("ThemeRadio"));
            var radios = choices.Items.Cast<RadioButton>().ToArray();
            Assert.Equal(new[] { "system", "light", "dark" }, radios.Select(radio => radio.Tag?.ToString()).ToArray());
            Assert.False((bool)typeof(SettingsPage).GetField("_themeUiReady", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!);
            foreach (var theme in Themes)
            {
                foreach (var radio in radios)
                    Assert.True(VisualStateManager.GoToState(radio, "PointerOver", false));
                host.RequestedTheme = theme;
                Collect();
                await Settle(root);
                Assert.Equal(theme, page.ActualTheme);
                foreach (var radio in radios)
                {
                    CheckRadio(radio, host, theme, "held-pointer-during-theme-change");
                    // Initialization is deliberately disabled: these real checked
                    // states cannot persist preferences or change the user's theme.
                    foreach (var selected in new[] { false, true })
                    {
                        radio.IsChecked = selected;
                        await Settle(root, 40);
                        await Exercise(radio, root, stateName => CheckRadio(radio, host, theme,
                            (selected ? "checked-" : "unchecked-") + stateName));
                        Assert.Equal(selected, radio.IsChecked);
                    }
                }
                if (Preview && theme == ElementTheme.Light)
                    await GoalGuideCases.SaveControlPreviewAsync(host, "settings-theme-radio-interactions-light.png");
            }
            root.Children.Remove(host);
            await Settle(root);
            host.RequestedTheme = OppositeAppTheme;
            root.Children.Add(host);
            Collect();
            await Settle(root);
            foreach (var radio in radios)
                await Exercise(radio, root, stateName => CheckRadio(radio, host, OppositeAppTheme, "remount-" + stateName));
            Assert.False((bool)typeof(SettingsPage).GetField("_themeUiReady", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!);
        });
    }

    private static async Task Exercise(Control control, Grid root, Action<string> verify)
    {
        Assert.True(control.IsEnabled && control.IsTabStop);
        foreach (var state in new[] { "Normal", "PointerOver", "Pressed", "Normal" })
        {
            Assert.True(VisualStateManager.GoToState(control, state, useTransitions: false),
                "Missing real native interaction state " + state);
            await Settle(root, 100);
            verify(state);
        }
        Assert.True(control.Focus(FocusState.Keyboard), "The real control must accept keyboard focus.");
        await Settle(root, 50);
        Assert.Equal(FocusState.Keyboard, control.FocusState);
        verify("keyboard-focus");
    }

    private static void CheckQuickCard(Button button, Grid host, ElementTheme theme, string state)
    {
        Assert.Equal(theme, button.ActualTheme);
        var row = Assert.IsType<Grid>(button.Content);
        var labels = Assert.Single(row.Children.OfType<StackPanel>()).Children.Cast<TextBlock>().ToArray();
        Assert.Equal(2, labels.Length);
        Assert.Equal(button.Tag?.ToString(), labels[0].Text);
        var icons = row.Children.OfType<FontIcon>().ToArray();
        Assert.Equal(2, icons.Length);
        foreach (var text in labels)
        {
            Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0);
            CheckContrast(Backdrop(text, host), text.Foreground, "quick-card/" + state + "/" + text.Text);
        }
        foreach (var icon in icons)
        {
            Assert.True(icon.ActualWidth > 0 && icon.ActualHeight > 0);
            CheckContrast(Backdrop(icon, host), icon.Foreground, "quick-card/" + state + "/icon");
        }
        Console.WriteLine($"HOME_APPEARANCE|app={Application.Current.RequestedTheme}|owner={theme}|card={button.Tag}|state={state}|contrast>=4.5");
    }

    private static void CheckRadio(RadioButton radio, Grid host, ElementTheme theme, string state)
    {
        Assert.Equal(theme, radio.ActualTheme);
        var text = Assert.Single(Nodes(radio).OfType<TextBlock>().Where(text => text.Text == radio.Content?.ToString()));
        Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0);
        CheckContrast(Backdrop(text, host), text.Foreground, "appearance-radio/" + state + "/" + text.Text);
        Console.WriteLine($"HOME_APPEARANCE|app={Application.Current.RequestedTheme}|owner={theme}|radio={radio.Tag}|state={state}|contrast>=4.5");
    }

    private static Color Backdrop(FrameworkElement element, Grid boundary)
    {
        var background = Assert.IsType<SolidColorBrush>(boundary.Background).Color;
        var brushes = new List<Brush>();
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null;
            parent = VisualTreeHelper.GetParent(parent))
        {
            var brush = parent switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                ContentPresenter presenter => presenter.Background,
                Control control => control.Background,
                _ => null,
            };
            if (brush is not null) brushes.Add(brush);
            if (ReferenceEquals(parent, boundary)) break;
        }
        for (var i = brushes.Count - 1; i >= 0; i--) background = Composite(brushes[i], background);
        return background;
    }

    private static Color Composite(Brush brush, Color background)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        var color = solid.Color;
        var alpha = color.A / 255d * solid.Opacity;
        byte C(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
        return Color.FromArgb(255, C(color.R, background.R), C(color.G, background.G), C(color.B, background.B));
    }

    private static void CheckContrast(Color background, Brush foreground, string label)
    {
        static double L(Color color)
        {
            static double C(byte b) { var n = b / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
        }
        var a = L(background); var b = L(Composite(foreground, background));
        var contrast = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        Assert.True(contrast >= 4.5, $"{label}: {contrast:F2}:1; background={background}; foreground={Assert.IsType<SolidColorBrush>(foreground).Color}");
    }

    private static void RequireResources()
    {
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT")));
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        Assert.Null(ThemeResourceResolver.CurrentThemeKeyOverrideForTest);
        Assert.Null(ThemeResourceResolver.ExtraRootDictionariesForTest);
    }
    private static ElementTheme AppTheme => Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
    private static ElementTheme OppositeAppTheme => AppTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
    private static ElementTheme[] Themes => [OppositeAppTheme, AppTheme, OppositeAppTheme];
    private static Grid Canvas() => Assert.IsType<Grid>(XamlReader.Load("""
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              Background="{ThemeResource ApplicationPageBackgroundThemeBrush}" />
        """));
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Nodes(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static async Task Settle(Grid root, int delay = 180) { await Task.Delay(delay); root.UpdateLayout(); }
    private static bool Preview => Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1";
}
