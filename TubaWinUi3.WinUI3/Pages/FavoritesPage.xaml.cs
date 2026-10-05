using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Text;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Pages;

public sealed partial class FavoritesPage : Page, ILocalizablePage
{
    private readonly ObservableCollection<ToolItem> _tools = [];
    private int _aiFavCount;   // ZXAI：AI 收藏数量（编辑模式显隐用）
    private CancellationTokenSource? _iconLoadCts;
    private bool _isEditing;

    public FavoritesPage()
    {
        InitializeComponent();
        ToolsGrid.ItemsSource = _tools;
    }

    private void ToolsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var panel = ToolsGrid.ItemsPanelRoot as ItemsWrapGrid;
        if (panel is null) return;

        double minItemWidth = 280;
        double spacing = 12;
        double availableWidth = ToolsGrid.ActualWidth - ToolsGrid.Padding.Left - ToolsGrid.Padding.Right;

        if (availableWidth <= 0) return;

        int columns = Math.Max(1, (int)((availableWidth + spacing) / (minItemWidth + spacing)));
        double itemWidth = (availableWidth - (columns - 1) * spacing) / columns;
        panel.ItemWidth = Math.Max(minItemWidth, itemWidth);
    }

    /// <summary>ZXAI 2026-09-22：AI 收藏卡片网格列宽（与「我的收藏」同响应式逻辑）。</summary>
    private void AiFavGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var panel = AiFavGrid.ItemsPanelRoot as ItemsWrapGrid;
        if (panel is null) return;

        double minItemWidth = 280;
        double spacing = 12;
        double availableWidth = AiFavGrid.ActualWidth - AiFavGrid.Padding.Left - AiFavGrid.Padding.Right;
        if (availableWidth <= 0) return;

        int columns = Math.Max(1, (int)((availableWidth + spacing) / (minItemWidth + spacing)));
        double itemWidth = (availableWidth - (columns - 1) * spacing) / columns;
        panel.ItemWidth = Math.Max(minItemWidth, itemWidth);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadToolsAsync();
        LoadAiFavorites();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ExitEditMode();
    }

    /// <summary>ZXAI 2026-09-20：AI 卡收藏区（在 AI 卡片上点 ☆ 后进这里）。</summary>
    private void LoadAiFavorites()
    {
        var rows = new List<AiFavRow>();
        foreach (var name in UsageStats.GetAiFavorites())
        {
            var card = AiCategoryPage.FindCardByName(name);
            rows.Add(new AiFavRow(name, AiCardTexts.Desc(card?.Desc), card?.Glyph ?? "\uE99A"));
        }
        _aiFavCount = rows.Count;
        AiFavGrid.ItemsSource = rows;
        AiFavSection.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAiFavSubtitle();
    }

    private void AiFavAskAi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name) return;
        App.MainWindow?.OpenAiAssistantWithPrompt(string.Format(LocalizationService.L("Favorites_AiInstallPrompt", "帮我安装 {0}（先问我问题，问清楚再带我装）"), name));
    }

    private void AiFavUnfav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name) return;
        UsageStats.ToggleAiFavorite(name);
        LoadAiFavorites();
    }

    /// <summary>ZXAI 2026-09-22：「⬇ 一键安装」——本应用直接用 winget 安装（与 AI 分类页同逻辑）。</summary>
    private async void AiFavQuickInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name || string.IsNullOrWhiteSpace(name)) return;
        var key = AiToolLinks.QuickInstallKey(name);
        if (key is null) return;

        var confirm = new ContentDialog
        {
            Title = string.Format(LocalizationService.L("Favorites_InstallDialogTitle", "一键安装 {0}"), name),
            Content = string.Format(LocalizationService.L("Favorites_InstallDialogMessage", "将直接用 winget 安装到系统（不需要 AI 助手，可能持续几分钟，期间可继续用应用）：\n{0}"), name),
            PrimaryButtonText = LocalizationService.L("Favorites_InstallStart", "开始安装"),
            CloseButtonText = LocalizationService.L("Common_Cancel", "取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var old = b.Content;
        b.IsEnabled = false;
        string report;
        try
        {
            var reports = new List<string>();
            foreach (var req in AiToolLinks.RequiresOf(name))
            {
                b.Content = LocalizationService.L("Favorites_InstallCheckingDeps", "检查依赖…");
                if (await TubaWinUi3.Services.AppManagement.SystemInstaller.IsInstalledAsync(req, CancellationToken.None))
                    continue;
                b.Content = string.Format(LocalizationService.L("Favorites_InstallInstallingDep", "安装依赖（{0}）…"), req);
                var dep = await TubaWinUi3.Services.AppManagement.SystemInstaller.InstallAsync(req, null, CancellationToken.None);
                reports.Add(dep);
                if (!dep.StartsWith("✅") && !dep.Contains("已经装过"))
                {
                    reports.Add(string.Format(LocalizationService.L("Favorites_InstallDepFailed", "（依赖 {0} 未装成功，已中止主安装）"), req));
                    throw new InvalidOperationException(string.Join("\n\n", reports));
                }
            }
            b.Content = LocalizationService.L("Common_Installing", "安装中…");
            reports.Add(await TubaWinUi3.Services.AppManagement.SystemInstaller.InstallAsync(key, null, CancellationToken.None));
            report = string.Join("\n\n", reports);
        }
        catch (Exception ex)
        {
            report = ex.Message.StartsWith("❌") || ex.Message.Contains("依赖") ? ex.Message : string.Format(LocalizationService.L("Favorites_InstallFailed", "❌ 安装失败：{0}"), ex.Message);
        }
        finally
        {
            b.Content = old;
            b.IsEnabled = true;
        }

        _ = new ContentDialog
        {
            Title = report.StartsWith("✅") ? LocalizationService.L("Common_InstallDone", "安装完成") : LocalizationService.L("Favorites_InstallIncomplete", "安装未完成"),
            Content = report,
            CloseButtonText = LocalizationService.L("Common_OK", "好"),
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }

    /// <summary>ZXAI 2026-09-22：「🌐 官网」——浏览器打开官网（未收录自动回退搜索页）。</summary>
    private async void AiFavOpenOfficial_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name || string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(AiToolLinks.OfficialUrl(name)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Favorites] open url failed: {ex.Message}");
        }
    }



    private async Task LoadToolsAsync()
    {
        _iconLoadCts?.Cancel();
        _tools.Clear();

        var favPaths = FavoritesService.GetFavorites();
        if (favPaths.Count == 0)
        {
            UpdateToolCountText();
            ClearAllButton.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            ToolsGrid.Visibility = Visibility.Collapsed;
            return;
        }

        List<ToolItem> favTools;
        try
        {
            favTools = await Task.Run(() =>
            {
                var all = ToolCatalog.GetCategories()
                    .SelectMany(ToolCatalog.GetTools)
                    .ToList();
                // 按收藏列表顺序匹配,而不是目录分类顺序
                return favPaths
                    .Select(p => all.FirstOrDefault(t => t.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    .OfType<ToolItem>() // 收藏了但工具已不存在的路径跳过
                    .ToList();
            });
        }
        catch
        {
            ToolCountText.Text = LocalizationService.L("Favorites_LoadFailed", "加载失败");
            return;
        }

        foreach (var tool in favTools)
        {
            _tools.Add(tool);
        }

        UpdateToolCountText();
        var hasTools = _tools.Count > 0;
        ClearAllButton.Visibility = hasTools ? Visibility.Visible : Visibility.Collapsed;
        EditOrderButton.Visibility = hasTools ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasTools ? Visibility.Collapsed : Visibility.Visible;
        ToolsGrid.Visibility = hasTools ? Visibility.Visible : Visibility.Collapsed;

        if (favTools.Count > 0)
        {
            _iconLoadCts = new CancellationTokenSource();
            _ = ToolIconService.LoadIconsAsync(favTools, DispatcherQueue);
        }
    }

    /// <summary>编辑排序模式开关:进入后切换到专用排序列表,整行自实现拖拽。</summary>
    private void EditOrderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditing)
            ExitEditMode();
        else
            EnterEditMode();
    }

    private void EnterEditMode()
    {
        _isEditing = true;
        EditOrderIcon.Glyph = "\uE73E"; // CheckMark:完成
        EditOrderButtonText.Text = LocalizationService.L("Common_Done", "完成");
        ClearAllButton.Visibility = Visibility.Collapsed;
        ToolsGrid.Visibility = Visibility.Collapsed;
        AiFavSection.Visibility = Visibility.Collapsed;

        EditRowsPanel.Children.Clear();
        foreach (var tool in _tools)
            EditRowsPanel.Children.Add(CreateEditRow(tool));
        EditModePanel.Visibility = Visibility.Visible;
    }

    private void ExitEditMode()
    {
        if (!_isEditing) return;
        _isEditing = false;
        FinishDrag(); // 拖动到一半退出时归位
        EditOrderIcon.Glyph = "\uE70F"; // Edit:编辑排序
        EditOrderButtonText.Text = LocalizationService.L("Favorites_EditOrderButtonText", "编辑排序");
        EditModePanel.Visibility = Visibility.Collapsed;
        ClearAllButton.Visibility = _tools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolsGrid.Visibility = _tools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiFavSection.Visibility = _aiFavCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 拖拽过程已实时保存,这里兜底保证最终顺序落盘
        FavoritesService.SaveOrder(_tools.Select(t => t.Path));
    }

    private const double EditRowHeight = 64;
    private const double EditRowSpacing = 8;
    private static double EditRowStride => EditRowHeight + EditRowSpacing;

    private Border? _dragRow;
    private uint _dragPointerId;
    private double _dragStartY; // 按下时指针相对 EditRowsPanel 的 Y
    private int _dragStartIndex;
    private int _dragCurrentIndex;
    private bool _dragging;
    private TranslateTransform? _dragTranslate;

    private Border CreateEditRow(ToolItem tool)
    {
        var row = new Border
        {
            Height = EditRowHeight,
            Padding = new Thickness(12, 0, 12, 0),
            Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Tag = tool
        };
        row.PointerPressed += EditRow_PointerPressed;
        row.PointerMoved += EditRow_PointerMoved;
        row.PointerReleased += EditRow_PointerReleased;
        row.PointerCaptureLost += EditRow_PointerCaptureLost;

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 拖动手柄
        grid.Children.Add(new FontIcon
        {
            Glyph = "\uE700",
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8
        });

        // 图标(与常用推荐卡片相同的双元素绑定)
        var iconGrid = new Grid
        {
            Width = 36,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var image = new Image
        {
            Width = 28,
            Height = 28,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
        };
        image.SetBinding(Image.SourceProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Path = new PropertyPath(nameof(ToolItem.IconPath)),
            Source = tool,
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
        });
        image.SetBinding(Image.VisibilityProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Path = new PropertyPath(nameof(ToolItem.IconPath)),
            Source = tool,
            Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay,
            Converter = (Microsoft.UI.Xaml.Data.IValueConverter)Resources["NullToCollapse"]
        });
        var fontIcon = new FontIcon
        {
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Glyph = tool.IconGlyph ?? "",
            Visibility = tool.IconGlyph is not null ? Visibility.Visible : Visibility.Collapsed
        };
        iconGrid.Children.Add(image);
        iconGrid.Children.Add(fontIcon);
        Grid.SetColumn(iconGrid, 1);
        grid.Children.Add(iconGrid);

        // 名称 + 分类
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        textStack.Children.Add(new TextBlock
        {
            Text = tool.NameDisplay,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        textStack.Children.Add(new TextBlock
        {
            Text = tool.CategoryDisplayText,
            FontSize = 12,
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(textStack, 2);
        grid.Children.Add(textStack);

        row.Child = grid;
        return row;
    }

    private void EditRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging || sender is not Border row) return;
        _dragRow = row;
        _dragPointerId = e.Pointer.PointerId;
        _dragStartY = e.GetCurrentPoint(EditRowsPanel).Position.Y;
        _dragStartIndex = EditRowsPanel.Children.IndexOf(row);
        _dragCurrentIndex = _dragStartIndex;
        _dragging = false;
        row.CapturePointer(e.Pointer);
    }

    private void EditRow_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragRow is null || e.Pointer.PointerId != _dragPointerId) return;

        // 移动超过阈值才开始拖动,避免把普通点击当成拖动
        var deltaY = e.GetCurrentPoint(EditRowsPanel).Position.Y - _dragStartY;
        if (!_dragging)
        {
            if (Math.Abs(deltaY) < 6) return;
            _dragging = true;
            _dragTranslate = new TranslateTransform();
            _dragRow.RenderTransform = _dragTranslate;
            _dragRow.Opacity = 0.75;
        }

        // 限制拖动范围,行不会飞出列表
        var minDelta = -_dragStartIndex * EditRowStride;
        var maxDelta = (EditRowsPanel.Children.Count - 1 - _dragStartIndex) * EditRowStride;
        var clampedDelta = Math.Clamp(deltaY, minDelta, maxDelta);

        // 目标索引变化时实时重排行与数据源
        var target = _dragStartIndex + (int)Math.Round(clampedDelta / EditRowStride);
        if (target != _dragCurrentIndex)
        {
            EditRowsPanel.Children.Move((uint)_dragCurrentIndex, (uint)target);
            _tools.Move(_dragCurrentIndex, target);
            _dragCurrentIndex = target;
        }

        // 被拖行始终跟随指针:抵消重排带来的布局位移
        _dragTranslate!.Y = clampedDelta - (_dragCurrentIndex - _dragStartIndex) * EditRowStride;
    }

    private void EditRow_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId == _dragPointerId)
            FinishDrag();
    }

    private void EditRow_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId == _dragPointerId)
            FinishDrag();
    }

    private void FinishDrag()
    {
        if (_dragRow is null) return;
        if (_dragging)
        {
            _dragRow.RenderTransform = null;
            _dragRow.Opacity = 1.0;
            // 顺序已实时同步到 _tools,立即落盘
            FavoritesService.SaveOrder(_tools.Select(t => t.Path));
        }
        _dragRow = null;
        _dragTranslate = null;
        _dragging = false;
    }

    private void ToolsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ToolItem tool)
        {
            ShowToolDetail(tool);
        }
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolItem tool })
        {
            LaunchTool(tool, runAsAdmin: false);
        }
    }

    private void RunAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolItem tool })
        {
            LaunchTool(tool, runAsAdmin: true);
        }
    }

    private void RemoveFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToolItem tool })
        {
            FavoritesService.RemoveFavorite(tool.Path);
            _tools.Remove(tool);
            UpdateToolCountText();
            ClearAllButton.Visibility = _tools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EditOrderButton.Visibility = _tools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = _tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolsGrid.Visibility = _tools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SendToDesktopButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToolItem tool })
        {
            try
            {
                WindowsSearchIndexService.CreateDesktopShortcut(tool);
                ShowStatus(LocalizationService.L("Common_Created", "已创建"), string.Format(LocalizationService.L("Common_DesktopShortcutCreated", "已将「{0}」快捷方式发送到桌面"), tool.Name), InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowStatus(LocalizationService.L("Common_CreateFailed", "创建失败"), ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    private void FavItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ToolItem tool)
        {
            var flyout = (MenuFlyout)Resources["FavItemFlyout"];
            PopulateArchSubmenu(flyout, tool);
            UpdateTutorialVisibility(flyout, tool);
            UpdateBuiltinLinkFlyoutItems(flyout, tool);
            UpdateFavoriteMenuItem(flyout, tool);
            flyout.ShowAt(fe, e.GetPosition(fe));
        }
    }

    private static void UpdateFavoriteMenuItem(MenuFlyout flyout, ToolItem tool)
    {
        var item = flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(i => i.Name == "FavMenuToggleFavorite");
        if (item is null) return;
        item.Text = tool.IsFavorite ? LocalizationService.L("Common_Unfavorite", "取消收藏") : LocalizationService.L("Common_Favorite", "收藏");
        if (item.Icon is FontIcon icon)
            icon.Glyph = tool.IsFavorite ? "\uE735" : "\uE734";
    }

    private void FavMenu_ToggleFavorite(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ToolItem tool })
        {
            FavoritesService.ToggleFavorite(tool.Path);
            tool.IsFavorite = !tool.IsFavorite;
            if (!tool.IsFavorite)
            {
                _tools.Remove(tool);
                UpdateToolCountText();
                ClearAllButton.Visibility = _tools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                EmptyState.Visibility = _tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ToolsGrid.Visibility = _tools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private static void UpdateTutorialVisibility(MenuFlyout flyout, ToolItem tool)
    {
        var tutorialItem = flyout.Items.OfType<MenuFlyoutItem>()
            .FirstOrDefault(i => i.Name == "FavMenuOpenTutorial");
        if (tutorialItem is not null)
            tutorialItem.Visibility = tool.HasTutorial ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void UpdateBuiltinLinkFlyoutItems(MenuFlyout flyout, ToolItem tool)
    {
        var isBuiltin = tool.IsBuiltinLink;
        var sendToDesktop = flyout.Items.OfType<MenuFlyoutItem>()
            .FirstOrDefault(i => i.Name == "FavMenuSendToDesktop");
        // 内置工具只要有注册 Id 也能发桌面快捷方式（--open-builtin 启动），
        // 仅缺注册信息的旧链接才隐藏
        if (sendToDesktop is not null)
            sendToDesktop.Visibility = isBuiltin && string.IsNullOrWhiteSpace(tool.BuiltinToolId)
                ? Visibility.Collapsed
                : Visibility.Visible;

        var runAsAdmin = flyout.Items.OfType<MenuFlyoutItem>()
            .FirstOrDefault(i => i.Name == "FavMenuRunAsAdmin");
        if (runAsAdmin is not null)
            runAsAdmin.Visibility = isBuiltin ? Visibility.Collapsed : Visibility.Visible;

        var openDir = flyout.Items.OfType<MenuFlyoutItem>()
            .FirstOrDefault(i => i.Name == "FavMenuOpenDirectory");
        if (openDir is not null)
            openDir.Visibility = isBuiltin ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FavMenu_SendToDesktop(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ToolItem tool })
        {
            try
            {
                WindowsSearchIndexService.CreateDesktopShortcut(tool);
                ShowStatus(LocalizationService.L("Common_Created", "已创建"), string.Format(LocalizationService.L("Common_DesktopShortcutCreated", "已将「{0}」快捷方式发送到桌面"), tool.Name), InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowStatus(LocalizationService.L("Common_CreateFailed", "创建失败"), ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    private void FavMenu_RunAsAdmin(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ToolItem tool })
            LaunchTool(tool, runAsAdmin: true);
    }

    private void FavMenu_OpenDirectory(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ToolItem tool })
            OpenToolDirectory(tool);
    }

    private void FavMenu_OpenTutorial(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ToolItem tool } && tool.HasTutorial)
            BrowserPage.Open(tool.TutorialUrl!, string.Format(LocalizationService.L("Favorites_TutorialWindowTitle", "{0} - 使用教程"), tool.Name));
    }

    private static void OpenToolDirectory(ToolItem tool)
    {
        try
        {
            var dir = tool.EffectiveWorkingDir;
            if (Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { }
    }

    private void PopulateArchSubmenu(MenuFlyout flyout, ToolItem tool)
    {
        var submenu = flyout.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault(i => i.Name == "FavArchSubmenu");
        if (submenu is null) return;

        submenu.Items.Clear();

        if (tool.ArchOptions.Count <= 1)
        {
            submenu.Visibility = Visibility.Collapsed;
            return;
        }

        submenu.Visibility = Visibility.Visible;
        foreach (var opt in tool.ArchOptions)
        {
            var label = string.IsNullOrEmpty(opt.Arch) ? LocalizationService.L("Common_Default", "默认") : opt.Arch;
            var item = new ToggleMenuFlyoutItem
            {
                Text = label,
                IsChecked = opt == tool.SelectedArch,
                DataContext = opt
            };
            item.Click += (s, e) =>
            {
                if (s is ToggleMenuFlyoutItem { DataContext: ArchOption selected })
                    tool.SelectedArch = selected;
            };
            submenu.Items.Add(item);
        }
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = LocalizationService.L("Favorites_ClearAllDialogTitle", "清空全部收藏"),
            Content = LocalizationService.L("Favorites_ClearAllDialogMessage", "确定要取消所有工具的收藏吗？此操作不可撤销。"),
            PrimaryButtonText = LocalizationService.L("Favorites_ClearAllDialogPrimary", "清空"),
            CloseButtonText = LocalizationService.L("Common_Cancel", "取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };

        if (dialog.ShowAsync() is not null)
        {
            dialog.PrimaryButtonClick += (_, _) =>
            {
                FavoritesService.RemoveAll();
                _ = LoadToolsAsync();
            };
        }
    }

    private void ShowToolDetail(ToolItem tool)
    {
        _detailTool = tool;
        if (tool.IsBuiltinLink)
        {
            ToolDetailTip.Title = tool.NameDisplay;
            ToolDetailTip.Subtitle = LocalizationService.GetCategoryDisplayName(tool.Category);
            DetailDescriptionText.Text = string.IsNullOrWhiteSpace(tool.Description)
                ? LocalizationService.L("Common_NoDescription", "暂无介绍。")
                : tool.DescriptionDisplay;
            DetailPublisherText.Text = string.Format(LocalizationService.L("Favorites_DetailType", "类型：{0}"), tool.BuiltinKindDisplay ?? LocalizationService.L("Common_Builtin", "内置"));
            DetailVersionText.Text = "";
            DetailPathText.Text = "";
            ToolDetailTip.IsOpen = true;
            return;
        }

        ToolDetailTip.Title = tool.NameDisplay;
        ToolDetailTip.Subtitle = LocalizationService.GetCategoryDisplayName(tool.Category);
        DetailDescriptionText.Text = string.IsNullOrWhiteSpace(tool.Description)
            ? LocalizationService.L("Common_NoDescription", "暂无介绍。")
            : tool.DescriptionDisplay;
        DetailPublisherText.Text = string.Format(LocalizationService.L("Favorites_DetailPublisher", "发布者：{0}"), ValueOrUnknown(tool.Publisher));
        DetailVersionText.Text = string.Format(LocalizationService.L("Favorites_DetailVersion", "版本：{0}"), ValueOrUnknown(tool.Version));
        DetailPathText.Text = tool.Path;
        ToolDetailTip.IsOpen = true;
    }

    private void LaunchTool(ToolItem tool, bool runAsAdmin)
    {
        if (tool.IsBuiltinLink)
        {
            _ = LaunchBuiltinToolAsync(tool);
            return;
        }

        if (!string.IsNullOrWhiteSpace(tool.RemoteUrl))
        {
            Pages.BrowserPage.Open(tool.RemoteUrl, tool.Name);
            LaunchHistoryService.RecordLaunch(tool.Path);
            ShowStatus(LocalizationService.L("Common_Opened", "已打开"), tool.Name, InfoBarSeverity.Success);
            return;
        }

        var exePath = tool.EffectivePath;
        if (!File.Exists(exePath))
        {
            ShowStatus(LocalizationService.L("Common_LaunchFailed", "启动失败"), string.Format(LocalizationService.L("Favorites_StatusFileNotFound", "找不到文件：{0}"), exePath), InfoBarSeverity.Error);
            return;
        }

        try
        {
            ToolProcessLauncher.Launch(exePath, tool.EffectiveWorkingDir, runAsAdmin);

            LaunchHistoryService.RecordLaunch(tool.Path);
            ShowStatus(runAsAdmin ? LocalizationService.L("Common_LaunchedAsAdmin", "已以管理员身份启动") : LocalizationService.L("Common_Launched", "已启动"), tool.Name, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(LocalizationService.L("Common_LaunchFailed", "启动失败"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task LaunchBuiltinToolAsync(ToolItem tool)
    {
        var builtinTool = BuiltinToolRegistry.GetById(tool.BuiltinToolId!);
        if (builtinTool is null)
        {
            ShowStatus(LocalizationService.L("Common_LaunchFailed", "启动失败"), LocalizationService.L("Favorites_StatusBuiltinNotFound", "找不到对应的内置工具"), InfoBarSeverity.Error);
            return;
        }

        try
        {
            var context = new BuiltinToolContext
            {
                XamlRoot = XamlRoot,
                OnProgress = msg => DispatcherQueue.TryEnqueue(() =>
                    ShowStatus(builtinTool.Name, msg, InfoBarSeverity.Informational))
            };
            MainWindow.ActiveToolName = builtinTool.Name;
            await builtinTool.ExecuteAsync(context);
            LaunchHistoryService.RecordLaunch(tool.Path);
            ShowStatus(LocalizationService.L("Common_Launched", "已启动"), tool.Name, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(LocalizationService.L("Common_LaunchFailed", "启动失败"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            MainWindow.ActiveToolName = null;
        }
    }

    private DispatcherTimer? _statusBarTimer;

    private ToolItem? _detailTool;   // 语言切换时重刷已打开的详情面板

    public void ApplyLocalization()
    {
        UpdateToolCountText();
        UpdateAiFavSubtitle();
        EditOrderButtonText.Text = _isEditing
            ? LocalizationService.L("Common_Done", "完成")
            : LocalizationService.L("Favorites_EditOrderButtonText", "编辑排序");

        foreach (var item in _tools)
            item.NotifyLocalizationChanged();

        LoadAiFavorites();

        if (ToolDetailTip.IsOpen && _detailTool is not null)
            ShowToolDetail(_detailTool);
    }

    private void UpdateToolCountText()
    {
        ToolCountText.Text = _tools.Count > 0
            ? string.Format(LocalizationService.L("Favorites_ToolCount", "共 {0} 个工具"), _tools.Count)
            : LocalizationService.L("Favorites_NoFavorites", "暂无收藏");
    }

    private void UpdateAiFavSubtitle()
    {
        AiFavSubtitle.Text = _aiFavCount > 0
            ? string.Format(LocalizationService.L("Favorites_AiFavCount", "共 {0} 张 · 点 ★ 可取消收藏"), _aiFavCount)
            : LocalizationService.L("Favorites_AiEmptyHint.Text", "在 AI 工具卡片上点 ☆ 收藏");
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;

        _statusBarTimer?.Stop();
        _statusBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusBarTimer.Tick += (s, e) =>
        {
            StatusBar.IsOpen = false;
            ((DispatcherTimer)s!).Stop();
        };
        _statusBarTimer.Start();
    }

    private static string ValueOrUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? LocalizationService.L("Common_Unknown", "未知") : value;
    }
}

/// <summary>ZXAI：收藏目录里的 AI 卡行。</summary>
public sealed record AiFavRow(string Name, string Desc, string Glyph)
{
    /// <summary>ZXAI 2026-09-22：厂商图标（无则回退 Glyph）。</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? IconSource => Services.AiIconService.GetIcon(Name);

    public Visibility IconVisibility => IconSource is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GlyphVisibility => IconSource is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>ZXAI 2026-09-22：与 AI 分类页一致——有 winget 映射才显示「⬇ 一键安装」；纯网页工具藏掉。</summary>
    public Visibility QuickInstallVisibility => AiToolLinks.HasQuickInstall(Name) && !AiToolLinks.IsWebOnly(Name) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>ZXAI 2026-09-22（用户口径）：纯网页工具（如 DeepSeek）不显示「AI 安装」。</summary>
    public Visibility AiInstallVisibility => AiToolLinks.CanAiInstall(Name) ? Visibility.Visible : Visibility.Collapsed;
}
