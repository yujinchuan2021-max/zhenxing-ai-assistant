using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Controls;
using TubaWinUi3.Services;
using Windows.Foundation;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>Actual ItemsRepeater virtualization with synthetic data only.</summary>
internal static class MasonryLayoutCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("Masonry_VirtualizedHeightsAppendResizeAndReset", VirtualizedHeightsAppendResizeAndReset),
    ];

    public sealed record Sample(int Index, double CardHeight, double EstimateHeight)
    {
        public string Label => "合成技能 " + Index;
    }

    private static async Task VirtualizedHeightsAppendResizeAndReset()
    {
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        var items = new ObservableCollection<Sample>(Enumerable.Range(0, 2000).Select(index =>
            new Sample(index, index == 0 ? 360 : 130 + index % 5 * 29, index == 0 ? 100 : 130 + index % 5 * 29)));
        var layout = new SkillLibraryMasonryLayout
        {
            MinimumColumnWidth = 280, ColumnSpacing = 14, RowSpacing = 14,
            ItemHeightProvider = (item, _) => ((Sample)item).EstimateHeight,
        };
        var repeater = new ItemsRepeater
        {
            Layout = layout, ItemsSource = items, VerticalCacheLength = 1, HorizontalCacheLength = 0,
            ItemTemplate = Assert.IsType<DataTemplate>(XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Border Height="{Binding CardHeight}" Padding="12" Background="#FFE7E4E1">
                        <TextBlock Text="{Binding Label}" Foreground="#FF2B2B30" TextWrapping="Wrap" />
                    </Border>
                </DataTemplate>
                """)),
        };
        var active = new Dictionary<int, UIElement>();
        var prepared = 0;
        var cleared = 0;
        repeater.ElementPrepared += (_, args) => { prepared++; active[args.Index] = args.Element; };
        repeater.ElementClearing += (_, args) =>
        {
            cleared++;
            foreach (var pair in active.Where(pair => ReferenceEquals(pair.Value, args.Element)).ToArray()) active.Remove(pair.Key);
        };
        // No viewport yet: even a large finite measure cannot inflate 2,000 cards.
        repeater.Measure(new Size(1080, double.PositiveInfinity));
        Assert.True(prepared < 120, "An unattached repeater realized the catalog before it had a viewport.");
        var scroll = new ScrollViewer
        {
            Content = repeater, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
        {
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1100, 820));
            root.Children.Add(scroll);
            await Settle(root);
            Assert.True(active.Count > 0);
            Assert.True(active.Count < 120, "The initial view must realize a bounded window, not every skill.");
            Assert.Equal(3, ColumnCount(active, repeater));
            Assert.True(scroll.ScrollableHeight > 50000);
            AssertNoOverlap(active, repeater, items);
            var first = Assert.IsType<Border>(repeater.TryGetElement(0));
            Assert.True(first.ActualHeight >= 359, "A card taller than its estimate must expand without clipping.");
            var topPositions = active.ToDictionary(pair => pair.Key, pair => Bounds(pair.Value, repeater));
            Assert.True(topPositions.Values.Select(bounds => Math.Round(bounds.Height)).Distinct().Count() > 1,
                "This must be an unequal-height masonry, not an equal-row grid.");

            // Move far enough that the first viewport leaves the realization cache.
            var clearedBefore = cleared;
            Assert.True(scroll.ChangeView(null, 24000, null, disableAnimation: true));
            await Settle(root);
            Assert.True(scroll.VerticalOffset > 20000);
            Assert.True(cleared > clearedBefore, "Far-away native elements were not recycled.");
            Assert.True(active.Count < 120);
            Assert.True(active.Keys.Count(index => index > 100) > 5);
            AssertNoOverlap(active, repeater, items);

            // Appending data must preserve every already laid-out visible item,
            // and must not reset the scroll offset to the top.
            var offset = scroll.VerticalOffset;
            var beforeAppend = active.ToDictionary(pair => pair.Key, pair => Bounds(pair.Value, repeater));
            for (var index = 2000; index < 2040; index++) items.Add(new Sample(index, 145 + index % 4 * 31, 145 + index % 4 * 31));
            await Settle(root);
            Assert.InRange(Math.Abs(scroll.VerticalOffset - offset), 0, 1);
            foreach (var (index, oldBounds) in beforeAppend)
                if (repeater.TryGetElement(index) is { } element) AssertRect(oldBounds, Bounds(element, repeater));
            Assert.True(active.Count < 120);
            AssertNoOverlap(active, repeater, items);

            foreach (var (width, expectedColumns) in new[] { (690, 2), (360, 1), (1100, 3) })
            {
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, width, 820));
                await Settle(root);
                Assert.True(active.Count > 0 && active.Count < 120);
                Assert.Equal(expectedColumns, ColumnCount(active, repeater));
                AssertNoOverlap(active, repeater, items);
            }

            // Reset/filter data, including replacement height, cannot reuse old
            // geometry or leave old item wrappers pinned by the layout.
            scroll.ChangeView(null, 0, null, disableAnimation: true);
            items.Clear();
            for (var index = 0; index < 30; index++) items.Add(new Sample(index, 170 + index % 3 * 40, 170 + index % 3 * 40));
            await Settle(root);
            Assert.True(active.Count > 0 && active.Count <= 30);
            Assert.All(active.Keys, index => Assert.InRange(index, 0, 29));
            AssertNoOverlap(active, repeater, items);
            var beforeReplacement = Assert.IsType<Border>(repeater.TryGetElement(0));
            var oldReplacementData = Assert.IsType<Sample>(beforeReplacement.DataContext);
            var oldReplacementBounds = Bounds(beforeReplacement, repeater);
            var preparedBeforeReplacement = prepared;
            var clearedBeforeReplacement = cleared;
            items[0] = new Sample(0, 450, 450);
            items[1] = new Sample(1, 335, 335); // A second replacement before layout must also rebind.
            await Settle(root);
            var replacement = Assert.IsType<Border>(repeater.TryGetElement(0));
            var replacementData = replacement.DataContext as Sample;
            var replacementBounds = Bounds(replacement, repeater);
            var replacementSlot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(replacement);
            var replacementEvidence = $"MASONRY_REPLACE|sourceHeight={items[0].CardHeight}|dataHeight={replacementData?.CardHeight}|sameData={ReferenceEquals(items[0], replacement.DataContext)}|sameElement={ReferenceEquals(beforeReplacement, replacement)}|oldDataHeight={oldReplacementData.CardHeight}|boundHeight={replacement.Height}|desiredHeight={replacement.DesiredSize.Height}|actualHeight={replacement.ActualHeight}|oldRect={oldReplacementBounds}|rect={replacementBounds}|slot={replacementSlot}|offset={scroll.VerticalOffset}|prepared={prepared - preparedBeforeReplacement}|cleared={cleared - clearedBeforeReplacement}";
            Console.WriteLine(replacementEvidence);
            Assert.Same(items[0], replacement.DataContext);
            Assert.Same(items[1], Assert.IsType<Border>(repeater.TryGetElement(1)).DataContext);
            Assert.True(replacement.ActualHeight >= 449, replacementEvidence);
            AssertNoOverlap(active, repeater, items);

            root.Children.Remove(scroll);
            await Settle(root);
            root.Children.Add(scroll);
            await Settle(root);
            Assert.True(active.Count > 0 && active.Count <= items.Count);
            AssertNoOverlap(active, repeater, items);
            Console.WriteLine($"MASONRY_LAYOUT|items={items.Count}|active={active.Count}|prepared={prepared}|recycled={cleared}|columns=3,2,1|append=stable");
        });
    }

    private static int ColumnCount(Dictionary<int, UIElement> active, ItemsRepeater repeater) =>
        active.Values.Select(element => Math.Round(Bounds(element, repeater).X)).Distinct().Count();

    private static void AssertNoOverlap(Dictionary<int, UIElement> active, ItemsRepeater repeater, IReadOnlyList<Sample> items)
    {
        var bounds = active.Where(pair => pair.Key < items.Count)
            .Select(pair => (pair.Key, Rect: Bounds(pair.Value, repeater))).ToArray();
        foreach (var entry in bounds)
        {
            Assert.True(entry.Rect.Width > 0 && entry.Rect.Height >= items[entry.Key].CardHeight - 1);
            Assert.True(entry.Rect.X >= -1 && entry.Rect.Right <= repeater.ActualWidth + 1,
                "A masonry card is outside the available column width.");
        }
        for (var i = 0; i < bounds.Length; i++)
            for (var j = i + 1; j < bounds.Length; j++)
            {
                var a = bounds[i].Rect; var b = bounds[j].Rect;
                Assert.False(a.Left < b.Right - 1 && a.Right > b.Left + 1 && a.Top < b.Bottom - 1 && a.Bottom > b.Top + 1,
                    $"Masonry items {bounds[i].Key} and {bounds[j].Key} overlap.");
            }
    }
    private static Rect Bounds(UIElement element, ItemsRepeater repeater)
    {
        var control = Assert.IsAssignableFrom<FrameworkElement>(element);
        var origin = control.TransformToVisual(repeater).TransformPoint(new Point(0, 0));
        return new Rect(origin.X, origin.Y, control.ActualWidth, control.ActualHeight);
    }
    private static void AssertRect(Rect expected, Rect actual)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, .5);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, .5);
        Assert.InRange(Math.Abs(expected.Width - actual.Width), 0, .5);
        Assert.InRange(Math.Abs(expected.Height - actual.Height), 0, .5);
    }
    private static async Task Settle(Grid root)
    {
        await Task.Delay(400);
        root.UpdateLayout();
        await Task.Delay(150);
        root.UpdateLayout();
    }
}
