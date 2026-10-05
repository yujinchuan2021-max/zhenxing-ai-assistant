using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Specialized;
using Windows.Foundation;

namespace TubaWinUi3.Controls;

/// <summary>
/// Data-sized masonry for a vertically scrolling ItemsRepeater. Geometry is cheap
/// metadata; only the realization rectangle (which already includes the repeater's
/// cache) creates native controls. Appending preserves existing item assignments.
/// </summary>
public sealed class SkillLibraryMasonryLayout : VirtualizingLayout
{
    private int _revision;
    private Func<object, double, double>? _itemHeightProvider;

    public SkillLibraryMasonryLayout() { }

    public double MinimumColumnWidth
    {
        get => (double)GetValue(MinimumColumnWidthProperty);
        set => SetValue(MinimumColumnWidthProperty, value);
    }
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }
    public static readonly DependencyProperty MinimumColumnWidthProperty = DependencyProperty.Register(
        nameof(MinimumColumnWidth), typeof(double), typeof(SkillLibraryMasonryLayout), new PropertyMetadata(280d, Changed));
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(SkillLibraryMasonryLayout), new PropertyMetadata(14d, Changed));
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(SkillLibraryMasonryLayout), new PropertyMetadata(14d, Changed));

    /// <summary>CPU-only size prediction for an item at the supplied column width.
    /// Include padding and reserve sufficient line height; never create XAML here.</summary>
    public Func<object, double, double>? ItemHeightProvider
    {
        get => _itemHeightProvider;
        set
        {
            if (ReferenceEquals(_itemHeightProvider, value)) return;
            _itemHeightProvider = value;
            InvalidateItemHeights();
        }
    }

    /// <summary>Call when item text changes in place without a collection notification.</summary>
    public void InvalidateItemHeights() { _revision++; InvalidateMeasure(); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SkillLibraryMasonryLayout)sender).InvalidateItemHeights();

    protected override void InitializeForContextCore(VirtualizingLayoutContext context)
    {
        base.InitializeForContextCore(context);
        context.LayoutState = new State();
    }

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
    {
        if (context.LayoutState is State state) state.Realized.Clear();
        context.LayoutState = null;
        base.UninitializeForContextCore(context);
    }

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source,
        NotifyCollectionChangedEventArgs args)
    {
        if (context.LayoutState is State state &&
            !(args.Action == NotifyCollectionChangedAction.Add && args.NewStartingIndex >= state.Placements.Count))
        {
            if (args.Action == NotifyCollectionChangedAction.Replace)
            {
                // Repeater preserves existing elements for same-index Replace.
                // Recycle the affected views during the collection notification
                // so its factory prepares fresh bindings (including x:Bind).
                var end = args.OldStartingIndex + (args.OldItems?.Count ?? 0);
                foreach (var pair in state.Realized.Where(pair =>
                    pair.Key >= args.OldStartingIndex && pair.Key < end).ToArray())
                {
                    context.RecycleElement(pair.Value);
                    state.Realized.Remove(pair.Key);
                }
            }
            state.Placements.Clear();
            // Keep unaffected indices for an equal-count replacement, including
            // consecutive replacements before the next measure. Other edits let
            // the repeater remap indices before we request them again.
            if (args.Action != NotifyCollectionChangedAction.Replace || args.OldItems?.Count != args.NewItems?.Count)
                state.Realized.Clear();
            Array.Clear(state.Bottoms);
        }
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var state = context.LayoutState as State ?? new State();
        context.LayoutState = state;
        var minWidth = Positive(MinimumColumnWidth, 280);
        var columnGap = Nonnegative(ColumnSpacing);
        var rowGap = Nonnegative(RowSpacing);
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : Positive(context.RealizationRect.Width, minWidth);
        var columns = Math.Max(1, (int)Math.Min(64, Math.Floor((width + columnGap) / (minWidth + columnGap))));
        var columnWidth = Math.Max(1, (width - columnGap * (columns - 1)) / columns);
        if (state.Revision != _revision || Math.Abs(state.Width - width) > .1 || state.Bottoms.Length != columns)
        {
            state.Placements.Clear();
            state.Bottoms = new double[columns];
            state.Width = width;
            state.Revision = _revision;
        }
        if (context.ItemCount < state.Placements.Count)
        {
            state.Placements.Clear();
            Array.Clear(state.Bottoms);
        }
        for (var index = state.Placements.Count; index < context.ItemCount; index++)
        {
            var column = 0;
            for (var candidate = 1; candidate < columns; candidate++)
                if (state.Bottoms[candidate] < state.Bottoms[column]) column = candidate;
            var height = Positive(_itemHeightProvider?.Invoke(context.GetItemAt(index), columnWidth) ?? 240, 240);
            var bounds = new Rect(column * (columnWidth + columnGap), state.Bottoms[column], columnWidth, height);
            state.Placements.Add(new Placement(column, bounds));
            state.Bottoms[column] = bounds.Bottom + rowGap;
        }
        context.LayoutOrigin = new Point(0, 0);

        // The normal path performs one pass. If a realized card is taller than
        // predicted, grow it without clipping and reconcile this viewport once.
        // Existing cards retain their column; only affected successors move down.
        for (var pass = 0; pass < 2; pass++)
        {
            var wanted = Wanted(context, state);
            foreach (var previous in state.Realized.Keys.Where(index => !wanted.Contains(index)).ToArray())
            {
                context.RecycleElement(state.Realized[previous]);
                state.Realized.Remove(previous);
            }
            var grew = false;
            foreach (var index in wanted)
            {
                var placement = state.Placements[index];
                // Request every retained item too, so the framework marks it in use.
                var element = context.GetOrCreateElementAt(index);
                state.Realized[index] = element;
                element.Measure(new Size(columnWidth, double.PositiveInfinity));
                var desired = element.DesiredSize.Height;
                if (double.IsFinite(desired) && desired > placement.Bounds.Height + .5)
                {
                    placement.Bounds = new Rect(placement.Bounds.X, placement.Bounds.Y, columnWidth, desired);
                    grew = true;
                }
            }
            if (!grew) break;
            Reflow(state, rowGap);
            if (pass == 1) InvalidateMeasure();
        }
        return new Size(width, ExtentHeight(state, rowGap));
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context.LayoutState is State state)
            foreach (var (index, element) in state.Realized)
                if (index < state.Placements.Count) element.Arrange(state.Placements[index].Bounds);
        return finalSize;
    }

    private static HashSet<int> Wanted(VirtualizingLayoutContext context, State state)
    {
        var result = new HashSet<int>();
        var viewport = context.RealizationRect;
        // A disconnected/hidden repeater can report an empty or infinite region.
        // That must never mean "inflate the entire catalog".
        if (!double.IsFinite(viewport.X) || !double.IsFinite(viewport.Y) ||
            !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
            viewport.Width <= 0 || viewport.Height <= 0) return result;
        for (var index = 0; index < state.Placements.Count; index++)
        {
            var bounds = state.Placements[index].Bounds;
            if (bounds.Bottom >= viewport.Top && bounds.Top <= viewport.Bottom &&
                bounds.Right >= viewport.Left && bounds.Left <= viewport.Right) result.Add(index);
        }
        // Keyboard focus / StartBringIntoView may ask for one offscreen anchor.
        // Realizing that one item is bounded and lets the native repeater scroll it.
        if (context.RecommendedAnchorIndex >= 0 && context.RecommendedAnchorIndex < state.Placements.Count)
            result.Add(context.RecommendedAnchorIndex);
        return result;
    }

    private static void Reflow(State state, double rowGap)
    {
        Array.Clear(state.Bottoms);
        foreach (var placement in state.Placements)
        {
            var bounds = placement.Bounds;
            placement.Bounds = new Rect(bounds.X, state.Bottoms[placement.Column], bounds.Width, bounds.Height);
            state.Bottoms[placement.Column] = placement.Bounds.Bottom + rowGap;
        }
    }
    private static double ExtentHeight(State state, double rowGap) => state.Placements.Count == 0
        ? 0 : Math.Max(0, state.Bottoms.Max() - rowGap);
    private static double Positive(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
    private static double Nonnegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;

    private sealed class State
    {
        public int Revision = -1;
        public double Width = -1;
        public double[] Bottoms = [];
        public List<Placement> Placements { get; } = [];
        public Dictionary<int, UIElement> Realized { get; } = [];
    }
    private sealed class Placement(int column, Rect bounds)
    {
        public int Column { get; } = column;
        public Rect Bounds { get; set; } = bounds;
    }
}
