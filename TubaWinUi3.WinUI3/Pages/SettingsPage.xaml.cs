using System.Diagnostics;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Shapes = Microsoft.UI.Xaml.Shapes;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ActiveIntercept;
using TubaWinUi3.Services.Ai;
using TubaWinUi3.Services.ToolFlows;
using TubaWinUi3.Models;
using Windows.UI;
using static TubaWinUi3.Services.ConfigManager;

namespace TubaWinUi3.Pages;

public sealed partial class SettingsPage : Page, ILocalizablePage
{
    private bool _isCheckingUpdate;
    private bool _isCheckingToolsBundle;
    private bool _fastModeInitializing;
    private bool _navLayoutInitializing;
    private bool _rememberWindowInitializing;
    private bool _builtinToolOpenModeInitializing;
    private bool _backdropInitializing;
    private bool _opacityChanging;
    private bool _uiFontInitializing;
    private Border[] _backdropOptions = [];
    private Border[] _tintSwatches = [];
    private Color _currentTintColor = BackdropSettings.DefaultTintColor;
    private bool _hardwareFitScreenInitializing;
    private bool _hardwareMultiDeviceNewLineInitializing;
    private bool _cpuzBusy;
    private bool _aiSettingsInitializing;
    private bool _toolflowUploadInitializing = true;
    private bool _aiTesting;
    private bool _aiConfigurationOnly;

    private bool _storageUsageBusy;
    private bool _storeRatingBusy;
    private readonly List<Storyboard> _ratingThanksStoryboards = [];
    private DispatcherQueueTimer? _ratingThanksTimer;

    private const string StorePackageName = "DA3D64F4.winui3";
    private const string StoreProductId = "9P15095X7MGB";

    private FrameworkElement? _generalExpanderContent;
    private FrameworkElement? _appearanceExpanderContent;
    private FrameworkElement? _hardwareAiExpanderContent;
    private FrameworkElement? _aiServiceExpanderContent;   // 【UI 改版】AI 服务拆分后独立缓存（导航定位用）
    private FrameworkElement? _toolsCommunityExpanderContent;
    private FrameworkElement? _creditsExpanderContent;

