using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using Windows.UI;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

internal static class SkillLibraryCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("SkillLibrary_MasonryTwoPagesThenSingleAppend", TwoPagesThenAppend),
        ("SkillLibrary_FullCatalogueSearchAndCancellation", SearchAndCancellation),
        ("SkillLibrary_LeaveDuringLoadCancelsAndIgnoresOldResponse", LeaveDuringLoad),
    ];

    private static SkillCatalogItem[] Entries() => Enumerable.Range(0, 4000).Select(index => new SkillCatalogItem
    {
        Id = "fixture_" + index, DisplayName = "技能条目 " + index,
        Description = index % 3 == 0 ? "简短的技能说明。" :
            string.Concat(Enumerable.Repeat("用于验证不同卡片高度与滚动的合成技能说明。", index % 3 + 1)),
        Category = index == 3999 ? "game" : "development",
        Details = index == 3999 ? "unique_tail_marker" : "synthetic details",
        Author = "Synthetic author", License = "MIT", CanInstall = index % 2 == 1,
    }).ToArray();

    private static SkillCatalog Slice(SkillCatalogItem[] entries, int page) =>
        new(1, "synthetic", entries.Skip(page * 100).Take(100).ToArray(), (page + 1) * 100 < entries.Length, page);

    private static async Task TwoPagesThenAppend()
    {
        EnsureResources();
        var entries = Entries();
        var uiThread = Environment.CurrentManagedThreadId;
        int requests = 0, uiRequests = 0;
        var thirdRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdResponse = new TaskCompletionSource<SkillCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
        {
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1180, 1000));
            // The product page is intentionally transparent over the window's
            // backdrop. Capture its real native Light canvas too, not alpha pixels
            // that an image viewer may composite against black.
            var canvas = Assert.IsType<Grid>(XamlReader.Load("""
                <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      Background="{ThemeResource ApplicationPageBackgroundThemeBrush}" />
                """));
            canvas.RequestedTheme = ElementTheme.Light;
            var page = new SkillLibraryPage((pageIndex, token) =>
            {
                Interlocked.Increment(ref requests);
                if (Environment.CurrentManagedThreadId == uiThread) Interlocked.Increment(ref uiRequests);
                if (pageIndex == 2) { thirdRequested.TrySetResult(); return thirdResponse.Task; }
                return Task.FromResult(Slice(entries, pageIndex));
            }) { RequestedTheme = ElementTheme.Light };
            var cards = Assert.IsType<ItemsRepeater>(page.FindName("Cards"));
            var scroll = Assert.IsType<ScrollViewer>(page.FindName("CardScroll"));
            var rows = Assert.IsType<ObservableCollection<SkillLibraryCard>>(cards.ItemsSource);
            var elapsed = Stopwatch.StartNew();
            canvas.Children.Add(page);
            root.Children.Add(canvas);
            await WaitUntil(root, () => rows.Count == 201 && !Field<bool>(page, "_loading"), "first two pages");
            await Settle(root);
            Assert.Equal(2, requests);
            Assert.Equal(0, uiRequests);
            AssertVirtualized(cards, scroll, rows.Count, "initial");
            Assert.Contains(VisualNodes(cards).OfType<TextBlock>(), text => text.Text.StartsWith("技能条目 ", StringComparison.Ordinal));
            AssertLightTextContrast(page, cards, canvas);
            var first = rows[1];
            var heights = Enumerable.Range(0, rows.Count).Select(index => cards.TryGetElement(index))
                .OfType<FrameworkElement>().Select(element => Math.Round(element.ActualHeight)).Distinct().ToArray();
            Assert.True(heights.Length > 1, "Masonry cards must have genuinely different heights.");
            Console.WriteLine($"SKILL_LIBRARY_LOADED|metadata=200|requests=2|milliseconds={elapsed.ElapsedMilliseconds}");

            scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
            await thirdRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Settle(root);
            var beforeOffset = scroll.VerticalOffset;
            // While page 2 is still pending, repeated scroll notifications must not fetch it again.
            scroll.ChangeView(null, Math.Max(0, beforeOffset - 8), null, disableAnimation: true);
            scroll.ChangeView(null, beforeOffset, null, disableAnimation: true);
            await Settle(root);
            Assert.Equal(3, requests);
            thirdResponse.SetResult(Slice(entries, 2));
            await WaitUntil(root, () => rows.Count == 301 && !Field<bool>(page, "_loading"), "one appended page");
            await Settle(root);
            Assert.Equal(3, requests);
            Assert.Same(rows, cards.ItemsSource);
            Assert.Same(first, rows[1]);
            Assert.InRange(Math.Abs(scroll.VerticalOffset - beforeOffset), 0, 2);
            Assert.Equal(300, rows.Skip(1).Select(row => row.Item.Id).Distinct().Count());
            AssertVirtualized(cards, scroll, rows.Count, "append");
            foreach (var width in new[] { 760, 1180 })
            {
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, width, 1000));
                await Settle(root);
                AssertVirtualized(cards, scroll, rows.Count, "width=" + width);
            }
            scroll.ChangeView(null, 0, null, disableAnimation: true);
            await Settle(root);
            AssertLightTextContrast(page, cards, canvas);
            if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                await GoalGuideCases.SaveControlPreviewAsync(canvas, "skill-library-masonry-light.png");
            root.Children.Remove(canvas);
            await Settle(root);
        });
    }

    private static async Task SearchAndCancellation()
    {
        EnsureResources();
        var entries = Entries();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int thirdAttempts = 0, requests = 0;
        await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
        {
            var page = new SkillLibraryPage(async (pageIndex, token) =>
            {
                Interlocked.Increment(ref requests);
                if (pageIndex == 2 && Interlocked.Increment(ref thirdAttempts) == 1)
                {
                    using var registration = token.Register(() => cancelled.TrySetResult());
                    blocked.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                return Slice(entries, pageIndex);
            });
            var cards = Assert.IsType<ItemsRepeater>(page.FindName("Cards"));
            var rows = Assert.IsType<ObservableCollection<SkillLibraryCard>>(cards.ItemsSource);
            root.Children.Add(page);
            await WaitUntil(root, () => rows.Count == 201 && !Field<bool>(page, "_loading"), "initial browsing pages");
            var search = Field<TextBox>(page, "_search");
            search.Text = "unique_tail_marker";
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            search.Text = "";
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntil(root, () => rows.Count == 201 && !Field<bool>(page, "_loading"), "cleared search returns to browsing");
            Assert.Equal(3, requests);
            Assert.Equal(200, Field<SkillCatalogItem[]>(page, "_items").Length);
            Assert.Same(rows, cards.ItemsSource);

            search.Text = "old term";
            search.Text = "unique_tail_marker";
            await WaitUntil(root, () => rows.Count == 2 && rows[1].Item.Id == "fixture_3999" &&
                !Field<bool>(page, "_loading"), "search includes metadata beyond the first two pages");
            Assert.Equal(4000, Field<SkillCatalogItem[]>(page, "_items").Length);
            Assert.Equal(41, requests); // page 2 was cancelled once, then all 40 pages are present.
            var categories = Field<ComboBox>(page, "_categories");
            categories.SelectedItem = categories.Items.OfType<ComboBoxItem>().Single(item => (string?)item.Tag == "game");
            await Settle(root);
            Assert.Equal("fixture_3999", rows[1].Item.Id);
            categories.SelectedItem = categories.Items.OfType<ComboBoxItem>().Single(item => (string?)item.Tag == "all");
            search.Text = "";
            await WaitUntil(root, () => rows.Count == 201, "full metadata still starts with two visible pages");
            Assert.Equal(41, requests);
            Assert.Same(rows, cards.ItemsSource);
            root.Children.Remove(page);
            await Settle(root);
        });
    }

    private static async Task LeaveDuringLoad()
    {
        EnsureResources();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = new TaskCompletionSource<SkillCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
        {
            var page = new SkillLibraryPage((pageIndex, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    token.Register(() => cancelled.TrySetResult());
                    entered.TrySetResult();
                    return stale.Task;
                }
                return Task.FromResult(new SkillCatalog(1, "fresh",
                    [new() { Id = "fresh_item", DisplayName = "当前目录", Description = "新导航结果" }], false, pageIndex));
            });
            var cards = Assert.IsType<ItemsRepeater>(page.FindName("Cards"));
            var rows = Assert.IsType<ObservableCollection<SkillLibraryCard>>(cards.ItemsSource);
            root.Children.Add(page);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            root.Children.Remove(page);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(page.IsLoaded);
            var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(root.DispatcherQueue.TryEnqueue(() => heartbeat.TrySetResult()));
            await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(stale.Task.IsCompleted);
            root.Children.Add(page);
            stale.SetResult(new(1, "old", [new() { Id = "stale_item", DisplayName = "旧目录" }], false, 0));
            await WaitUntil(root, () => rows.Count == 2 && rows[1].Item.Id == "fresh_item", "only remounted results apply");
            Assert.Equal(2, calls);
            Assert.DoesNotContain(rows, row => row.Item.Id == "stale_item");
            root.Children.Remove(page);
            await Settle(root);
        });
    }

    private static void AssertVirtualized(ItemsRepeater cards, ScrollViewer scroll, int count, string label)
    {
        var elements = Enumerable.Range(0, count).Select(index => cards.TryGetElement(index)).OfType<FrameworkElement>().ToArray();
        Assert.InRange(elements.Length, 1, 120);
        Assert.True(scroll.ViewportHeight > 100 && scroll.ViewportHeight < 1000, "Masonry must have a finite scrolling viewport.");
        foreach (var element in elements)
        {
            var position = element.TransformToVisual(cards).TransformPoint(new Windows.Foundation.Point(0, 0));
            Assert.True(position.X >= -1 && position.X + element.ActualWidth <= cards.ActualWidth + 1, "Card extends horizontally outside the masonry viewport.");
        }
        Console.WriteLine($"SKILL_LIBRARY_VIRTUALIZED|{label}|items={count}|containers={elements.Length}|viewport={scroll.ViewportWidth:F0}x{scroll.ViewportHeight:F0}");
    }

    private static T Field<T>(object target, string name) => Assert.IsType<T>(target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target));

    private static void AssertLightTextContrast(SkillLibraryPage page, ItemsRepeater cards, Grid canvas)
    {
        Assert.Equal(ElementTheme.Light, page.ActualTheme);
        var canvasColor = Assert.IsType<SolidColorBrush>(canvas.Background).Color;
        Assert.Equal((byte)255, canvasColor.A);
        var header = new[] { "_title", "_hint", "_status", "_inventory" }.Select(name => Field<TextBlock>(page, name));
        var count = Field<ObservableCollection<SkillLibraryCard>>(page, "_shown").Count;
        var cardTexts = Enumerable.Range(0, count).Select(index => cards.TryGetElement(index))
            .OfType<DependencyObject>().SelectMany(VisualNodes).OfType<TextBlock>()
            .Where(text => !string.IsNullOrWhiteSpace(text.Text)).ToArray();
        Assert.Contains(cardTexts, text => text.Text.StartsWith("技能条目 ", StringComparison.Ordinal));
        foreach (var text in header.Concat(cardTexts))
        {
            Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0, "The checked text must be part of the actual rendered page.");
            var backgrounds = new List<Brush>();
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
                if (brush is not null) backgrounds.Add(brush);
                if (ReferenceEquals(parent, canvas)) break;
            }
            var background = canvasColor;
            for (var index = backgrounds.Count - 1; index >= 0; index--) background = Composite(backgrounds[index], background);
            var foreground = Composite(text.Foreground, background);
            var a = Luminance(background); var b = Luminance(foreground);
            var ratio = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
            Assert.True(ratio >= 4.5, $"Skill library text contrast {ratio:F2}:1: {text.Text}");
        }
    }

    private static Color Composite(Brush brush, Color behind)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        var color = solid.Color;
        var alpha = color.A / 255d * solid.Opacity;
        byte Blend(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
        return Color.FromArgb(255, Blend(color.R, behind.R), Blend(color.G, behind.G), Blend(color.B, behind.B));
    }

    private static double Luminance(Color color)
    {
        static double C(byte value) { var n = value / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
        return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
    }

    private static IEnumerable<DependencyObject> VisualNodes(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var node in VisualNodes(VisualTreeHelper.GetChild(root, index))) yield return node;
    }
    private static void EnsureResources()
    {
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT")));
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
    }
    private static async Task WaitUntil(Grid root, Func<bool> condition, string reason)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(12))
        {
            root.UpdateLayout();
            if (condition()) return;
            await Task.Delay(30);
        }
        Assert.True(condition(), "Timed out: " + reason);
    }
    private static async Task Settle(Grid root) { await Task.Delay(300); root.UpdateLayout(); }
}
