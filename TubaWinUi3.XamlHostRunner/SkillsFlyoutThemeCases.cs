using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using TubaWinUi3.Services.Ai;
using TubaWinUi3.Services.Ai.Dsh;
using Windows.Graphics.Imaging;
using Windows.UI;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// Actual page click, resource Flyout and native presenter. The unconnected dsh
/// object supplies only the real fixed-skill state; no Send, launch builder,
/// persistence, external button or model is invoked. Native ThemeResource
/// expressions in the same popup are the reference, never the managed resolver.
/// </summary>
internal static class SkillsFlyoutThemeCases
{
    private const string CheckedSkill = "native_theme_checked";
    private const string UncheckedSkill = "native_theme_unchecked";

    internal static (string Name, Func<Task> Body)[] All() =>
    [ ("SkillsFlyout_RealPage_ThemeCycleAndReopen", ThemeCycleAndReopen) ];

    private static async Task ThemeCycleAndReopen()
    {
        var dataRoot = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(dataRoot));
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.Equal(Path.GetFullPath(dataRoot!), Path.GetFullPath(ConfigManager.GetDataDir()), ignoreCase: true);
        Assert.Null(AiAssistantService.HistoryDirOverride);
        Assert.Null(AiProviderStore.StoragePathOverride);
        Assert.Null(ThemeResourceResolver.CurrentThemeKeyOverrideForTest);
        Assert.Null(ThemeResourceResolver.ExtraRootDictionariesForTest);
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        var registry = (List<AgentSkill>)typeof(AgentSkillRegistry).GetField("_skills", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var priorSkills = AgentSkillRegistry.All.ToArray();
        var priorResolver = AgentEngine.RuntimeResolverOverrideForTest;
        var probe = typeof(AgentEngine).GetField("_probeStarted", BindingFlags.Static | BindingFlags.NonPublic)!;
        var state = typeof(AgentEngine).GetField("_dshState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var priorProbe = probe.GetValue(null);
        var priorState = state.GetValue(null);
        var runtimeCalls = 0;
        AiAgentPage? page = null;
        DshSession? session = null;
        Flyout? flyout = null;
        try
        {
            lock (AgentSkillRegistry.Sync)
            {
                registry.Clear();
                registry.Add(Skill(AiAgentWorkflowSkill.Id, "枕星目标助手", "理解目标，复用已有工具，准备环境并接续使用。"));
                registry.Add(Skill(CheckedSkill, "已启用的自定义技能", "合成技能说明：用于检查已勾选条目的文字，内容不会交给模型。"));
                registry.Add(Skill(UncheckedSkill, "未启用的自定义技能", "合成技能说明：未勾选也应清晰可读，主题改变不应修改状态。"));
            }
            probe.SetValue(null, 1);
            state.SetValue(null, 2);
            AgentEngine.RuntimeResolverOverrideForTest = () =>
            { runtimeCalls++; throw new InvalidOperationException("The skills popup fixture cannot resolve a real runtime."); };
            // Constructor and SetSkillEnabled change only in-memory state. Never
            // call DshLaunchConfig.Build, SendAsync, Save or a connection method.
            session = new DshSession("", dataRoot!, new DshLaunchConfig
            {
                BuiltinGoalSkillProjected = true, DataDir = dataRoot,
                DshPathOverride = Path.Combine(dataRoot!, "never-start-a-runtime.exe"),
                Endpoint = "https://example.invalid/not-requested",
            });
            session.SetSkillEnabled(UncheckedSkill, false);
            var expectedActive = session.ActiveSkillIds.Order().ToArray();
            await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
            {
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1100, 920));
                page = new AiAgentPage(compact: false, autoLoadLatest: false) { RequestedTheme = ElementTheme.Dark };
                PageField("_session").SetValue(page, session);
                root.Children.Add(page);
                await Settle(root);
                Assert.True(page.IsLoaded && page.XamlRoot is not null);
                flyout = Assert.IsType<Flyout>(page.Resources["SkillsFlyout"]);
                var panel = Assert.IsType<StackPanel>(flyout.Content);
                var observedSurfaces = new Dictionary<ElementTheme, (Brush Background, Thickness Padding)>();
                try
                {
                    RecordManagedBrushes(page, "before-first-open");
                    var popup = await Open(page, panel, root);
                    var originalRows = SkillRows(panel);
                    var originalText = originalRows.Values.SelectMany(RowText).ToArray();
                    foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light, ElementTheme.Dark })
                    {
                        page.RequestedTheme = theme;
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        await Settle(root);
                        RecordManagedBrushes(page, "live");
                        Assert.True(popup.IsOpen);
                        var currentRows = SkillRows(panel);
                        foreach (var pair in originalRows) Assert.Same(pair.Value, currentRows[pair.Key]);
                        foreach (var pair in originalText.Zip(currentRows.Values.SelectMany(RowText))) Assert.Same(pair.First, pair.Second);
                        await AssertPopup(page, panel, popup, theme, root, "live");
                        var presenter = Presenter(panel)!;
                        observedSurfaces[theme] = (presenter.Background, presenter.Padding);
                        Assert.Equal(expectedActive, session.ActiveSkillIds.Order().ToArray());
                        AssertUnconnected(session);
                    }
                    flyout.Hide();
                    await WaitUntil(() => !panel.IsLoaded, "The skills popup did not close.");
                    foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                    {
                        page.RequestedTheme = theme;
                        await Settle(root);
                        popup = await Open(page, panel, root);
                        await AssertPopup(page, panel, popup, theme, root, "reopen");
                        Assert.Equal(expectedActive, session.ActiveSkillIds.Order().ToArray());
                        AssertUnconnected(session);
                        flyout.Hide();
                        await WaitUntil(() => !panel.IsLoaded, "The reopened skills popup did not close.");
                    }

                    popup = await Open(page, panel, root);
                    root.Children.Remove(page);
                    await WaitUntil(() => !popup.IsOpen && !panel.IsLoaded, "Removing the owner must close its skills popup.");
                    AssertUnconnected(session);
                    if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                    {
                        // RTB cannot capture compositor popups. After genuine popup
                        // checks pass, capture the same generated content with the
                        // observed opaque native surface, clearly labeled below.
                        flyout.Content = null;
                        await CaptureContent(root, panel, observedSurfaces);
                    }
                    Console.WriteLine("SKILLS_FLYOUT_NATIVE|actual-popup|dark-light-dark|reopen-light-dark|disabled-goal-checked|custom-checked-and-unchecked|no-skill-action|no-runtime");
                }
                finally
                {
                    flyout.Hide();
                    root.Children.Remove(page);
                    // Avoid the public terminal Unload's save path for a real
                    // dsh type. This fixture never persists the synthetic session.
                    PageField("_session").SetValue(page, null);
                    page.Unload();
                    page = null;
                    await Settle(root);
                }
            });
            Assert.Equal(0, runtimeCalls);
            AssertUnconnected(session);
        }
        finally
        {
            flyout?.Hide();
            if (page is not null) { PageField("_session").SetValue(page, null); page.Unload(); }
            session?.Dispose();
            lock (AgentSkillRegistry.Sync) { registry.Clear(); registry.AddRange(priorSkills); }
            probe.SetValue(null, priorProbe);
            state.SetValue(null, priorState);
            AgentEngine.RuntimeResolverOverrideForTest = priorResolver;
        }
    }

    private static async Task<Popup> Open(AiAgentPage page, StackPanel panel, Grid root)
    {
        var anchor = Assert.IsType<Button>(page.FindName("SkillsButton"));
        typeof(AiAgentPage).GetMethod("SkillsButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [anchor, new RoutedEventArgs()]);
        await WaitUntil(() => panel.IsLoaded && Presenter(panel) is not null, "The actual skills FlyoutPresenter did not attach.");
        await Settle(root);
        var presenter = Presenter(panel)!;
        return Assert.Single(VisualTreeHelper.GetOpenPopupsForXamlRoot(anchor.XamlRoot)
            .Where(popup => popup.IsOpen && popup.Child is { } child && Nodes(child).Any(node => ReferenceEquals(node, presenter))));
    }

    private static async Task AssertPopup(AiAgentPage page, StackPanel panel, Popup popup,
        ElementTheme theme, Grid root, string phase)
    {
        var presenter = Assert.IsType<FlyoutPresenter>(Presenter(panel));
        Assert.Equal(theme, page.ActualTheme);
        Assert.Equal(theme, panel.ActualTheme);
        Assert.Equal(theme, presenter.ActualTheme);
        Assert.True(presenter.IsLoaded && presenter.ActualWidth > 0 && presenter.ActualHeight > 0);
        Assert.Null(popup.SystemBackdrop);
        var flyout = Assert.IsType<Flyout>(page.Resources["SkillsFlyout"]);
        Assert.Null(flyout.SystemBackdrop);
        var background = Assert.IsType<SolidColorBrush>(presenter.Background);
        Assert.Equal((byte)255, background.Color.A);
        Assert.Equal(1d, background.Opacity);
        var nativeSurface = Assert.IsType<Border>(VisualTreeHelper.GetChild(presenter, 0));
        Assert.Same(presenter.Background, nativeSurface.Background);
        Assert.Equal(1d, nativeSurface.Opacity);

        var reference = Assert.IsType<Grid>(XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Width="1" Height="1" Opacity="0" IsHitTestVisible="False">
                <TextBlock Text="Primary reference" Foreground="{ThemeResource TextFillColorPrimaryBrush}" />
                <TextBlock Text="Secondary reference" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                <Border Background="{ThemeResource SolidBackgroundFillColorBaseBrush}" />
            </Grid>
            """));
        panel.Children.Add(reference);
        try
        {
            // The popup has a separate native root; updating the main window
            // does not guarantee layout/Loaded for a reference just inserted
            // into its content. Wait for the real popup subtree, never replace
            // it with a detached color probe or accept a closed popup.
            RecordReferenceState("inserted", reference, panel, presenter, popup, theme, phase);
            await WaitUntil(() =>
            {
                Assert.True(popup.IsOpen, $"{phase}/{theme}: the popup closed while attaching its native reference.");
                Assert.True(panel.IsLoaded && presenter.IsLoaded,
                    $"{phase}/{theme}: the actual popup content detached while attaching its reference.");
                Assert.True(panel.Children.Contains(reference), $"{phase}/{theme}: the native reference was removed from its popup.");
                presenter.UpdateLayout();
                return reference.IsLoaded && reference.Children.OfType<FrameworkElement>().All(child => child.IsLoaded);
            }, $"{phase}/{theme}: the native reference did not load in the actual open skills popup.");
            await Settle(root);
            RecordReferenceState("loaded", reference, panel, presenter, popup, theme, phase);
            Assert.True(popup.IsOpen);
            Assert.True(reference.IsLoaded);
            Assert.Same(panel, VisualTreeHelper.GetParent(reference));
            Assert.Equal(theme, reference.ActualTheme);
            var primary = Assert.IsType<TextBlock>(reference.Children[0]);
            var secondary = Assert.IsType<TextBlock>(reference.Children[1]);
            var surface = Assert.IsType<Border>(reference.Children[2]);
            Assert.Equal(Assert.IsType<SolidColorBrush>(surface.Background).Color, background.Color);
            Assert.True(theme == ElementTheme.Dark ? Luminance(background.Color) < .2 : Luminance(background.Color) > .65);
            var rows = SkillRows(panel);
            Assert.Equal(3, rows.Count);
            foreach (var (id, row) in rows)
            {
                Assert.Equal(id != AiAgentWorkflowSkill.Id, row.IsEnabled);
                Assert.Equal(id != UncheckedSkill, row.IsChecked);
                Assert.True(row.IsLoaded && row.ActualHeight > 0);
                var text = RowText(row);
                Assert.Equal(2, text.Length);
                AssertText(text[0], primary, presenter, theme, phase + "/" + id + "/title");
                AssertText(text[1], secondary, presenter, theme, phase + "/" + id + "/description");
                var content = Assert.IsType<StackPanel>(row.Content);
                var icon = Assert.Single(content.Children.OfType<FontIcon>());
                AssertBrush(icon.Foreground, secondary.Foreground);
                Assert.True(icon.ActualWidth > 0 && icon.ActualHeight > 0);
            }
            var notices = panel.Children.OfType<TextBlock>().ToArray();
            Assert.Equal(2, notices.Length);
            foreach (var text in notices) AssertText(text, secondary, presenter, theme, phase + "/notice");
            Assert.Equal(6, Nodes(panel).OfType<Button>().Count()); // No imports, editors, folders or web pages are opened.
        }
        finally { panel.Children.Remove(reference); }
    }

    private static void AssertText(TextBlock text, TextBlock reference, FlyoutPresenter presenter, ElementTheme theme, string label)
    {
        Assert.False(string.IsNullOrWhiteSpace(text.Text));
        Assert.True(text.IsLoaded && text.ActualWidth > 0 && text.ActualHeight > 0, label + " must be genuinely arranged.");
        Assert.Equal(theme, text.ActualTheme);
        AssertBrush(text.Foreground, reference.Foreground);
        var foreground = Assert.IsType<SolidColorBrush>(text.Foreground);
        var ancestors = new List<DependencyObject>();
        double opacity = text.Opacity;
        for (var node = VisualTreeHelper.GetParent(text); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            ancestors.Add(node);
            if (node is UIElement ui)
            {
                Assert.Equal(Visibility.Visible, ui.Visibility);
                opacity *= ui.Opacity;
            }
            if (ReferenceEquals(node, presenter)) break;
        }
        Assert.Contains(presenter, ancestors);
        var background = Assert.IsType<SolidColorBrush>(presenter.Background).Color;
        foreach (var ancestor in ancestors.AsEnumerable().Reverse())
        {
            var brush = ancestor switch { Border border => border.Background, Panel panel => panel.Background,
                Control control => control.Background, _ => null };
            if (brush is SolidColorBrush solid) background = Blend(solid.Color, solid.Opacity, background);
            else Assert.Null(brush); // Do not silently use Acrylic.FallbackColor as actual rendered pixels.
        }
        var effective = Blend(foreground.Color, foreground.Opacity * opacity, background);
        var a = Luminance(effective); var b = Luminance(background);
        var contrast = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        Console.WriteLine($"SKILLS_FLYOUT_TEXT|{label}|{theme}|foreground={foreground.Color}|background={background}|ancestor-opacity={opacity:F3}|contrast={contrast:F2}");
        Assert.True(contrast >= 4.5, $"{label}/{theme}: actual text contrast {contrast:F2}:1 is below 4.5:1.");
    }

    private static async Task CaptureContent(Grid root, StackPanel panel,
        Dictionary<ElementTheme, (Brush Background, Thickness Padding)> surfaces)
    {
        var surface = new Border { Child = panel, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(20) };
        root.Children.Add(surface);
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                surface.RequestedTheme = panel.RequestedTheme = theme;
                surface.Background = surfaces[theme].Background;
                surface.Padding = surfaces[theme].Padding;
                await Settle(root);
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(surface);
                Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
                var data = await bitmap.GetPixelsAsync();
                var pixels = new byte[data.Length]; data.CopyTo(pixels);
                var colors = new HashSet<uint>();
                for (var i = 0; i + 3 < pixels.Length && colors.Count <= 4; i += 4)
                    if (pixels[i + 3] > 0) colors.Add(BitConverter.ToUInt32(pixels, i));
                Assert.True(colors.Count > 4, "The skill-content preview must contain visible rendered content.");
                var path = Path.Combine(Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT")!,
                    "skills-flyout-" + theme.ToString().ToLowerInvariant() + ".png");
                using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                using var stream = file.AsRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
                await encoder.FlushAsync();
                Console.WriteLine($"SKILLS_FLYOUT_PREVIEW|{path}|same-generated-content|observed-native-surface|not-popup-compositor-pixels");
            }
        }
        finally { surface.Child = null; root.Children.Remove(surface); await Settle(root); }
    }

    private static void RecordManagedBrushes(AiAgentPage page, string phase)
    {
        foreach (var key in new[] { "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "DividerStrokeColorDefaultBrush" })
        {
            var brush = ThemeResourceResolver.ResolveBrush(page, key);
            Console.WriteLine($"SKILLS_FLYOUT_MANAGED_RESOLVER|{phase}|page={page.ActualTheme}|app={Application.Current.RequestedTheme}|{key}|" +
                (brush is SolidColorBrush solid ? $"{solid.Color},opacity={solid.Opacity}" : brush?.GetType().Name ?? "null"));
        }
    }
    private static void RecordReferenceState(string stage, Grid reference, StackPanel panel,
        FlyoutPresenter presenter, Popup popup, ElementTheme theme, string phase) =>
        Console.WriteLine($"SKILLS_FLYOUT_REFERENCE|{phase}/{theme}/{stage}|popup-open={popup.IsOpen}" +
            $"|presenter-loaded={presenter.IsLoaded}|panel-loaded={panel.IsLoaded}|reference-loaded={reference.IsLoaded}" +
            $"|parent={VisualTreeHelper.GetParent(reference)?.GetType().Name ?? "null"}" +
            $"|children-loaded={string.Join(',', reference.Children.OfType<FrameworkElement>().Select(child => child.IsLoaded))}");
    private static void AssertBrush(Brush actual, Brush expected)
    {
        var a = Assert.IsType<SolidColorBrush>(actual);
        var b = Assert.IsType<SolidColorBrush>(expected);
        Assert.True(a.Color.A > 0 && a.Opacity > 0, "The actual text/icon brush cannot be transparent.");
        Assert.Equal(b.Color, a.Color); Assert.Equal(b.Opacity, a.Opacity);
    }
    private static Dictionary<string, CheckBox> SkillRows(StackPanel panel) => panel.Children.OfType<CheckBox>()
        .ToDictionary(row => Assert.IsType<string>(row.Tag), StringComparer.Ordinal);
    private static TextBlock[] RowText(CheckBox row) => Assert.Single(Assert.IsType<StackPanel>(row.Content)
        .Children.OfType<StackPanel>()).Children.OfType<TextBlock>().ToArray();
    private static AgentSkill Skill(string id, string name, string description) => new()
    { Id = id, DisplayName = name, Description = description, Glyph = "\uE99A", SystemPromptFragment = "Synthetic UI fixture only." };
    private static void AssertUnconnected(DshSession session)
    {
        Assert.False(session.IsConnected); Assert.False(session.IsRunning);
        Assert.Null(session.DshSessionId);
        Assert.Null(typeof(DshSession).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));
        Assert.Null(typeof(DshSession).GetField("_connectTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));
    }
    private static FlyoutPresenter? Presenter(DependencyObject child)
    {
        for (var node = VisualTreeHelper.GetParent(child); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FlyoutPresenter presenter) return presenter;
        return null;
    }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Nodes(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static Color Blend(Color front, double opacity, Color back)
    {
        var alpha = front.A / 255d * opacity;
        byte Channel(byte a, byte b) => (byte)Math.Round(a * alpha + b * (1 - alpha));
        return Color.FromArgb(255, Channel(front.R, back.R), Channel(front.G, back.G), Channel(front.B, back.B));
    }
    private static double Luminance(Color c)
    {
        static double Linear(byte b) { var v = b / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    }
    private static FieldInfo PageField(string name) => typeof(AiAgentPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Settle(Grid root) { await Task.Delay(260); root.UpdateLayout(); await Task.Delay(80); }
    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        var end = DateTime.UtcNow.AddSeconds(6);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(40);
        Assert.True(condition(), message);
    }
}
