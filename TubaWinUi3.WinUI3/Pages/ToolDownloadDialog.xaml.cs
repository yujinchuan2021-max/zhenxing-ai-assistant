using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Pages;

public sealed partial class ToolDownloadDialog : ContentDialog
{
    private readonly string _toolName;
    private readonly string _downloadUrl;
    private readonly string? _filter;
    private readonly string _destinationDir;
    private CancellationTokenSource? _cts;
    private bool _isDownloading;
    private bool _destinationUnresolved;

    public bool DownloadSucceeded { get; private set; }
    public string? DownloadedFilePath { get; private set; }

    public ToolDownloadDialog(string toolName, string toolDesc, string downloadUrl, string? filter, string destinationDir)
    {
        InitializeComponent();
        XamlRoot = App.MainWindow?.Content?.XamlRoot;

        _toolName = toolName;
        _downloadUrl = downloadUrl;
        _filter = filter;
        // 【GUI 隔离】下载/解压是写操作：目标解析 fail-closed——隔离态把 Tools 树内的目标映射到
        // 可写根（ZXAI_DATA_ROOT\Tools）；树外路径无法映射 → 标记拒绝（绝不回落原目录，避免把
        // 写操作引向只读的随包 Tools）。生产态恒等返回（语义不变）。
        if (ToolCatalog.TryResolveDownloadTarget(destinationDir, out var writableDest))
        {
            _destinationDir = writableDest;
        }
        else
        {
            _destinationDir = destinationDir;
            _destinationUnresolved = true;
        }

        Title = MiscTexts.TSub($"下载 {toolName}");
        ToolNameText.Text = toolName;
        ToolDescText.Text = toolDesc;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_destinationUnresolved)
        {
            args.Cancel = true;
            ErrorBar.Message = MiscTexts.T("隔离模式下无法解析可写下载目标，已拒绝本次下载（不会写入只读的随包 Tools）。");
            ErrorBar.IsOpen = true;
            return;
        }

        if (_isDownloading)
        {
            args.Cancel = true;
            return;
        }

        // 【结构化统计】下载按钮点击（与真实下载请求分开计数）；统计失败不影响下载。
        DownloadMetricsStore.Default.ReportButtonClicked(_toolName);

        var deferral = args.GetDeferral();
        args.Cancel = true;

        try
        {
            await StartDownloadAsync();
        }
        finally
        {
            try { deferral.Complete(); } catch { }
        }
    }

    private async Task StartDownloadAsync()
    {
        _cts = new CancellationTokenSource();
        _isDownloading = true;
        IsPrimaryButtonEnabled = false;

        try
        {
            ResolvingSection.Visibility = Visibility.Visible;
            ProgressSection.Visibility = Visibility.Collapsed;

            var info = await ToolDownloaderService.ResolveDownloadUrlAsync(
                _downloadUrl, _filter, _cts.Token);

            ResolvingSection.Visibility = Visibility.Collapsed;

            if (info is null)
            {
                ErrorBar.Message = MiscTexts.T("无法获取下载地址，请检查网络连接。");
                ErrorBar.IsOpen = true;
                IsPrimaryButtonEnabled = true;
                _isDownloading = false;
                return;
            }

            ProgressSection.Visibility = Visibility.Visible;
            PrimaryButtonText = MiscTexts.T("下载中...");

            var progress = new Progress<ToolDownloadProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() => UpdateProgress(p));
            });

            var filePath = await ToolDownloaderService.DownloadToFileAsync(
                info.DownloadUrl, _destinationDir, info.FileName, progress, _cts.Token);

            DownloadedFilePath = filePath;

            if (info.IsArchive)
            {
                PercentText.Text = MiscTexts.T("解压中...");
                DownloadProgressBar.IsIndeterminate = true;
                await ToolDownloaderService.ExtractArchiveAsync(filePath, _destinationDir, _cts.Token);
            }

            DownloadSucceeded = true;
            Hide();

            if (info.IsInstaller)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    });
                }
                catch { }
            }

            await ShowSuccessDialog(info);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            IsPrimaryButtonEnabled = true;
            PrimaryButtonText = MiscTexts.T("重试");
        }
        finally
        {
            _isDownloading = false;
        }
    }

    private void UpdateProgress(ToolDownloadProgress p)
    {
        DownloadProgressBar.Value = p.Percentage;
        PercentText.Text = $"{p.Percentage:F1}%";
        SpeedText.Text = ToolDownloaderService.FormatSpeed(p.SpeedMbps);
        SizeText.Text = $"{ToolDownloaderService.FormatSize(p.BytesReceived)} / {ToolDownloaderService.FormatSize(p.TotalBytes)}";
        TimeText.Text = ToolDownloaderService.FormatTime(p.EstimatedRemaining);
    }

    private async Task ShowSuccessDialog(ToolDownloadInfo info)
    {
        var dialog = new ContentDialog
        {
            Title = MiscTexts.T("下载完成"),
            XamlRoot = XamlRoot,
            PrimaryButtonText = info.IsInstaller ? MiscTexts.T("已启动安装") : MiscTexts.T("完成"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ThemeService.CurrentElementTheme
        };

        var stack = new StackPanel { Spacing = 12 };

        var border = new Border
        {
            Padding = new Thickness(20, 16, 20, 16),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBorder = new Border
        {
            Width = 48,
            Height = 48,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green),
            CornerRadius = new CornerRadius(12)
        };
        iconBorder.Child = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 24,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
        };
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        infoStack.Children.Add(new TextBlock
        {
            Text = MiscTexts.TSub($"{_toolName} 下载完成！"),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        });
        infoStack.Children.Add(new TextBlock
        {
            Text = info.IsInstaller ? MiscTexts.T("安装程序已启动，请按提示完成安装。") :
                   info.IsArchive ? MiscTexts.T("已解压到工具目录，刷新后可直接打开。") :
                   MiscTexts.T("文件已保存到工具目录。"),
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        infoStack.Children.Add(new TextBlock
        {
            Text = MiscTexts.TSub($"文件：{info.FileName}"),
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        border.Child = grid;
        stack.Children.Add(border);
        dialog.Content = stack;

        await dialog.ShowAsync();
    }
}
