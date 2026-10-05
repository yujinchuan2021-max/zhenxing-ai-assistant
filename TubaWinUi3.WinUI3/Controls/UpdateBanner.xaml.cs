using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Controls;

public sealed partial class UpdateBanner : UserControl
{
    private enum BannerState { Hidden, Available, ManualDownload, Downloading, Ready, Failed }

    private UpdateInfo? _updateInfo;
    private bool _isDownloaded;
    private bool _isDownloading;
    private BannerState _state = BannerState.Hidden;
    private string? _failureDetail;
    private bool _validationFailed;

    /// <summary>仅手动下载（平台/形态不匹配）：动作按钮去官网，不进入下载队列。</summary>
    private bool _manualOnly;

    public UpdateBanner()
    {
        InitializeComponent();
        Loaded += (_, _) => LocalizationService.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;
        ApplyLocalization();
    }

    private void OnLanguageChanged()
    {
        if (DispatcherQueue.HasThreadAccess) ApplyLocalization();
        else DispatcherQueue.TryEnqueue(ApplyLocalization);
    }

    /// <summary>Refresh code-behind text when the user changes language in the title bar.</summary>
    private void ApplyLocalization()
    {
        ChangelogButton.Content = LocalizationService.L("UpdateBanner_ChangelogButton.Content", "查看更新日志");

        switch (_state)
        {
            case BannerState.Available when _updateInfo is not null:
                BannerText.Text = string.Format(
                    LocalizationService.L("UpdateBanner_NewPortableVersion", "发现新版本 V{0}（便携版更新包）"),
                    _updateInfo.Version);
                ActionButton.Content = LocalizationService.L("UpdateBanner_DownloadPackageButton", "下载更新包");
                break;
            case BannerState.ManualDownload when _updateInfo is not null:
                BannerText.Text = string.Format(
                    LocalizationService.L("UpdateBanner_ManualVersion", "发现新版本 V{0}：当前平台请在官网手动下载"),
                    _updateInfo.Version);
                ActionButton.Content = LocalizationService.L("UpdateBanner_OfficialDownloadButton", "前往官网下载");
                break;
            case BannerState.Downloading:
                BannerText.Text = LocalizationService.L("UpdateBanner_Downloading", "正在下载更新...");
                ActionButton.Content = LocalizationService.L("UpdateBanner_DownloadingButton", "下载中");
                break;
            case BannerState.Ready:
                BannerText.Text = LocalizationService.L("UpdateBanner_PortableReady", "更新包已下载并通过校验，解压后即可使用");
                ActionButton.Content = LocalizationService.L("UpdateBanner_OpenFolderButton", "打开下载文件夹");
                break;
            case BannerState.Failed:
                string detail = _validationFailed
                    ? LocalizationService.L("UpdateBanner_InvalidPortable", "更新包未通过校验或已被清理，请重新下载")
                    : _failureDetail ?? LocalizationService.L("Common_UnknownError", "未知错误");
                BannerText.Text = string.Format(
                    LocalizationService.L("UpdateBanner_DownloadFailed", "更新下载失败：{0}"), detail);
                ActionButton.Content = LocalizationService.L("UpdateBanner_RetryButton", "重试");
                break;
        }
    }

