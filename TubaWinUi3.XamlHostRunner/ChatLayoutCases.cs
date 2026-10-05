using System.Globalization;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// Native Measure/Arrange checks of the message presenter and StackPanel, without
/// a Window, default framework templates, a product page or configuration reads.
/// The layout values come from the production MsgScroll/MsgPanel XAML. The isolated
/// ContentPresenter exercises its content alignment directly; it does not cover
/// ScrollViewer's scrolling, scrollbar chrome, animation or pixel rendering.
/// </summary>
internal static class ChatLayoutCases
{
    public static (string Name, Func<Task> Body)[] All() =>
    [
        ("ChatLayout_FirstShortUser", () => { FirstShortUser(); return Task.CompletedTask; }),
        ("ChatLayout_LaterMessages", () => { LaterMessages(); return Task.CompletedTask; }),
        ("ChatLayout_ResetAndResize", () => { ResetAndResize(); return Task.CompletedTask; }),
        ("ChatLayout_DefaultAlignmentRegression", () => { DefaultAlignmentRegression(); return Task.CompletedTask; }),
    ];

    private static void FirstShortUser()
    {
        foreach (var width in new[] { 360d, 760d, 1020d, 1440d })
        {
            var fixture = CreateFixture();
            fixture.Layout(width);
            AssertPanel(fixture, width);
            var before = fixture.Panel.ActualWidth;
            var user = UserBubble("你好");
            fixture.Panel.Children.Add(user);
            fixture.Layout(width);
            AssertPanel(fixture, width);
            AssertNear(before, fixture.Panel.ActualWidth, "First message changed panel width.");
            AssertUserAtRight(fixture, user);
        }
    }

    private static void LaterMessages()
    {
        var fixture = CreateFixture();
        var first = UserBubble("帮我做个游戏");
        fixture.Panel.Children.Add(first);
        fixture.Layout(1440);
        var initialWidth = fixture.Panel.ActualWidth;
        var initialX = X(fixture.Panel, fixture.Root);
        AssertPanel(fixture, 1440);
        AssertUserAtRight(fixture, first);

        var assistant = new Border
        {
            Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 16),
            Child = new TextBlock
            {
                Text = string.Concat(Enumerable.Repeat("先确认目标，再选择适合的模型和开发环境。", 24)),
                TextWrapping = TextWrapping.Wrap,
            },
        };
        var second = UserBubble(string.Concat(Enumerable.Repeat("希望同时支持桌面和安卓。", 35)));
        fixture.Panel.Children.Add(assistant);
        fixture.Panel.Children.Add(second);
        fixture.Layout(1440);
        AssertPanel(fixture, 1440);
        AssertNear(initialWidth, fixture.Panel.ActualWidth, "Later text changed panel width.");
        AssertNear(initialX, X(fixture.Panel, fixture.Root), "Later text moved the panel.");
        AssertUserAtRight(fixture, first);
        AssertUserAtRight(fixture, second);
        AssertNear(fixture.Panel.ActualWidth - fixture.Panel.Padding.Left - fixture.Panel.Padding.Right,
            assistant.ActualWidth, "Assistant message did not fill the message column.");
        Assert.True(second.ActualWidth <= 520.5, "Long user message exceeded its bubble limit.");

