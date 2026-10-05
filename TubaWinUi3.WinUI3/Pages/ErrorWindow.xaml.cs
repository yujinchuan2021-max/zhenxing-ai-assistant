using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using TubaWinUi3.Services;
using Windows.Graphics;

namespace TubaWinUi3.Pages;

public sealed partial class ErrorWindow : Window
{
    private string _errorDetail = "";
    private string _systemInfo = "";
    private static string? _cachedSystemInfo;
    private bool _sysInfoExpanded;

    public ErrorWindow()
    {
        InitializeComponent();

        AppWindow.Title = MiscTexts.T("枕星图吧AI助手 - 错误报告");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var screenArea = displayArea.WorkArea;
        var width = (int)(screenArea.Width * 0.55);
        var height = (int)(screenArea.Height * 0.7);
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            (screenArea.Width - width) / 2,
            (screenArea.Height - height) / 2));

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }

        if (Content is FrameworkElement root)
            root.RequestedTheme = ThemeService.CurrentElementTheme;

        var ex = App.ConsumePendingException();
        if (ex is not null)
            SetError(ex);

        LoadSystemInfo();
    }

    private void SetError(Exception ex)
    {
        _errorDetail = MiscTexts.TSub($"异常类型：{ex.GetType().FullName}\n") +
                       MiscTexts.TSub($"消息：{ex.Message}\n") +
                       MiscTexts.TSub($"堆栈：\n{ex.StackTrace}");

        if (ex.InnerException is not null)
        {
            _errorDetail += MiscTexts.TSub($"\n\n内部异常：{ex.InnerException.GetType().FullName}\n") +
                            MiscTexts.TSub($"消息：{ex.InnerException.Message}\n") +
                            MiscTexts.TSub($"堆栈：\n{ex.InnerException.StackTrace}");
        }

        ErrorText.Text = _errorDetail;
    }

    private void LoadSystemInfo()
    {
        try
        {
            var info = _cachedSystemInfo ??= ErrorReportService.CollectSystemInfo();
            _systemInfo = info;
            SysInfoText.Text = info;

            var firstLine = info.Split('\n')[0];
            SysInfoSummary.Text = firstLine;
        }
        catch
        {
            _systemInfo = MiscTexts.T("无法收集系统信息");
            SysInfoText.Text = _systemInfo;
            SysInfoSummary.Text = _systemInfo;
        }
    }

    private void SysInfoHeader_Click(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _sysInfoExpanded = !_sysInfoExpanded;
        SysInfoContent.Visibility = _sysInfoExpanded ? Visibility.Visible : Visibility.Collapsed;
        SysInfoChevron.Glyph = _sysInfoExpanded ? "\uE70E" : "\uE70D";
        SysInfoSummary.Visibility = _sysInfoExpanded ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(_errorDetail);
        Clipboard.SetContent(package);
        CopyButtonText.Text = MiscTexts.T("已复制");
    }

    private void CopySysInfoButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(_systemInfo);
        Clipboard.SetContent(package);
        CopySysInfoButtonText.Text = MiscTexts.T("已复制");
    }

    private async void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        ReproStepsBox.Header = null;
        var opened = false;
        try
        {
            opened = await Launcher.LaunchUriAsync(
                ErrorReportService.CreateFeedbackDraft(_errorDetail, ReproStepsBox.Text));
        }
        catch { }

        if (!opened)
        {
            await new ContentDialog
            {
                Title = MiscTexts.T("无法打开邮件应用"),
                Content = MiscTexts.TSub($"请手动发送邮件至 {FeedbackContact.Email}。"),
                CloseButtonText = MiscTexts.T("关闭"),
                XamlRoot = Content.XamlRoot,
                RequestedTheme = ThemeService.CurrentElementTheme,
            }.ShowAsync();
        }
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(Environment.ProcessPath!);
        Close();
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
