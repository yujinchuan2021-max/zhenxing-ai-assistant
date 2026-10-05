using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Automation;
using TubaWinUi3.Controls;
using TubaWinUi3.Models;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using Windows.UI;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace TubaWinUi3;

public sealed partial class MainWindow : Window
{
    private bool _syncingNavSelection;
    private bool _suppressSearch;
    private bool _searchDismissed;
    private readonly ObservableCollection<SearchResult> _searchResults = [];
    private readonly DispatcherQueueTimer _searchDebounceTimer;
    private Flyout? _downloadFlyout;
    private int _lastBadgeCount;
    private bool _refreshCategoriesInFlight;
    private bool _refreshCategoriesPending;

    /// <summary>当前正在执行的内置工具名称（入口页在 ExecuteAsync 前设置），供独立窗口标题使用。</summary>
    public static string? ActiveToolName { get; set; }

    public Image? GetBackgroundImage() => BackgroundImg;

    /// <summary>
    /// 显示更新 Banner。autoDownload 参数保留以兼容既有调用点：自有通道的下载一律由用户在 Banner 上主动点击触发，
    /// 不自动开始下载；验证过大小与 SHA-256 后也只打开下载文件夹（便携版不自动覆盖运行目录）。
    /// </summary>
    public void ShowUpdateBanner(Models.UpdateInfo update, bool autoDownload)
    {
        UpdateBanner.ShowUpdateAvailable(update);
    }

    /// <summary>发现新版但平台/形态不匹配（无法自动下载）时的提示：按钮去官网，不进入下载队列。</summary>
    public void ShowManualDownloadBanner(Models.UpdateInfo update)
    {
        UpdateBanner.ShowManualDownload(update);
    }

    public void ShowUpdateAlreadyDownloaded(Models.UpdateInfo update)
    {
        UpdateBanner.ShowUpdateAvailable(update);
        UpdateBanner.ShowDownloadComplete();
    }

    private DispatcherTimer? _toolUpdateToastTimer;

    public void ShowToolUpdateToast(string toolName)
    {
        ToolUpdateToast.Title = LocalizationService.L("MainWindow_ToolUpdateDoneTitle", "工具更新完成");
        ToolUpdateToast.Message = string.Format(LocalizationService.L("MainWindow_ToolUpdateDoneMessage", "「{0}」已更新到最新版本"), toolName);
        ToolUpdateToast.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
        ToolUpdateToast.IsOpen = true;
        StartToastAutoClose();
    }

    public void ShowToolUpdateProgressToast(string toolName)
    {
        ToolUpdateToast.Title = LocalizationService.L("MainWindow_ToolUpdateProgressTitle", "正在更新工具");
        ToolUpdateToast.Message = string.Format(LocalizationService.L("MainWindow_ToolUpdateProgressMessage", "「{0}」正在同步更新..."), toolName);
        ToolUpdateToast.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
        ToolUpdateToast.IsOpen = true;
    }

    public void ShowToolUpdateFailedToast(string toolName, string error)
    {
        ToolUpdateToast.Title = LocalizationService.L("MainWindow_ToolUpdateFailedTitle", "工具更新失败");
        ToolUpdateToast.Message = string.Format(LocalizationService.L("MainWindow_ToolUpdateFailedMessage", "「{0}」更新失败：{1}"), toolName, error);
        ToolUpdateToast.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        ToolUpdateToast.IsOpen = true;
        StartToastAutoClose();
    }

    private void StartToastAutoClose()
    {
        _toolUpdateToastTimer?.Stop();
        _toolUpdateToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toolUpdateToastTimer.Tick += (s, e) =>
        {
            ToolUpdateToast.IsOpen = false;
            ((DispatcherTimer)s!).Stop();
        };
        _toolUpdateToastTimer.Start();
    }

#pragma warning disable CS0414
    private bool _initialized;
#pragma warning restore CS0414

    public MainWindow()
    {
        InitializeComponent();

        SearchListView.ItemsSource = _searchResults;

        // AutoSuggestBox 会先于实例处理 Escape（置 Handled），键盘导航必须用 handledEventsToo 注册
        SearchBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(SearchBox_KeyDown), handledEventsToo: true);

        _searchDebounceTimer = DispatcherQueue.CreateTimer();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(100);
        _searchDebounceTimer.Tick += OnSearchDebounceTick;

        // 标题栏定制一律走 SafeTitleBar：部分 Windows 10 版本 AppWindow.TitleBar 为 null，
        // 构造函数里的空引用会以 STOWED_EXCEPTION_80004003 让进程闪退（见 SafeTitleBar 注释）
        SafeTitleBar.ApplyExtendedTall(this, AppTitleBar);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        try
        {
            if (File.Exists(iconPath))
                AppWindow.SetIcon(iconPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] 设置窗口图标失败（已忽略）: {ex.Message}");
        }

        ApplyTitleBarTheme(ElementTheme.Default);

        BackdropService.ApplyBackdrop(this);
        BackdropService.BackdropChanged += OnBackdropChanged;

        WindowSizeService.ApplySavedWindowSize(this);

        Closed += MainWindow_Closed;
        AppWindow.Changed += AppWindow_Changed;
        NavFrame.Navigated += NavFrame_Navigated;
        NavView.ItemInvoked += NavView_ItemInvoked;
        // 【返修4】手动展开/收起窗格时按真实 IsPaneOpen 刷新子项布局：
        // 展开（200px）→ 恢复层级缩进与可见性；关闭（62px 轨道）→ 隐藏+归零缩进。
        NavView.RegisterPropertyChangedCallback(
            Microsoft.UI.Xaml.Controls.NavigationView.IsPaneOpenProperty,
            (_, _) =>
            {
                try
                {
                    RefreshNavChildItemLayout();
                }
                catch { }
            });

        // ZXAI 2026-09-20：折叠组——「AI 工具 / 图吧工具」组头单击展开/收起。
        // 方案：组头=无子项的普通项（避开 NavigationView 内部折叠逻辑的干扰——
        // 实测其内部对组头点击"瞬时折叠又恢复"，净效果=点了没反应），
        // 子项平铺，点击组头时自行切换子项 Visibility；组头 IsExpanded 借用为折叠状态位。
        CollectNavGroupChildren();
        ApplyNavLayoutMode();
        ApplyLocalization();
        LocalizationService.LanguageChanged += OnLanguageChanged;
        // ZXAI 2026-09-23：社区导航项已删除（用户口径），此前的 MSIX 特判移除逻辑随之移除。

