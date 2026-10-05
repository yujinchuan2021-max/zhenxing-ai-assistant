using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TubaWinUi3.Controls;
using Windows.System;

namespace TubaWinUi3.Services;

public sealed class LatencyImageQueryTool : IBuiltinTool
{
	public string Id => "latency-image-query";
	public string Name => MiscTexts.T("核间延迟查询");
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

	public string Description => MiscTexts.T("查看社区上传的核间延迟热力图，对比不同 CPU 的核心间通信延迟。");
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

	public string Glyph => "\ue9d9";
	public string Category => "硬件工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

	public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

	public Task ExecuteAsync(BuiltinToolContext context)
	{
		App.MainWindow?.NavigateToToolPage(typeof(LatencyImageQueryPage));
		return Task.CompletedTask;
	}
}

public sealed partial class LatencyImageQueryPage : Page
{
	private const int BatchSize = 30;

	private GridView _gridView = null!;
	private TextBlock _statusText = null!;
	private TextBox _searchBox = null!;
	private StackPanel _loadingPanel = null!;
	private StackPanel _errorPanel = null!;
	private TextBlock _errorMessage = null!;
	private List<BenchmarkCloudService.LatencyImageInfo> _all = [];
	private string _query = "";
	private int _visibleCount;
	private ScrollViewer? _scrollViewer;
	private bool _scrollHooked;
	private bool _loadingMore;

	public LatencyImageQueryPage()
	{
		InitializeComponent();
		Content = BuildContent();
		Loaded += async (_, _) => await LoadImagesAsync();
	}

