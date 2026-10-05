using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    private bool _latestScrollQueued;
    private int _latestScrollVersion;
    private bool _messageDirectManipulation;
    private double _lastMessageOffset;
    private ScrollBar? _messageVerticalScrollBar;
    private double _messageScrollBarOldValue;

    private void InitializeMessageScrolling()
    {
        // Content grows after the streaming callback returns, during XAML layout.
        // Neither content growth nor viewport reflow is an instruction to pause.
        MsgPanel.SizeChanged += (_, _) => SmartScroll();
        MsgScroll.SizeChanged += (_, _) => SmartScroll();
        MsgScroll.Loaded += (_, _) =>
        {
            AttachMessageScrollBar();
            _lastMessageOffset = MsgScroll.VerticalOffset;
            SmartScroll();
        };
        MsgScroll.Unloaded += (_, _) =>
        {
            CancelPendingLatestScroll();
            _messageDirectManipulation = false;
            DetachMessageScrollBar();
        };
        MsgScroll.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(MessageScroll_PointerWheelChanged), handledEventsToo: true);
        MsgScroll.AddHandler(UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(MessageScroll_PreviewKeyDown), handledEventsToo: true);
        MsgScroll.DirectManipulationStarted += (_, _) =>
        {
            _messageDirectManipulation = true;
            _lastMessageOffset = MsgScroll.VerticalOffset;
            CancelPendingLatestScroll();
        };
        MsgScroll.DirectManipulationCompleted += (_, _) =>
        {
            _messageDirectManipulation = false;
            SmartScroll();
        };
    }

    private void ResumeLatestFollowing()
    {
        CancelPendingLatestScroll();
        _messageDirectManipulation = false;
        _followLatest = true;
        UpdateLatestMessageButton();
        SmartScroll();
    }

    private void PauseLatestFollowing()
    {
        CancelPendingLatestScroll();
        _followLatest = false;
        UpdateLatestMessageButton();
    }

    private void CancelPendingLatestScroll()
    {
        _latestScrollVersion++;
        _latestScrollQueued = false;
    }

    private void MessageScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!IsOuterMessageScrollInput(e.OriginalSource, skipEditing: false)) return;
        var properties = e.GetCurrentPoint(MsgScroll).Properties;
        if (!properties.IsHorizontalMouseWheel && (e.KeyModifiers & VirtualKeyModifiers.Control) == 0)
            ObserveMessageWheel(properties.MouseWheelDelta);
    }

    private void ObserveMessageWheel(int delta)
    {
        if (delta > 0) PauseLatestFollowing();
    }

    private void MessageScroll_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // In particular, an editor's caret/Home keys and a nested details
        // ScrollViewer must not change the surrounding conversation's mode.
        if (IsOuterMessageScrollInput(e.OriginalSource, skipEditing: true)) ObserveMessageScrollKey(e.Key);
    }

    private void ObserveMessageScrollKey(VirtualKey key)
    {
        if (key is VirtualKey.Up or VirtualKey.PageUp or VirtualKey.Home) PauseLatestFollowing();
    }

    private bool IsOuterMessageScrollInput(object source, bool skipEditing)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (skipEditing && node is TextBox or RichEditBox or PasswordBox) return false;
            if (node is ScrollViewer scroll) return ReferenceEquals(scroll, MsgScroll);
        }
        return false;
    }

    private void AttachMessageScrollBar()
    {
        DetachMessageScrollBar();
        MsgScroll.ApplyTemplate();
        _messageVerticalScrollBar = FindVerticalBar(MsgScroll);
        if (_messageVerticalScrollBar is null) return;
        _messageScrollBarOldValue = _messageVerticalScrollBar.Value;
        _messageVerticalScrollBar.ValueChanged += MessageScrollBar_ValueChanged;
        _messageVerticalScrollBar.Scroll += MessageScrollBar_Scroll;
    }

    private ScrollBar? FindVerticalBar(DependencyObject node)
    {
        // Search only the outer viewer's template, never message content.
        if (ReferenceEquals(node, MsgViewport)) return null;
        if (node is ScrollBar bar && bar.Orientation == Orientation.Vertical) return bar;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindVerticalBar(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
        return null;
    }

    private void DetachMessageScrollBar()
    {
        if (_messageVerticalScrollBar is null) return;
        _messageVerticalScrollBar.ValueChanged -= MessageScrollBar_ValueChanged;
        _messageVerticalScrollBar.Scroll -= MessageScrollBar_Scroll;
        _messageVerticalScrollBar = null;
    }

    private void MessageScrollBar_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        _messageScrollBarOldValue = e.OldValue;

    private void MessageScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        // Scroll is a native user-action event. ValueChanged alone also occurs
        // during layout/programmatic ChangeView and must never pause following.
        if (e.ScrollEventType is ScrollEventType.SmallDecrement or ScrollEventType.LargeDecrement or ScrollEventType.First ||
            (e.ScrollEventType is ScrollEventType.ThumbTrack or ScrollEventType.ThumbPosition && e.NewValue < _messageScrollBarOldValue))
            PauseLatestFollowing();
    }

    private void MsgScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        var offset = MsgScroll.VerticalOffset;
        if (_messageDirectManipulation && offset < _lastMessageOffset - .5) PauseLatestFollowing();
        _lastMessageOffset = offset;
        UpdateLatestMessageButton();
        if (_followLatest && MsgScroll.ScrollableHeight - offset > .5) SmartScroll();
    }

    private void UpdateLatestMessageButton() => ScrollToBottomButton.Visibility =
        !_followLatest && MsgScroll.ScrollableHeight > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ScrollToBottomButton_Click(object sender, RoutedEventArgs e) => ResumeLatestFollowing();

    private void SmartScroll()
    {
        if (!_followLatest || _latestScrollQueued || _messageDirectManipulation || !MsgScroll.IsLoaded) return;
        _latestScrollQueued = true;
        var version = _latestScrollVersion;
        var epoch = _displayEpoch;
        if (!_dq.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            // A user scroll, view unload, or conversation change invalidates a
            // queued scroll, even when another newer request has already begun.
            if (version != _latestScrollVersion) return;
            _latestScrollQueued = false;
            if (epoch != _displayEpoch || !_followLatest || _messageDirectManipulation || !MsgScroll.IsLoaded) return;
            MsgScroll.UpdateLayout();
            if (!_followLatest) return;
            var bottom = MsgScroll.ScrollableHeight;
            if (Math.Abs(bottom - MsgScroll.VerticalOffset) > .5)
                MsgScroll.ChangeView(null, bottom, null, disableAnimation: true);
            UpdateLatestMessageButton();
        })) _latestScrollQueued = false;
    }
}