        var version = UpdateService.CurrentVersion;
        SplashVersionText.Text = $"V{version.Major}.{version.Minor}";
        _ = InitializeAfterSplashAsync();
    }

    private void LanguageToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _ = SwitchLanguageAsync();
    }

    private async Task SwitchLanguageAsync()
    {
        if (!LanguageToggleButton.IsEnabled) return;
        LanguageToggleButton.IsEnabled = false;
        try
        {
            string next = LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage
                ? LocalizationService.ChineseLanguage
                : LocalizationService.EnglishLanguage;
            await LocalizationService.SetLanguageAsync(next);
        }
        finally
        {
            LanguageToggleButton.IsEnabled = true;
        }
    }

    private void OnLanguageChanged()
    {
        // LanguageChanged may also be raised by a settings page; marshal shell and page updates to this window.
        DispatcherQueue.TryEnqueue(ApplyLocalization);
    }

    private void ApplyLocalization()
    {
        bool english = LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage;
        string Text(string key, string zh, string en) => LocalizationService.L(key, english ? en : zh);

        string productTitle = Text("App_Title", "枕星图吧AI助手", "Zhenxing AI Assistant");
        Title = productTitle;
        AppTitleText.Text = productTitle;
        SplashTitleText.Text = productTitle;
        SplashStatusText.Text = Text("MainWindow_SplashStatus.Text", "正在初始化...", "Starting up...");
        SearchBox.PlaceholderText = Text("MainWindow_SearchPlaceholder.PlaceholderText", "搜索工具、设置...", "Search tools and settings...");

        LanguageToggleButton.Content = english ? "EN → 中" : "中 → EN";
        string languageTip = Text("Shell_LanguageToggle", "切换为英文", "Switch to Chinese");
        ToolTipService.SetToolTip(LanguageToggleButton, languageTip);
        AutomationProperties.SetName(LanguageToggleButton, languageTip);

        SetButtonText(AiQuickButton, Text("MainWindow_AiButton", "AI 助手", "AI Assistant"));
        SetButtonText(DownloadQueueButton, Text("MainWindow_DownloadQueueButton", "下载队列", "Downloads"));
        SetButtonText(TitleBarSettingsButton, Text("MainWindow_NavSettings", "设置", "Settings"));
        SetButtonText(TitleBarAppCenterButton, Text("Shell_AppCenter", "应用中心", "App Center"));

        SetNavText(AiToolsGroup, Text("Shell_NavAiTools", "AI 工具", "AI Tools"));
        SetNavText(TubaToolsGroup, Text("Shell_NavTubaTools", "图吧工具", "Toolbox"));
        SetNavText("home", Text("Shell_NavHome", "主页", "Home"));
        SetNavText("favorites", Text("Shell_NavFavorites", "收藏", "Favorites"));
        SetNavText("hardware", Text("MainWindow_NavHardware.Content", "硬件信息", "Hardware"));
        SetNavText("ai-news", Text("Shell_NavAiNews", "枕星AI资讯", "Zhenxing AI News"));
        SetNavText("skill-library", LocalizationService.L("SkillHub_Library", "官方技能库"));
        SetNavText("community-hub", Text("Shell_NavCommunityHub", "枕星AI社区", "Zhenxing AI Community"));
        SetNavText("ai-cat:assistant", Text("Shell_NavChatAssistant", "聊天助手", "Chat Assistant"));
        SetNavText("ai-cat:agent", Text("Shell_NavAiAgent", "AI Agent", "AI Agent"));
        SetNavText("ai-cat:local", Text("Shell_NavLocalAi", "AI 本地部署", "Local AI"));
        SetNavText("ai-cat:models", Text("Shell_NavAiModels", "AI 模型", "AI Models"));
        SetNavText("ai-cat:dev", Text("Shell_NavSoftwareDev", "软件开发", "Software Development"));
        SetNavText("ai-cat:game", Text("Shell_NavGameDev", "游戏开发", "Game Development"));
        SetNavText("ai-cat:office", Text("Shell_NavOffice", "办公效率", "Productivity"));
        SetNavText("ai-cat:create", Text("Shell_NavCreation", "内容创作", "Content Creation"));
        SetNavText("ai-cat:image", Text("Shell_NavImage", "图像设计", "Image Design"));
        SetNavText("ai-cat:video", Text("Shell_NavVideo", "视频制作", "Video Production"));
        SetNavText("ai-cat:audio", Text("Shell_NavAudio", "音频制作", "Audio Production"));
        SetNavText("ai-cat:writing", Text("Shell_NavWriting", "文案写作", "Writing"));
        SetNavText("ai-cat:translate", Text("Shell_NavTranslation", "翻译工具", "Translation"));
        SetNavText("all", Text("Shell_NavAllTools", "全部工具", "All Tools"));
        SetNavText("builtin", Text("MainWindow_NavBuiltin.Content", "内置", "Built-in"));
        SetNavText("benchmark", Text("MainWindow_NavBenchmark.Content", "性能测试", "Benchmarks"));

        foreach (var item in _dynamicNavItems)
        {
            string category = item.Tag as string ?? string.Empty;
            string label = LocalizationService.GetCategoryDisplayName(category);
            item.Content = label;
            ToolTipService.SetToolTip(item, label);
        }

        if (NavFrame.Content is ILocalizablePage page)
            page.ApplyLocalization();
        if (_activeToolContent is ILocalizablePage active && !ReferenceEquals(active, NavFrame.Content))
            active.ApplyLocalization();

        if (SearchPopup.IsOpen)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text)) PopulateSearchSuggestions();
            else _ = SearchInBackgroundAsync(SearchBox.Text.Trim());
        }
    }

    private static void SetButtonText(Button button, string label)
    {
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
    }

    private void SetNavText(string tag, string label)
    {
        var item = FindNavItemByTag(tag);
        if (item is not null) SetNavText(item, label);
    }

    private static void SetNavText(NavigationViewItem item, string label)
    {
        item.Content = label;
        ToolTipService.SetToolTip(item, label);
    }

    private async Task InitializeAfterSplashAsync()
    {
        _initialized = true;

        NavigateToDefaultPage();

        // 仅应用本地自定义背景（品牌壁纸彩蛋已移除：不再自动检测主板品牌、下载或加载壁纸）
        ApplyBackground();

        _ = Task.Run(async () =>
        {
            try
            {
                _ = ToolCatalog.ToolsRoot;
                var categories = ToolCatalog.GetCategories().ToList();

                if (!ToolCatalog.IsCacheReady)
                {
                    await ToolCatalog.GetAllToolsAsync();
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    PopulateCategories(categories);
                    ApplyNavLayoutMode();

                    // 仅在「默认页 = 动态分类」时补一次导航：首次导航时分类菜单
                    // 尚未填充无法选中，需要填充后重新定位；默认页为静态项（如"全部
                    // 工具"）时首次导航已生效，跳过以免 HomePage 重复实例化
                    var defaultPage = AppSettings.Get("DefaultPage") ?? "home";
                    if (categories.Any(c => c.Equals(defaultPage, StringComparison.OrdinalIgnoreCase)))
                        NavigateToDefaultPage();
                    // ZXAI 兜底：主页=AI助手在窗口构造期打开可能因 Frame 未就绪而夭折，
                    // 初始化完成后幂等重试一次（已在 AI 页会直接跳过，不会重复重建）
                    else if (defaultPage == "home")
                        OpenAiAssistant();

                    NavLayoutModeService.NavLayoutModeChanged += OnNavLayoutModeChanged;
                });
            }
            catch { }
        });

        DownloadQueueService.Initialize(DispatcherQueue);
        DownloadQueueService.QueueChanged += OnDownloadQueueChanged;
        ToolUpdateService.Initialize(DispatcherQueue);
        UpdateDownloadBadge();

        AppSettings.SettingChanged += OnBackgroundSettingChanged;

        await FadeOutSplashAsync();
    }

    private async Task FadeOutSplashAsync()
    {
        var storyboard = new Storyboard();
        var duration = TimeSpan.FromMilliseconds(350);
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };

        storyboard.Children.Add(CreateSplashAnimation("Opacity", 1.0, 0.0, duration, easing));
        storyboard.Children.Add(CreateSplashAnimation("(UIElement.RenderTransform).(ScaleTransform.ScaleX)", 1.0, 0.95, duration, easing));
        storyboard.Children.Add(CreateSplashAnimation("(UIElement.RenderTransform).(ScaleTransform.ScaleY)", 1.0, 0.95, duration, easing));

        var tcs = new TaskCompletionSource();
        storyboard.Completed += (_, _) => tcs.TrySetResult();
        storyboard.Begin();
        await tcs.Task;

        SplashOverlay.Visibility = Visibility.Collapsed;
    }

    private DoubleAnimation CreateSplashAnimation(string targetProperty, double from, double to, TimeSpan duration, EasingFunctionBase easing)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(animation, SplashOverlay);
        Storyboard.SetTargetProperty(animation, targetProperty);
        return animation;
    }

    private void OnBackgroundSettingChanged(string key)
    {
        if (key == "BackgroundImagePath" || key == "BackgroundOpacity")
            DispatcherQueue.TryEnqueue(ApplyBackground);
    }

    private void ApplyBackground()
    {
        var bmp = BackgroundService.LoadBackgroundImage();
        if (bmp is not null)
        {
            BackgroundImg.Source = bmp;
            BackgroundImg.Opacity = BackgroundService.GetBackgroundOpacity();
            BackgroundImg.Visibility = Visibility.Visible;
        }
        else
        {
            BackgroundImg.Source = null;
            BackgroundImg.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSplashStatus(string text)
    {
        DispatcherQueue.TryEnqueue(() => SplashStatusText.Text = text);
    }

    /// <summary>【NAV 修复·Codex】成员清单是稳定模型：只允许构造期收集一次；之后只由
    /// PopulateCategories 维护增删，绝不从 MenuItems 反推（紧凑移除后重收会得到空清单，
    /// 导致恢复判断失败、子项永久丢失——已实测复现）。</summary>
    private bool _navChildrenCollected;

    /// <summary>【NAV 修复·Codex】动态分类项独立跟踪清单：PopulateCategories 重建时据此
    /// 精确清理，不再依赖可能为空的 _tubaGroupChildren.LastOrDefault 反推。</summary>
    private readonly List<NavigationViewItem> _dynamicNavItems = new();

    private bool _updatingNavItems;

    /// <summary>当前是否处于关闭的图标栏；与内容页类型无关。</summary>
    private bool IsCompactTrackActive => NavView.PaneDisplayMode != NavigationViewPaneDisplayMode.Top
        && !NavView.IsPaneOpen;


    /// <summary>
    /// 【返修3·子项泄漏】+【返修4·用户截图：图标被右边界裁切】
    /// 统一刷新两组平铺子项（含运行期动态分类）的【缩进与可见性】：
    /// · 紧凑关闭态：子项隐藏 + Margin 归零（28px 展开态缩进在 62px 轨道里会把图标推向
    ///   右边界裁成半个/一条边——用户实测；归零双保险：任何可见路径都完整居中、点击区域/选中背景不越界）；
    /// · 其余状态：Margin 恢复 28,0,0,0 + 按组头 IsExpanded 恢复可见性（展开时原层级缩进）。
    /// 覆盖时机：手动展开/收起窗格（IsPaneOpen 回调）、窗口尺寸变化、动态分类加载后。
    /// 本方法幂等、轻量（~19 项），可安全重复调用。
    /// </summary>
    private void RefreshNavChildItemLayout()
    {
        if (!_navChildrenCollected || _updatingNavItems) return;
        _updatingNavItems = true;
        var wasSyncing = _syncingNavSelection;
        _syncingNavSelection = true;
        try
        {
            var compactClosed = IsCompactTrackActive;
            UpdateNavGroupItems(AiToolsGroup, _aiGroupChildren, !compactClosed && AiToolsGroup.IsExpanded);
            UpdateNavGroupItems(TubaToolsGroup, _tubaGroupChildren, !compactClosed && TubaToolsGroup.IsExpanded);
        }
        finally
        {
            _syncingNavSelection = wasSyncing;
            _updatingNavItems = false;
        }
    }

    /// <summary>隐藏项不留在 ItemsRepeater 中占位；成员和顺序始终来自独立清单。</summary>
    private void UpdateNavGroupItems(NavigationViewItem anchorHeader, List<NavigationViewItem> children, bool show)
    {
        var anchor = NavView.MenuItems.IndexOf(anchorHeader);
        if (anchor < 0) return;
        var k = 0;
        foreach (var c in children)
        {
            if (!show)
            {
                NavView.MenuItems.Remove(c);
                c.Margin = new Thickness(0);
                continue;
            }
            c.Margin = new Thickness(28, 0, 0, 0);
            c.Visibility = Visibility.Visible;
            var expectedIndex = anchor + 1 + k;
            var currentIndex = NavView.MenuItems.IndexOf(c);
            if (currentIndex != expectedIndex)
            {
                if (currentIndex >= 0) NavView.MenuItems.RemoveAt(currentIndex);
                NavView.MenuItems.Insert(Math.Min(expectedIndex, NavView.MenuItems.Count), c);
            }
            k++;
        }
    }

    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        AppTitleBar.IsBackButtonVisible = NavFrame.CanGoBack;
        // 返回历史页不会经过 NavigateToToolPage；以本次导航参数更新真实工具内容。
        _activeToolContent = e.SourcePageType == typeof(ToolContentPage)
            ? (e.Parameter as ToolContentPageParam)?.Content
            : e.Content as AiAgentPage;

        // 选中同步以当前页为准，所有导航入口共用此逻辑。
        // 之前顶栏入口（设置/收藏/应用中心）跳过同步后 NavView 选中态停在旧值（如主页），
        // 后续点击"主页"因已选中不触发 SelectionChanged，无法返回。
        // 此处统一同步（只改选中态，不触发导航——_syncingNavSelection 抑制 SelectionChanged）。
        _syncingNavSelection = true;
        try
        {
            if (e.SourcePageType == typeof(SettingsPage))
            {
                NavView.SelectedItem = NavView.SettingsItem;
            }
            else if (e.SourcePageType == typeof(AppCenterPage))
            {
                NavView.SelectedItem = null; // 应用中心为顶栏入口，无对应菜单项
            }
            else
            {
                var targetTag = ResolvePageTag(e.SourcePageType, e.Parameter);
                if (targetTag is not null)
                {
                    var navItem = FindNavItemByTag(targetTag);
                    if (navItem is not null) NavView.SelectedItem = navItem;
                }
            }
        }
        finally
        {
            _syncingNavSelection = false;
        }
    }

    private static string? ResolvePageTag(Type pageType, object? parameter)
    {
        if (pageType == typeof(AiAgentPage)
            || (pageType == typeof(ToolContentPage) && parameter is ToolContentPageParam { Content: AiAgentPage or AiConversationHost }))
            return "home";
        if (pageType == typeof(SettingsPage)) return "settings";
        if (pageType == typeof(FavoritesPage)) return "favorites";
        if (pageType == typeof(HardwarePage)) return "hardware";
        // ZXAI 2026-09-23：枕星AI社区（顶级导航项；返回/重入时据此同步选中态）
        if (pageType == typeof(CommunityHubPage)) return "community-hub";
        if (pageType == typeof(AiNewsPage)) return "ai-news";
        if (pageType == typeof(SkillLibraryPage)) return "skill-library";
        if (pageType == typeof(BuiltinToolsPage)) return "builtin";
        if (pageType == typeof(AiCategoryPage)) return parameter is string cat ? $"ai-cat:{cat}" : null;

        if (pageType == typeof(HomePage))
        {
            if (parameter is string category) return category;
            return "all";
        }
        return null;
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPointProperties props = e.GetCurrentPoint(null).Properties;
        var frame = NavFrame;

        if (props.IsXButton1Pressed)
        {
            if (frame.CanGoBack)
            {
                frame.GoBack();
                e.Handled = true;
            }
        }
        else if (props.IsXButton2Pressed)
        {
            if (frame.CanGoForward)
            {
                frame.GoForward();
                e.Handled = true;
            }
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        // Gracefully stop EnergyStar throttling so any throttled processes recover
        // their normal scheduling priority before the app exits. If the user has
        // enabled the scheduled-task auto-start, the next logon will re-enable it.
        try { EnergyStarService.Shutdown(); } catch { }

        if (App.IsLiteMode)
        {
            args.Handled = true;
            AppWindow.Hide();
            return;
        }
        BackdropService.BackdropChanged -= OnBackdropChanged;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        AppWindow.Changed -= AppWindow_Changed;
        WindowSizeService.SaveWindowSize(this);
        DownloadQueueService.QueueChanged -= OnDownloadQueueChanged;
        AppSettings.SettingChanged -= OnBackgroundSettingChanged;
        NavLayoutModeService.NavLayoutModeChanged -= OnNavLayoutModeChanged;
        // AppSettings 落盘是去抖的（500ms 合并），退出前同步刷一次避免丢最后变更
        AppSettings.Flush();

        // 硬件监控句柄与 FPS 的 ETW 会话必须显式释放：内核 ETW 会话不会随进程终止自动回收，
        // 残留会让下次启动的 FPS 采集失效；轮询定时器/自动覆盖层/未落盘记录一并收尾。
        try { LiteMonitorService.Instance.Dispose(); } catch { }
        try { GameOverlayAutoService.Instance.Stop(); } catch { }

        // 【主审修复·2026-09-22】真正关窗口：即使当前停在别的页（设置/AI 页不在视觉树中），
        // 也要对主窗口唯一 AI 页执行"快照保存（含在途部分回复）+ 释放"——
        // 否则保活的在途生成/事件订阅/dsh 子进程不随窗口关闭收尾。
        try { _aiPageOwner?.Unload(); } catch { }   // Unload 内部：先落盘快照再 Dispose
        _aiPageOwner = null;
        _aiPageOwnerParam = null;
    }

    private void OnDownloadQueueChanged()
    {
        DispatcherQueue.TryEnqueue(UpdateDownloadBadge);
    }

    private void UpdateDownloadBadge()
    {
        var count = DownloadQueueService.PendingCount;
        if (count > 0)
        {
            DownloadQueueBadge.Value = count > 99 ? 99 : count;
            DownloadQueueBadge.Visibility = Visibility.Visible;
        }
        else
        {
            DownloadQueueBadge.Visibility = Visibility.Collapsed;
        }

        if (count > _lastBadgeCount)
        {
            PlayDownloadPulseAnimation();
        }
        _lastBadgeCount = count;
    }

    private void PlayDownloadPulseAnimation()
    {
        var btn = DownloadQueueButton;
        btn.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        btn.RenderTransform = new ScaleTransform();

        var scaleX = new DoubleAnimationUsingKeyFrames();
        Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
        Storyboard.SetTarget(scaleX, btn);
        scaleX.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 1.0 });
        scaleX.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(150), Value = 1.3, EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } });
        scaleX.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(400), Value = 1.0, EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseInOut } });

        var scaleY = new DoubleAnimationUsingKeyFrames();
        Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
        Storyboard.SetTarget(scaleY, btn);
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 1.0 });
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(150), Value = 1.3, EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } });
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(400), Value = 1.0, EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseInOut } });

        var sb = new Storyboard();
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Begin();
    }

    private void DownloadQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadFlyout is null)
        {
            _downloadFlyout = DownloadQueueFlyout.CreateThemedFlyout(DownloadQueueButton);
        }
        _downloadFlyout.ShowAt(DownloadQueueButton);
    }

    private void AiQuickButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new Flyout
        {
            Content = new AiQuickAskFlyout(),
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
        };
        // FlyoutPresenter 默认 MaxWidth=456（FlyoutThemeMaxWidth），会把内容钳在 456px；
        // 覆盖 presenter 宽度/高度限制，让 850px 面板完整显示。
        flyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
        {
            Setters =
            {
                new Setter(FlyoutPresenter.MaxWidthProperty, 880.0),
                new Setter(FlyoutPresenter.MaxHeightProperty, 810.0)
            }
        };
        flyout.ShowAt(AiQuickButton);
    }


    private void OnBackdropChanged()
    {
        DispatcherQueue.TryEnqueue(() => BackdropService.ApplyBackdrop(this));
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange) return;
        // 【返修4】窗口尺寸变化可能改变 Adaptive 布局/轨道状态：按当前模式重刷子项布局。
        try { RefreshNavChildItemLayout(); } catch { }
        var size = sender.Size;
        var minWidth = 800;
        var minHeight = 600;
        var needsResize = false;
        var newW = size.Width;
        var newH = size.Height;

        if (size.Width < minWidth)
        {
            newW = minWidth;
            needsResize = true;
        }
        if (size.Height < minHeight)
        {
            newH = minHeight;
            needsResize = true;
        }

        if (needsResize)
        {
            sender.Resize(new Windows.Graphics.SizeInt32(newW, newH));
        }
    }

    public void ApplyTitleBarTheme(ElementTheme theme)
    {
        var isDark = theme == ElementTheme.Dark ||
                     (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        TitleBarPalette.Apply(SafeTitleBar.Get(this), isDark);
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        var frame = NavFrame;
        if (frame.CanGoBack)
            frame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingNavSelection) return;

        // 【修复·2026-09-22】用户手动导航切换 → 解除搜索聚焦标记（不误恢复）
        _focusedCategoryKey = null;

        // 设置入口改由 ItemInvoked 统一处理：布局切换（Top/侧边栏）后 NavigationView
        // 内部选中状态可能残留（设置项与菜单项同时选中），点击设置不再触发
        // SelectionChanged；而 ItemInvoked 与选中状态无关、必然触发。
        if (args.IsSettingsSelected) return;

        if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "all":
                    NavFrame.Navigate(typeof(HomePage), null);
                    break;
                case "favorites":
                    NavFrame.Navigate(typeof(FavoritesPage));
                    break;
                case "hardware":
                    NavFrame.Navigate(typeof(HardwarePage));
                    break;
                // ZXAI 2026-09-23：枕星AI社区（顶级导航项，位于「硬件信息」之后）——
                // 页面是 WebView2 容器，打开自托管 Discourse 社区站点；站点地址只有一个入口
                //（Services/Community/CommunitySite.OfficialUrl），未配置时页面显示「社区即将开放」。
                case "ai-news":
                    NavFrame.Navigate(typeof(AiNewsPage));
                    break;
                case "skill-library":
                    NavFrame.Navigate(typeof(SkillLibraryPage));
                    break;
                case "community-hub":
                    NavFrame.Navigate(typeof(CommunityHubPage));
                    break;
                case "builtin":
                    NavFrame.Navigate(typeof(BuiltinToolsPage));
                    break;
                // ZXAI 2026-09-23：社区频道已删除（工具分摊到分类页）

                case "home":
                    // 主页 = 枕星图吧AI助手页面：用户进来直接就是对话界面（ZXAI 2026-09-19）
                    OpenAiAssistant();
                    break;
                case "benchmark":
                    _ = ExecuteBenchmarkToolAsync();
                    break;
                case string aiCat when aiCat.StartsWith("ai-cat:", StringComparison.Ordinal):
                    // ZXAI：AI 工具分类（AI agent / 软件开发 / 游戏开发）→ 分类目录页
                    NavFrame.Navigate(typeof(AiCategoryPage), aiCat["ai-cat:".Length..]);
                    break;
                case string category:
                    NavFrame.Navigate(typeof(HomePage), category);
                    break;
            }
        }
    }

    /// <summary>ZXAI 自检：直接打开指定工具分类页（--zxtest-cat 用）。</summary>
    public void ZxOpenCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        // ZXAI 2026-09-22：自检直达 AI 分类页（--zxtest-cat ai-cat:assistant 等）
        if (category.StartsWith("ai-cat:", StringComparison.OrdinalIgnoreCase))
        {
            NavFrame.Navigate(typeof(Pages.AiCategoryPage), category["ai-cat:".Length..]);
            return;
        }
        // ZXAI 2026-09-22：自检直达收藏页（--zxtest-cat favorites）
        if (string.Equals(category, "favorites", StringComparison.OrdinalIgnoreCase))
        {
            NavFrame.Navigate(typeof(Pages.FavoritesPage));
            return;
        }
        // ZXAI 2026-09-23：自检直达枕星AI社区（--zxtest-cat community）——后续 GUI 验收用
        if (string.Equals(category, "community", StringComparison.OrdinalIgnoreCase))
        {
            SyncNavSelection("community-hub");
            NavFrame.Navigate(typeof(Pages.CommunityHubPage));
            return;
        }
        // ZXAI 2026-09-22：自检直达设置/性能测试页（字体与页面回归用）
        if (string.Equals(category, "settings", StringComparison.OrdinalIgnoreCase))
        {
            NavFrame.Navigate(typeof(Pages.SettingsPage));
            return;
        }
        if (string.Equals(category, "performance", StringComparison.OrdinalIgnoreCase))
        {
            NavFrame.Navigate(typeof(Pages.PerformanceBenchmarkPage));
            return;
        }
        NavFrame.Navigate(typeof(HomePage), category);
    }

    /// <summary>最近打开的内置工具页内容（用于「主页=AI助手」的重复打开去重）。</summary>
    private object? _activeToolContent;

    /// <summary>「主页」打开 AI 的延迟订阅标记（Frame 未就绪时只排一次）。</summary>
    private bool _aiOpenPending;

    /// <summary>打开 AI 助手（内置工具「枕星图吧AI助手」）——左导航「主页」与主页卡片共用入口。
    /// ZXAI 修复：启动期窗口构造中 Frame 尚未加载，此时导航会夭折（白屏、页面永不加载）；
    /// 未就绪时延迟到 NavFrame.Loaded；已在 AI 助手页时直接跳过（防启动期二次触发重复重建）。</summary>
    /// 主窗口持有唯一会话宿主，所有主窗口入口复用它。
    /// 宿主为每个会话保留独立页面，切换会话或临时离开都不会取消后台生成。
    /// 真正关窗口时由 MainWindow_Closed 统一保存并释放所有会话。
    /// 独立工具窗口（BuiltinToolWindow）仍各自持有自己的实例，不共享此所有者。
    private Pages.AiConversationHost? _aiPageOwner;
    private ToolContentPageParam? _aiPageOwnerParam;

    public void OpenAiAssistant()
    {
        // 【主审修复·2026-09-22】入口语义一（主窗口入口：左导航「主页」、启动默认页、主窗口内卡片）：
        // 始终在主窗口 NavFrame 内复用唯一 owner——即使"内置工具在新窗口打开"设置生效，
        // 左主页也始终在主窗口内实际返回 AI 页；owner 绝不进独立窗口
        // （独立窗口关闭会 Detach→OnClose→Unload 释放页面，复用会把主窗口会话一起释放）。
        // 需要独立窗口的卡片入口走 OpenAiAssistantFromCard()。

        // ZXAI：已在 AI 页则刷新模型下拉（设置页加模型后点主页回来，不重建也要同步）
        if (NavFrame.IsLoaded && NavFrame.Content is ToolContentPage && _activeToolContent is Pages.AiConversationHost alreadyOpen)
        {
            alreadyOpen.ZxRefreshProviders();
            return;
        }
        if (!NavFrame.IsLoaded)
        {
            if (_aiOpenPending) return;
            _aiOpenPending = true;
            void OnFrameLoadedOnce(object? s, RoutedEventArgs e)
            {
                NavFrame.Loaded -= OnFrameLoadedOnce;
                _aiOpenPending = false;
                OpenAiAssistant();
            }
            NavFrame.Loaded += OnFrameLoadedOnce;
            return;
        }

        if (NavFrame.Content is ToolContentPage && _activeToolContent is AiConversationHost)
            return;

        // 【主审修复】唯一所有者：仅在首次创建，之后所有主窗口入口均复用同一实例
        // （页面内的会话/displayLog/在途生成随实例保活，不因来回导航而重建）。
        if (_aiPageOwner is null)
        {
            var page = new Pages.AiConversationHost();
            _aiPageOwner = page;
            _aiPageOwnerParam = new ToolContentPageParam
            {
                Title = LocalizationService.L("App_Title", "枕星图吧AI助手"),
                Description = "以 AI 助手为主：说出想做的项目，组合可选工具流并把能自动的装好配好；需要你操作的步骤会逐步引导。图吧工具箱是附加的现成工具与排障底座。",
                Glyph = "\uE946",
                Content = page,
                OnClose = () => page.Unload(),
                // 导航离开 ≠ 关闭：只落盘快照（含在途部分回复），不 Dispose、不取消在途生成。
                OnNavigatedAway = () => page.OnNavigatedAway(),
                HideBackButton = true,
                HideHeader = true
            };
        }
        // 【主审修复】直接在主窗口 NavFrame 内导航（绕过 NavigateToToolPage 的「独立窗口」分支）：
        // 保证 owner 不出主窗口、左主页始终实际返回 AI 页（即使"工具新窗口打开"设置开启）。
        _activeToolContent = _aiPageOwnerParam?.Content;
        NavFrame.Navigate(typeof(ToolContentPage), _aiPageOwnerParam, new DrillInNavigationTransitionInfo());
    }

    /// <summary>【主审修复·2026-09-22】入口语义二（工具卡片入口：内置工具卡片/AI 分类卡片/收藏卡片）：
    /// 按「内置工具在新窗口打开」设置分流——
    /// 独立窗口模式：独立新实例（各自持有、关闭时释放自己，不影响主窗口会话）；
    /// 主窗口模式：复用主窗口唯一 owner（与左主页为同一会话）。</summary>
    public void OpenAiAssistantFromCard()
    {
        if (BuiltinToolWindow.ForceWindowMode || AppSettings.GetBool("BuiltinToolsOpenInWindow", false))
        {
            OpenAiAssistantInWindow();
            return;
        }
        OpenAiAssistant();
    }

    /// <summary>创建并在独立工具窗口打开 AI 助手（独立实例，绝不复用主窗口 owner）。
    /// 【主审修复·2026-09-22】默认新会话（autoLoadLatest:false）——不自动载入最近历史，
    /// 避免与主窗口占用同一会话 ID 后两实例互相覆盖记录（主审 14:59 复现）。
    /// 之后在本窗口手动选择历史仍走 LoadConversation 的归属检查（被占用则清楚提示、不加载）。</summary>
    private Pages.AiConversationHost OpenAiAssistantInWindow()
    {
        var winPage = new Pages.AiConversationHost(compact: false, autoLoadLatest: false);
        BuiltinToolWindow.Show(typeof(ToolContentPage), new ToolContentPageParam
        {
            Title = LocalizationService.L("App_Title", "枕星图吧AI助手"),
            Description = "智能系统代理，可诊断问题、优化配置、执行操作并联网搜索",
            Glyph = "\uE946",
            Content = winPage,
            OnClose = () => winPage.Unload(),
            OnNavigatedAway = () => winPage.OnNavigatedAway(),
            HideBackButton = true,
            HideHeader = true
        }, LocalizationService.L("App_Title", "枕星图吧AI助手"));
        return winPage;
    }

    /// <summary>ZXAI：打开 AI 助手并向输入框预填一条消息（AI 工具分类卡片入口；只预填不自动发送）。
    /// 【主审修复】独立窗口模式时预填发生在独立实例上，不触碰主窗口 owner。</summary>
    public void OpenAiAssistantWithPrompt(string prompt)
    {
        if (BuiltinToolWindow.ForceWindowMode || AppSettings.GetBool("BuiltinToolsOpenInWindow", false))
        {
            var winPage = OpenAiAssistantInWindow();
            _ = PrefillAiInputAsync(prompt, winPage);
            return;
        }
        OpenAiAssistant();
        _ = PrefillAiInputAsync(prompt);
    }

    /// <summary>预填轮询：页面可能尚在导航 / 初始化（含构造期延迟路径），重试约 12 秒。
    /// target 非空时只对独立窗口实例预填（不触发主窗口补开）。</summary>
    private async Task PrefillAiInputAsync(string prompt, Pages.AiConversationHost? target = null)
    {
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(500);
            if (target is not null)
            {
                if (target.TryPrefillInput(prompt)) return;
                continue;
            }
            if (_activeToolContent is AiConversationHost page && page.TryPrefillInput(prompt)) return;
            if (i == 6) OpenAiAssistant(); // 兜底补开（幂等：已在 AI 页则跳过）
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem news && news.Tag is "ai-news"
            && ReferenceEquals(NavView.SelectedItem, news) && NavFrame.Content is not AiNewsPage)
        {
            NavFrame.Navigate(typeof(AiNewsPage));
            return;
        }
        if (args.InvokedItemContainer is NavigationViewItem header
            && (ReferenceEquals(header, AiToolsGroup) || ReferenceEquals(header, TubaToolsGroup)))
        {
            // 收起的图标栏中点击分组，打开该组；展开后再次点击，只切换该组。
            // 使用固定 Left 布局，避免原生 overlay auto-close 与分组开关相互覆盖。
            if (IsCompactTrackActive)
            {
                header.IsExpanded = true;
                NavView.IsPaneOpen = true;
                RefreshNavChildItemLayout();
            }
            else
            {
                ToggleNavGroup(header);
            }
            return;
        }
        // 【主审修复·2026-09-22】主页入口幂等兜底：
        // SelectionChanged 只在"选中态变化"时触发；顶栏入口（设置/收藏）后主页仍处选中态，
        // 此时点击主页不会发 SelectionChanged——由 ItemInvoked（与选中无关、必然触发）兜底打开。
        // 未选中时交由 SelectionChanged 处理，避免双重建/双导航。
        if (args.InvokedItemContainer is NavigationViewItem plainHome
            && plainHome.Tag is "home"
            && ReferenceEquals(NavView.SelectedItem, plainHome))
        {
            OpenAiAssistant();
            return;
        }
        // 【社区入口幂等兜底·2026-09-23】与「主页」同理：选中态相同时 NavigationView 不发
        // SelectionChanged，而内置工具页等导航不会改变选中项——此时点「枕星AI社区」会变成死点击。
        // 仅当"选中项是社区、但内容页不是社区"时补导航（已在社区页则不动，保持幂等）；
        // 硬件信息/AI 工具/图吧工具的既有行为不受影响（它们的兜底与否维持原样）。
        if (args.InvokedItemContainer is NavigationViewItem plainCommunity
            && plainCommunity.Tag is "community-hub"
            && ReferenceEquals(NavView.SelectedItem, plainCommunity)
            && NavFrame.Content is not CommunityHubPage)
        {
            NavFrame.Navigate(typeof(CommunityHubPage));
            return;
        }
        if (!args.IsSettingsInvoked) return;
        // 设置项点击与选中状态无关，保证布局切换后仍能打开设置
        NavFrame.Navigate(typeof(SettingsPage));
    }

    /// <summary>ZXAI：顶栏「设置」按钮（2026-09-19 由左栏底部移至下载队列右侧）。</summary>
    private void TitleBarSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        NavFrame.Navigate(typeof(SettingsPage));
    }

    /// <summary>ZXAI：顶栏「应用中心」按钮（2026-09-19，设置按钮右侧）。</summary>
    private void TitleBarAppCenterButton_Click(object sender, RoutedEventArgs e)
    {
        NavFrame.Navigate(typeof(AppCenterPage));
    }

    private void NavigateToDefaultPage()
    {
        // ZXAI 2026-09-20：用户口径——启动固定进主页（AI 助手），删除"默认启动页面"设置后不再读配置
        SyncNavSelection("home");
        OpenAiAssistant();
    }



    private void PopulateCategories(IReadOnlyList<string> categories)
    {
        // ZXAI 2026-09-20：动态分类平铺在「图吧工具」组头之后（随组折叠而显隐）
        // 【NAV 修复·Codex】用独立跟踪清单精确清理（旧实现依赖可能为空的
        // _tubaGroupChildren.LastOrDefault，紧凑移除后会漏删/重复添加）。
        foreach (var dyn in _dynamicNavItems.ToList())
        {
            NavView.MenuItems.Remove(dyn);
            _tubaGroupChildren.Remove(dyn);
        }
        _dynamicNavItems.Clear();

        var otherCategory = categories.FirstOrDefault(c => c.Contains("其他"));
        var restCategories = categories.Where(c => !c.Contains("其他"));

        void AddCategoryItem(string category)
        {
            var item = new NavigationViewItem
            {
                Content = LocalizationService.GetCategoryDisplayName(category),
                Tag = category,
                Margin = new Thickness(28, 0, 0, 0),
                Icon = new FontIcon { Glyph = GetCategoryGlyphStatic(category) }
            };
            ToolTipService.SetToolTip(item, item.Content);
            if (!TubaToolsGroup.IsExpanded) item.Visibility = Visibility.Collapsed;
            NavView.MenuItems.Add(item);
            if (!_tubaGroupChildren.Contains(item)) _tubaGroupChildren.Add(item);
            _dynamicNavItems.Add(item);
        }

        foreach (var category in restCategories) AddCategoryItem(category);
        if (otherCategory != null) AddCategoryItem(otherCategory);
        // 【返修4】动态分类项创建后立即按当前状态刷新（紧凑轨道下必须隐藏+归零缩进，
        // 否则新项带 28px 缩进漏进 62px 轨道、被右边界裁切——用户实测路径）。
        RefreshNavChildItemLayout();
    }

    public static string GetCategoryGlyphStatic(string category)
    {
        var customGlyph = AppSettings.Get($"CategoryGlyph_{category}");
        if (!string.IsNullOrWhiteSpace(customGlyph))
            return customGlyph;

        if (category.Contains("处理器", StringComparison.CurrentCultureIgnoreCase))
            return "\uEEA1";
        if (category.Contains("显卡", StringComparison.CurrentCultureIgnoreCase))
            return "\uF211";
        if (category.Contains("显示器", StringComparison.CurrentCultureIgnoreCase))
            return "\uE7F4";
        if (category.Contains("硬盘", StringComparison.CurrentCultureIgnoreCase))
            return "\uEDA2";
        if (category.Contains("内存", StringComparison.CurrentCultureIgnoreCase))
            return "\uEEA0";
        if (category.Contains("外设", StringComparison.CurrentCultureIgnoreCase))
            return "\uE962";
        if (category.Contains("游戏", StringComparison.CurrentCultureIgnoreCase))
            return "\uE7FC";
        if (category.Contains("声卡", StringComparison.CurrentCultureIgnoreCase))
            return "\uE7F5";
        if (category.Contains("网卡", StringComparison.CurrentCultureIgnoreCase))
            return "\uEDA3";
        if (category.Contains("烤鸡", StringComparison.CurrentCultureIgnoreCase))
            return "\uECAD";
        if (category.Contains("综合", StringComparison.CurrentCultureIgnoreCase))
            return "\uEC4E";
        if (category.Contains("其他", StringComparison.CurrentCultureIgnoreCase))
            return "\uE712";

        return "\uE8B7";
    }

    public void RefreshToolCategories()
    {
        if (_refreshCategoriesInFlight)
        {
            _refreshCategoriesPending = true;
            return;
        }
        _refreshCategoriesInFlight = true;
        _ = RefreshToolCategoriesCoreAsync();
    }

    private async Task RefreshToolCategoriesCoreAsync()
    {
        try
        {
            var categories = await Task.Run(() => ToolCatalog.GetCategories().ToList());

            if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    PopulateCategories(categories);
                }
                finally
                {
                    _refreshCategoriesInFlight = false;
                    if (_refreshCategoriesPending)
                    {
                        _refreshCategoriesPending = false;
                        RefreshToolCategories();
                    }
                }
            }))
            {
                _refreshCategoriesInFlight = false;
                _refreshCategoriesPending = false;
            }
        }
        catch
        {
            _refreshCategoriesInFlight = false;
            _refreshCategoriesPending = false;
        }
    }

    private void OnNavLayoutModeChanged(string mode)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyNavLayoutMode();
            // PaneDisplayMode 切换会重建侧边栏容器，NavigationView 的选中内部状态
            // 会残留（设置项与菜单项可能同时处于选中态），导致之后点击设置不再触发
            // SelectionChanged。先清空再按默认页重选，重建干净状态。
            NavView.SelectedItem = null;
            if (NavView.SettingsItem is NavigationViewItem settingsItem)
                settingsItem.IsSelected = false;
            NavView.UpdateLayout();
            NavigateToDefaultPage();
        });
    }

    private void ApplyNavLayoutMode()
    {
        // 导航宽度由用户控制；切换内容页不修改窗格或分组的展开状态。
        var isTabMode = NavLayoutModeService.IsTabMode();
        var paneOpen = NavView.IsPaneOpen;
        NavView.PaneDisplayMode = isTabMode
            ? NavigationViewPaneDisplayMode.Top
            : NavigationViewPaneDisplayMode.Left;
        NavView.IsPaneOpen = paneOpen;
        AppTitleBar.IsPaneToggleButtonVisible = !isTabMode;
        RefreshNavChildItemLayout();
    }

    private async Task ExecuteBenchmarkToolAsync()
    {
        NavigateToBenchmark();
    }

    public void NavigateToBenchmark()
    {
        NavFrame.Navigate(typeof(PerformanceBenchmarkPage));
    }

    public void NavigateToSkillLibrary()
    {
        if (FindNavItemByTag("skill-library") is { } item) NavView.SelectedItem = item;
        if (NavFrame.Content is not SkillLibraryPage) NavFrame.Navigate(typeof(SkillLibraryPage));
    }

    public void NavigateToToolPage(Type pageType, object? parameter = null)
    {
        // 设置"独立窗口"模式，或处于 AI 助手强制独立窗口作用域时，内置工具在新窗口的 Frame 中打开
        if (BuiltinToolWindow.ForceWindowMode || AppSettings.GetBool("BuiltinToolsOpenInWindow", false))
        {
            var title = parameter is ToolContentPageParam p
                ? p.Title
                : (ActiveToolName ?? "内置工具");
            BuiltinToolWindow.Show(pageType, parameter, title);
            return;
        }
        // ZXAI：记录当前打开的工具页内容（供「主页=AI助手」幂等去重判断）
        _activeToolContent = (parameter as ToolContentPageParam)?.Content;
        NavFrame.Navigate(pageType, parameter, new DrillInNavigationTransitionInfo());
    }

    public void NavigateToSettings(string? highlightSettingKey = null)
    {
        NavFrame.Navigate(typeof(SettingsPage),
            highlightSettingKey is null
                ? null
                : new SearchNavigationTarget { HighlightSettingKey = highlightSettingKey });
        SyncNavSelection("settings");
    }

    public void NavigateBack()
    {
        // 若前台是独立工具窗口，返回/关闭操作作用于该窗口，避免误操作主窗口导航
        if (BuiltinToolWindow.ActiveWindow is { } toolWindow)
        {
            toolWindow.GoBackOrClose();
            return;
        }
        if (NavFrame.CanGoBack)
            NavFrame.GoBack();
    }

    public bool CanNavigateBack()
    {
        return NavFrame.CanGoBack;
    }

    private void PopulateSearchSuggestions()
    {
        var items = UnifiedSearchService.GetQuickPanelItems();
        _searchResults.Clear();
        foreach (var item in items)
            _searchResults.Add(item);
    }

    private void ShowSearchPopup()
    {
        if (_searchDismissed) return;
        SearchPopup.IsOpen = _searchResults.Count > 0;
    }

    private void HideSearchPopup()
    {
        SearchPopup.IsOpen = false;
    }

    private void SearchPopup_GettingFocus(object sender, GettingFocusEventArgs e)
    {
        e.TryCancel();
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressSearch || _searchDismissed) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
            PopulateSearchSuggestions();
        ShowSearchPopup();
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!SearchBox.FocusState.HasFlag(FocusState.Programmatic))
                HideSearchPopup();
        });
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_suppressSearch) return;

        _searchDismissed = false;
        var query = SearchBox.Text.Trim();

        if (query.Length == 0)
        {
            _searchDebounceTimer.Stop();
            // 【完整修复·2026-09-22】搜索词清空 → 若当前停留在"搜索聚焦"的 AI 分类页，
            // 自动恢复为完整分类视图（避免"删掉搜索词后页面只剩一两张卡"的锁死状态）。
            RestoreFocusedCategoryIfNeeded();
            PopulateSearchSuggestions();
            ShowSearchPopup();
            return;
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    /// <summary>搜索聚焦态标记：从搜索结果跳进 AI 分类页时记录分类键；清空搜索框时据此恢复全量。</summary>
    private string? _focusedCategoryKey;

    /// <summary>【修复·2026-09-22】清空搜索词时，把仍处于聚焦过滤态的 AI 分类页恢复为完整列表。</summary>
    private void RestoreFocusedCategoryIfNeeded()
    {
        if (_focusedCategoryKey is null) return;
        var key = _focusedCategoryKey;
        _focusedCategoryKey = null;
        if (NavFrame.Content is Pages.AiCategoryPage)
            NavFrame.Navigate(typeof(Pages.AiCategoryPage), key);   // 无过滤参数 = 完整分类视图
    }

    private void OnSearchDebounceTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;

        _ = SearchInBackgroundAsync(query);
    }

    private async Task SearchInBackgroundAsync(string query)
    {
        try
        {
            var results = await Task.Run(() => UnifiedSearchService.Search(query));
            _searchResults.Clear();
            foreach (var r in results)
                _searchResults.Add(r);
            SearchPopup.IsOpen = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Search] {ex}");
        }
    }

    private void SearchListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResult result)
        {
            HideSearchPopup();
            HandleSearchResult(result);
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var idx = SearchListView.SelectedIndex;
        SearchResult? result = idx >= 0 && idx < _searchResults.Count
            ? _searchResults[idx]
            : _searchResults.FirstOrDefault();

        if (result is null) return;

        HideSearchPopup();
        HandleSearchResult(result);
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            SearchListView.SelectedIndex = -1;
            HideSearchPopup();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Down)
        {
            if (SearchListView.Items.Count > 0)
            {
                var next = SearchListView.SelectedIndex < 0
                    ? 0
                    : Math.Min(SearchListView.SelectedIndex + 1, SearchListView.Items.Count - 1);
                SearchListView.SelectedIndex = next;
                SearchListView.ScrollIntoView(SearchListView.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Up)
        {
            if (SearchListView.Items.Count > 0)
            {
                var prev = SearchListView.SelectedIndex <= 0
                    ? 0
                    : SearchListView.SelectedIndex - 1;
                SearchListView.SelectedIndex = prev;
                SearchListView.ScrollIntoView(SearchListView.SelectedItem);
            }
            e.Handled = true;
        }
    }

    private void SearchListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (SearchListView.SelectedItem is SearchResult result)
            {
                _suppressSearch = true;
                SearchBox.Text = string.Empty;
                _suppressSearch = false;
                HideSearchPopup();
                HandleSearchResult(result);
            }
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            HideSearchPopup();
            e.Handled = true;
        }
    }

    private void HandleSearchResult(SearchResult result)
    {
        var frame = NavFrame;

        switch (result.Kind)
        {
            case SearchItemKind.ExternalTool:
            case SearchItemKind.CustomTool:
                NavigateToTool(result.MatchKey);
                break;
            case SearchItemKind.BuiltinTool:
                frame.Navigate(typeof(BuiltinToolsPage),
                    new SearchNavigationTarget { HighlightBuiltinId = result.MatchKey });
                SyncNavSelection("builtin");
                break;

            case SearchItemKind.Setting:
                frame.Navigate(typeof(SettingsPage),
                    new SearchNavigationTarget { HighlightSettingKey = result.MatchKey });
                SyncNavSelection("settings");
                break;
            case SearchItemKind.QuickAction:
                HandleQuickAction(result.MatchKey);
                break;
            case SearchItemKind.AiTool:
                // ZXAI：AI 工具卡 → 打开对应 AI 工具分类页（MatchKey = "分类键|工具名"）
                // 【修复·2026-09-22】之前只 SyncNavSelection（仅同步高亮、不跳页），补上真实导航，
                // 与 Settings/BuiltinTool 分支同一模式：先 frame.Navigate，再同步导航选中。
                // 【聚焦·2026-09-22】把当前搜索词 + 被点卡名传给页面：目标页只展示与该搜索相关的卡
                //（主卡第一 + 模糊命中），不再整分类全列；左侧导航直达时无参数仍显示全部。
                var aiParts = result.MatchKey.Split('|');
                var aiCatKey = aiParts[0];
                var aiFocusName = aiParts.Length > 1 ? aiParts[1] : null;
                var aiTerm = SearchBox.Text?.Trim();
                var aiNavParam = string.IsNullOrEmpty(aiTerm)
                    ? aiCatKey
                    : $"{aiCatKey}|{aiTerm}|{aiFocusName}";
                // 记录聚焦态：清空搜索框时恢复完整分类视图（RestoreFocusedCategoryIfNeeded）
                _focusedCategoryKey = string.IsNullOrEmpty(aiTerm) ? null : aiCatKey;
                frame.Navigate(typeof(AiCategoryPage), aiNavParam);
                SyncNavSelection($"ai-cat:{aiCatKey}");
                break;
        }
    }

    private void SyncNavSelection(string tag)
    {
        _syncingNavSelection = true;
        var navItem = FindNavItemByTag(tag);
        if (navItem is not null) NavView.SelectedItem = navItem;
        _syncingNavSelection = false;
    }

    private List<NavigationViewItem> _aiGroupChildren = new();
    private List<NavigationViewItem> _tubaGroupChildren = new();

    /// <summary>收集两组的子项清单（AI 组按 Tag 前缀；图吧组=组头之后的所有项，含动态分类，ZXAI 2026-09-20）。</summary>
    private void CollectNavGroupChildren()
    {
        // 【NAV 修复·Codex】只允许收集一次（构造期：MenuItems 含全部 XAML 静态项、
        // 尚未动态添加/移除）。任何路径都不得从 MenuItems 反推成员。
        if (_navChildrenCollected) return;
        _aiGroupChildren = NavView.MenuItems.OfType<NavigationViewItem>()
            .Where(i => i.Tag is string t && t.StartsWith("ai-cat:")).ToList();
        var idx = NavView.MenuItems.IndexOf(TubaToolsGroup);
        _tubaGroupChildren = idx >= 0
            ? NavView.MenuItems.Skip(idx + 1).OfType<NavigationViewItem>().ToList()
            : new List<NavigationViewItem>();
        _navChildrenCollected = true;
    }

    /// <summary>点击组头：切换该组子项的显示/隐藏（ZXAI 2026-09-20）。</summary>
    private void ToggleNavGroup(NavigationViewItem header)
    {
        header.IsExpanded = !header.IsExpanded;
        RefreshNavChildItemLayout();
    }

    /// <summary>按 Tag 查找导航项（支持折叠组的子项，ZXAI 2026-09-20）。</summary>
    private NavigationViewItem? FindNavItemByTag(string tag)
    {
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>()
            .Concat(_aiGroupChildren).Concat(_tubaGroupChildren).Distinct())
        {
            if (item is not NavigationViewItem nvi) continue;
            if (nvi.Tag is string t && t == tag) return nvi;
            foreach (var child in nvi.MenuItems)
            {
                if (child is NavigationViewItem cn && cn.Tag is string ct && ct == tag) return cn;
            }
        }
        return null;
    }

    private void NavigateToTool(string toolPath)
    {
        try
        {
            var tools = ToolCatalog.GetAllToolsCached();
            var tool = tools.FirstOrDefault(t => t.Path.Equals(toolPath, StringComparison.OrdinalIgnoreCase));
            if (tool is not null)
            {
                NavFrame.Navigate(typeof(HomePage),
                    new SearchNavigationTarget { HighlightToolPath = toolPath });

                if (!string.IsNullOrEmpty(tool.Category))
                    SyncNavSelection(tool.Category);
            }
        }
        catch { }
    }

    private void HandleQuickAction(string action)
    {
        if (!action.StartsWith("navigate:")) return;
        var target = action["navigate:".Length..];
        var frame = NavFrame;

        switch (target)
        {
            case "hardware":
                frame.Navigate(typeof(HardwarePage));
                SyncNavSelection("hardware");
                break;
            case "favorites":
                frame.Navigate(typeof(FavoritesPage));
                SyncNavSelection("favorites");
                break;
            case "builtin":
                frame.Navigate(typeof(BuiltinToolsPage));
                SyncNavSelection("builtin");
                break;
            case "benchmark":
                _ = ExecuteBenchmarkToolAsync();
                break;

            case "settings":
                frame.Navigate(typeof(SettingsPage));
                break;
        }
    }
}
