using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TubaWinUi3.Services;

namespace TubaWinUi3.Pages;

public sealed partial class ErrorPage : Page
{
    private string _errorDetail = "";

    public ErrorPage()
    {
        InitializeComponent();

        LoadErrorGif();

        var ex = App.ConsumePendingException();
        if (ex is not null)
            SetError(ex);
    }

    private void LoadErrorGif()
    {
        try
        {
            var gifPath = Path.Combine(AppContext.BaseDirectory, "Assets", "error.gif");
            if (File.Exists(gifPath))
            {
                var bitmap = new BitmapImage(new Uri(gifPath)) { AutoPlay = true };
                ErrorGifImage.Source = bitmap;
            }
        }
        catch
        {
        }
    }

    public void SetError(Exception ex)
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

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(_errorDetail);
        Clipboard.SetContent(package);
        CopyButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (CopyButton.Content is StackPanel sp)
        {
            sp.Children.Add(new FontIcon { FontSize = 12, Glyph = "\uE73E" });
            sp.Children.Add(new TextBlock { FontSize = 12, Text = MiscTexts.T("已复制") });
        }
    }

    private async void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        var reproSteps = ReproStepsBox.Text.Trim();

        if (string.IsNullOrEmpty(reproSteps))
        {
            ReproStepsBox.Header = MiscTexts.T("⚠️ 复现步骤为必填项");
            var dialog = new ContentDialog
            {
                Title = MiscTexts.T("请填写复现步骤"),
                Content = MiscTexts.T("发送反馈前请描述遇到此错误时的操作步骤，这能帮助我们定位问题。"),
                CloseButtonText = MiscTexts.T("知道了"),
                XamlRoot = XamlRoot,
                RequestedTheme = ThemeService.CurrentElementTheme,
            };
            await dialog.ShowAsync();
            ReproStepsBox.Focus(FocusState.Programmatic);
            return;
        }

        ReproStepsBox.Header = null;

        if (!await Launcher.LaunchUriAsync(ErrorReportService.CreateFeedbackDraft(_errorDetail, reproSteps)))
        {
            await new ContentDialog
            {
                Title = MiscTexts.T("无法打开邮件应用"),
                Content = MiscTexts.TSub($"请手动发送邮件至 {FeedbackContact.Email}。"),
                CloseButtonText = MiscTexts.T("关闭"),
                XamlRoot = XamlRoot,
            }.ShowAsync();
        }
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(Environment.ProcessPath!);
        App.MainWindow?.Close();
    }

}