    private string? _pendingHighlightKey;
    private CancellationTokenSource? _highlightCts;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public string lpTemplateName;
        public nint pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);


    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_NOCHANGEDIR = 0x00000008;

    private static readonly Dictionary<string, string> SettingKeyToExpander = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NavLayoutMode"] = "GeneralExpander",
        ["DefaultPage"] = "GeneralExpander",
        ["FastMode"] = "GeneralExpander",
        ["RememberWindow"] = "GeneralExpander",
        ["Update"] = "GeneralExpander",
        ["ToolsBundle"] = "GeneralExpander",
        ["Background"] = "AppearanceExpander",
        ["Backdrop"] = "AppearanceExpander",
        ["InterfaceFont"] = "AppearanceExpander",
        ["HardwareFitScreen"] = "HardwareAiExpander",
        ["HardwareMultiDeviceNewLine"] = "HardwareAiExpander",
        ["AiApiEndpoint"] = "AiServiceExpander",
        ["AiModelName"] = "AiServiceExpander",
        ["AiApiKey"] = "AiServiceExpander",
        ["AiAgentEngine"] = "AiServiceExpander",
        ["AiAgentPersona"] = "AiServiceExpander",
        ["AiConfiguration"] = "AiServiceExpander",
        [AppSettings.ToolflowUploadEnabledKey] = "AiServiceExpander",
        ["HttpDownload"] = "ToolsCommunityExpander",
        ["HttpDownloadPath"] = "ToolsCommunityExpander",
        ["HttpDownloadAction"] = "ToolsCommunityExpander",
        ["ConfigManager"] = "ToolsCommunityExpander",
        ["StorageUsage"] = "ToolsCommunityExpander",
        ["CustomToolManager"] = "ToolsCommunityExpander",
        ["CommunityTool"] = "ToolsCommunityExpander",
        ["ActiveInterceptEnabled"] = "ToolsCommunityExpander",
        ["ActiveInterceptNotifyMode"] = "ToolsCommunityExpander",
        ["WindowsSearchIndex"] = "ToolsCommunityExpander",
    };

    private static readonly Dictionary<string, string> SettingKeyToCardName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NavLayoutMode"] = "SettingsNavLayoutCard",
        ["DefaultPage"] = "SettingsDefaultPageCard",
        ["FastMode"] = "SettingsFastModeCard",
        ["RememberWindow"] = "SettingsRememberWindowCard",
        ["Update"] = "SettingsUpdateCard",
        ["ToolsBundle"] = "SettingsToolsBundleCard",
        ["Background"] = "SettingsBackgroundCard",
        ["Backdrop"] = "SettingsBackdropCard",
        ["InterfaceFont"] = "SettingsUiFontCard",
        ["HardwareFitScreen"] = "SettingsHardwareFitScreenCard",
        ["HardwareMultiDeviceNewLine"] = "SettingsHardwareMultiDeviceNewLineCard",
        ["AiApiEndpoint"] = "SettingsAiEndpointCard",
        ["AiModelName"] = "SettingsAiEndpointCard",
        ["AiApiKey"] = "SettingsAiEndpointCard",
        ["AiAgentEngine"] = "SettingsAgentCard",
        ["AiAgentPersona"] = "SettingsAgentCard",
        ["AiConfiguration"] = "SettingsAiEndpointCard",
        [AppSettings.ToolflowUploadEnabledKey] = "SettingsToolflowUploadCard",
        ["HttpDownload"] = "SettingsHttpDownloadCard",
        ["HttpDownloadPath"] = "SettingsHttpDownloadCard",
        ["HttpDownloadAction"] = "SettingsHttpDownloadCard",
        ["ConfigManager"] = "SettingsConfigManagerCard",
        ["StorageUsage"] = "SettingsStorageUsageCard",
        ["CustomToolManager"] = "SettingsCustomToolCard",
        ["CommunityTool"] = "SettingsCommunityCard",
        ["ActiveInterceptEnabled"] = "SettingsActiveInterceptCard",
        ["ActiveInterceptNotifyMode"] = "SettingsActiveInterceptNotifyCard",
        ["WindowsSearchIndex"] = "SettingsSearchIndexCard",
    };

    public SettingsPage() : this(initializeSettings: true)
    {
    }

    // The isolated native UI host renders the real XAML without loading settings,
    // starting probes, or allowing initialization callbacks to write configuration.
    internal SettingsPage(bool initializeSettings)
    {
        if (!initializeSettings)
        {
            _fastModeInitializing = _navLayoutInitializing = _rememberWindowInitializing = true;
            _builtinToolOpenModeInitializing = _backdropInitializing = _opacityChanging = true;
            _uiFontInitializing = _hardwareFitScreenInitializing = _hardwareMultiDeviceNewLineInitializing = true;
            _aiSettingsInitializing = _activeInterceptInitializing = _searchIndexInitializing = true;
            _activeInterceptNotifyModeInitializing = true;
        }
        InitializeComponent();
        if (!initializeSettings) return;

        _generalExpanderContent = GeneralExpander.Content as FrameworkElement;
        _appearanceExpanderContent = AppearanceExpander.Content as FrameworkElement;
        _hardwareAiExpanderContent = HardwareAiExpander.Content as FrameworkElement;
        _aiServiceExpanderContent = AiServiceExpander.Content as FrameworkElement;
        _toolsCommunityExpanderContent = ToolsCommunityExpander.Content as FrameworkElement;
        _creditsExpanderContent = CreditsExpander.Content as FrameworkElement;

        InitNavLayoutComboBox();
        InitFastModeToggle();
        InitRememberWindowToggle();
        InitUpdateSection();
        InitThemeSection();
        InitBackdropSettings();
        LoadBackgroundSettings();
        InitUiFontSettings();
        InitHardwareFitScreenToggle();
        InitHardwareMultiDeviceNewLineToggle();
        InitCpuzDataSourceStatus();
        InitAiSettings();
        InitToolflowUploadToggle();
        InitGitHubLoginStatus();
        LoadCreditsAvatar();
        InitBuiltinToolOpenModeComboBox();
        InitHttpDownloadSettings();
        InitActiveInterceptToggle();
        InitActiveInterceptNotifyModeComboBox();
        InitSearchIndexToggle();

        if (RuntimeHelper.IsMsixPackaged)
        {
            SettingsCommunityCard.Visibility = Visibility.Collapsed;
            SettingsStoreRatingCard.Visibility = Visibility.Visible;
            ToolsCommunityTitleText.Text = LocalizationService.L("Settings_ToolsCommunity_TitleMsix", "工具");
            ToolsCommunityDescText.Text = LocalizationService.L("Settings_ToolsCommunity_DescMsix", "配置管理、自定义工具、导出");
        }
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DownloadQueueService.QueueChanged += UpdateDownloadQueueStatus;

        RestoreExpanderContent(GeneralExpander, _generalExpanderContent);
        RestoreExpanderContent(AppearanceExpander, _appearanceExpanderContent);
        RestoreExpanderContent(HardwareAiExpander, _hardwareAiExpanderContent);
        RestoreExpanderContent(AiServiceExpander, _aiServiceExpanderContent);
        RestoreExpanderContent(ToolsCommunityExpander, _toolsCommunityExpanderContent);
        RestoreExpanderContent(CreditsExpander, _creditsExpanderContent);

        // ZXAI：每次进入设置页重读 AI 配置——页面实例被帧复用，不重读的话"从主页引导
        // 填过 Key"之后设置页会一直显示旧的空 Key（反向同理：这里填好回主页自动解锁）。
        InitAiSettings();
        InitToolflowUploadToggle();
        ApplyFontCredit();
        PopulateAdditionalThirdPartyCredits();

        if (GeneralExpander is not null)
            GeneralExpander.IsExpanded = true;

        if (e.Parameter is SearchNavigationTarget target && target.HighlightSettingKey is not null)
        {
            _pendingHighlightKey = target.HighlightSettingKey;
        }
        _aiConfigurationOnly = IsAiConfigurationKey((e.Parameter as SearchNavigationTarget)?.HighlightSettingKey);
        ApplyAiConfigurationView();

        if (_pendingHighlightKey is not null)
        {
            StartHighlight(_pendingHighlightKey);
            _pendingHighlightKey = null;
        }
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _highlightCts?.Cancel();
        DownloadQueueService.QueueChanged -= UpdateDownloadQueueStatus;
    }

    internal static bool IsAiConfigurationKey(string? key) => key is "AiConfiguration" or "AiApiEndpoint" or "AiApiKey" or "AiModelName" or "AiAgentEngine" or "AiAgentPersona";

    private void ApplyAiConfigurationView()
    {
        foreach (var section in new[] { GeneralExpander, AppearanceExpander, HardwareAiExpander, ToolsCommunityExpander, CreditsExpander })
            section.Visibility = _aiConfigurationOnly ? Visibility.Collapsed : Visibility.Visible;
        SettingsToolflowUploadCard.Visibility = _aiConfigurationOnly ? Visibility.Collapsed : Visibility.Visible;
        AllSettingsButton.Visibility = _aiConfigurationOnly ? Visibility.Visible : Visibility.Collapsed;
        AllSettingsButton.Content = LocalizationService.L("Settings_AllSettings", "全部设置");
        SettingsTitleText.Text = _aiConfigurationOnly ? LocalizationService.L("Settings_Add078.Text", "AI 与 Agent")
            : LocalizationService.L("Settings_PageTitle.Text", "设置");
        SettingsSubtitleText.Text = _aiConfigurationOnly ? LocalizationService.L("Settings_AiGlobalDescription", "修改后自动保存，聊天、工具流和资讯使用同一份配置。")
            : LocalizationService.L("Settings_PageSubtitle.Text", "管理应用外观、更新及查看关于信息。");
        if (_aiConfigurationOnly) AiServiceExpander.IsExpanded = true;
    }

    private void AllSettingsButton_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateToSettings();

    private static void RestoreExpanderContent(Expander? expander, FrameworkElement? savedContent)
    {
        if (expander is null || savedContent is null) return;
        if (expander.Content is null || IsExpanderContentEmpty(expander))
            expander.Content = savedContent;
    }

    private static bool IsExpanderContentEmpty(Expander expander)
    {
        if (expander.Content is not FrameworkElement content) return true;
        if (content is ScrollViewer sv && sv.Content is StackPanel sp)
            return sp.Children.Count == 0;
        if (content is StackPanel sp2)
            return sp2.Children.Count == 0;
        return false;
    }

    private void StartHighlight(string settingKey)
    {
        _highlightCts?.Cancel();
        _highlightCts = new CancellationTokenSource();
        _ = HighlightSettingAsync(settingKey, _highlightCts.Token);
    }

    private async Task HighlightSettingAsync(string settingKey, CancellationToken ct)
    {
        if (SettingKeyToExpander.TryGetValue(settingKey, out var expanderName) &&
            SettingKeyToCardName.TryGetValue(settingKey, out var cardName))
        {
            if (FindName(expanderName) is Expander expander)
            {
                expander.IsExpanded = true;
            }

            try { await Task.Delay(300, ct); } catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;

            if (FindName(cardName) is Border border)
            {
                border.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = 0.5
                });

                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }

                if (ct.IsCancellationRequested) return;
                SearchHighlightService.HighlightBorder(border);
            }
        }
    }

    public static string? ResolveExpanderName(string settingKey)
    {
        if (!SettingKeyToExpander.TryGetValue(settingKey, out var value))
            return null;
        return value;
    }

    private void NavWhatsNew_Tapped(object sender, TappedRoutedEventArgs e)
    {
        WhatsNewWindow.Show();
    }


    private void InitNavLayoutComboBox()
    {
        _navLayoutInitializing = true;
        try
        {
            NavLayoutComboBox.Items.Clear();
            NavLayoutComboBox.Items.Add(LocalizationService.L("Settings_NavLayoutOptionSidebar", "侧边栏"));
            NavLayoutComboBox.Items.Add(LocalizationService.L("Settings_NavLayoutOptionTabs", "顶部标签页"));
            NavLayoutComboBox.SelectedIndex = NavLayoutModeService.IsTabMode() ? 1 : 0;
        }
        finally
        {
            _navLayoutInitializing = false;
        }
    }

    private void NavLayoutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navLayoutInitializing) return;
        var mode = NavLayoutComboBox.SelectedIndex == 1 ? "tabs" : "sidebar";
        NavLayoutModeService.SetNavLayoutMode(mode);
    }



    private void InitBuiltinToolOpenModeComboBox()
    {
        _builtinToolOpenModeInitializing = true;
        try
        {
            BuiltinToolOpenModeComboBox.Items.Clear();
            BuiltinToolOpenModeComboBox.Items.Add(LocalizationService.L("Settings_BuiltinOpenModeOptionEmbedded", "嵌入页面"));
            BuiltinToolOpenModeComboBox.Items.Add(LocalizationService.L("Settings_BuiltinOpenModeOptionWindow", "独立窗口"));
            BuiltinToolOpenModeComboBox.SelectedIndex = AppSettings.GetBool("BuiltinToolsOpenInWindow", false) ? 1 : 0;
        }
        finally
        {
            _builtinToolOpenModeInitializing = false;
        }
    }

    private void BuiltinToolOpenModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_builtinToolOpenModeInitializing) return;
        AppSettings.Set("BuiltinToolsOpenInWindow", BuiltinToolOpenModeComboBox.SelectedIndex == 1);
    }

    private void InitFastModeToggle()
    {
        _fastModeInitializing = true;
        FastModeToggle.IsOn = FastModeService.IsFastModeEnabled();
        _fastModeInitializing = false;
    }

    private void FastModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_fastModeInitializing) return;
        FastModeService.SetFastModeEnabled(FastModeToggle.IsOn);
    }

    private void InitRememberWindowToggle()
    {
        _rememberWindowInitializing = true;
        RememberWindowToggle.IsOn = WindowSizeService.IsRememberEnabled();
        _rememberWindowInitializing = false;
    }

    private void RememberWindowToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_rememberWindowInitializing) return;
        WindowSizeService.SetRememberEnabled(RememberWindowToggle.IsOn);
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCheckingUpdate) return;
        _isCheckingUpdate = true;
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = LocalizationService.L("Settings_UpdateChecking", "正在检查更新...");

        try
        {
            var result = await UpdateService.CheckForUpdateResultAsync();

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.Update is not null:
                    UpdateStatusText.Text = string.Format(LocalizationService.L("Settings_UpdateFound", "发现 {0}，请查看顶部更新提示"), UpdateService.GetReleaseDisplayName(result.Update));
                    (App.MainWindow as MainWindow)?.ShowUpdateBanner(result.Update, false);
                    break;

                case UpdateCheckStatus.ManualDownload:
                    UpdateStatusText.Text = result.Detail ?? string.Format(LocalizationService.L("Settings_UpdateManual", "发现新版本，请前往官网手动下载：{0}"), UpdateService.OwnDownloadPageUrl);
                    if (result.Update is not null)
                        (App.MainWindow as MainWindow)?.ShowManualDownloadBanner(result.Update);
                    break;

                case UpdateCheckStatus.UpToDate:
                    UpdateStatusText.Text = string.Format(LocalizationService.L("Settings_UpdateUpToDate", "已是当前预览通道最新版：{0}（v{1}）"), UpdateService.CurrentReleaseLabel, UpdateService.CurrentVersion);
                    break;

                default:
                    // 清单无效/网络失败：如实显示错误原因，不再谎报“已是最新版本”
                    UpdateStatusText.Text = string.Format(LocalizationService.L("Settings_UpdateFailed", "检查更新失败：{0}"), result.Detail ?? LocalizationService.L("Common_UnknownError", "未知错误"));
                    break;
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = string.Format(LocalizationService.L("Settings_UpdateCheckFailed", "检查失败: {0}"), ex.Message);
        }
        finally
        {
            _isCheckingUpdate = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void InitUpdateSection()
    {
        if (RuntimeHelper.IsMsixPackaged || RuntimeHelper.IsLiteBuild)
        {
            SettingsUpdateCard.Visibility = Visibility.Collapsed;
            SettingsToolsBundleCard.Visibility = Visibility.Visible;
            _toolsBundleState = ("described", null);
            ToolsBundleStatusText.Text = DescribeToolsBundleStatus();
        }
        else
        {
            SettingsUpdateCard.Visibility = Visibility.Visible;
            SettingsToolsBundleCard.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>按内核变种/来源生成状态文案（MSIX 商店版与精简版便携共用）。</summary>
    private static string DescribeToolsBundleStatus()
    {
        var version = ToolsBundleService.GetCurrentVersion();
        if (version is not null)
        {
            return ToolsBundleService.GetInstalledKind() == ToolsBundleService.KindLite
                ? string.Format(LocalizationService.L("Settings_ToolsBundleLite", "当前精简版内核 v{0}，可升级完整版"), version)
                : string.Format(LocalizationService.L("Settings_ToolsBundleFull", "当前完整版内核 v{0}"), version);
        }

        // 精简版便携随包内置工具（未通过内核包安装过）
        if (RuntimeHelper.IsLiteBuild && Directory.Exists(
                Path.Combine(ToolCatalog.AppDirectory, "Tools")))
        {
            return LocalizationService.L("Settings_ToolsBundleLiteBuiltIn", "已内置精简工具集，可下载完整版内核");
        }

        if (!ToolsBundleService.IsToolsBundleReady())
        {
            return LocalizationService.L("Settings_ToolsBundleNotDownloaded", "内核未下载");
        }

        return LocalizationService.L("Settings_ToolsBundleReadyUnknown", "内核已就绪（版本未知）");
    }

    private async void CheckToolsBundleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCheckingToolsBundle) return;
        _isCheckingToolsBundle = true;
        CheckToolsBundleButton.IsEnabled = false;
        _toolsBundleState = ("checking", null);
        ToolsBundleStatusText.Text = LocalizationService.L("Settings_ToolsBundleChecking", "正在检查内核更新...");

        try
        {
            var info = await ToolsBundleService.CheckForToolsUpdateAsync();

            if (info is null)
            {
                _toolsBundleState = ("failedretry", null);
                ToolsBundleStatusText.Text = LocalizationService.L("Settings_ToolsBundleCheckFailedRetry", "检查失败，请稍后重试");
                return;
            }

            // 完整版已是最新：无事可做（不可降级精简版）；其余情况打开对话框
            // （有新版本 → 选版本下载；无新版本且非完整版 → 升级完整版）。
            if (!info.HasUpdate && ToolsBundleService.GetInstalledKind() == ToolsBundleService.KindFull)
            {
                _toolsBundleState = ("latest", info.Version);
                ToolsBundleStatusText.Text = string.Format(LocalizationService.L("Settings_ToolsBundleLatest", "当前内核已是最新版本 (v{0})"), info.Version);
                return;
            }

            if (info.HasUpdate)
            {
                _toolsBundleState = ("newversion", info.Version);
                ToolsBundleStatusText.Text = string.Format(LocalizationService.L("Settings_ToolsBundleNewVersion", "发现新版本 v{0}"), info.Version);
            }
            else
            {
                _toolsBundleState = ("described", null);
                ToolsBundleStatusText.Text = DescribeToolsBundleStatus();
            }

            var dialog = new ToolsBundleDownloadDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ThemeService.CurrentElementTheme
            };
            await dialog.ShowDownloadAsync(info);

            if (dialog.DownloadEnqueued)
            {
                _toolsBundleState = ("queued", null);
                ToolsBundleStatusText.Text = LocalizationService.L("Settings_ToolsBundleQueued", "已加入下载队列，可在标题栏下载按钮查看进度");
            }
            else if (info.HasUpdate)
            {
                _toolsBundleState = ("promptcheck", null);
                ToolsBundleStatusText.Text = LocalizationService.L("Settings_ToolsBundle_Status", "点击检查内核是否有新版本");
            }
            else
            {
                _toolsBundleState = ("described", null);
                ToolsBundleStatusText.Text = DescribeToolsBundleStatus();
            }
        }
        catch (Exception ex)
        {
            _toolsBundleState = ("error", ex.Message);
            ToolsBundleStatusText.Text = string.Format(LocalizationService.L("Settings_UpdateCheckFailed", "检查失败: {0}"), ex.Message);
        }
        finally
        {
            _isCheckingToolsBundle = false;
            CheckToolsBundleButton.IsEnabled = true;
        }
    }

    private void InitBackdropSettings()
    {
        _backdropInitializing = true;
        _backdropOptions = [BackdropMicaOption, BackdropMicaAltOption, BackdropAcrylicOption, BackdropAcrylicThinOption];
        _tintSwatches = TintSwatchPanel.Children.OfType<Border>().ToArray();

        var currentType = BackdropService.GetBackdropType();
        UpdateBackdropOptionSelection(currentType);

        var customization = BackdropService.GetCustomization();
        CustomTintToggle.IsOn = customization.UseCustomTint;
        TintOpacitySlider.Minimum = 0;
        TintOpacitySlider.Maximum = 100;
        TintOpacitySlider.StepFrequency = 5;
        TintOpacitySlider.Value = customization.TintOpacity * 100;
        TintLuminositySlider.Minimum = 0;
        TintLuminositySlider.Maximum = 100;
        TintLuminositySlider.StepFrequency = 5;
        TintLuminositySlider.Value = customization.LuminosityOpacity * 100;
        TintOpacityText.Text = $"{(int)(customization.TintOpacity * 100)}%";
        TintLuminosityText.Text = $"{(int)(customization.LuminosityOpacity * 100)}%";
        UpdateTintColorSelection(customization.TintColor);
        UpdateCustomTintPanelVisibility();
        _backdropInitializing = false;
    }

    private void UpdateBackdropOptionSelection(BackdropType selected)
    {
        foreach (var border in _backdropOptions)
        {
            if (border is null) continue;
            var tag = border.Tag?.ToString();
            var isSelected = tag == selected.ToString();
            border.BorderBrush = isSelected
                ? new SolidColorBrush(Color.FromArgb(255, 0, 120, 215))
                : (Brush)App.Current.Resources["SubtleFillColorSecondaryBrush"];
        }
    }

    private bool _themeUiReady;

    /// <summary>【UI 改版】主题区初始化：按当前保存值回显选中项（初始化期间不触发写回）。</summary>
    private void InitThemeSection()
    {
        ThemeRadio.SelectedIndex = ThemeService.CurrentTheme switch
        {
            TubaWinUi3.Services.AppTheme.Light => 1,
            TubaWinUi3.Services.AppTheme.Dark => 2,
            _ => 0,
        };
        _themeUiReady = true;
    }

    private void ThemeRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeUiReady) return;   // 初始化回显不算用户切换
        if (ThemeRadio.SelectedItem is RadioButton rb && rb.Tag is string tag)
            ThemeService.SetTheme(ThemeService.ParseTheme(tag));
    }

    private void BackdropOption_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_backdropInitializing) return;
        if (sender is not Border border) return;
        if (!Enum.TryParse<BackdropType>(border.Tag?.ToString(), out var type)) return;

        BackdropService.SetBackdropType(type);
        UpdateBackdropOptionSelection(type);
    }

    private void BackdropOption_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Opacity = 0.85;
        }
    }

    private void BackdropOption_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Opacity = 1.0;
        }
    }

    private void UpdateCustomTintPanelVisibility()
    {
        CustomTintPanel.Visibility = CustomTintToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CustomTintToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_backdropInitializing) return;
        UpdateCustomTintPanelVisibility();
        SaveCustomization();
    }

    private void TintSwatch_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_backdropInitializing) return;
        if (sender is not Border border) return;
        var color = BackdropSettings.ParseColor(border.Tag?.ToString(), BackdropSettings.DefaultTintColor);
        UpdateTintColorSelection(color);
        SaveCustomization();
    }

    private void TintColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_backdropInitializing) return;
        if (!CustomTintToggle.IsOn) return;
        UpdateTintColorSelection(args.NewColor);
        SaveCustomization();
    }

    private void TintOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_backdropInitializing) return;
        TintOpacityText.Text = $"{(int)e.NewValue}%";
        SaveCustomization();
    }

    private void TintLuminositySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_backdropInitializing) return;
        TintLuminosityText.Text = $"{(int)e.NewValue}%";
        SaveCustomization();
    }

    /// <summary>刷新色板选中环与"自定义颜色"色块,并同步 ColorPicker。</summary>
    private void UpdateTintColorSelection(Color color)
    {
        _currentTintColor = color;
        CustomTintColorChip.Background = new SolidColorBrush(color);
        if (TintColorPicker.Color != color)
            TintColorPicker.Color = color; // 赋值会触发 ColorChanged,值相同则跳过避免递归

        foreach (var swatch in _tintSwatches)
        {
            var isSelected = BackdropSettings.ParseColor(swatch.Tag?.ToString(), Color.FromArgb(0, 0, 0, 0)) == color;
            swatch.BorderBrush = isSelected
                ? new SolidColorBrush(Color.FromArgb(255, 0, 120, 215))
                : (Brush)App.Current.Resources["ControlStrokeColorDefaultBrush"];
        }
    }

    private void SaveCustomization()
    {
        var customization = new BackdropCustomization(
            CustomTintToggle.IsOn,
            _currentTintColor,
            TintOpacitySlider.Value / 100.0,
            TintLuminositySlider.Value / 100.0);
        BackdropService.SetCustomization(customization);
    }

    private void LoadBackgroundSettings()
    {
        _opacityChanging = true;
        BgOpacitySlider.Minimum = 5;
        BgOpacitySlider.Maximum = 80;
        BgOpacitySlider.StepFrequency = 5;

        var path = BackgroundService.GetBackgroundPath();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            ShowBgPreview(path);
        }

        var opacity = BackgroundService.GetBackgroundOpacity();
        BgOpacitySlider.Value = (int)(opacity * 100);
        _opacityChanging = false;
        BgOpacityText.Text = $"{(int)(opacity * 100)}%";

        PopulateBgList();
    }

    private void PopulateBgList()
    {
        var entries = BackgroundService.GetImportedBackgrounds();
        BgListPanel.Children.Clear();

        if (entries.Count == 0)
        {
            BgListEmptyText.Visibility = Visibility.Visible;
            BgListScrollViewer.Visibility = Visibility.Collapsed;
            BgHistoryCountText.Text = "";
            BgHistoryExpander.Visibility = Visibility.Collapsed;
            return;
        }

        BgListEmptyText.Visibility = Visibility.Collapsed;
        BgListScrollViewer.Visibility = Visibility.Visible;
        BgHistoryCountText.Text = $"({entries.Count})";
        BgHistoryExpander.Visibility = Visibility.Visible;

        foreach (var entry in entries)
        {
            var item = CreateBgListItem(entry);
            BgListPanel.Children.Add(item);
        }
    }

    private Border CreateBgListItem(BackgroundImageEntry entry)
    {
        var isSelected = entry.IsSelected;
        var accentBrush = (Brush)App.Current.Resources["AccentFillColorDefaultBrush"];

        var thumbnailBorder = new Border
        {
            Width = 140,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            BorderBrush = isSelected ? accentBrush : (Brush)App.Current.Resources["CardStrokeColorDefaultBrush"],
            Tag = entry.Path,
            Padding = new Thickness(0),
        };

        var grid = new Grid { RowSpacing = 0 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var image = new Image
        {
            Stretch = Stretch.UniformToFill,
            Source = new BitmapImage(new Uri(entry.Path)),
        };
        Grid.SetRow(image, 0);
        grid.Children.Add(image);

        var infoPanel = new Grid
        {
            Padding = new Thickness(6, 4, 6, 4),
            ColumnSpacing = 4,
        };
        Grid.SetRow(infoPanel, 1);
        infoPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        infoPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = entry.FileName,
            FontSize = 11,
            Opacity = 0.72,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(nameText, 0);
        infoPanel.Children.Add(nameText);

        var deleteButton = new Button
        {
            Padding = new Thickness(2),
            MinWidth = 0,
            MinHeight = 0,
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = entry.Path,
        };
        var deleteIcon = new FontIcon
        {
            Glyph = "\uE74D",
            FontSize = 10,
            Foreground = (Brush)App.Current.Resources["TextFillColorSecondaryBrush"],
        };
        deleteButton.Content = deleteIcon;
        deleteButton.Click += BgDeleteItem_Click;
        Grid.SetColumn(deleteButton, 1);
        infoPanel.Children.Add(deleteButton);

        grid.Children.Add(infoPanel);
        thumbnailBorder.Child = grid;

        if (isSelected)
        {
            var checkBadge = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = accentBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
            };
            var checkIcon = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 10,
                Foreground = (Brush)App.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            checkBadge.Child = checkIcon;
            grid.Children.Add(checkBadge);
        }

        thumbnailBorder.PointerPressed += (s, e) =>
        {
            BgListItem_Tapped(entry.Path);
        };

        return thumbnailBorder;
    }

    private void BgListItem_Tapped(string path)
    {
        if (!File.Exists(path)) return;

        BackgroundService.SelectBackground(path);
        ShowBgPreview(path);
        PopulateBgList();
    }

    private void BgDeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path) return;

        BackgroundService.DeleteBackground(path);

        var currentPath = BackgroundService.GetBackgroundPath();
        if (string.IsNullOrWhiteSpace(currentPath))
            HideBgPreview();
        else
            ShowBgPreview(currentPath);

        PopulateBgList();
    }

    private void ShowBgPreview(string path)
    {
        try
        {
            BgPreviewImage.Source = new BitmapImage(new Uri(path));
            BgFileNameText.Text = Path.GetFileName(path);
            BgPreviewPanel.Visibility = Visibility.Visible;
            BgPreviewBorder.Visibility = Visibility.Visible;
            ClearBgButton.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private void HideBgPreview()
    {
        BgPreviewImage.Source = null;
        BgFileNameText.Text = string.Empty;
        BgPreviewPanel.Visibility = Visibility.Collapsed;
        BgPreviewBorder.Visibility = Visibility.Collapsed;
        ClearBgButton.Visibility = Visibility.Collapsed;
    }

    private async void ImportBgButton_Click(object sender, RoutedEventArgs e)
    {
        var ofn = new OPENFILENAME();
        ofn.lStructSize = Marshal.SizeOf(ofn);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        ofn.hwndOwner = hwnd;
        ofn.lpstrFilter = LocalizationService.L("Settings_BgImageFilterImages", "图片文件") + "\0*.jpg;*.jpeg;*.png;*.bmp\0" + LocalizationService.L("Settings_BgImageFilterAll", "所有文件") + "\0*.*\0\0";
        ofn.lpstrFile = new string(new char[260]);
        ofn.nMaxFile = 260;
        ofn.lpstrTitle = LocalizationService.L("Settings_BgImageDialogTitle", "选择背景图片");
        ofn.Flags = OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR;
        ofn.nFilterIndex = 1;

        if (!GetOpenFileName(ref ofn))
            return;

        var sourcePath = ofn.lpstrFile.TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return;

        try
        {
            var bgDir = ConfigManager.GetBackgroundsDir();
            Directory.CreateDirectory(bgDir);

            var destName = $"bg_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{Path.GetExtension(sourcePath)}";
            var destPath = Path.Combine(bgDir, destName);
            File.Copy(sourcePath, destPath, true);

            BackgroundService.SetBackgroundPath(destPath);
            ShowBgPreview(destPath);
        }
        catch
        {
            BackgroundService.SetBackgroundPath(sourcePath);
            ShowBgPreview(sourcePath);
        }

        PopulateBgList();
    }

    private void ClearBgButton_Click(object sender, RoutedEventArgs e)
    {
        BackgroundService.SetBackgroundPath(null);
        HideBgPreview();
        PopulateBgList();
    }

    private void BgOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_opacityChanging) return;
        var percent = e.NewValue;
        BackgroundService.SetBackgroundOpacity(percent / 100.0);
        BgOpacityText.Text = $"{(int)percent}%";
    }

    /// <summary>「设置 > 外观 > 界面字体」：候选项与顺序来自 app-font.json choices[]（唯一权威），
    /// 保存到 settings.json 的 UiFontChoice；选择在下次启动应用时生效（App.xaml 资源字典根在解析期注入）。</summary>
    private void InitUiFontSettings()
    {
        _uiFontInitializing = true;
        UiFontComboBox.Items.Clear();
        foreach (var choice in AppFonts.Choices)
            UiFontComboBox.Items.Add(MiscTexts.T(choice.DisplayName));
        var index = AppFonts.IndexOfChoice(AppSettings.Get(AppFonts.UiFontChoiceKey));
        UiFontComboBox.SelectedIndex = index >= 0 ? index : 0;
        _uiFontInitializing = false;
    }

    private void UiFontComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiFontInitializing) return;
        var index = UiFontComboBox.SelectedIndex;
        if (index >= 0 && index < AppFonts.Choices.Count)
            AppSettings.Set(AppFonts.UiFontChoiceKey, AppFonts.Choices[index].Id);
    }

    private void InitHardwareFitScreenToggle()
    {
        _hardwareFitScreenInitializing = true;
        HardwareFitScreenToggle.IsOn = AppSettings.GetBool("HardwareFitScreen", true);
        _hardwareFitScreenInitializing = false;
    }

    private void HardwareFitScreenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_hardwareFitScreenInitializing) return;
        AppSettings.Set("HardwareFitScreen", HardwareFitScreenToggle.IsOn);
    }

    private void InitHardwareMultiDeviceNewLineToggle()
    {
        _hardwareMultiDeviceNewLineInitializing = true;
        HardwareMultiDeviceNewLineToggle.IsOn = AppSettings.GetBool("HardwareMultiDeviceNewLine", true);
        _hardwareMultiDeviceNewLineInitializing = false;
    }

    private void HardwareMultiDeviceNewLineToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_hardwareMultiDeviceNewLineInitializing) return;
        AppSettings.Set("HardwareMultiDeviceNewLine", HardwareMultiDeviceNewLineToggle.IsOn);
        HardwareInfoService.InvalidateCache();
    }

    private bool _activeInterceptInitializing;
    private bool _searchIndexInitializing;

    private void InitActiveInterceptToggle()
    {
        // MSIX 沙箱下不支持主动拦截后端，隐藏相关卡片
        if (RuntimeHelper.IsMsixPackaged)
        {
            if (SettingsActiveInterceptCard is not null)
                SettingsActiveInterceptCard.Visibility = Visibility.Collapsed;
            if (SettingsActiveInterceptNotifyCard is not null)
                SettingsActiveInterceptNotifyCard.Visibility = Visibility.Collapsed;
            return;
        }

        _activeInterceptInitializing = true;
        ActiveInterceptToggle.IsOn = AppSettings.GetBool("ActiveInterceptEnabled", false);
        _activeInterceptInitializing = false;
        UpdateActiveInterceptStatus();
    }

    private void ActiveInterceptToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_activeInterceptInitializing) return;
        var enabled = ActiveInterceptToggle.IsOn;
        AppSettings.Set("ActiveInterceptEnabled", enabled);

        if (enabled)
        {
            ActiveInterceptService.SyncBackend();
        }
        else
        {
            // 同步而非裸停止：游戏后台监控仍开着时后端必须继续常驻
            ActiveInterceptService.SyncBackend();
        }
        UpdateActiveInterceptStatus();
    }

    private void UpdateActiveInterceptStatus()
    {
        var enabled = AppSettings.GetBool("ActiveInterceptEnabled", false);
        if (ActiveInterceptStatusText is null) return;

        if (!enabled)
        {
            ActiveInterceptStatusText.Text = LocalizationService.L("Settings_InterceptStatusOff", "已关闭");
            ActiveInterceptStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }
        else if (ActiveInterceptService.IsRunning)
        {
            ActiveInterceptStatusText.Text = LocalizationService.L("Settings_InterceptStatusRunning", "运行中");
            ActiveInterceptStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
        }
        else
        {
            ActiveInterceptStatusText.Text = LocalizationService.L("Settings_InterceptStatusBackendMissing", "未运行（后端缺失）");
            ActiveInterceptStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        }
    }

    private void InitSearchIndexToggle()
    {
        _searchIndexInitializing = true;
        SearchIndexToggle.IsOn = AppSettings.GetBool("WindowsSearchIndex", false);
        _searchIndexInitializing = false;
    }

    private void SearchIndexToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_searchIndexInitializing) return;
        var enabled = SearchIndexToggle.IsOn;
        AppSettings.Set("WindowsSearchIndex", enabled);

        if (enabled)
        {
            _ = WindowsSearchIndexService.RegisterAllToolsAsync();
        }
        else
        {
            WindowsSearchIndexService.RemoveAll();
        }
    }

    private bool _activeInterceptNotifyModeInitializing;

    private void InitActiveInterceptNotifyModeComboBox()
    {
        _activeInterceptNotifyModeInitializing = true;
        try
        {
            ActiveInterceptNotifyModeComboBox.Items.Clear();
            ActiveInterceptNotifyModeComboBox.Items.Add(LocalizationService.L("Settings_InterceptNotifyAlways", "每次拦截都通知"));
            ActiveInterceptNotifyModeComboBox.Items.Add(LocalizationService.L("Settings_InterceptNotifyBatch", "仅批量时通知"));
            ActiveInterceptNotifyModeComboBox.Items.Add(LocalizationService.L("Settings_InterceptNotifyNever", "从不通知"));

            var mode = AppSettings.Get("ActiveInterceptNotifyMode") ?? "always";
            ActiveInterceptNotifyModeComboBox.SelectedIndex = mode switch
            {
                "batch_only" => 1,
                "never" => 2,
                _ => 0,
            };
        }
        finally
        {
            _activeInterceptNotifyModeInitializing = false;
        }
    }

    private void ActiveInterceptNotifyModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_activeInterceptNotifyModeInitializing) return;
        var mode = ActiveInterceptNotifyModeComboBox.SelectedIndex switch
        {
            1 => "batch_only",
            2 => "never",
            _ => "always",
        };
        AppSettings.Set("ActiveInterceptNotifyMode", mode);
        // 重启后端使新配置生效
        ActiveInterceptService.RestartBackend();
    }

    private void InitCpuzDataSourceStatus()
    {
        UpdateCpuzDataSourceUI();
    }

    private void UpdateCpuzDataSourceUI()
    {
        var useCpuz = AppSettings.GetBool("UseCpuzDataSource", false);
        var cpuzAvailable = CpuzInfoService.FindCpuzExe() != null;

        if (useCpuz && CpuzInfoService.CachedInfo != null)
        {
            CpuzDataSourceStatusText.Text = LocalizationService.L("Settings_CpuzStatusInUse", "当前使用 CPU-Z 数据源（真实硬件读取）");
            CpuzDataSourceButtonText.Text = LocalizationService.L("Settings_CpuzButtonRevert", "切回默认");
            CpuzDataSourceIcon.Glyph = "\uE73E";
        }
        else if (useCpuz)
        {
            CpuzDataSourceStatusText.Text = cpuzAvailable
                ? LocalizationService.L("Settings_CpuzStatusWaiting", "CPU-Z 数据源已启用，等待获取数据...")
                : LocalizationService.L("Settings_CpuzStatusNotFound", "CPU-Z 数据源已启用，但未找到 CPU-Z");
            CpuzDataSourceButtonText.Text = LocalizationService.L("Settings_CpuzButtonRevert", "切回默认");
            CpuzDataSourceIcon.Glyph = "\uE950;";
        }
        else
        {
            CpuzDataSourceStatusText.Text = cpuzAvailable
                ? LocalizationService.L("Settings_CpuzStatusWmiCanSwitch", "当前使用 WMI 数据源，可切换为 CPU-Z 获取真实信息")
                : LocalizationService.L("Settings_CpuzStatusWmiNoTool", "当前使用 WMI 数据源（未找到 CPU-Z 工具）");
            CpuzDataSourceButtonText.Text = LocalizationService.L("Settings_CpuzSource_Button", "切换");
            CpuzDataSourceIcon.Glyph = "\uE950";
        }
    }

    private async void CpuzDataSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cpuzBusy) return;

        var useCpuz = AppSettings.GetBool("UseCpuzDataSource", false);

        if (useCpuz)
        {
            AppSettings.Set("UseCpuzDataSource", false);
            UpdateCpuzDataSourceUI();
            return;
        }

        var cpuzExe = CpuzInfoService.FindCpuzExe();
        if (cpuzExe == null)
        {
            await ShowMessageAsync(LocalizationService.L("Settings_CpuzNotFoundTitle", "未找到 CPU-Z"), LocalizationService.L("Settings_CpuzNotFoundMessage", "在工具目录中未找到 CPU-Z 可执行文件，无法使用此功能。\n\n请确保 Tools/处理器工具/CPUZ/ 目录下存在 cpuz_x64.exe。"));
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.L("Settings_CpuzDialogTitle", "切换硬件信息数据源"),
            PrimaryButtonText = LocalizationService.L("Settings_CpuzDialogPrimary", "确认切换"),
            CloseButtonText = LocalizationService.L("Common_Cancel", "取消"),
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ThemeService.CurrentElementTheme
        };

        var stack = new StackPanel { Spacing = 12 };

        stack.Children.Add(new TextBlock
        {
            Text = LocalizationService.L("Settings_CpuzIntro", "当前硬件信息通过 WMI（Windows 管理规范）获取，数据来源于厂商在 SMBIOS/DMI 中填写的内容。"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        });

        var problemBorder = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(
                ThemeService.CurrentTheme == AppTheme.Dark
                    ? Color.FromArgb(40, 255, 185, 0)
                    : Color.FromArgb(30, 200, 130, 0)),
            BorderBrush = new SolidColorBrush(
                ThemeService.CurrentTheme == AppTheme.Dark
                    ? Color.FromArgb(80, 255, 185, 0)
                    : Color.FromArgb(60, 200, 130, 0)),
            BorderThickness = new Thickness(1)
        };
        problemBorder.Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = LocalizationService.L("Settings_CpuzWmiWarningTitle", "⚠ WMI 数据可能被伪造"),
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    FontSize = 14
                },
                new TextBlock
                {
                    Text = LocalizationService.L("Settings_CpuzWmiWarningBody", "部分厂商或商家可能通过修改 BIOS/SMBIOS 信息来伪造 CPU 型号、内存品牌、主板型号等，导致 WMI 读取到的信息与实际硬件不符。"),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85,
                    FontSize = 13
                }
            }
        };
        stack.Children.Add(problemBorder);

        var solutionBorder = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(
                ThemeService.CurrentTheme == AppTheme.Dark
                    ? Color.FromArgb(40, 0, 200, 100)
                    : Color.FromArgb(25, 0, 160, 80)),
            BorderBrush = new SolidColorBrush(
                ThemeService.CurrentTheme == AppTheme.Dark
                    ? Color.FromArgb(80, 0, 200, 100)
                    : Color.FromArgb(60, 0, 160, 80)),
            BorderThickness = new Thickness(1)
        };
        solutionBorder.Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = LocalizationService.L("Settings_CpuzHowTitle", "✓ CPU-Z 读取原理"),
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    FontSize = 14
                },
                new TextBlock
                {
                    Text = LocalizationService.L("Settings_CpuzHowBody", "CPU-Z 通过 CPUID 指令直接读取 CPU 硬件寄存器，通过 PCI 枚举直接扫描硬件，通过 SPD 芯片直接读取内存条信息——这些是底层硬件级别的数据，厂商无法通过修改 SMBIOS 来伪造。"),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85,
                    FontSize = 13
                }
            }
        };
        stack.Children.Add(solutionBorder);

        var warnBorder = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(
                ThemeService.CurrentTheme == AppTheme.Dark
                    ? Color.FromArgb(40, 100, 150, 255)
                    : Color.FromArgb(25, 60, 120, 255)),
            BorderBrush = new SolidColorBrush(
                ThemeService.CurrentTheme == AppTheme.Dark
                    ? Color.FromArgb(80, 100, 150, 255)
                    : Color.FromArgb(60, 60, 120, 255)),
            BorderThickness = new Thickness(1)
        };
        warnBorder.Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = LocalizationService.L("Settings_CpuzNotesTitle", "⏱ 注意事项"),
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    FontSize = 14
                },
                new TextBlock
                {
                    Text = LocalizationService.L("Settings_CpuzNotesBody", "• 使用 CPU-Z 获取信息需要约 3~8 秒，期间会短暂启动 CPU-Z 进程\n• 获取完成后会自动关闭 CPU-Z 进程\n• 切换后可在设置中随时切回 WMI 数据源"),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85,
                    FontSize = 13
                }
            }
        };
        stack.Children.Add(warnBorder);

        dialog.Content = new ScrollViewer
        {
            MaxHeight = 400,
            Content = stack
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        _cpuzBusy = true;
        CpuzDataSourceButton.IsEnabled = false;
        CpuzDataSourceStatusText.Text = LocalizationService.L("Settings_CpuzFetching", "正在通过 CPU-Z 获取硬件信息，请稍候...");

        try
        {
            var cpuzInfo = await CpuzInfoService.FetchAsync(timeoutMs: 30000);

            if (cpuzInfo != null)
            {
                AppSettings.Set("UseCpuzDataSource", true);
                UpdateCpuzDataSourceUI();
            }
            else
            {
                CpuzInfoService.KillCpuzProcesses();
                await ShowMessageAsync(LocalizationService.L("Settings_CpuzFetchFailedTitle", "获取失败"), LocalizationService.L("Settings_CpuzFetchFailedMessage", "CPU-Z 未能成功获取硬件信息。\n\n可能原因：\n• CPU-Z 运行超时\n• CPU-Z 被安全软件拦截\n• 当前架构不支持此版本 CPU-Z"));
                UpdateCpuzDataSourceUI();
            }
        }
        catch (Exception ex)
        {
            CpuzInfoService.KillCpuzProcesses();
            await ShowMessageAsync(LocalizationService.L("Settings_CpuzFetchFailedTitle", "获取失败"), string.Format(LocalizationService.L("Settings_CpuzFetchError", "CPU-Z 获取过程中出现错误：\n{0}"), ex.Message));
            UpdateCpuzDataSourceUI();
        }
        finally
        {
            _cpuzBusy = false;
            CpuzDataSourceButton.IsEnabled = true;
        }
    }

    private void InitAiSettings()
    {
        _aiSettingsInitializing = true;
        try
        {
            RefreshAiProviderList();
            LoadAiProviderIntoUi(null);
            AiAgentEngineCombo.SelectedIndex = AgentEngine.Current == "builtin" ? 1 : 0;
            RefreshAiPersonaOptions();
        }
        finally
        {
            _aiSettingsInitializing = false;
        }
    }

    private void InitToolflowUploadToggle()
    {
        _toolflowUploadInitializing = true;
        try { ToolflowUploadToggle.IsOn = AppSettings.IsToolflowUploadEnabled; }
        finally { _toolflowUploadInitializing = false; }
    }

    private void RefreshAiPersonaOptions()
    {
        bool previous = _aiSettingsInitializing; _aiSettingsInitializing = true;
        try
        {
            var selected = TubaWinUi3.Services.Agent.AgentPersonaCatalog.Resolve(AppSettings.Get(TubaWinUi3.Services.Agent.AgentPersonaCatalog.SettingKey)).Id;
            AiAgentPersonaCombo.Items.Clear();
            foreach (var persona in TubaWinUi3.Services.Agent.AgentPersonaCatalog.All)
            {
                var option = new ComboBoxItem { Tag = persona.Id, Content = LocalizationService.L("AiAgent_Persona_" + persona.Id + "_Name", persona.Name) };
                AiAgentPersonaCombo.Items.Add(option);
                if (persona.Id == selected) AiAgentPersonaCombo.SelectedItem = option;
            }
        }
        finally { _aiSettingsInitializing = previous; }
    }

    private void AiAgentEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_aiSettingsInitializing || AiAgentEngineCombo.SelectedItem is not ComboBoxItem { Tag: string engine }) return;
        AgentEngine.SetCurrent(engine);
    }

    private void AiAgentPersonaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_aiSettingsInitializing || AiAgentPersonaCombo.SelectedItem is not ComboBoxItem { Tag: string persona }) return;
        AppSettings.Set(TubaWinUi3.Services.Agent.AgentPersonaCatalog.SettingKey, persona);
    }

    private async void ToolflowUploadToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toolflowUploadInitializing) return;
        if (ToolflowUploadToggle.IsOn)
        {
            // Reopening starts with new selections; revoked records must not be uploaded later.
            if (!ToolFlowUploadSwitch.TryVoidPendingUploads())
            {
                _toolflowUploadInitializing = true;
                try { ToolflowUploadToggle.IsOn = false; }
                finally { _toolflowUploadInitializing = false; }
                AppSettings.Set(AppSettings.ToolflowUploadEnabledKey, false);
                await ShowMessageAsync(LocalizationService.L("Settings_NoticeTitle", "提示"),
                    LocalizationService.L("Settings_ToolflowVoidFailed", "暂时无法开启上传，请稍后重试。"));
                return;
            }
            AppSettings.Set(AppSettings.ToolflowUploadEnabledKey, true);
            return;
        }
        AppSettings.Set(AppSettings.ToolflowUploadEnabledKey, false);
        ToolFlowUploadSwitch.TryVoidPendingUploads();
    }

    private AiProvider? CurrentAiProvider() => AiProviderCombo.SelectedItem as AiProvider;

    private void RefreshAiProviderList()
    {
        var providers = AiProviderStore.GetProviders();
        var selectedId = AiProviderStore.SelectedProviderId;
        // 必须传副本：传活列表实例时，列表被原地修改后 ItemsSourceView 快照不刷新，
        // 同步设置 SelectedItem 会抛 E_INVALIDARG（Value does not fall within the expected range）
        AiProviderCombo.ItemsSource = providers.ToList();
        AiProviderCombo.SelectedItem = providers.FirstOrDefault(p => p.Id == selectedId) ?? providers.FirstOrDefault();
    }

    /// <summary>把指定提供商（null = 当前选中）加载到编辑器控件。</summary>
    private void LoadAiProviderIntoUi(string? providerId)
    {
        var provider = providerId is null
            ? CurrentAiProvider() ?? AiProviderStore.SelectedProvider
            : AiProviderStore.GetProvider(providerId) ?? AiProviderStore.SelectedProvider;

        // 回显与用户编辑分别处理；不能为防止程序回显误删而禁止用户主动清空 Key。
        var wasInitializing = _aiSettingsInitializing;
        _aiSettingsInitializing = true;
        try
        {
            AiEndpointTextBox.Text = provider.BaseUrl ?? "";
            AiEndpointTextBox.IsEnabled = !provider.EndpointLocked;
            AiApiKeyBox.Password = provider.ApiKey ?? "";

            // ItemsSource 用同一份列表实例，保证 SelectedItem 引用一致
            var models = provider.Models.ToList();
            AiModelsList.ItemsSource = models;
            AiDefaultModelCombo.ItemsSource = models;
            AiDefaultModelCombo.SelectedItem = models
                .FirstOrDefault(m => m.Id.Equals(AiProviderStore.SelectedModelId, StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault();

            AiKeyLinkButton.Visibility = string.IsNullOrWhiteSpace(provider.KeyHintUrl) ? Visibility.Collapsed : Visibility.Visible;
            AiApiKeyHintText.Text = AiProviderStore.NeedsKeyReentry(provider.Id)
                ? LocalizationService.L("Settings_AiKeyReentryHint", "原 API Key 无法在当前 Windows 账户解密，请重新填写；旧密文会保留到替换成功。")
                : LocalizationService.L("Settings_AiKeyProtectionHint", "API 密钥由当前 Windows 账户保护；换电脑或账户后需重新填写。");
        }
        finally
        {
            _aiSettingsInitializing = wasInitializing;
        }

        UpdateAiConfigStatus();
    }



    /// <summary>【R2】语言切换后重算动态文本（MainWindow 对当前页面调用 ILocalizablePage）。</summary>
    public void ApplyLocalization()
    {
        ApplyAiConfigurationView();
        RefreshAiPersonaOptions();
        try { UpdateAiConfigStatus(); } catch { }

        if (RuntimeHelper.IsMsixPackaged)
        {
            ToolsCommunityTitleText.Text = LocalizationService.L("Settings_ToolsCommunity_TitleMsix", "工具");
            ToolsCommunityDescText.Text = LocalizationService.L("Settings_ToolsCommunity_DescMsix", "配置管理、自定义工具、导出");
        }

        try { InitNavLayoutComboBox(); } catch { }
        try { InitBuiltinToolOpenModeComboBox(); } catch { }
        try { InitActiveInterceptNotifyModeComboBox(); } catch { }
        try { UpdateActiveInterceptStatus(); } catch { }
        try { UpdateCpuzDataSourceUI(); } catch { }
        try { UpdateDownloadQueueStatus(); } catch { }
        try { RefreshHttpDownloadActionLabels(); } catch { }
        try { PopulateAdditionalThirdPartyCredits(); } catch { }
        try { ApplyFontCredit(); } catch { }

        // GitHub 登录状态：不发网络，仅按会话状态与已缓存用户名重刷
        if (GitHubAuthService.IsLoggedIn)
        {
            if (_githubUserName is not null)
                GitHubLoginStatusText.Text = string.Format(LocalizationService.L("Settings_GitHubLoggedIn", "已登录：{0}"), _githubUserName);
        }
        else
        {
            GitHubLoginStatusText.Text = LocalizationService.L("Settings_GitHub_Status", "未登录");
        }

        RestoreToolsBundleStatusText();

    }

    /// <summary>语言切换后按真状态重格式化工具内核状态（不重新检查、不清掉结果）。</summary>
    private void RestoreToolsBundleStatusText()
    {
        try
        {
            ToolsBundleStatusText.Text = _toolsBundleState.kind switch
            {
                "checking" => LocalizationService.L("Settings_ToolsBundleChecking", "正在检查内核更新..."),
                "failedretry" => LocalizationService.L("Settings_ToolsBundleCheckFailedRetry", "检查失败，请稍后重试"),
                "latest" => string.Format(LocalizationService.L("Settings_ToolsBundleLatest", "当前内核已是最新版本 (v{0})"), _toolsBundleState.arg),
                "newversion" => string.Format(LocalizationService.L("Settings_ToolsBundleNewVersion", "发现新版本 v{0}"), _toolsBundleState.arg),
                "queued" => LocalizationService.L("Settings_ToolsBundleQueued", "已加入下载队列，可在标题栏下载按钮查看进度"),
                "promptcheck" => LocalizationService.L("Settings_ToolsBundle_Status", "点击检查内核是否有新版本"),
                "error" => string.Format(LocalizationService.L("Settings_UpdateCheckFailed", "检查失败: {0}"), _toolsBundleState.arg),
                _ => DescribeToolsBundleStatus(),
            };
        }
        catch { }
    }

    /// <summary>
    /// AI 服务状态（三态，2026-09-25 接入引导）：未配置 / 已保存未验证 / 已验证连接。
    /// 「已验证」以指纹（提供商+地址+模型+Key）与 AiVerifiedFingerprint 一致为准——
    /// 测试连接成功或真实发送成功都会写入该指纹（与 AI 页徽标共用同一份状态，不新建 Key 存储）。
    /// </summary>
    private void UpdateAiConfigStatus()
    {
        var selected = AiProviderStore.SelectedProvider;
        if (AiProviderStore.NeedsKeyReentry(selected.Id))
        {
            AiConfigStatusText.Text = LocalizationService.L("Settings_AiKeyReentryStatus", "原 API Key 无法在当前 Windows 账户解密，请重新填写后再使用 AI 服务。");
            AiConfigStatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            return;
        }
        // 【R3 2026-09-25】删除「空白自定义=内置默认服务」分支：空白自定义提供商按未配置处理，
        // 由下方 !IsProviderReady 分支给出缺失项提示（请求配置不再回退旧默认地址/内置 Key）。

        var provider = AiProviderStore.SelectedProvider;
        if (!AiProviderStore.IsProviderReady(provider))
        {
            // 【接入引导·返修】选中但配置不完整（地址/Key 缺一）：不得再显示绿色「已配置」。
            // R2：地址为空与 Key 为空分别提示——地址为空时多填出的 Key 不能发到默认地址（见 AiService.GetConfig）。
            AiConfigStatusText.Text = string.IsNullOrWhiteSpace(provider.BaseUrl)
                ? LocalizationService.L("Settings_AiStatusMissingEndpoint", "尚未配置服务地址：填写 Base URL 后点「测试连接」。")
                : LocalizationService.L("Settings_AiStatusMissingKey", "尚未配置 API Key：填写后点「测试连接」验证；本地/自定义服务若无鉴权可填写任意占位值。");
            AiConfigStatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            return;
        }

        if (AgentEngine.IsSelectedConfigVerified())
        {
            AiConfigStatusText.Text = string.Format(LocalizationService.L("Settings_AiStatusVerified", "已验证连接：{0} · {1}"), provider.Name, AiProviderStore.SelectedModelId);
            AiConfigStatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];   // 【UI 改版】语义色
        }
        else
        {
            AiConfigStatusText.Text = LocalizationService.L("Settings_AiStatusSavedUnverified", "已保存，尚未验证：点「测试连接」确认可用。");
            AiConfigStatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
    }

    private void AiProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_aiSettingsInitializing) return;
        if (CurrentAiProvider() is not { } provider) return;

        AiProviderStore.SetSelected(provider.Id);
        LoadAiProviderIntoUi(provider.Id);
    }

    private void AiAddProviderButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = AiProviderStore.AddCustomProvider();
        RefreshAiProviderList();
        LoadAiProviderIntoUi(provider.Id);
    }

    private void AiResetProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAiProvider() is not { } provider) return;
        AiProviderStore.ResetProviderDefaults(provider.Id);
        LoadAiProviderIntoUi(provider.Id);
    }

    private void AiEndpointTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_aiSettingsInitializing) return;
        if (CurrentAiProvider() is not { } provider) return;
        provider.BaseUrl = AiEndpointTextBox.Text.Trim();
        AiProviderStore.Save();
        UpdateAiConfigStatus();
    }

    private void AiApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_aiSettingsInitializing) return;
        if (CurrentAiProvider() is not { } provider) return;
        var value = AiApiKeyBox.Password.Trim();
        if (value == (provider.ApiKey ?? "")) return;
        AiProviderStore.SetApiKey(provider.Id, value);
        AppSettings.Remove("AiVerifiedFingerprint");
        AppSettings.Save();
        UpdateAiConfigStatus();
    }

    private void AiKeyLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAiProvider() is not { } provider) return;
        if (string.IsNullOrWhiteSpace(provider.KeyHintUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(provider.KeyHintUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private void AiModelDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAiProvider() is not { } provider) return;
        if ((sender as Button)?.Tag is not AiModelOption model) return;

        provider.Models.Remove(model);
        if (string.IsNullOrWhiteSpace(provider.DefaultModel) || provider.DefaultModel == model.Id)
            provider.DefaultModel = provider.Models.FirstOrDefault()?.Id ?? "";

        AiProviderStore.Save();
        LoadAiProviderIntoUi(provider.Id);
    }

    private void AiAddModelButton_Click(object sender, RoutedEventArgs e) => AddAiModel();

    private void AiNewModelBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            AddAiModel();
        }
    }

    private void AddAiModel()
    {
        if (CurrentAiProvider() is not { } provider) return;

        var id = AiNewModelBox.Text.Trim();
        if (id.Length == 0) return;

        provider.AddModel(id);
        if (string.IsNullOrWhiteSpace(provider.DefaultModel))
            provider.DefaultModel = id;

        AiProviderStore.Save();
        AiNewModelBox.Text = "";
        LoadAiProviderIntoUi(provider.Id);
    }

    private void AiDefaultModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_aiSettingsInitializing) return;
        if (CurrentAiProvider() is not { } provider) return;
        if (AiDefaultModelCombo.SelectedItem is not AiModelOption model) return;

        AiProviderStore.SetGlobalModel(provider.Id, model.Id);
        UpdateAiConfigStatus();
    }



    private async void AiTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_aiTesting) return;

        // 【R2】配置不完整（地址/Key 缺一）不发起任何请求：空地址绝不能把已填 Key 打到默认地址；
        // 也不把内置默认 Key 当作「无 Key 可用」的假凭据。只提示缺什么，引导补齐后再测。
        if (CurrentAiProvider() is { } guardProvider &&
            (string.IsNullOrWhiteSpace(guardProvider.BaseUrl) || string.IsNullOrWhiteSpace(guardProvider.ApiKey)))
        {
            AiConfigStatusText.Text = string.IsNullOrWhiteSpace(guardProvider.BaseUrl)
                ? LocalizationService.L("Settings_AiStatusMissingEndpoint", "尚未配置服务地址：填写 Base URL 后点「测试连接」。")
                : LocalizationService.L("Settings_AiStatusMissingKey", "尚未配置 API Key：填写后点「测试连接」验证；本地/自定义服务若无鉴权可填写任意占位值。");
            AiConfigStatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            ToolTipService.SetToolTip(AiConfigStatusText, null);
            return;
        }

        _aiTesting = true;
        AiTestButton.IsEnabled = false;
        AiTestButtonText.Text = LocalizationService.L("Settings_AiTesting", "测试中...");
        AiTestIcon.Glyph = "\uE950";

        try
        {
            // 请求参数和指纹来自同一次读取，测试实际所选模型，而不是提供商的默认模型。
            var testConfig = AgentEngine.CaptureConnectionTestConfig();
            var testFingerprint = testConfig.Fingerprint;
            var result = await AiService.TestConnectionAsync(
                endpoint: testConfig.Endpoint,
                model: testConfig.Model,
                apiKey: testConfig.Key);

            // 测试期间可能编辑或切换配置，旧结果不得覆盖新配置的显示状态。
            if (AgentEngine.CurrentLaunchFingerprint() != testFingerprint)
            {
                UpdateAiConfigStatus();
                return;
            }

            if (result.Success)
            {
                AiTestIcon.Glyph = "\uE73E";
                AiTestButtonText.Text = LocalizationService.L("Settings_AiTestSuccess", "连接成功");
                AiConfigStatusText.Text = LocalizationService.L("Settings_AiTestSuccessStatus", "AI 服务已配置，连接测试成功");
                // 【UI 改版】语义色（随主题），不再硬编码 Green
                AiConfigStatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                ToolTipService.SetToolTip(AiConfigStatusText, null);
                // 【接入引导 2026-09-25】测试成功 = 已验证：写入与 AI 页共用的「已验证指纹」
                // （仅指纹，不落任何 Key 内容），此后设置页状态显示「已验证连接」。
                // 【R2】结束指纹 == 开始指纹才写入；测试期间配置被改动则结果不适用，按当前配置重算状态。
                string? nowFingerprint = null;
                try { nowFingerprint = AgentEngine.CurrentLaunchFingerprint(); } catch { }
                if (testFingerprint.Length > 0 && nowFingerprint == testFingerprint)
                {
                    try
                    {
                        AppSettings.Set("AiVerifiedFingerprint", testFingerprint);
                        AppSettings.Save();
                    }
                    catch { }
                }
                else
                {
                    UpdateAiConfigStatus();
                }
            }
            else
            {
                AiTestIcon.Glyph = "\uE783";
                AiTestButtonText.Text = LocalizationService.L("Settings_AiTestFailed", "连接失败");
                // 【接入引导 2026-09-25】失败 = 给出用户能理解的下一步（错误详情仍在悬停提示里）。
                AiConfigStatusText.Text = string.Format(LocalizationService.L("Settings_AiTestFailedStatus", "连接失败：{0}"), result.Error)
                    + " " + LocalizationService.L("Settings_AiTestFailedNextStep", "请核对 Key 是否正确、网络是否可用后重试；本地/自定义服务请确认地址与服务已启动。");
                // 【UI 改版】语义色 + 就地错误详情（悬停可看全文），不再弹出重复模态框
                AiConfigStatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                ToolTipService.SetToolTip(AiConfigStatusText, result.Error ?? LocalizationService.L("Settings_UnknownError", "未知错误"));
                // 【R2】失败不得保留同配置的旧「已验证」假状态：既有指纹正是本次测试的配置时清除。
                try
                {
                    var stored = AppSettings.Get("AiVerifiedFingerprint");
                    if (testFingerprint.Length > 0 && !string.IsNullOrWhiteSpace(stored) && stored == testFingerprint)
                    {
                        AppSettings.Remove("AiVerifiedFingerprint");
                        AppSettings.Save();
                    }
                }
                catch { }
            }
        }
        finally
        {
            _aiTesting = false;
            AiTestButton.IsEnabled = true;

            await Task.Delay(2000);

            if (!_aiTesting)
            {
                AiTestIcon.Glyph = "\uE73E";
                AiTestButtonText.Text = LocalizationService.L("Settings_AiTest_Button", "测试连接");
            }
        }
    }

    private void ConfigManagerButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfigManagerDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };
        _ = dialog.ShowAsync();
    }

    private async void StorageUsageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageUsageBusy) return;

        _storageUsageBusy = true;
        StorageUsageButton.IsEnabled = false;

        try
        {
            var dialog = new StorageUsageDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ThemeService.CurrentElementTheme
            };
            await dialog.ShowAsync();

            if (!string.IsNullOrEmpty(dialog.ResultSummary))
                StorageUsageStatusText.Text = $"{dialog.ResultSummary}（{DateTime.Now:MM-dd HH:mm}）";
        }
        catch (Exception ex)
        {
            StorageUsageStatusText.Text = string.Format(LocalizationService.L("Settings_StorageUsageOpenFailed", "打开存储占用失败: {0}"), ex.Message);
        }
        finally
        {
            StorageUsageButton.IsEnabled = true;
            _storageUsageBusy = false;
        }
    }

    private string? _githubUserName;

    private bool _httpDownloadActionRefreshing;   // 语言切换重建标签期间为 true：不写配置

    private (string kind, string? arg) _toolsBundleState = ("described", null);   // 工具内核状态（语言切换后按真状态重格式化）

    private async void InitGitHubLoginStatus()
    {
        try
        {
            if (GitHubAuthService.IsLoggedIn)
            {
                var user = await GitHubAuthService.GetCurrentUserAsync();
                if (user is not null)
                {
                    _githubUserName = user.Name ?? user.Login;
                    GitHubLoginStatusText.Text = string.Format(LocalizationService.L("Settings_GitHubLoggedIn", "已登录：{0}"), _githubUserName);
                    GitHubLoginButton.Visibility = Visibility.Collapsed;
                    GitHubLogoutButton.Visibility = Visibility.Visible;
                    GitHubAvatar.Visibility = Visibility.Visible;

                    if (!string.IsNullOrWhiteSpace(user.AvatarUrl))
                    {
                        GitHubAvatar.ProfilePicture = new BitmapImage(new Uri(user.AvatarUrl));
                    }
                    return;
                }
            }

            GitHubLoginStatusText.Text = LocalizationService.L("Settings_GitHub_Status", "未登录");
            GitHubLoginButton.Visibility = Visibility.Visible;
            GitHubLogoutButton.Visibility = Visibility.Collapsed;
            GitHubAvatar.Visibility = Visibility.Collapsed;
        }
        catch
        {
            GitHubLoginStatusText.Text = LocalizationService.L("Settings_GitHub_Status", "未登录");
        }
    }

    private async void GitHubLoginButton_Click(object sender, RoutedEventArgs e)
    {
        await GitHubAuthService.StartDeviceFlowAsync(XamlRoot);
        InitGitHubLoginStatus();
    }

    private void GitHubLogoutButton_Click(object sender, RoutedEventArgs e)
    {
        GitHubAuthService.Logout();
        InitGitHubLoginStatus();
    }

    private async void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        var descriptionBox = new TextBox
        {
            PlaceholderText = LocalizationService.L("Settings_FeedbackDescPlaceholder", "请描述您的问题或建议..."),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            MaxHeight = 160,
            FontSize = 13,
        };

        var stepsBox = new TextBox
        {
            PlaceholderText = LocalizationService.L("Settings_FeedbackStepsPlaceholderOptional", "若是问题反馈，可填写复现步骤；功能建议可留空"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            MaxHeight = 160,
            FontSize = 13,
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = LocalizationService.L("Settings_FeedbackDescLabel", "问题描述"), FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 14 });
        panel.Children.Add(descriptionBox);
        panel.Children.Add(new TextBlock { Text = LocalizationService.L("Settings_FeedbackStepsLabelOptional", "复现步骤（可选）"), FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 14 });
        panel.Children.Add(stepsBox);

        var dialog = new ContentDialog
        {
            Title = LocalizationService.L("Settings_FeedbackMailTitle", "邮件反馈"),
            Content = panel,
            PrimaryButtonText = LocalizationService.L("Settings_FeedbackMailOpenButton", "打开邮件应用"),
            CloseButtonText = LocalizationService.L("Common_Cancel", "取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var steps = stepsBox.Text.Trim();
        var description = descriptionBox.Text.Trim();
        var body = LocalizationService.L("Settings_FeedbackMailBodyHeader", "问题或建议：\n") + (string.IsNullOrEmpty(description) ? LocalizationService.L("Settings_FeedbackMailBodyMissing", "（请填写）") : description) +
                   (string.IsNullOrEmpty(steps) ? "" : LocalizationService.L("Settings_FeedbackMailBodyStepsHeader", "\n\n复现步骤：\n") + steps) +
                   LocalizationService.L("Settings_FeedbackMailBodyFooter", "\n\n发送前请检查邮件内容。");
        if (!await global::Windows.System.Launcher.LaunchUriAsync(FeedbackContact.CreateDraft(LocalizationService.L("Settings_FeedbackMailSubject", "枕星图吧AI助手反馈"), body)))
            await ShowMessageAsync(LocalizationService.L("Settings_FeedbackMailOpenFailedTitle", "无法打开邮件应用"), string.Format(LocalizationService.L("Settings_FeedbackMailOpenFailedMessage", "请手动发送邮件至 {0}。"), FeedbackContact.Email));
    }

    private async void ErrorReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await global::Windows.System.Launcher.LaunchUriAsync(ErrorReportService.CreateFeedbackDraft()))
                return;
        }
        catch
        {
            // 没有邮件应用或关联失效时仍告知固定收件地址。
        }
        await ShowMessageAsync(LocalizationService.L("Settings_FeedbackMailOpenFailedTitle", "无法打开邮件应用"),
            string.Format(LocalizationService.L("Settings_FeedbackMailOpenFailedMessage", "请手动发送邮件至 {0}。"), FeedbackContact.Email));
    }

    private void LoadCreditsAvatar()
    {
        try
        {
            AuthorAvatar.ProfilePicture = new BitmapImage(new Uri("https://github.com/yujinchuan2021-max.png"));
            HaJiYiAvatar.ProfilePicture = new BitmapImage(new Uri("https://github.com/luolangaga.png"));
        }
        catch
        {
        }
    }

    private async void StoreRatingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storeRatingBusy) return;
        _storeRatingBusy = true;
        StoreRatingButton.IsEnabled = false;
        try
        {
            if (!IsStorePackagedApp())
            {
                await ShowMessageAsync(LocalizationService.L("Settings_StoreRatingUnavailableTitle", "暂不可用"), LocalizationService.L("Settings_StoreRatingUnavailableMessage", "当前为开发版本，商店评分仅在从 Microsoft Store 安装的版本中可用。"));
                return;
            }

            try
            {
                var storeContext = Windows.Services.Store.StoreContext.GetDefault();
                WinRT.Interop.InitializeWithWindow.Initialize(storeContext, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

                var result = await storeContext.RequestRateAndReviewAppAsync();
                if (result.Status is Windows.Services.Store.StoreRateAndReviewStatus.Succeeded)
                {
                    ShowRatingThanks();
                    return;
                }

                if (result.Status is Windows.Services.Store.StoreRateAndReviewStatus.CanceledByUser)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsPage] 商店评分组件调用失败，回退到商店评价页: {ex}");
            }

            await OpenStoreReviewPageAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] 打开商店评分失败: {ex}");
        }
        finally
        {
            _storeRatingBusy = false;
            StoreRatingButton.IsEnabled = true;
        }
    }

    private static bool IsStorePackagedApp()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.Id.Name == StorePackageName;
        }
        catch
        {
            return false;
        }
    }

    private async Task OpenStoreReviewPageAsync()
    {
        try
        {
            var launched = await Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-windows-store://review/?ProductId={StoreProductId}"));
            if (!launched)
                await ShowMessageAsync(LocalizationService.L("Settings_OpenFailedTitle", "打开失败"), LocalizationService.L("Settings_StoreRatingOpenFailed", "暂时无法打开商店评分，请稍后重试。"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] 打开商店评分页失败: {ex}");
            await ShowMessageAsync(LocalizationService.L("Settings_OpenFailedTitle", "打开失败"), LocalizationService.L("Settings_StoreRatingOpenFailed", "暂时无法打开商店评分，请稍后重试。"));
        }
    }

    private void RatingThanksScrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        HideRatingThanks();
    }

    private void RatingThanksCloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideRatingThanks();
    }

    private void ShowRatingThanks()
    {
        RatingThanksOverlay.Visibility = Visibility.Visible;
        RatingThanksOverlay.UpdateLayout();

        RatingThanksScrim.Opacity = 0;
        RatingThanksCard.Opacity = 0;
        RatingThanksCardTransform.ScaleX = 0.88;
        RatingThanksCardTransform.ScaleY = 0.88;
        RatingThanksCardTransform.TranslateY = 16;
        RatingThanksBadgeTransform.ScaleX = 0.3;
        RatingThanksBadgeTransform.ScaleY = 0.3;
        RatingThanksBadgeTransform.Rotation = -80;
        RatingThanksHalo.Opacity = 0;
        RatingThanksRing1.Opacity = 0;
        RatingThanksRing2.Opacity = 0;
        RatingThanksRing1Transform.ScaleX = 0.4;
        RatingThanksRing1Transform.ScaleY = 0.4;
        RatingThanksRing2Transform.ScaleX = 0.4;
        RatingThanksRing2Transform.ScaleY = 0.4;

        PlayRatingThanksFlourish();
        SpawnRatingSparkles();
        ScheduleRatingThanksAutoHide();
    }

    private void HideRatingThanks()
    {
        _ratingThanksTimer?.Stop();

        foreach (var storyboard in _ratingThanksStoryboards) storyboard.Stop();
        _ratingThanksStoryboards.Clear();
        RatingThanksParticleCanvas.Children.Clear();

        RatingThanksOverlay.Visibility = Visibility.Collapsed;
    }

    private void ScheduleRatingThanksAutoHide()
    {
        if (_ratingThanksTimer is null)
        {
            _ratingThanksTimer = DispatcherQueue.CreateTimer();
            _ratingThanksTimer.IsRepeating = false;
            _ratingThanksTimer.Tick += (_, _) => HideRatingThanks();
        }

        _ratingThanksTimer.Stop();
        _ratingThanksTimer.Interval = TimeSpan.FromSeconds(7);
        _ratingThanksTimer.Start();
    }

    private void PlayRatingThanksFlourish()
    {
        if (FastModeService.IsFastModeEnabled())
        {
            RatingThanksScrim.Opacity = 1;
            RatingThanksCard.Opacity = 1;
            RatingThanksCardTransform.ScaleX = 1;
            RatingThanksCardTransform.ScaleY = 1;
            RatingThanksCardTransform.TranslateY = 0;
            RatingThanksBadgeTransform.ScaleX = 1;
            RatingThanksBadgeTransform.ScaleY = 1;
            RatingThanksBadgeTransform.Rotation = 0;
            RatingThanksHalo.Opacity = 0.9;
            return;
        }

        var storyboard = new Storyboard();
        AddAnimation(storyboard, RatingThanksScrim, "Opacity", 1, 220, 0, new CubicEase { EasingMode = EasingMode.EaseOut });
        AddAnimation(storyboard, RatingThanksHalo, "Opacity", 0.9, 720, 180, new CubicEase { EasingMode = EasingMode.EaseOut });
        AddAnimation(storyboard, RatingThanksCard, "Opacity", 1, 320, 40, new CubicEase { EasingMode = EasingMode.EaseOut });
        AddAnimation(storyboard, RatingThanksCardTransform, "ScaleX", 1, 520, 40, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
        AddAnimation(storyboard, RatingThanksCardTransform, "ScaleY", 1, 520, 40, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
        AddAnimation(storyboard, RatingThanksCardTransform, "TranslateY", 0, 520, 40, new CubicEase { EasingMode = EasingMode.EaseOut });
        AddAnimation(storyboard, RatingThanksBadgeTransform, "ScaleX", 1, 640, 140, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
        AddAnimation(storyboard, RatingThanksBadgeTransform, "ScaleY", 1, 640, 140, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
        AddAnimation(storyboard, RatingThanksBadgeTransform, "Rotation", 0, 760, 140, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
        AddShockwave(storyboard, RatingThanksRing1, RatingThanksRing1Transform, 0);
        AddShockwave(storyboard, RatingThanksRing2, RatingThanksRing2Transform, 240);

        storyboard.Begin();
        _ratingThanksStoryboards.Add(storyboard);
    }

    private static void AddShockwave(Storyboard storyboard, UIElement ring, CompositeTransform transform, double beginMs)
    {
        var opacity = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(900),
            BeginTime = TimeSpan.FromMilliseconds(beginMs)
        };
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0 });
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), Value = 0.85 });
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), Value = 0 });
        Storyboard.SetTarget(opacity, ring);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Children.Add(opacity);

        AddAnimation(storyboard, transform, "ScaleX", 1.75, 900, beginMs, new CubicEase { EasingMode = EasingMode.EaseOut });
        AddAnimation(storyboard, transform, "ScaleY", 1.75, 900, beginMs, new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    private void SpawnRatingSparkles()
    {
        RatingThanksParticleCanvas.Children.Clear();
        if (FastModeService.IsFastModeEnabled()) return;

        var badgeWidth = RatingThanksBadge.ActualWidth;
        var badgeHeight = RatingThanksBadge.ActualHeight;
        if (badgeWidth <= 0 || badgeHeight <= 0) return;

        var canvas = RatingThanksParticleCanvas;
        var origin = RatingThanksBadge.TransformToVisual(canvas).TransformPoint(new Point(badgeWidth / 2, badgeHeight / 2));

        var random = new Random();
        Color[] tints =
        [
            Color.FromArgb(255, 255, 214, 102),
            Color.FromArgb(255, 255, 236, 179),
            Color.FromArgb(255, 255, 179, 71),
            Color.FromArgb(255, 255, 255, 255),
        ];

        const int count = 18;
        for (var i = 0; i < count; i++)
        {
            var angle = 360.0 / count * i + random.Next(-11, 12);
            var radians = angle * Math.PI / 180;
            var distance = 116 + random.Next(0, 120);
            var size = 4.5 + random.NextDouble() * 7;
            var duration = 820 + random.Next(0, 420);
            var delay = 120 + random.Next(0, 300);
            var tint = new SolidColorBrush(tints[random.Next(tints.Length)]);

            Shapes.Shape shape = i % 3 == 0
                ? CreateSparkleStar(size, tint)
                : new Shapes.Ellipse { Width = size * 1.7, Height = size * 1.7, Fill = tint };

            var width = shape.Width;
            var height = shape.Height;
            var transform = new CompositeTransform { CenterX = width / 2, CenterY = height / 2 };
            shape.RenderTransform = transform;
            Canvas.SetLeft(shape, origin.X - width / 2);
            Canvas.SetTop(shape, origin.Y - height / 2);
            canvas.Children.Add(shape);

            var storyboard = new Storyboard();
            AddAnimation(storyboard, transform, "TranslateX", Math.Cos(radians) * distance, duration, delay, new CubicEase { EasingMode = EasingMode.EaseOut });
            AddAnimation(storyboard, transform, "TranslateY", Math.Sin(radians) * distance + 30, duration, delay, new CubicEase { EasingMode = EasingMode.EaseOut });
            AddAnimation(storyboard, transform, "Rotation", random.Next(-200, 201), duration, delay, new CubicEase { EasingMode = EasingMode.EaseOut });

            var scale = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(duration),
                BeginTime = TimeSpan.FromMilliseconds(delay)
            };
            scale.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0.2 });
            scale.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration * 0.22)), Value = 1.1 });
            scale.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration)), Value = 0.55 });
            Storyboard.SetTarget(scale, transform);
            Storyboard.SetTargetProperty(scale, "ScaleX");
            storyboard.Children.Add(scale);

            var scaleY = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(duration),
                BeginTime = TimeSpan.FromMilliseconds(delay)
            };
            scaleY.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0.2 });
            scaleY.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration * 0.22)), Value = 1.1 });
            scaleY.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration)), Value = 0.55 });
            Storyboard.SetTarget(scaleY, transform);
            Storyboard.SetTargetProperty(scaleY, "ScaleY");
            storyboard.Children.Add(scaleY);

            var opacity = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(duration),
                BeginTime = TimeSpan.FromMilliseconds(delay)
            };
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0 });
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration * 0.18)), Value = 1 });
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration * 0.62)), Value = 1 });
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration)), Value = 0 });
            Storyboard.SetTarget(opacity, shape);
            Storyboard.SetTargetProperty(opacity, "Opacity");
            storyboard.Children.Add(opacity);

            storyboard.Begin();
            _ratingThanksStoryboards.Add(storyboard);
        }
    }

    private static Shapes.Polygon CreateSparkleStar(double size, Brush fill)
    {
        var points = new PointCollection
        {
            new Point(size, 0),
            new Point(size * 1.3, size * 0.7),
            new Point(size * 2, size),
            new Point(size * 1.3, size * 1.3),
            new Point(size, size * 2),
            new Point(size * 0.7, size * 1.3),
            new Point(0, size),
            new Point(size * 0.7, size * 0.7),
        };

        return new Shapes.Polygon { Points = points, Fill = fill, Width = size * 2, Height = size * 2 };
    }

    private static void AddAnimation(Storyboard storyboard, DependencyObject target, string property, double to, double durationMs, double beginMs, EasingFunctionBase? easing = null)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            BeginTime = TimeSpan.FromMilliseconds(beginMs)
        };

        if (easing is not null) animation.EasingFunction = easing;

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void OpenSourceButton_Click(object sender, RoutedEventArgs e)
    {
        DrawerOverlay.Visibility = Visibility.Visible;
        if (FastModeService.IsFastModeEnabled())
        {
            DrawerOverlayBackground.Opacity = 1;
            DrawerPanelTransform.X = 0;
        }
        else
        {
            DrawerOpenStoryboard.Begin();
        }
    }

    /// <summary>「开源与致谢」字体卡片：名称/署名/许可/链接全部取自 app-font.json（换字体后随动；XAML 不再含字面量）。</summary>
    private void ApplyFontCredit()
    {
        FontCreditName.Text = AppFonts.FamilyName;
        FontCreditDetail.Text = AppFonts.AttributionDetail;
        if (System.Uri.TryCreate(AppFonts.AttributionUrl, System.UriKind.Absolute, out var url))
            FontCreditLink.NavigateUri = url;
    }

    private void PopulateAdditionalThirdPartyCredits()
    {
        AdditionalThirdPartyCredits.Children.Clear();

        AddCreditHeading(LocalizationService.L("Settings_CreditDepsHeading", "应用依赖（其余 NuGet 直接依赖）"));
        (string Name, string Version)[] packages =
        [
            ("Downloader", "5.9.6"),
            ("FieldCure.AssistStudio.Controls.WinUI", "0.21.0"),
            ("HtmlAgilityPack", "1.12.4"),
            ("LiveChartsCore.SkiaSharpView.WinUI", "2.0.4"),
            ("Microsoft.Diagnostics.Tracing.TraceEvent", "3.2.2"),
            ("Microsoft.Extensions.AI.OpenAI", "10.8.3"),
            ("Microsoft.Toolkit.Uwp.Notifications", "7.1.3"),
            ("Microsoft.Web.WebView2", "1.0.4022.49"),
            ("QRCoder", "1.6.0"),
            ("System.Text.Encoding.CodePages", "10.0.0"),
            ("SIPSorcery", "10.0.14"),
            ("System.Speech", "10.0.10"),
            ("PdfPig", "0.1.14"),
            ("AngleSharp", "1.5.0"),
            ("WinUI3Localizer", "2.3.0"),
        ];
        foreach (var (name, version) in packages)
            AddThirdPartyCredit(name, string.Format(LocalizationService.L("Settings_CreditNuGetDetail", "NuGet {0} · 许可与依赖信息见包页"), version), $"https://www.nuget.org/packages/{name}/{version}");

        AddCreditHeading(LocalizationService.L("Settings_CreditWebHeading", "随包网页组件"));
        AddThirdPartyCredit("markdown-it 15.0.0", LocalizationService.L("Settings_CreditMarkdown", "文档 Markdown 渲染"), "https://github.com/markdown-it/markdown-it");
        AddThirdPartyCredit("docx-preview 0.3.5", LocalizationService.L("Settings_CreditDocxPreview", "DOCX 预览"), "https://github.com/VolodymyrBaydalka/docxjs");
        AddThirdPartyCredit("JSZip 3.10.1", LocalizationService.L("Settings_CreditArchive", "压缩包处理"), "https://github.com/Stuk/jszip");
        AddThirdPartyCredit("SheetJS xlsx 0.18.5", LocalizationService.L("Settings_CreditSheet", "表格处理"), "https://sheetjs.com");
        AddThirdPartyCredit("pdfjs-dist 4.10.38", LocalizationService.L("Settings_CreditPdfPreview", "PDF 预览"), "https://github.com/mozilla/pdf.js");
        AddThirdPartyCredit("pdf-lib 1.17.1", LocalizationService.L("Settings_CreditPdfProcess", "PDF 处理"), "https://github.com/Hopding/pdf-lib");

        AddCreditHeading(LocalizationService.L("Settings_CreditSourceHeading", "源码、规则与其他资源"));
        AddThirdPartyCredit("图吧工具箱 CE", LocalizationService.L("Settings_CreditUpstream", "枕星版所基于的上游项目"), "https://github.com/luolangaga/tubatools");
        AddThirdPartyCredit("EnergyStarX", LocalizationService.L("Settings_CreditEnergyStar", "节能功能移植来源；许可文本随源码附带"), "https://github.com/JasonWei512/EnergyStarX");
        AddThirdPartyCredit("bloub", LocalizationService.L("Settings_CreditBloub", "AI 助手头像动画引擎；许可文本随源码附带"), "https://github.com/jeremy-prt/bloub");
        AddThirdPartyCredit("FluentCleaner", LocalizationService.L("Settings_CreditCleanerSource", "系统清理代码移植来源"), "https://github.com/builtbybel/FluentCleaner");
        AddThirdPartyCredit("RogueCleaner", LocalizationService.L("Settings_CreditCleanerSource", "系统清理代码移植来源"), "https://github.com/aakk007/RogueCleaner");
        AddThirdPartyCredit("Winapp2", LocalizationService.L("Settings_CreditWinapp2", "清理规则库；遵循 CC BY-SA 4.0"), "https://github.com/MoscaDotTo/Winapp2");
        AddThirdPartyCredit("OfficeCLI", LocalizationService.L("Settings_CreditOfficeCli", "按需下载的可选文档引擎"), "https://github.com/iOfficeAI/OfficeCLI");

        AddCreditHeading(LocalizationService.L("Settings_CreditFontHeading", "字体"));
        if (!string.Equals(AppFonts.FamilyName, "Sarasa UI SC", StringComparison.OrdinalIgnoreCase))
            AddThirdPartyCredit("更纱黑体 Sarasa UI SC", LocalizationService.L("Settings_CreditOptionalFont", "可选界面字体；许可文本随包附带"), "https://github.com/be5invis/Sarasa-Gothic");
        if (!string.Equals(AppFonts.FamilyName, "Noto Sans SC", StringComparison.OrdinalIgnoreCase))
            AddThirdPartyCredit("Noto Sans SC", LocalizationService.L("Settings_CreditOptionalFont", "可选界面字体；许可文本随包附带"), "https://fonts.google.com/noto/specimen/Noto+Sans+SC");
        if (!string.Equals(AppFonts.FamilyName, "HarmonyOS Sans SC", StringComparison.OrdinalIgnoreCase))
            AddThirdPartyCredit("HarmonyOS Sans SC", LocalizationService.L("Settings_CreditHarmonyFont", "可选界面字体；华为自有许可，协议随包附带"), "https://developer.huawei.com/images/download/general/HarmonyOS-Sans.zip");
        AddThirdPartyCredit("Maple Mono CN", LocalizationService.L("Settings_CreditMapleFont", "代码与数字区域使用的等宽字体；许可文本随包附带"), "https://github.com/subframe7536");
    }

    private void AddCreditHeading(string title)
    {
        AdditionalThirdPartyCredits.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 12, 0, 2),
        });
    }

    private void AddThirdPartyCredit(string name, string detail, string sourceUrl)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new HyperlinkButton
        {
            Content = LocalizationService.L("Settings_CreditSourceLink", "来源与许可"),
            NavigateUri = new Uri(sourceUrl),
            Padding = new Thickness(0),
            FontSize = 12,
        });
        AdditionalThirdPartyCredits.Children.Add(new Border
        {
            Style = (Style)Resources["ThirdPartyCreditStyle"],
            Child = content,
        });
    }

    private void DrawerCloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseDrawer();
    }

    private void DrawerOverlayBackground_Tapped(object sender, TappedRoutedEventArgs e)
    {
        CloseDrawer();
    }

    private void CloseDrawer()
    {
        if (FastModeService.IsFastModeEnabled())
        {
            DrawerOverlay.Visibility = Visibility.Collapsed;
            DrawerOverlayBackground.Opacity = 0;
            DrawerPanelTransform.X = 420;
            return;
        }
        DrawerCloseStoryboard.Completed += OnDrawerCloseCompleted;
        DrawerCloseStoryboard.Begin();
    }

    private void OnDrawerCloseCompleted(object? sender, object e)
    {
        DrawerCloseStoryboard.Completed -= OnDrawerCloseCompleted;
        DrawerOverlay.Visibility = Visibility.Collapsed;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = LocalizationService.L("Common_OK", "确定"),
            RequestedTheme = ThemeService.CurrentElementTheme
        };

        await dialog.ShowAsync();
    }

    private static string GetDefaultHttpDownloadPath()
        => Win32Dialogs.GetDownloadsDirectory();

    private static string GetHttpDownloadPath()
    {
        var path = PathResolver.MakeAbsolute(AppSettings.Get("HttpDownloadPath"));
        return string.IsNullOrWhiteSpace(path) ? GetDefaultHttpDownloadPath() : path;
    }

    private static string GetHttpDownloadAction()
    {
        var action = AppSettings.Get("HttpDownloadAction");
        return action is "none" or "extract" or "install" ? action : "install";
    }

    private void InitHttpDownloadSettings()
    {
        HttpDownloadPathText.Text = GetHttpDownloadPath();

        HttpDownloadActionComboBox.ItemsSource = new[]
        {
            new { Key = "none", Label = LocalizationService.L("Settings_DownloadActionNone", "仅下载") },
            new { Key = "extract", Label = LocalizationService.L("Settings_DownloadActionExtract", "下载并解压") },
            new { Key = "install", Label = LocalizationService.L("Settings_DownloadActionInstall", "下载并运行") },
        };
        HttpDownloadActionComboBox.DisplayMemberPath = "Label";
        HttpDownloadActionComboBox.SelectedValuePath = "Key";

        var savedAction = GetHttpDownloadAction();
        HttpDownloadActionComboBox.SelectedValue = savedAction;
        if (HttpDownloadActionComboBox.SelectedIndex < 0)
            HttpDownloadActionComboBox.SelectedIndex = 2;

        HttpDownloadActionComboBox.SelectionChanged += HttpDownloadActionComboBox_SelectionChanged;

        UpdateDownloadQueueStatus();
    }

    /// <summary>语言切换后仅重建「下载后操作」下拉的显示标签：保留当前选择，不重挂事件、不重置路径、不重跑 InitHttpDownloadSettings。</summary>
    private void RefreshHttpDownloadActionLabels()
    {
        var saved = HttpDownloadActionComboBox.SelectedValue as string ?? GetHttpDownloadAction();
        _httpDownloadActionRefreshing = true;   // 重建期间不写配置（AppSettings 无同值去重）
        try
        {
            HttpDownloadActionComboBox.ItemsSource = new[]
            {
                new { Key = "none", Label = LocalizationService.L("Settings_DownloadActionNone", "仅下载") },
                new { Key = "extract", Label = LocalizationService.L("Settings_DownloadActionExtract", "下载并解压") },
                new { Key = "install", Label = LocalizationService.L("Settings_DownloadActionInstall", "下载并运行") },
            };
            HttpDownloadActionComboBox.SelectedValue = saved;
            if (HttpDownloadActionComboBox.SelectedIndex < 0)
                HttpDownloadActionComboBox.SelectedIndex = 2;
        }
        finally
        {
            _httpDownloadActionRefreshing = false;
        }
    }

    private void HttpDownloadActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_httpDownloadActionRefreshing) return;
        var selected = HttpDownloadActionComboBox.SelectedValue as string;
        if (selected is not null)
            AppSettings.Set("HttpDownloadAction", selected);
    }

    private void UpdateDownloadQueueStatus()
    {
        var pending = DownloadQueueService.PendingCount;
        var total = DownloadQueueService.Queue.Count;
        DispatcherQueue.TryEnqueue(() =>
        {
            HttpDownloadQueueStatusText.Text = pending > 0
                ? string.Format(LocalizationService.L("Settings_DlQueuePending", "队列中 {0} 项，{1} 项待下载"), total, pending)
                : total > 0
                    ? string.Format(LocalizationService.L("Settings_DlQueueDone", "队列中 {0} 项，全部完成"), total)
                    : LocalizationService.L("Settings_DlQueueEmpty", "队列为空");
        });
    }

    private async void HttpDownloadBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dir = await Win32Dialogs.PickFolderAsync();
        if (string.IsNullOrEmpty(dir))
            return;

        AppSettings.Set("HttpDownloadPath", dir);
        HttpDownloadPathText.Text = dir;
    }

    private void HttpDownloadResetPathButton_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Remove("HttpDownloadPath");
        HttpDownloadPathText.Text = GetDefaultHttpDownloadPath();
    }

    private void HttpDownloadAddButton_Click(object sender, RoutedEventArgs e)
    {
        var url = HttpDownloadUrlTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            _ = ShowMessageAsync(LocalizationService.L("Settings_NoticeTitle", "提示"), LocalizationService.L("Settings_EnterDownloadUrl", "请输入下载链接"));
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _ = ShowMessageAsync(LocalizationService.L("Settings_NoticeTitle", "提示"), LocalizationService.L("Settings_EnterValidUrl", "请输入有效的 HTTP/HTTPS 链接"));
            return;
        }

        var destPath = GetHttpDownloadPath();
        Directory.CreateDirectory(destPath);

        var action = GetHttpDownloadAction();
        IDownloadPostProcessor? postProcessor = action switch
        {
            "extract" => new ArchiveExtractProcessor(),
            "install" => new InstallerLaunchProcessor(),
            _ => null
        };

        var fileName = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('?') || fileName.Contains('='))
            fileName = null;

        var displayName = fileName ?? string.Format(LocalizationService.L("Settings_DownloadFileName", "下载文件 {0}"), DateTime.Now.ToString("HH:mm:ss"));

        DownloadQueueService.Enqueue(displayName, url, destPath, postProcessor,
            description: url, glyph: "\uE896");

        HttpDownloadUrlTextBox.Text = "";
        UpdateDownloadQueueStatus();

        _ = ShowMessageAsync(LocalizationService.L("Settings_AddedToDownload", "已加入下载"), string.Format(LocalizationService.L("Settings_AddedToDownloadMsg", "\"{0}\" 已加入下载队列\n保存至：{1}"), displayName, destPath));
    }

    private Flyout? _downloadFlyout;

    private void HttpDownloadViewQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadFlyout is null)
        {
            _downloadFlyout = new Flyout
            {
                Content = new DownloadQueueFlyout(),
                Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
            };
        }
        _downloadFlyout.ShowAt(sender as FrameworkElement);
    }
}
