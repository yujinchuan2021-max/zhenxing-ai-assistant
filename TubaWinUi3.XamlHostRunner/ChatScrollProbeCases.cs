using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Xunit;
using TubaWinUi3.Pages;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// A dedicated off-screen Window attaches only a ScrollViewer and synthetic messages.
/// It never creates the product App/page, reads conversation/configuration data, or
/// activates another Window. Unlike detached Measure/Arrange, the connected native
/// ScrollContentPresenter computes a real viewport, extent and visual content offset.
/// </summary>
internal static class ChatScrollProbeCases
{
    private static Window? _hostWindow;
    private static Grid? _hostRoot;

    public static (string Name, Func<Task> Body)[] All() =>
    [
        ("ChatScroll_ContentSizedExtentRegression", ContentSizedExtentRegressionAsync),
        ("ChatScroll_AttachedViewportLayout", AttachedViewportLayoutAsync),
    ];

    private static async Task ContentSizedExtentRegressionAsync()
    {
        var panel = new StackPanel
        {
            MaxWidth = 1020, HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(26, 28, 26, 20),
        };
        var scroll = new ScrollViewer
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };
        await WithWindowAsync(async (window, root) =>
        {
            root.Children.Add(scroll);
            await SettleAsync(root);
            Assert.True(scroll.ViewportWidth > 1020, "The negative control needs a connected wide viewport.");
            var x = X(panel, scroll);
            Console.WriteLine($"REGRESSION|viewport={scroll.ViewportWidth}|extent={scroll.ExtentWidth}|panel={panel.ActualWidth}|x={x}");
            Assert.True(scroll.ExtentWidth < panel.ActualWidth - 100,
                "The old direct StackPanel structure did not reproduce a content-sized extent.");
            Assert.True(x + panel.ActualWidth > scroll.ViewportWidth + 100,
                "The negative control did not reproduce the message column drifting outside the viewport.");
            AssertNear(0, scroll.HorizontalOffset, "The negative control must not rely on a horizontal scroll offset.");
        });
    }

    private static async Task AttachedViewportLayoutAsync()
    {
        await WithWindowAsync(async (window, root) =>
        {
            foreach (var transitions in new[] { false, true })
            {
                var fixture = CreateProductionFixture();
                if (transitions)
                    fixture.Panel.ChildrenTransitions = new TransitionCollection
                    {
                        new EntranceThemeTransition { FromVerticalOffset = 16, IsStaggeringEnabled = true },
                        new RepositionThemeTransition(),
                    };
                // The second view already has its first message before first Loaded.
                // This models switching to a newly cached page while it starts work.
                Border? user = transitions ? UserBubble("我要开发一个吃鸡游戏") : null;
                if (user is not null) fixture.Panel.Children.Add(user);
                root.Children.Clear();
                root.Children.Add(fixture.Scroll);
                await SettleAsync(root);
                AssertStable("loaded", transitions, fixture);

                if (user is null)
                {
                    user = UserBubble("我要开发一个吃鸡游戏");
                    fixture.Panel.Children.Add(user);
                    await SettleAsync(root);
                    AssertStable("first-short-user", transitions, fixture);
                }
                var assistant = new Border
                {
                    Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 16),
                    Child = new TextBlock { Text = "正在思考…", TextWrapping = TextWrapping.Wrap },
                };
                fixture.Panel.Children.Add(assistant);
                root.UpdateLayout();
                AssertStable("thinking-now", transitions, fixture, checkChildren: false);
                await Task.Delay(600);
                root.UpdateLayout();
                AssertStable("thinking-settled", transitions, fixture);
                AssertUserAtRight(fixture, user);

                assistant.Child = new TextBlock
                {
                    Text = string.Concat(Enumerable.Repeat("先确认目标，再选择适合的模型和开发环境。", 240)),
                    TextWrapping = TextWrapping.Wrap,
                };
                await SettleAsync(root);
                Assert.True(fixture.Scroll.ScrollableHeight > 0,
                    "The long reply must create real vertical scrolling.");
                fixture.Scroll.ChangeView(null, fixture.Scroll.ScrollableHeight, null, true);
                await SettleAsync(root);
                AssertStable("long-reply-scroll", transitions, fixture);
                Assert.True(fixture.Scroll.VerticalOffset > 0, "The fixture did not scroll vertically.");

                foreach (var width in new[] { 360, 1020, 760, 1440, 1603 })
                {
                    window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, width, 800));
                    await SettleAsync(root);
                    AssertStable("resize-" + width, transitions, fixture);
                    AssertUserAtRight(fixture, user);
                }
                fixture.Panel.MaxWidth = 760;
                await SettleAsync(root);
                AssertStable("compact-column", transitions, fixture);
                AssertUserAtRight(fixture, user);

                root.Children.Clear();
                await Task.Delay(60);
                root.Children.Add(fixture.Scroll);
                await SettleAsync(root);
                AssertStable("reattached", transitions, fixture);

                fixture.Panel.Children.Clear();
                fixture.Scroll.ChangeView(0, 0, null, true);
                await SettleAsync(root);
                AssertStable("reset-empty", transitions, fixture);
                var nextUser = UserBubble("新的会话");
                fixture.Panel.Children.Add(nextUser);
                await SettleAsync(root);
                AssertStable("reset-first-user", transitions, fixture);
                AssertUserAtRight(fixture, nextUser);
            }
        });
    }

    private static Fixture CreateProductionFixture()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var page = XDocument.Load(FindPageXaml());
        var fragment = new XElement(page.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "MsgScroll"));
        fragment.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        fragment.Attribute("ViewChanged")?.Remove(); // The product page callback is deliberately not loaded.
        var scroll = Assert.IsType<ScrollViewer>(XamlReader.Load(fragment.ToString()));
        var viewport = Assert.IsType<Grid>(scroll.Content);
        var panel = Assert.IsType<StackPanel>(Assert.Single(viewport.Children));
        Assert.Null(fragment.Elements().Single().Attribute("Width"));
        Assert.Equal(HorizontalAlignment.Left, viewport.HorizontalAlignment);
        Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollMode.Disabled, scroll.HorizontalScrollMode);
        ChatViewportLayout.Attach(scroll, viewport);
        ChatViewportLayout.Attach(scroll, viewport); // Repeated initialization must remain harmless.
        return new Fixture(scroll, viewport, panel);
    }

    internal static async Task WithWindowAsync(Func<Window, Grid, Task> body)
    {
        // Keep one owned Window alive until the host has emitted its final output.
        // Closing WinUI's last Window between async cases can end Application.Start
        // while the next case waits for Loaded, without reporting the pending case.
        if (_hostWindow is null)
        {
            _hostRoot = new Grid();
            _hostWindow = new Window { Content = _hostRoot };
            var loaded = new TaskCompletionSource();
            _hostRoot.Loaded += (_, _) => loaded.TrySetResult();
            _hostWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1603, 800));
            _hostWindow.AppWindow.Show(false);
            Assert.True(await Task.WhenAny(loaded.Task, Task.Delay(6000)) == loaded.Task,
                "The isolated message fixture did not load.");
        }
        var window = _hostWindow ?? throw new InvalidOperationException("The owned layout Window was not created.");
        var root = _hostRoot!;
        root.Children.Clear();
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1603, 800));
        try
        {
            await body(window, root);
        }
        finally { root.Children.Clear(); }
    }

    internal static void CloseHostWindow()
    {
        var window = _hostWindow;
        _hostRoot?.Children.Clear();
        _hostRoot = null;
        _hostWindow = null;
        window?.Close();
    }

    private static async Task SettleAsync(Grid root)
    {
        await Task.Delay(120);
        root.UpdateLayout();
    }

    private static void AssertStable(string stage, bool transitions, Fixture fixture, bool checkChildren = true)
    {
        var scroll = fixture.Scroll;
        var panel = fixture.Panel;
        var x = X(panel, scroll);
        Console.WriteLine($"LAYOUT|{stage}|transitions={transitions}|scroll={scroll.ActualWidth}|panel={panel.ActualWidth}|x={x}|extent={scroll.ExtentWidth}|viewport={scroll.ViewportWidth}|offset={scroll.HorizontalOffset}");
        Assert.True(scroll.ViewportWidth > 0, "The connected ScrollViewer lost its real viewport.");
        AssertNear(scroll.ViewportWidth, fixture.Viewport.ActualWidth,
            "The production SizeChanged/Loaded width synchronization did not follow the actual viewport.");
        AssertNear(scroll.ViewportWidth, scroll.ExtentWidth,
            "The scroll extent collapsed to the short message's measured width.");
        AssertNear(0, scroll.HorizontalOffset, "The conversation acquired a horizontal offset.");
        AssertNear(Math.Min(scroll.ViewportWidth, panel.MaxWidth), panel.ActualWidth,
            "The message column width did not obey the viewport and configured maximum.");
        AssertNear((scroll.ViewportWidth - panel.ActualWidth) / 2, x,
            "The message column drifted away from the viewport center.");
        Assert.True(x >= -0.5 && x + panel.ActualWidth <= scroll.ViewportWidth + 0.5,
            "The message column was clipped outside its viewport.");
        if (checkChildren)
            foreach (var child in panel.Children.OfType<FrameworkElement>())
            {
                var childX = X(child, scroll);
                Assert.True(childX >= -0.5 && childX + child.ActualWidth <= scroll.ViewportWidth + 0.5,
                    $"A visible message escaped the viewport at {stage}.");
            }
    }

    private static Border UserBubble(string text) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 520,
        Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 0, 12),
        Child = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap },
    };

    private static void AssertUserAtRight(Fixture fixture, Border user) =>
        AssertNear(fixture.Panel.ActualWidth - fixture.Panel.Padding.Right,
            X(user, fixture.Panel) + user.ActualWidth,
            "The user bubble lost its intended right alignment inside the bounded column.");

    private static double X(UIElement element, UIElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0)).X;

    private static void AssertNear(double expected, double actual, string message) =>
        Assert.True(Math.Abs(expected - actual) <= 0.6, $"{message} Expected {expected}, actual {actual}.");

    private static string FindPageXaml()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "TubaWinUi3.WinUI3", "Pages", "AiAgentPage.xaml");
                if (File.Exists(path)) return path;
            }
        throw new FileNotFoundException("Production AiAgentPage.xaml was not found.");
    }

    private sealed record Fixture(ScrollViewer Scroll, Grid Viewport, StackPanel Panel);
}
