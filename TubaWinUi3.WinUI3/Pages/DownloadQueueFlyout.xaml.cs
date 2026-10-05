using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Pages;

public sealed partial class DownloadQueueFlyout : UserControl
{
    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    private readonly Dictionary<string, DownloadItemViewModel> _vmMap = [];
    private readonly DispatcherQueue _dq;

    public DownloadQueueFlyout() : this(null) { }

    // A supplied snapshot renders the same native template without subscribing to the live queue.
    internal DownloadQueueFlyout(IEnumerable<DownloadItem>? snapshot)
    {
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();

        QueueRepeater.ItemsSource = Items;

        foreach (var item in snapshot ?? DownloadQueueService.Queue)
            AddViewModel(item);

        if (snapshot is null)
        {
            DownloadQueueService.Queue.CollectionChanged += OnQueueCollectionChanged;
            DownloadQueueService.QueueChanged += OnQueueChanged;
        }

        UpdateEmptyState();
    }

    internal static Flyout CreateThemedFlyout(FrameworkElement themeSource, DownloadQueueFlyout? content = null)
    {
        content ??= new DownloadQueueFlyout();
        var flyout = new Flyout
        {
            Content = content,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            SystemBackdrop = null,
        };
        ElementTheme? styledTheme = null;
        FlyoutPresenter? boundPresenter = null;
        void ApplyTheme()
        {
            var theme = themeSource.ActualTheme;
            content.RequestedTheme = theme;
            if (styledTheme != theme)
            {
                // Acrylic's composited surface can disagree with its correct FallbackColor.
                // Use the content's native solid theme resource for the whole popup, including padding.
                flyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
                {
                    Setters =
                    {
                        new Setter(FrameworkElement.RequestedThemeProperty, theme),
                        new Setter(Control.BackgroundProperty, content.Background),
                    },
                };
                styledTheme = theme;
            }
            for (var parent = VisualTreeHelper.GetParent(content); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is FlyoutPresenter presenter)
                {
                    presenter.RequestedTheme = theme;
                    if (!ReferenceEquals(boundPresenter, presenter))
                    {
                        presenter.SetBinding(Control.BackgroundProperty, new Binding
                        {
                            Source = content, Path = new PropertyPath(nameof(Background)), Mode = BindingMode.OneWay,
                        });
                        boundPresenter = presenter;
                    }
                    break;
                }
        }
        void SourceThemeChanged(FrameworkElement sender, object args) => ApplyTheme();
        void ContentLoaded(object sender, RoutedEventArgs args) => ApplyTheme();
        void SourceUnloaded(object sender, RoutedEventArgs args)
        {
            Detach();
            flyout.Hide();
        }
        void Detach()
        {
            themeSource.ActualThemeChanged -= SourceThemeChanged;
            themeSource.Unloaded -= SourceUnloaded;
            content.Loaded -= ContentLoaded;
        }
        flyout.Opening += (_, _) =>
        {
            Detach();
            themeSource.ActualThemeChanged += SourceThemeChanged;
            themeSource.Unloaded += SourceUnloaded;
            content.Loaded += ContentLoaded;
            ApplyTheme();
        };
        flyout.Opened += (_, _) => ApplyTheme();
        flyout.Closed += (_, _) => Detach();
        ApplyTheme();
        return flyout;
    }

    private void OnQueueCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _dq.TryEnqueue(() =>
        {
            if (e.NewItems is not null)
                foreach (DownloadItem item in e.NewItems)
                    AddViewModel(item);

            if (e.OldItems is not null)
                foreach (DownloadItem item in e.OldItems)
                    RemoveViewModel(item);

            UpdateEmptyState();
        });
    }

    private void OnQueueChanged()
    {
        _dq.TryEnqueue(UpdateEmptyState);
    }

    private void AddViewModel(DownloadItem item)
    {
        var vm = new DownloadItemViewModel(item);
        _vmMap[item.Id] = vm;
        Items.Insert(0, vm);
    }

    private void RemoveViewModel(DownloadItem item)
    {
        if (_vmMap.Remove(item.Id, out var vm))
            Items.Remove(vm);
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueScrollViewer.Visibility = Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ClearAllButton.Visibility = Items.Any(i => i.State is DownloadItemState.Completed
            or DownloadItemState.Failed or DownloadItemState.Cancelled)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
            DownloadQueueService.Pause(id);
    }

    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            DownloadQueueService.Resume(id);
            if (_vmMap.TryGetValue(id, out var vm))
                vm.Rebind();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
            DownloadQueueService.Cancel(id);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            DownloadQueueService.Retry(id);
            if (_vmMap.TryGetValue(id, out var vm))
                vm.Rebind();
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
            DownloadQueueService.Remove(id);
    }

    private void DeleteFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
            DownloadQueueService.DeleteFile(id);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var item = DownloadQueueService.Queue.FirstOrDefault(i => i.Id == id);
            if (item is null) return;
            var path = item.DestinationPath;
            if (Directory.Exists(path))
            {
                var psi = new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    Verb = "open"
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadQueueService.ClearCompleted();
    }
}