        fixture.Panel.MaxWidth = 760; // The compact page changes this same property.
        fixture.Panel.Padding = new Thickness(14, 18, 14, 16);
        fixture.Layout(360);
        AssertPanel(fixture, 360);
        AssertUserAtRight(fixture, first);
        AssertUserAtRight(fixture, second);
        Assert.True(second.ActualWidth <= 332.5, "Narrow user message escaped the viewport padding.");
        Assert.True(assistant.ActualHeight > 0, "Wrapped assistant message was not measured.");
    }

    private static void ResetAndResize()
    {
        var fixture = CreateFixture();
        fixture.Panel.Children.Add(UserBubble("Previous conversation"));
        fixture.Panel.Children.Add(new TextBlock
        {
            Text = string.Concat(Enumerable.Repeat("A longer assistant reply. ", 40)),
            TextWrapping = TextWrapping.Wrap,
        });
        fixture.Layout(1440);
        fixture.Panel.Children.Clear();
        fixture.Layout(1440);
        AssertPanel(fixture, 1440);

        var first = UserBubble("New conversation");
        fixture.Panel.Children.Add(first);
        foreach (var width in new[] { 360d, 1440d, 760d, 1020d, 360d })
        {
            fixture.Layout(width);
            AssertPanel(fixture, width);
            AssertUserAtRight(fixture, first);
        }
    }

    private static void DefaultAlignmentRegression()
    {
        var fixture = CreateFixture();
        // Copy the production ScrollViewer's default into the isolated presenter.
        // ContentPresenter has a different default, so using its own value would
        // not represent an omitted ScrollViewer content alignment.
        fixture.Viewport.Children.Remove(fixture.Panel);
        fixture.Presenter.Content = fixture.Panel;
        fixture.Presenter.HorizontalContentAlignment = new ScrollViewer().HorizontalContentAlignment;
        fixture.Panel.Children.Add(UserBubble("Hi"));
        fixture.Layout(1440);
        Assert.True(fixture.Panel.ActualWidth < fixture.Panel.MaxWidth - 1,
            "The negative control did not reproduce content-sized layout.");

        fixture.Presenter.Content = null;
        fixture.Viewport.Children.Add(fixture.Panel);
        fixture.Presenter.Content = fixture.Viewport;
        fixture.Presenter.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        fixture.Layout(1440);
        AssertPanel(fixture, 1440);
        AssertUserAtRight(fixture, (Border)fixture.Panel.Children[0]);
    }

    private static Border UserBubble(string text) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 520,
        Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 0, 12),
        Child = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap },
    };

    private static void AssertPanel(Fixture fixture, double viewportWidth)
    {
        var expected = Math.Min(viewportWidth, fixture.Panel.MaxWidth);
        AssertNear(expected, fixture.Panel.ActualWidth, "Message panel width differs from its bounded viewport.");
        AssertNear((viewportWidth - expected) / 2, X(fixture.Panel, fixture.Root),
            "Message panel is not centered in the viewport.");
        Assert.True(fixture.Panel.ActualWidth > 0, "Native panel was not arranged.");
    }

    private static void AssertUserAtRight(Fixture fixture, Border user)
    {
        Assert.True(user.ActualWidth > 0, "User bubble was not arranged.");
        AssertNear(fixture.Panel.ActualWidth - fixture.Panel.Padding.Right,
            X(user, fixture.Panel) + user.ActualWidth, "User bubble escaped the message column's right edge.");
        Assert.True(X(user, fixture.Panel) >= fixture.Panel.Padding.Left - 0.5,
            "User bubble escaped the message column's left padding.");
    }

    private static double X(UIElement element, UIElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0)).X;

    private static void AssertNear(double expected, double actual, string message) =>
        Assert.True(Math.Abs(expected - actual) <= 0.5, $"{message} Expected {expected}, actual {actual}.");

    private static Fixture CreateFixture()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var page = XDocument.Load(FindPageXaml());
        var scrollXaml = page.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "MsgScroll");
        var panelXaml = scrollXaml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "MsgPanel");
        var scroll = new ScrollViewer(); // Created only; never templated, measured or shown.
        ApplyHorizontal(scrollXaml, "HorizontalContentAlignment", value => scroll.HorizontalContentAlignment = value);
        ApplyVertical(scrollXaml, "VerticalContentAlignment", value => scroll.VerticalContentAlignment = value);
        if (Enum.TryParse<ScrollBarVisibility>((string?)scrollXaml.Attribute("HorizontalScrollBarVisibility"), out var visibility))
            scroll.HorizontalScrollBarVisibility = visibility;
        if (Enum.TryParse<ScrollMode>((string?)scrollXaml.Attribute("HorizontalScrollMode"), out var mode))
            scroll.HorizontalScrollMode = mode;
        Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollMode.Disabled, scroll.HorizontalScrollMode);

        var panel = new StackPanel
        {
            MaxWidth = double.Parse(panelXaml.Attribute("MaxWidth")!.Value, CultureInfo.InvariantCulture),
            Padding = ParseThickness(panelXaml.Attribute("Padding")!.Value),
        };
        ApplyHorizontal(panelXaml, "HorizontalAlignment", value => panel.HorizontalAlignment = value);
        var viewport = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Children = { panel } };
        var presenter = new ContentPresenter
        {
            Content = viewport,
            HorizontalContentAlignment = scroll.HorizontalContentAlignment,
            VerticalContentAlignment = scroll.VerticalContentAlignment,
        };
        var root = new Grid { Children = { presenter } };
        return new Fixture(root, presenter, panel, viewport);
    }

    private static void ApplyHorizontal(XElement source, string property, Action<HorizontalAlignment> assign)
    {
        if (Enum.TryParse<HorizontalAlignment>((string?)source.Attribute(property), out var value)) assign(value);
    }

    private static void ApplyVertical(XElement source, string property, Action<VerticalAlignment> assign)
    {
        if (Enum.TryParse<VerticalAlignment>((string?)source.Attribute(property), out var value)) assign(value);
    }

    private static Thickness ParseThickness(string value)
    {
        var parts = value.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return parts.Length switch
        {
            1 => new Thickness(parts[0]),
            2 => new Thickness(parts[0], parts[1], parts[0], parts[1]),
            4 => new Thickness(parts[0], parts[1], parts[2], parts[3]),
            _ => throw new FormatException("Unexpected message panel padding."),
        };
    }

    private static string FindPageXaml()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "TubaWinUi3.WinUI3", "Pages", "AiAgentPage.xaml");
                if (File.Exists(path)) return path;
            }
        }
        throw new FileNotFoundException("Production AiAgentPage.xaml was not found.");
    }

    private sealed record Fixture(Grid Root, ContentPresenter Presenter, StackPanel Panel, Grid Viewport)
    {
        public void Layout(double width)
        {
            Viewport.Width = width;
            Root.InvalidateMeasure();
            Root.Measure(new Size(width, 640));
            Root.Arrange(new Rect(0, 0, width, 640));
        }
    }
}
