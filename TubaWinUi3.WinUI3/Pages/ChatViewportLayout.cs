using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TubaWinUi3.Pages;

/// <summary>
/// Keeps ScrollContentPresenter's measured extent as wide as the viewport, while
/// the inner message column can keep its own maximum width. ActualWidth bindings
/// are only evaluated initially in WinUI; size and reattachment need real events.
/// </summary>
internal static class ChatViewportLayout
{
    private static readonly ConditionalWeakTable<ScrollViewer, Attachment> Attachments = new();

    internal static void Attach(ScrollViewer scroll, FrameworkElement viewport)
    {
        ArgumentNullException.ThrowIfNull(scroll);
        ArgumentNullException.ThrowIfNull(viewport);
        if (Attachments.TryGetValue(scroll, out var existing))
        {
            if (!ReferenceEquals(existing.Viewport, viewport))
                throw new InvalidOperationException("The message ScrollViewer already owns a different viewport.");
            existing.UpdateWidth(scroll.ActualWidth);
            return;
        }
        Attachments.Add(scroll, new Attachment(scroll, viewport));
    }

    private sealed class Attachment
    {
        internal FrameworkElement Viewport { get; }

        internal Attachment(ScrollViewer scroll, FrameworkElement viewport)
        {
            Viewport = viewport;
            scroll.SizeChanged += (_, e) => UpdateWidth(e.NewSize.Width);
            scroll.Loaded += (_, _) => UpdateWidth(scroll.ActualWidth);
            UpdateWidth(scroll.ActualWidth);
        }

        internal void UpdateWidth(double width)
        {
            if (!double.IsFinite(width) || width < 0) return;
            if (double.IsNaN(Viewport.Width) || Math.Abs(Viewport.Width - width) > 0.01)
                Viewport.Width = width;
        }
    }
}