public sealed class DownloadItemViewModel : INotifyPropertyChanged
{
    private readonly DownloadItem _item;

    public DownloadItemViewModel(DownloadItem item)
    {
        _item = item;
        _item.PropertyChanged += OnItemPropertyChanged;
    }

    public string Id => _item.Id;
    public string DisplayName => _item.DisplayName;
    public string? Description => _item.Description;
    public string? Glyph => !string.IsNullOrEmpty(_item.Glyph) ? _item.Glyph : GetStateGlyph();
    public DownloadItemState State => _item.State;

    public string StateText => State switch
    {
        DownloadItemState.Queued => MiscTexts.T("排队中"),
        DownloadItemState.Resolving => MiscTexts.T("解析中"),
        DownloadItemState.Downloading => MiscTexts.T("下载中"),
        DownloadItemState.Paused => MiscTexts.T("已暂停"),
        DownloadItemState.Processing => MiscTexts.T("处理中"),
        DownloadItemState.Completed => MiscTexts.T("已完成"),
        DownloadItemState.Failed => MiscTexts.T("失败"),
        DownloadItemState.Cancelled => MiscTexts.T("已取消"),
        _ => ""
    };

    public bool IsIndeterminate => State is DownloadItemState.Resolving or DownloadItemState.Processing or DownloadItemState.Queued;
    public double ProgressValue => _item.Progress?.Percentage ?? 0;
    public bool IsError => State is DownloadItemState.Failed;
    public bool IsPaused => State is DownloadItemState.Paused;

    public Visibility ProgressVisible => State is DownloadItemState.Queued
        or DownloadItemState.Resolving
        or DownloadItemState.Downloading
        or DownloadItemState.Paused
        or DownloadItemState.Processing
        ? Visibility.Visible : Visibility.Collapsed;

    public string StatusLine
    {
        get
        {
            var p = _item.Progress;
            return State switch
            {
                DownloadItemState.Queued => MiscTexts.T("等待开始..."),
                DownloadItemState.Resolving => MiscTexts.T("正在解析下载地址..."),
                DownloadItemState.Downloading when p is not null =>
                    $"{DownloadQueueService.FormatSpeed(p.SpeedMbps)} · {DownloadQueueService.FormatTime(p.EstimatedRemaining)} · {DownloadQueueService.FormatSize(p.BytesReceived)}/{DownloadQueueService.FormatSize(p.TotalBytes)}",
                DownloadItemState.Downloading => MiscTexts.T("连接中..."),
                DownloadItemState.Paused when p is not null =>
                    MiscTexts.TSub($"已暂停 · {DownloadQueueService.FormatSize(p.BytesReceived)}/{DownloadQueueService.FormatSize(p.TotalBytes)}"),
                DownloadItemState.Paused => MiscTexts.T("已暂停"),
                DownloadItemState.Processing => _item.ProcessingStatus ?? MiscTexts.T("处理中..."),
                DownloadItemState.Completed => _item.ProcessingStatus is not null
                    ? MiscTexts.TSub($"{_item.ProcessingStatus} · 已完成")
                    : MiscTexts.T("下载完成"),
                DownloadItemState.Failed => _item.ErrorMessage ?? MiscTexts.T("下载失败"),
                DownloadItemState.Cancelled => MiscTexts.T("已取消下载"),
                _ => ""
            };
        }
    }

    public Visibility PauseVisible => State is DownloadItemState.Downloading
        or DownloadItemState.Queued
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ResumeVisible => State is DownloadItemState.Paused
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CancelVisible => State is DownloadItemState.Paused
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RetryVisible => State is DownloadItemState.Failed or DownloadItemState.Cancelled
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OpenFolderVisible => State is DownloadItemState.Completed
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RemoveVisible => State is DownloadItemState.Completed
        or DownloadItemState.Failed
        or DownloadItemState.Cancelled
        or DownloadItemState.Paused
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeleteFileVisible => State is DownloadItemState.Completed
        ? Visibility.Visible : Visibility.Collapsed;

    public void Rebind()
    {
        OnPropertyChanged(string.Empty);
    }

    private string GetStateGlyph() => State switch
    {
        DownloadItemState.Queued => "\uE9F5",
        DownloadItemState.Resolving => "\uE895",
        DownloadItemState.Downloading => "\uE896",
        DownloadItemState.Paused => "\uE769",
        DownloadItemState.Processing => "\uE9F5",
        DownloadItemState.Completed => "\uE73E",
        DownloadItemState.Failed => "\uE783",
        DownloadItemState.Cancelled => "\uE733",
        _ => "\uE896"
    };

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