    public void ShowUpdateAvailable(UpdateInfo update)
    {
        _updateInfo = update;
        _state = BannerState.Available;
        _manualOnly = false;
        _isDownloaded = false;
        _isDownloading = false;

        ApplyLocalization();
        BannerText.Visibility = Visibility.Visible;
        ActionButton.IsEnabled = true;
        ActionButton.Visibility = Visibility.Visible;
        DownloadProgressBar.Visibility = Visibility.Collapsed;
        ChangelogButton.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 发现新版但当前平台/安装形态不适用自动下载（如非 x64 便携包）：只显示去官网手动下载，
    /// 不给出无法工作的“下载”按钮。
    /// </summary>
    public void ShowManualDownload(UpdateInfo update)
    {
        _updateInfo = update;
        _state = BannerState.ManualDownload;
        _manualOnly = true;
        _isDownloaded = false;
        _isDownloading = false;

        ApplyLocalization();
        BannerText.Visibility = Visibility.Visible;
        ActionButton.IsEnabled = true;
        ActionButton.Visibility = Visibility.Visible;
        DownloadProgressBar.Visibility = Visibility.Collapsed;
        ChangelogButton.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
    }

    public void ShowDownloading()
    {
        _state = BannerState.Downloading;
        _isDownloading = true;
        _isDownloaded = false;

        ApplyLocalization();
        BannerText.Visibility = Visibility.Collapsed;
        DownloadProgressBar.Visibility = Visibility.Visible;
        ActionButton.IsEnabled = false;
        ChangelogButton.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
    }

    public void ShowDownloadProgress(double percentage)
    {
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = percentage;
    }

    /// <summary>
    /// 下载完成。提示“已通过校验”前，把当前文件与当前清单（文件名/大小/SHA-256）重新核对并重算哈希；
    /// 只有便携 ZIP 且完全相符才进入“打开下载文件夹”就绪态（同大小换包会被重算哈希识破）。
    /// </summary>
    public async void ShowDownloadComplete()
    {
        _isDownloaded = false;
        _isDownloading = false;
        _manualOnly = false;

        var update = _updateInfo;

        // 下载完成的文件可能已被安全软件/清理工具移除或替换；未通过绑定校验一律按失败呈现
        if (update is null || !await UpdateService.IsUpdateReadyAsync(update))
        {
            ShowDownloadFailed("更新包未通过校验或已被清理，请重新下载");
            _validationFailed = true;
            ApplyLocalization();
            return;
        }

        _isDownloaded = true;
        _state = BannerState.Ready;

        // 便携版：不自动覆盖/解压运行目录，只引导用户打开下载文件夹自行解压运行
        ApplyLocalization();
        BannerText.Visibility = Visibility.Visible;
        DownloadProgressBar.Visibility = Visibility.Collapsed;
        ActionButton.IsEnabled = true;
        ChangelogButton.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
    }

    public void ShowDownloadFailed(string error)
    {
        _state = BannerState.Failed;
        _failureDetail = error;
        _validationFailed = false;
        _isDownloading = false;
        _isDownloaded = false; // 失败后"重试"应重新下载而不是再次启动失效文件

        ApplyLocalization();
        BannerText.Visibility = Visibility.Visible;
        DownloadProgressBar.Visibility = Visibility.Collapsed;
        ActionButton.IsEnabled = true;
        ChangelogButton.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_manualOnly)
        {
            var url = _updateInfo?.HtmlUrl;
            if (!string.IsNullOrEmpty(url))
            {
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri(url)); }
                catch { }
            }
            return;
        }

        if (_isDownloaded)
        {
            // 便携版：不自动安装/覆盖运行目录，只打开下载文件夹让用户解压后自行运行
            UpdateService.OpenUpdateFolder();
            return;
        }

        if (_isDownloading) return;

        if (_updateInfo is not null)
        {
            var item = UpdateService.AutoDownloadUpdate(_updateInfo);
            if (item is not null)
            {
                ShowDownloading();
                item.PropertyChanged += OnDownloadItemPropertyChanged;
            }
        }
    }

    private void OnDownloadItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not DownloadItem item) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(DownloadItem.State):
                    switch (item.State)
                    {
                        case DownloadItemState.Completed:
                            item.PropertyChanged -= OnDownloadItemPropertyChanged;
                            ShowDownloadComplete();
                            break;
                        case DownloadItemState.Failed:
                            item.PropertyChanged -= OnDownloadItemPropertyChanged;
                            ShowDownloadFailed(item.ErrorMessage ?? LocalizationService.L("Common_UnknownError", "未知错误"));
                            break;
                    }
                    break;
                case nameof(DownloadItem.Progress):
                    if (item.Progress is not null && item.Progress.TotalBytes > 0)
                    {
                        ShowDownloadProgress(item.Progress.Percentage);
                    }
                    break;
            }
        });
    }

    private void ChangelogButton_Click(object sender, RoutedEventArgs e)
    {
        WhatsNewWindow.Show();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Visibility = Visibility.Collapsed;
        UpdateService.ClearPendingUpdate();
    }
}