	private Grid BuildContent()
	{
		var header = new ToolPageHeader
		{
			Title = MiscTexts.T("核间延迟查询"),
			Subtitle = MiscTexts.T("查看枕星后台审核采纳的 CPU 核间延迟热力图"),
			Glyph = "\ue9d9"
		};

		var refreshBtn = new Button
		{
			Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "\uE72C", FontSize = 12 }, new TextBlock { Text = MiscTexts.T("刷新") } } }
		};
		refreshBtn.Click += async (_, _) => await LoadImagesAsync(refresh: true);
		header.Actions.Add(refreshBtn);

		_searchBox = new TextBox
		{
			PlaceholderText = MiscTexts.T("搜索 CPU / 用户名..."),
			Width = 240,
			VerticalAlignment = VerticalAlignment.Center
		};
		_searchBox.TextChanged += (_, _) =>
		{
			_query = _searchBox.Text?.Trim() ?? "";
			RenderGrid();
		};

		_statusText = new TextBlock { FontSize = 12, Opacity = 0.68, VerticalAlignment = VerticalAlignment.Center };



		var controlRow = new Grid { Padding = new Thickness(24, 0, 24, 8), ColumnSpacing = 12 };
		controlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		controlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		controlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		controlRow.Children.Add(_searchBox); Grid.SetColumn(_searchBox, 0);
		controlRow.Children.Add(_statusText); Grid.SetColumn(_statusText, 2);

		_gridView = new GridView
		{
			SelectionMode = ListViewSelectionMode.None,
			IsItemClickEnabled = true,
			Padding = new Thickness(24, 8, 24, 24)
		};
		_gridView.ItemClick += (_, e) =>
		{
			if (e.ClickedItem is Border border && border.Tag is BenchmarkCloudService.LatencyImageInfo info)
			{
				_ = ShowImageDialogAsync(info);
			}
		};

		var loadingRing = new ProgressRing { Width = 40, Height = 40, IsActive = true };
		var loadingText = new TextBlock { Text = MiscTexts.T("正在加载图片列表..."), FontSize = 13, Opacity = 0.68 };
		_loadingPanel = new StackPanel
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Spacing = 8,
			Visibility = Visibility.Collapsed,
			Children = { loadingRing, loadingText }
		};

		var errorTitle = new TextBlock { Text = MiscTexts.T("加载失败"), FontSize = 16, FontWeight = FontWeights.Bold };
		_errorMessage = new TextBlock { FontSize = 13, Opacity = 0.78, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 };
		var retryBtn = new Button { Content = MiscTexts.T("重试"), Style = App.Current.Resources["AccentButtonStyle"] as Style };
		retryBtn.Click += async (_, _) => await LoadImagesAsync();
		_errorPanel = new StackPanel
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Spacing = 12,
			Visibility = Visibility.Collapsed,
			Children = { new FontIcon { Glyph = "\uE783", FontSize = 48, Foreground = new SolidColorBrush(ThemeColors.AccentRed) }, errorTitle, _errorMessage, retryBtn }
		};

		var root = new Grid();
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		root.Children.Add(header); Grid.SetRow(header, 0);
		root.Children.Add(controlRow); Grid.SetRow(controlRow, 1);
		root.Children.Add(_gridView); Grid.SetRow(_gridView, 2);
		root.Children.Add(_loadingPanel); Grid.SetRow(_loadingPanel, 2);
		root.Children.Add(_errorPanel); Grid.SetRow(_errorPanel, 2);
		return root;
	}

	private async Task LoadImagesAsync(bool refresh = false)
	{
		_loadingPanel.Visibility = Visibility.Visible;
		_errorPanel.Visibility = Visibility.Collapsed;
		_gridView.Visibility = Visibility.Collapsed;
		_gridView.Items.Clear();
		try
		{
			_all = await Task.Run(() => BenchmarkCloudService.GetLatencyImagesAsync(CancellationToken.None, refresh));
			RenderGrid();
			HookScrollViewer();
			_loadingPanel.Visibility = Visibility.Collapsed;
			_gridView.Visibility = Visibility.Visible;
		}
		catch (Exception ex)
		{
			_errorMessage.Text = ex.Message;
			_loadingPanel.Visibility = Visibility.Collapsed;
			_errorPanel.Visibility = Visibility.Visible;
		}
	}

	private List<BenchmarkCloudService.LatencyImageInfo> GetFilteredItems()
	{
		return _all
			.Where(i => string.IsNullOrEmpty(_query) || i.Name.Contains(_query, StringComparison.OrdinalIgnoreCase))
			.ToList();
	}

	private void RenderGrid()
	{
		_gridView.Items.Clear();
		_visibleCount = BatchSize;
		AppendVisibleItems();
	}

	private void AppendVisibleItems()
	{
		var items = GetFilteredItems();
		int end = Math.Min(_visibleCount, items.Count);
		for (int i = _gridView.Items.Count; i < end; i++)
		{
			_gridView.Items.Add(BuildCpuCard(items[i]));
		}
		if (end < items.Count)
		{
			_statusText.Text = string.IsNullOrEmpty(_query)
				? MiscTexts.TSub($"共 {_all.Count} 张图片（已加载 {end}/{items.Count}，滚动加载更多）")
				: MiscTexts.TSub($"筛选出 {items.Count}/{_all.Count} 张（已加载 {end}，滚动加载更多）");
		}
		else
		{
			_statusText.Text = string.IsNullOrEmpty(_query)
				? MiscTexts.TSub($"共 {_all.Count} 张图片")
				: MiscTexts.TSub($"筛选出 {items.Count}/{_all.Count} 张");
		}
	}

	private void HookScrollViewer()
	{
		if (_scrollHooked) return;
		_scrollHooked = true;
		_scrollViewer = FindScrollViewer(_gridView);
		if (_scrollViewer != null)
		{
			_scrollViewer.ViewChanged += OnGridScrolled;
		}
	}

	private static ScrollViewer? FindScrollViewer(DependencyObject root)
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is ScrollViewer sv) return sv;
			var found = FindScrollViewer(child);
			if (found != null) return found;
		}
		return null;
	}

	private void OnGridScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
	{
		if (e.IsIntermediate || _loadingMore) return;
		var sv = _scrollViewer;
		if (sv == null || sv.ScrollableHeight <= 0) return;
		if (sv.VerticalOffset < sv.ScrollableHeight - 600) return;
		_loadingMore = true;
		try
		{
			_visibleCount += BatchSize;
			AppendVisibleItems();
		}
		finally
		{
			_loadingMore = false;
		}
	}



	private static Border BuildCpuCard(BenchmarkCloudService.LatencyImageInfo info)
	{
		var (cpu, author) = (info.CpuName, info.Author);
		var cpuText = new TextBlock
		{
			Text = string.IsNullOrEmpty(cpu) ? info.Name : cpu,
			FontSize = 14.0,
			FontWeight = FontWeights.Bold,
			MaxWidth = 190,
			TextWrapping = TextWrapping.Wrap,
			MaxLines = 2,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		var authorText = new TextBlock
		{
			Text = author,
			FontSize = 12.0,
			Opacity = 0.68,
			MaxWidth = 190,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		var iconRow = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6.0,
			Children = { new FontIcon { Glyph = "\ue91b", FontSize = 12, Opacity = 0.6 }, new TextBlock { Text = MiscTexts.T("点击查看热力图"), FontSize = 11, Opacity = 0.6 } }
		};
		var border = new Border
		{
			Width = 220,
			MinHeight = 96,
			Margin = new Thickness(4),
			Padding = new Thickness(14),
			CornerRadius = new CornerRadius(8),
			Background = new SolidColorBrush(ThemeColors.CardBg),
			BorderBrush = new SolidColorBrush(ThemeColors.BorderColor),
			BorderThickness = new Thickness(1),
			Child = new StackPanel { Spacing = 6, Children = { cpuText, authorText, iconRow } },
			Tag = info
		};
		return border;
	}

	private async Task ShowImageDialogAsync(BenchmarkCloudService.LatencyImageInfo info)
	{
		var (cpu, author) = (info.CpuName, info.Author);
		var loadingRing = new ProgressRing { Width = 32, Height = 32, IsActive = true };
		var loadingText = new TextBlock { Text = MiscTexts.T("正在加载图片..."), FontSize = 13, Opacity = 0.68 };
		var loadingPanel = new StackPanel
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Spacing = 8,
			Children = { loadingRing, loadingText }
		};
		var image = new Image
		{
			Stretch = Stretch.Uniform,
			MaxWidth = 520,
			MaxHeight = 400,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		var imageScroll = new ScrollView
		{
			Width = 520,
			Height = 400,
			ContentOrientation = ScrollingContentOrientation.None,
			ZoomMode = ScrollingZoomMode.Enabled,
			IsTabStop = true,
			HorizontalScrollMode = ScrollingScrollMode.Auto,
			HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Auto,
			VerticalScrollMode = ScrollingScrollMode.Auto,
			VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto,
			Content = image,
			Visibility = Visibility.Collapsed
		};
		var detailPanel = new StackPanel
		{
			Spacing = 8,
			Visibility = Visibility.Collapsed,
			Children =
			{
				new TextBlock { Text = "CPU: " + (string.IsNullOrEmpty(cpu) ? MiscTexts.T("未知") : cpu), FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap },
				new TextBlock { Text = MiscTexts.T("发布者: @") + author, FontSize = 12, Opacity = 0.78 },
				new TextBlock { Text = MiscTexts.T("报告: ") + info.Name, FontSize = 12, Opacity = 0.68, TextWrapping = TextWrapping.Wrap },
				imageScroll
			}
		};
		var content = new Grid { Children = { detailPanel, loadingPanel } };
		var dialog = new ContentDialog
		{
			Title = MiscTexts.T("核间延迟热力图"),
			Content = content,
			CloseButtonText = MiscTexts.T("关闭"),
			XamlRoot = XamlRoot,
			RequestedTheme = ThemeService.CurrentElementTheme
		};
		var showTask = dialog.ShowAsync().AsTask();
		_ = LoadImageIntoDialogAsync(image, imageScroll, loadingPanel, detailPanel, loadingRing, info);
		await showTask;
	}

	private async Task LoadImageIntoDialogAsync(Image image, ScrollView imageScroll, StackPanel loadingPanel, StackPanel detailPanel, ProgressRing loadingRing, BenchmarkCloudService.LatencyImageInfo info)
	{
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
			byte[] bytes = await Task.Run(() => BenchmarkCloudService.GetLatencyImageBytesAsync(info, cts.Token));
			var bmp = new BitmapImage { DecodePixelWidth = 1400 };
			using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
			{
				var writer = new Windows.Storage.Streams.DataWriter(stream);
				writer.WriteBytes(bytes);
				await writer.StoreAsync();
				await writer.FlushAsync();
				writer.DetachStream();
				stream.Seek(0);
				await bmp.SetSourceAsync(stream);
			}
			image.Source = bmp;
			imageScroll.Visibility = Visibility.Visible;
			loadingPanel.Visibility = Visibility.Collapsed;
			detailPanel.Visibility = Visibility.Visible;
		}
		catch (OperationCanceledException)
		{
			loadingRing?.IsActive = false;
			loadingPanel.Children.Add(new TextBlock
			{
				Text = MiscTexts.T("图片加载超时，请检查网络后重试"),
				FontSize = 12,
				Opacity = 0.68,
				TextWrapping = TextWrapping.Wrap
			});
		}
		catch (Exception ex)
		{
			loadingRing?.IsActive = false;
			loadingPanel.Children.Add(new TextBlock
			{
				Text = MiscTexts.T("图片加载失败：") + ex.Message,
				FontSize = 12,
				Opacity = 0.68,
				TextWrapping = TextWrapping.Wrap
			});
		}
	}
}
